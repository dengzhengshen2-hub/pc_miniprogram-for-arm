#include "low_power_runtime.h"

#include <string.h>

#include "app_display_runtime.h"
#include "battery_monitor.h"
#include "clock_profile_service.h"
#include "esp_host_service.h"
#include "gpio_pm_service.h"
#include "power_manager.h"
#include "redpic1_app.h"
#include "redpic1_thermal.h"
#include "rtc_lp_service.h"
#include "settings_service.h"
#include "stm32f4xx_conf.h"
#include "watchdog_service.h"

#define LOW_POWER_RUNTIME_STOP_PREP_TIMEOUT_MS    400UL
#define LOW_POWER_RUNTIME_MANUAL_STANDBY_WAKE_MS  10000UL
#define LOW_POWER_RUNTIME_STANDBY_IDLE_MS         (30UL * 60UL * 1000UL)
#define LOW_POWER_RUNTIME_STANDBY_LOW_MV          3000U
#define LOW_POWER_RUNTIME_STANDBY_RECOVER_MV      3300U
#define LOW_POWER_RUNTIME_STANDBY_BACKOFF_MAX     5U
#define LOW_POWER_RUNTIME_CTX_MAGIC               0x4C505231UL
#define LOW_POWER_RUNTIME_SERVICE_MARGIN_MS       150UL

typedef struct
{
    uint32_t magic;
    uint32_t retry_count;
    uint32_t last_battery_mv;
    uint32_t next_period_ms;
} low_power_standby_ctx_t;

static lp_runtime_state_t s_runtime_state = LP_RUNTIME_STATE_RUN;
static power_state_t s_last_power_state = POWER_STATE_ACTIVE_UI;
static uint32_t s_screen_off_enter_ms = 0U;
static uint8_t s_stop_host_prepared = 0U;
static uint8_t s_manual_standby_pending = 0U;

static uint8_t low_power_runtime_service_prepare(app_service_cmd_id_t cmd_id, uint32_t timeout_ms)
{
    app_service_cmd_t cmd;
    app_service_rsp_t rsp;

    memset(&cmd, 0, sizeof(cmd));
    memset(&rsp, 0, sizeof(rsp));
    cmd.cmd_id = cmd_id;
    cmd.value = timeout_ms;

    return app_service_submit(&cmd, &rsp, timeout_ms + LOW_POWER_RUNTIME_SERVICE_MARGIN_MS);
}

static void low_power_runtime_quiesce_irqs_for_standby(void)
{
    TIM_Cmd(TIM3, DISABLE);
    TIM_ITConfig(TIM3, TIM_IT_Update, DISABLE);
    TIM_ClearITPendingBit(TIM3, TIM_IT_Update);

    TIM_Cmd(TIM5, DISABLE);
    TIM_ITConfig(TIM5, TIM_IT_Update, DISABLE);
    TIM_ClearITPendingBit(TIM5, TIM_IT_Update);

    USART_ITConfig(USART1, USART_IT_RXNE, DISABLE);
    USART_ITConfig(USART1, USART_IT_ERR, DISABLE);

    EXTI_ClearITPendingBit(EXTI_Line8);
    EXTI_ClearITPendingBit(EXTI_Line9);
    EXTI_ClearITPendingBit(EXTI_Line13);
    EXTI_ClearITPendingBit(EXTI_Line22);

    NVIC_ClearPendingIRQ(TIM3_IRQn);
    NVIC_ClearPendingIRQ(TIM5_IRQn);
    NVIC_ClearPendingIRQ(USART1_IRQn);
    NVIC_ClearPendingIRQ(EXTI9_5_IRQn);
    NVIC_ClearPendingIRQ(EXTI15_10_IRQn);
    NVIC_ClearPendingIRQ(RTC_WKUP_IRQn);
}

static uint32_t low_power_runtime_next_standby_period_ms(uint32_t retry_count)
{
    static const uint32_t periods_ms[] = { 60000UL, 120000UL, 300000UL, 600000UL, 900000UL, 1800000UL };

    if (retry_count >= LOW_POWER_RUNTIME_STANDBY_BACKOFF_MAX)
    {
        retry_count = LOW_POWER_RUNTIME_STANDBY_BACKOFF_MAX;
    }

    return periods_ms[retry_count];
}

static void low_power_runtime_store_ctx(uint32_t retry_count, uint32_t battery_mv, uint32_t next_period_ms)
{
    rtc_lp_backup_write(0U, LOW_POWER_RUNTIME_CTX_MAGIC);
    rtc_lp_backup_write(1U, retry_count);
    rtc_lp_backup_write(2U, battery_mv);
    rtc_lp_backup_write(3U, next_period_ms);
}

