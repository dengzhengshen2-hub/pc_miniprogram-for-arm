#include "power_manager.h"

#include "stm32f4xx_tim.h"
#include "sys.h"

#define POWER_MANAGER_DEFAULT_TIMEOUT_MS 15000UL
#define POWER_MANAGER_TICK_MS            10UL             

static volatile uint32_t s_tick_count = 0U;
static volatile uint32_t s_last_activity_tick = 0U;
static volatile power_lock_mask_t s_lock_mask = 0U;
static volatile power_state_t s_state = POWER_STATE_ACTIVE_UI;
static volatile power_policy_t s_policy = POWER_POLICY_BALANCED;
static volatile uint32_t s_screen_off_timeout_ticks = (POWER_MANAGER_DEFAULT_TIMEOUT_MS / POWER_MANAGER_TICK_MS);

static uint32_t power_manager_irq_save(void)
{
    uint32_t primask = __get_PRIMASK();

    __disable_irq();
    return primask;
}

static void power_manager_irq_restore(uint32_t primask)
{
    if (primask == 0U)
    {
        __enable_irq();
    }
}

static power_state_t power_manager_compute_state_locked(void)
{
    power_state_t next_state = POWER_STATE_ACTIVE_UI;
    power_lock_mask_t effective_lock_mask =
        (power_lock_mask_t)(s_lock_mask & (power_lock_mask_t)(~POWER_LOCK_ESP_HOST));

    if ((s_lock_mask & POWER_LOCK_THERMAL) != 0U)
    {
        next_state = POWER_STATE_ACTIVE_THERMAL;
    }
    else if ((s_policy != POWER_POLICY_PERFORMANCE) &&
             (effective_lock_mask == 0U) &&
             ((s_tick_count - s_last_activity_tick) >= s_screen_off_timeout_ticks))
    {
        next_state = POWER_STATE_SCREEN_OFF_IDLE;
    }

    return next_state;
}

static uint32_t power_manager_get_apb1_timer_clock_hz(void)
{
    uint32_t ppre1_bits = RCC->CFGR & RCC_CFGR_PPRE1;
    uint32_t hclk_hz = SystemCoreClock;
    uint32_t pclk1_hz = hclk_hz;

    switch (ppre1_bits)
    {
    case RCC_CFGR_PPRE1_DIV2:
        pclk1_hz = hclk_hz / 2U;
        break;
    case RCC_CFGR_PPRE1_DIV4:
        pclk1_hz = hclk_hz / 4U;
        break;
    case RCC_CFGR_PPRE1_DIV8:
        pclk1_hz = hclk_hz / 8U;
        break;
    case RCC_CFGR_PPRE1_DIV16:
        pclk1_hz = hclk_hz / 16U;
        break;
    default:
        pclk1_hz = hclk_hz;
        break;
    }

    return (ppre1_bits == RCC_CFGR_PPRE1_DIV1) ? pclk1_hz : (pclk1_hz * 2U);
}

static void power_manager_tim5_init(void)
{
    TIM_TimeBaseInitTypeDef tim_time_base;
    NVIC_InitTypeDef nvic_init;
    uint32_t timer_clock_hz = power_manager_get_apb1_timer_clock_hz();
    uint32_t prescaler = (timer_clock_hz / 10000UL);

    if (prescaler == 0U)
    {
        prescaler = 1U;
    }

    RCC_APB1PeriphClockCmd(RCC_APB1Periph_TIM5, ENABLE);

    TIM_Cmd(TIM5, DISABLE);
    TIM_DeInit(TIM5);

    tim_time_base.TIM_Period = 100U - 1U;
    tim_time_base.TIM_Prescaler = (uint16_t)(prescaler - 1U);
    tim_time_base.TIM_ClockDivision = TIM_CKD_DIV1;
    tim_time_base.TIM_CounterMode = TIM_CounterMode_Up;
    TIM_TimeBaseInit(TIM5, &tim_time_base);
    TIM_ITConfig(TIM5, TIM_IT_Update, ENABLE);

    nvic_init.NVIC_IRQChannel = TIM5_IRQn;
    nvic_init.NVIC_IRQChannelPreemptionPriority = 2;
    nvic_init.NVIC_IRQChannelSubPriority = 0;
    nvic_init.NVIC_IRQChannelCmd = ENABLE;
    NVIC_Init(&nvic_init);

    TIM_SetCounter(TIM5, 0U);
    TIM_Cmd(TIM5, ENABLE);
}

void power_manager_init(void)
{
    uint32_t primask = power_manager_irq_save();

    s_tick_count = 0U;
    s_last_activity_tick = 0U;
    s_lock_mask = 0U;
    s_state = POWER_STATE_ACTIVE_UI;
    s_policy = POWER_POLICY_BALANCED;
    s_screen_off_timeout_ticks = (POWER_MANAGER_DEFAULT_TIMEOUT_MS / POWER_MANAGER_TICK_MS);
    power_manager_irq_restore(primask);
    power_manager_tim5_init();
}

