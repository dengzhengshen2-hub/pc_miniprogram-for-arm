#include "gpio_pm_service.h"

#include "lcd_init.h"
#include "stm32f4xx_conf.h"

static void gpio_pm_prepare_uart1(void)
{
    GPIO_InitTypeDef gpio_init_structure;

    USART_Cmd(USART1, DISABLE);
    RCC_APB2PeriphClockCmd(RCC_APB2Periph_USART1, DISABLE);
    RCC_AHB1PeriphClockCmd(RCC_AHB1Periph_GPIOA, ENABLE);

    /* Keep TX idle-high during STOP so ESP32 RX does not see a false low-level wakeup.
     * Deep-sleep path on ESP32 does not depend on UART, but light-sleep wake does. */
    gpio_init_structure.GPIO_Pin = GPIO_Pin_9;
    gpio_init_structure.GPIO_Mode = GPIO_Mode_OUT;
    gpio_init_structure.GPIO_PuPd = GPIO_PuPd_UP;
    gpio_init_structure.GPIO_OType = GPIO_OType_PP;
    gpio_init_structure.GPIO_Speed = GPIO_Speed_2MHz;
    GPIO_Init(GPIOA, &gpio_init_structure);
    GPIO_SetBits(GPIOA, GPIO_Pin_9);

    /* Keep RX in a defined state as well to avoid needless leakage/noise while USART1 is off. */
    gpio_init_structure.GPIO_Pin = GPIO_Pin_10;
    gpio_init_structure.GPIO_Mode = GPIO_Mode_IN;
    gpio_init_structure.GPIO_PuPd = GPIO_PuPd_UP;
    GPIO_Init(GPIOA, &gpio_init_structure);
}

static void gpio_pm_prepare_mlx90640_i2c(void)
{
    GPIO_InitTypeDef gpio_init_structure;

    I2C_Cmd(I2C1, DISABLE);
    RCC_APB1PeriphClockCmd(RCC_APB1Periph_I2C1, DISABLE);
    RCC_AHB1PeriphClockCmd(RCC_AHB1Periph_GPIOB, ENABLE);

    gpio_init_structure.GPIO_Pin = GPIO_Pin_6 | GPIO_Pin_7;
    gpio_init_structure.GPIO_Mode = GPIO_Mode_AN;
    gpio_init_structure.GPIO_PuPd = GPIO_PuPd_NOPULL;
    gpio_init_structure.GPIO_OType = GPIO_OType_OD;
    gpio_init_structure.GPIO_Speed = GPIO_Speed_2MHz;
    GPIO_Init(GPIOB, &gpio_init_structure);
}

void gpio_pm_prepare_stop(void)
{
    lcd_prepare_gpio_for_low_power();
    gpio_pm_prepare_uart1();
    gpio_pm_prepare_mlx90640_i2c();
}

void gpio_pm_restore_after_stop(void)
{
    lcd_restore_gpio_after_low_power();
}

void gpio_pm_prepare_standby(void)
{
    lcd_prepare_gpio_for_low_power();
    gpio_pm_prepare_uart1();
    gpio_pm_prepare_mlx90640_i2c();
}