static void low_power_runtime_clear_ctx(void)
{
    rtc_lp_backup_write(0U, 0U);
    rtc_lp_backup_write(1U, 0U);
    rtc_lp_backup_write(2U, 0U);
    rtc_lp_backup_write(3U, 0U);
}

static uint8_t low_power_runtime_load_ctx(low_power_standby_ctx_t *ctx)
{
    if (ctx == 0)
    {
        return 0U;
    }

    ctx->magic = rtc_lp_backup_read(0U);
    ctx->retry_count = rtc_lp_backup_read(1U);
    ctx->last_battery_mv = rtc_lp_backup_read(2U);
    ctx->next_period_ms = rtc_lp_backup_read(3U);

    return (ctx->magic == LOW_POWER_RUNTIME_CTX_MAGIC) ? 1U : 0U;
}

static void low_power_runtime_update_clock_profile(void)
{
    const device_settings_t *settings = settings_service_get();
    power_lock_mask_t lock_mask = power_manager_get_lock_mask();

    if (settings->clock_profile_policy == CLOCK_PROFILE_POLICY_HIGH_ONLY)
    {
        clock_profile_set(CLOCK_PROFILE_HIGH);
        return;
    }

    if ((lock_mask & (POWER_LOCK_THERMAL | POWER_LOCK_OTA | POWER_LOCK_ESP_HOST)) != 0U ||
        power_manager_get_state() == POWER_STATE_ACTIVE_THERMAL)
    {
        clock_profile_set(CLOCK_PROFILE_HIGH);
    }
    else
    {
        clock_profile_set(CLOCK_PROFILE_MEDIUM);
    }
}

static void low_power_runtime_handle_post_wake(void)
{
    uint8_t woke_by_timer = 0U;

    woke_by_timer = rtc_lp_consume_wakeup_event();
    if ((woke_by_timer != 0U) && (power_manager_get_state() == POWER_STATE_SCREEN_OFF_IDLE))
    {
        clock_profile_restore_after_stop_keep_uart_sleep();
    }
    else
    {
        clock_profile_restore_after_stop();
    }
    gpio_pm_restore_after_stop();
    redpic1_thermal_restore_bus_after_stop();
    rtc_lp_disarm();
    if (woke_by_timer != 0U)
    {
        power_manager_advance_sleep_time(rtc_lp_get_last_elapsed_ms());
    }
    watchdog_service_note_stop_wake();
    s_runtime_state = LP_RUNTIME_STATE_RUN;
}

static void low_power_runtime_enter_stop(void)
{
    const device_settings_t *settings = settings_service_get();
    uint32_t wake_period_ms = settings->rtc_stop_wake_ms;
    uint8_t host_ready = 0U;
    esp_host_status_t host_status;

    if (wake_period_ms == 0U)
    {
        wake_period_ms = 1000U;
    }

    watchdog_service_force_feed();
    if (s_stop_host_prepared == 0U)
    {
        host_ready = low_power_runtime_service_prepare(APP_SERVICE_CMD_PREPARE_STOP,
                                                      LOW_POWER_RUNTIME_STOP_PREP_TIMEOUT_MS);
        esp_host_get_status_copy(&host_status);
        if (host_ready == 0U && host_status.online != 0U)
        {
            s_runtime_state = LP_RUNTIME_STATE_RUN;
            return;
        }

        s_stop_host_prepared = 1U;
    }

    gpio_pm_prepare_stop();
    rtc_lp_arm_ms(wake_period_ms);
    s_runtime_state = LP_RUNTIME_STATE_STOP_IDLE;

    PWR_ClearFlag(PWR_FLAG_WU);
    PWR_EnterSTOPMode(PWR_Regulator_LowPower, PWR_STOPEntry_WFI);

    low_power_runtime_handle_post_wake();
}

static void low_power_runtime_enter_manual_standby(void)
{
    watchdog_service_force_feed();
    (void)low_power_runtime_service_prepare(APP_SERVICE_CMD_PREPARE_STANDBY,
                                            LOW_POWER_RUNTIME_STOP_PREP_TIMEOUT_MS);
    (void)app_display_runtime_sleep();
    gpio_pm_prepare_standby();
    low_power_runtime_clear_ctx();
    low_power_runtime_quiesce_irqs_for_standby();
    rtc_lp_arm_ms(LOW_POWER_RUNTIME_MANUAL_STANDBY_WAKE_MS);
    s_runtime_state = LP_RUNTIME_STATE_STANDBY_PROTECT;

    PWR_ClearFlag(PWR_FLAG_WU);
    PWR_ClearFlag(PWR_FLAG_SB);
    __disable_irq();
    __DSB();
    __ISB();
    PWR_EnterSTANDBYMode();
}