void power_manager_notify_activity(void)
{
    uint32_t primask = power_manager_irq_save();

    s_last_activity_tick = s_tick_count;
    power_manager_irq_restore(primask);
}

void power_manager_acquire_lock(power_lock_mask_t mask)
{
    uint32_t primask = power_manager_irq_save();

    s_lock_mask |= mask;
    power_manager_irq_restore(primask);
}

void power_manager_release_lock(power_lock_mask_t mask)
{
    uint32_t primask = power_manager_irq_save();

    s_lock_mask &= (power_lock_mask_t)(~mask);
    power_manager_irq_restore(primask);
}

power_lock_mask_t power_manager_get_lock_mask(void)
{
    power_lock_mask_t lock_mask = 0U;
    uint32_t primask = power_manager_irq_save();

    lock_mask = s_lock_mask;
    power_manager_irq_restore(primask);

    return lock_mask;
}

void power_manager_set_policy(power_policy_t policy)
{
    uint32_t primask = power_manager_irq_save();

    if ((uint32_t)policy >= (uint32_t)POWER_POLICY_COUNT)
    {
        policy = POWER_POLICY_BALANCED;
    }

    s_policy = policy;
    s_state = power_manager_compute_state_locked();
    power_manager_irq_restore(primask);
}

power_policy_t power_manager_get_policy(void)
{
    power_policy_t policy = POWER_POLICY_BALANCED;
    uint32_t primask = power_manager_irq_save();

    policy = s_policy;
    power_manager_irq_restore(primask);

    return policy;
}

void power_manager_step(void)
{
    uint32_t primask = power_manager_irq_save();

    s_state = power_manager_compute_state_locked();
    power_manager_irq_restore(primask);
}

power_state_t power_manager_get_state(void)
{
    power_state_t state = POWER_STATE_ACTIVE_UI;
    uint32_t primask = power_manager_irq_save();

    state = s_state;
    power_manager_irq_restore(primask);

    return state;
}

uint32_t power_manager_get_tick_ms(void)
{
    uint32_t tick_count = 0U;
    uint32_t primask = power_manager_irq_save();

    tick_count = s_tick_count;
    power_manager_irq_restore(primask);

    return tick_count * POWER_MANAGER_TICK_MS;
}

void power_manager_advance_sleep_time(uint32_t elapsed_ms)
{
    uint32_t extra_ticks = (elapsed_ms + (POWER_MANAGER_TICK_MS / 2UL)) / POWER_MANAGER_TICK_MS;
    uint32_t primask = power_manager_irq_save();

    s_tick_count += extra_ticks;
    power_manager_irq_restore(primask);
}

void power_manager_set_low_power_enabled(uint8_t enabled)
{
    power_policy_t current_policy = power_manager_get_policy();

    if (enabled != 0U)
    {
        if (current_policy == POWER_POLICY_PERFORMANCE)
        {
            power_manager_set_policy(POWER_POLICY_BALANCED);
            return;
        }
    }
    else
    {
        power_manager_set_policy(POWER_POLICY_PERFORMANCE);
        return;
    }
}

uint8_t power_manager_is_low_power_enabled(void)
{
    return (power_manager_get_policy() != POWER_POLICY_PERFORMANCE) ? 1U : 0U;
}

void power_manager_set_screen_off_timeout_ms(uint32_t timeout_ms)
{
    uint32_t primask = power_manager_irq_save();

    if (timeout_ms < POWER_MANAGER_TICK_MS)
    {
        timeout_ms = POWER_MANAGER_DEFAULT_TIMEOUT_MS;
    }

    s_screen_off_timeout_ticks = timeout_ms / POWER_MANAGER_TICK_MS;
    if (s_screen_off_timeout_ticks == 0U)
    {
        s_screen_off_timeout_ticks = POWER_MANAGER_DEFAULT_TIMEOUT_MS / POWER_MANAGER_TICK_MS;
    }
    s_state = power_manager_compute_state_locked();
    power_manager_irq_restore(primask);
}

uint32_t power_manager_get_screen_off_timeout_ms(void)
{
    uint32_t timeout_ticks = 0U;
    uint32_t primask = power_manager_irq_save();

    timeout_ticks = s_screen_off_timeout_ticks;
    power_manager_irq_restore(primask);

    return timeout_ticks * POWER_MANAGER_TICK_MS;
}

void power_manager_reconfigure_timebase(void)
{
    power_manager_tim5_init();
}

void TIM5_IRQHandler(void)
{
    if (TIM_GetITStatus(TIM5, TIM_IT_Update) != RESET)
    {
        TIM_ClearITPendingBit(TIM5, TIM_IT_Update);
        s_tick_count++;
    }
}
