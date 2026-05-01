#include "rtc_lp_service.h"

#include "stm32f4xx_conf.h"

#define RTC_LP_BACKUP_REG_COUNT 5U

static const uint32_t s_backup_regs[RTC_LP_BACKUP_REG_COUNT] =
{
    RTC_BKP_DR0,
    RTC_BKP_DR1,
    RTC_BKP_DR2,
    RTC_BKP_DR3,
    RTC_BKP_DR4
};

static volatile uint8_t s_wakeup_pending = 0U;
static uint32_t s_programmed_sleep_ms = 1000UL;
static uint32_t s_last_elapsed_ms = 0U;
static rtc_lp_wakeup_reason_t s_wakeup_reason = RTC_LP_WAKE_NONE;
static uint8_t s_woke_from_standby = 0U;

static void rtc_lp_wait_wutwf(void)
{
    while (RTC_GetFlagStatus(RTC_FLAG_WUTWF) == RESET)
    {
    }
}

void rtc_lp_service_init(void)
{
    EXTI_InitTypeDef exti_init;
    NVIC_InitTypeDef nvic_init;
    RTC_InitTypeDef rtc_init;
    uint32_t rtcsel = 0U;

    RCC_APB1PeriphClockCmd(RCC_APB1Periph_PWR, ENABLE);
    PWR_BackupAccessCmd(ENABLE);

    s_woke_from_standby = (PWR_GetFlagStatus(PWR_FLAG_SB) != RESET) ? 1U : 0U;
    s_wakeup_reason = (s_woke_from_standby != 0U) ? RTC_LP_WAKE_STANDBY_RESET : RTC_LP_WAKE_NONE;

    RCC_LSICmd(ENABLE);
    while (RCC_GetFlagStatus(RCC_FLAG_LSIRDY) == RESET)
    {
    }

    rtcsel = RCC->BDCR & RCC_BDCR_RTCSEL;
    if (((RCC->BDCR & RCC_BDCR_RTCEN) == 0U) || (rtcsel != RCC_RTCCLKSource_LSI))
    {
        RCC_BackupResetCmd(ENABLE);
        RCC_BackupResetCmd(DISABLE);
        RCC_RTCCLKConfig(RCC_RTCCLKSource_LSI);
        RCC_RTCCLKCmd(ENABLE);
    }

    RTC_WaitForSynchro();

    rtc_init.RTC_AsynchPrediv = 127U;
    rtc_init.RTC_SynchPrediv = 249U;
    rtc_init.RTC_HourFormat = RTC_HourFormat_24;
    RTC_Init(&rtc_init);

    rtc_lp_disarm();

    EXTI_ClearITPendingBit(EXTI_Line22);
    exti_init.EXTI_Line = EXTI_Line22;
    exti_init.EXTI_Mode = EXTI_Mode_Interrupt;
    exti_init.EXTI_Trigger = EXTI_Trigger_Rising;
    exti_init.EXTI_LineCmd = ENABLE;
    EXTI_Init(&exti_init);
    EXTI->EMR |= EXTI_Line22;
    EXTI->IMR |= EXTI_Line22;

    nvic_init.NVIC_IRQChannel = RTC_WKUP_IRQn;
    nvic_init.NVIC_IRQChannelPreemptionPriority = 0;
    nvic_init.NVIC_IRQChannelSubPriority = 1;
    nvic_init.NVIC_IRQChannelCmd = ENABLE;
    NVIC_Init(&nvic_init);

    PWR_WakeUpPinCmd(DISABLE);
    PWR_ClearFlag(PWR_FLAG_WU);
    PWR_ClearFlag(PWR_FLAG_SB);
}

void rtc_lp_arm_ms(uint32_t period_ms)
{
    uint32_t wake_clock = RTC_WakeUpClock_RTCCLK_Div16;
    uint32_t ticks = 0U;

    if (period_ms < 500U)
    {
        period_ms = 500U;
    }

    rtc_lp_disarm();

    if (period_ms <= 30000UL)
    {
        ticks = (period_ms * 2000UL + 999UL) / 1000UL;
        wake_clock = RTC_WakeUpClock_RTCCLK_Div16;
    }
    else
    {
        ticks = (period_ms + 999UL) / 1000UL;
        wake_clock = RTC_WakeUpClock_CK_SPRE_16bits;
    }

    if (ticks == 0U)
    {
        ticks = 1U;
    }
    if (ticks > 0x10000UL)
    {
        ticks = 0x10000UL;
    }

    s_programmed_sleep_ms = period_ms;
    s_last_elapsed_ms = period_ms;
    s_wakeup_pending = 0U;

    RTC_WakeUpClockConfig(wake_clock);
    RTC_SetWakeUpCounter(ticks - 1UL);
    RTC_ITConfig(RTC_IT_WUT, ENABLE);
    RTC_WakeUpCmd(ENABLE);
}

void rtc_lp_disarm(void)
{
    RTC_ITConfig(RTC_IT_WUT, DISABLE);
    RTC_WakeUpCmd(DISABLE);
    rtc_lp_wait_wutwf();
    RTC_ClearITPendingBit(RTC_IT_WUT);
    RTC_ClearFlag(RTC_FLAG_WUTF);
    EXTI_ClearITPendingBit(EXTI_Line22);
}

void rtc_lp_handle_irq(void)
{
    if (RTC_GetITStatus(RTC_IT_WUT) != RESET)
    {
        RTC_ClearITPendingBit(RTC_IT_WUT);
        RTC_ClearFlag(RTC_FLAG_WUTF);
        EXTI_ClearITPendingBit(EXTI_Line22);
        s_wakeup_pending = 1U;
        s_wakeup_reason = RTC_LP_WAKE_TIMER;
    }
}

uint8_t rtc_lp_consume_wakeup_event(void)
{
    uint8_t pending = s_wakeup_pending;

    s_wakeup_pending = 0U;
    return pending;
}

uint32_t rtc_lp_get_last_elapsed_ms(void)
{
    return s_last_elapsed_ms;
}

uint32_t rtc_lp_get_last_programmed_ms(void)
{
    return s_programmed_sleep_ms;
}

rtc_lp_wakeup_reason_t rtc_lp_get_wakeup_reason(void)
{
    return s_wakeup_reason;
}

uint8_t rtc_lp_woke_from_standby(void)
{
    return s_woke_from_standby;
}

void rtc_lp_backup_write(uint32_t index, uint32_t value)
{
    if (index >= RTC_LP_BACKUP_REG_COUNT)
    {
        return;
    }

    RCC_APB1PeriphClockCmd(RCC_APB1Periph_PWR, ENABLE);
    PWR_BackupAccessCmd(ENABLE);
    RTC_WriteBackupRegister(s_backup_regs[index], value);
}

uint32_t rtc_lp_backup_read(uint32_t index)
{
    if (index >= RTC_LP_BACKUP_REG_COUNT)
    {
        return 0U;
    }

    return RTC_ReadBackupRegister(s_backup_regs[index]);
}