static void low_power_runtime_enter_standby(uint32_t retry_count)
{
    uint32_t battery_mv = battery_monitor_get_mv();
    uint32_t next_period_ms = low_power_runtime_next_standby_period_ms(retry_count);

    watchdog_service_force_feed();
    (void)low_power_runtime_service_prepare(APP_SERVICE_CMD_PREPARE_STANDBY,
                                            LOW_POWER_RUNTIME_STOP_PREP_TIMEOUT_MS);
    (void)app_display_runtime_sleep();
    gpio_pm_prepare_standby();
    low_power_runtime_store_ctx(retry_count, battery_mv, next_period_ms);
    low_power_runtime_quiesce_irqs_for_standby();
    rtc_lp_arm_ms(next_period_ms);
    s_runtime_state = LP_RUNTIME_STATE_STANDBY_PROTECT;

    PWR_ClearFlag(PWR_FLAG_WU);
    PWR_ClearFlag(PWR_FLAG_SB);
    __disable_irq();
    __DSB();
    __ISB();
    PWR_EnterSTANDBYMode();
}

uint8_t low_power_runtime_handle_early_boot(void)
{
    const device_settings_t *settings = settings_service_get();
    low_power_standby_ctx_t ctx;

    if (rtc_lp_woke_from_standby() == 0U)
    {
        return 0U;
    }

    if (low_power_runtime_load_ctx(&ctx) == 0U)
    {
        return 0U;
    }

    if (settings->standby_enabled == 0U || settings->power_policy != POWER_POLICY_ECO)
    {
        low_power_runtime_clear_ctx();
        return 0U;
    }

    if (battery_monitor_get_mv() >= LOW_POWER_RUNTIME_STANDBY_RECOVER_MV)
    {
        low_power_runtime_clear_ctx();
        return 0U;
    }

    low_power_runtime_enter_standby(ctx.retry_count + 1U);
    return 1U;
}

void low_power_runtime_init(void)
{
    s_runtime_state = LP_RUNTIME_STATE_RUN;
    s_last_power_state = power_manager_get_state();
    s_screen_off_enter_ms = 0U;
    s_stop_host_prepared = 0U;
    s_manual_standby_pending = 0U;
}

void low_power_runtime_step(void)
{
    const device_settings_t *settings = settings_service_get();
    uint32_t now_ms = power_manager_get_tick_ms();
    uint32_t screen_off_elapsed_ms = 0U;

    if (power_manager_get_state() != s_last_power_state)
    {
        s_last_power_state = power_manager_get_state();
        if (s_last_power_state == POWER_STATE_SCREEN_OFF_IDLE)
        {
            s_screen_off_enter_ms = now_ms;
            s_stop_host_prepared = 0U;
        }
        else
        {
            s_stop_host_prepared = 0U;
        }
    }

    low_power_runtime_update_clock_profile();

    if (s_manual_standby_pending != 0U)
    {
        s_manual_standby_pending = 0U;
        low_power_runtime_enter_manual_standby();
        return;
    }

    if (power_manager_get_state() != POWER_STATE_SCREEN_OFF_IDLE)
    {
        s_runtime_state = LP_RUNTIME_STATE_RUN;
        __WFI();
        return;
    }

    if (watchdog_service_can_enter_stop() == 0U)
    {
        s_runtime_state = LP_RUNTIME_STATE_RUN;
        __WFI();
        return;
    }

    screen_off_elapsed_ms = now_ms - s_screen_off_enter_ms;
    if (settings->power_policy == POWER_POLICY_ECO &&
        settings->standby_enabled != 0U &&
        screen_off_elapsed_ms >= LOW_POWER_RUNTIME_STANDBY_IDLE_MS &&
        battery_monitor_get_mv() < LOW_POWER_RUNTIME_STANDBY_LOW_MV)
    {
        low_power_runtime_enter_standby(0U);
        return;
    }

    low_power_runtime_enter_stop();
}

lp_runtime_state_t low_power_runtime_get_state(void)
{
    return s_runtime_state;
}

void low_power_runtime_request_manual_standby(void)
{
    s_manual_standby_pending = 1U;
}
