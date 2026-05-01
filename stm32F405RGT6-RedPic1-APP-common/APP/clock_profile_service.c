#include "clock_profile_service.h"

#include "delay.h"
#include "ota_service.h"
#include "power_manager.h"
#include "system_stm32f4xx.h"
#include "usart.h"
#include "exti_key.h"

static clock_profile_t s_active_profile = CLOCK_PROFILE_HIGH;

static void clock_profile_reconfigure_dependents(uint8_t reinit_uart)
{
    SystemCoreClockUpdate();
    delay_init((u8)(SystemCoreClock / 1000000UL));
    KEY_EXTI_ReconfigureDebounceTimer();
    ota_service_reconfigure_timebase();
    power_manager_reconfigure_timebase();
    if (reinit_uart != 0U)
    {
        uart_reinit_current_baud();
    }
}

void clock_profile_service_init(void)
{
    s_active_profile = CLOCK_PROFILE_HIGH;
    clock_profile_reconfigure_dependents(1U);
}

void clock_profile_set(clock_profile_t profile)
{
    if (profile == s_active_profile)
    {
        return;
    }

    if (profile == CLOCK_PROFILE_MEDIUM)
    {
        RCC_HCLKConfig(RCC_SYSCLK_Div2);
    }
    else
    {
        RCC_HCLKConfig(RCC_SYSCLK_Div1);
        profile = CLOCK_PROFILE_HIGH;
    }

    s_active_profile = profile;
    clock_profile_reconfigure_dependents(1U);
}

clock_profile_t clock_profile_get(void)
{
    return s_active_profile;
}

void clock_profile_restore_after_stop(void)
{
    uint32_t startup_counter = 0U;

    RCC_HSICmd(ENABLE);
    while (RCC_GetFlagStatus(RCC_FLAG_HSIRDY) == RESET)
    {
    }

    RCC_HSEConfig(RCC_HSE_ON);
    startup_counter = 0U;
    while (RCC_GetFlagStatus(RCC_FLAG_HSERDY) == RESET && startup_counter < 0x4000U)
    {
        startup_counter++;
    }

    if (RCC_GetFlagStatus(RCC_FLAG_HSERDY) != RESET)
    {
        RCC_PLLCmd(ENABLE);
        while (RCC_GetFlagStatus(RCC_FLAG_PLLRDY) == RESET)
        {
        }

        FLASH_SetLatency(FLASH_Latency_5);
        RCC_SYSCLKConfig(RCC_SYSCLKSource_PLLCLK);
        while (RCC_GetSYSCLKSource() != 0x08U)
        {
        }
    }

    if (s_active_profile == CLOCK_PROFILE_MEDIUM)
    {
        RCC_HCLKConfig(RCC_SYSCLK_Div2);
    }
    else
    {
        RCC_HCLKConfig(RCC_SYSCLK_Div1);
    }

    clock_profile_reconfigure_dependents(1U);
}

void clock_profile_restore_after_stop_keep_uart_sleep(void)
{
    uint32_t startup_counter = 0U;

    RCC_HSICmd(ENABLE);
    while (RCC_GetFlagStatus(RCC_FLAG_HSIRDY) == RESET)
    {
    }

    RCC_HSEConfig(RCC_HSE_ON);
    startup_counter = 0U;
    while (RCC_GetFlagStatus(RCC_FLAG_HSERDY) == RESET && startup_counter < 0x4000U)
    {
        startup_counter++;
    }

    if (RCC_GetFlagStatus(RCC_FLAG_HSERDY) != RESET)
    {
        RCC_PLLCmd(ENABLE);
        while (RCC_GetFlagStatus(RCC_FLAG_PLLRDY) == RESET)
        {
        }

        FLASH_SetLatency(FLASH_Latency_5);
        RCC_SYSCLKConfig(RCC_SYSCLKSource_PLLCLK);
        while (RCC_GetSYSCLKSource() != 0x08U)
        {
        }
    }

    if (s_active_profile == CLOCK_PROFILE_MEDIUM)
    {
        RCC_HCLKConfig(RCC_SYSCLK_Div2);
    }
    else
    {
        RCC_HCLKConfig(RCC_SYSCLK_Div1);
    }

    clock_profile_reconfigure_dependents(0U);
}
