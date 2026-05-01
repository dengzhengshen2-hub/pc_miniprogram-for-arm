#include "led.h" 


/* 初始化开发板上的状态 LED。
 * 这个工程主功能不是靠 LED 工作，所以这里只保留最基础的输出配置。 */
void LED_Init(void)
{    	 
  GPIO_InitTypeDef  GPIO_InitStructure;

  RCC_AHB1PeriphClockCmd(RCC_AHB1Periph_GPIOF, ENABLE);//使能GPIOF时钟

  //GPIOF9,F10初始化设置
  GPIO_InitStructure.GPIO_Pin = GPIO_Pin_9 | GPIO_Pin_10;
  GPIO_InitStructure.GPIO_Mode = GPIO_Mode_OUT;//普通输出模式
  GPIO_InitStructure.GPIO_OType = GPIO_OType_PP;//推挽输出
  GPIO_InitStructure.GPIO_Speed = GPIO_Speed_100MHz;//100MHz
  GPIO_InitStructure.GPIO_PuPd = GPIO_PuPd_UP;//上拉
  GPIO_Init(GPIOF, &GPIO_InitStructure);//初始化
	
	GPIO_SetBits(GPIOF,GPIO_Pin_9 | GPIO_Pin_10);//GPIOF9,F10设置高，灯灭

}

/* 配置 TIM2 的 1ms 周期中断。
 * 这个接口主要是给未来接入 LVGL 之类图形库时提供节拍基准。 */
void lvgl_TIM2_Config(void)
{
    TIM_TimeBaseInitTypeDef TIM_TimeBaseStructure;
    NVIC_InitTypeDef NVIC_InitStructure;
    
    // 使能TIM2时钟
    RCC_APB1PeriphClockCmd(RCC_APB1Periph_TIM2, ENABLE);
    
    // 定时器基础配置
    // 1ms中断：84MHz/8400 = 10kHz，10000个计数 = 1s
    // 但实际上我们只需要1ms，所以分频到1MHz，1000个计数=1ms
    TIM_TimeBaseStructure.TIM_Period = 1000 - 1;      // 自动重装载值，计数到1000
    TIM_TimeBaseStructure.TIM_Prescaler = 84 - 1;      // 分频系数，84MHz/84 = 1MHz
    TIM_TimeBaseStructure.TIM_ClockDivision = TIM_CKD_DIV1;
    TIM_TimeBaseStructure.TIM_CounterMode = TIM_CounterMode_Up;
    TIM_TimeBaseInit(TIM2, &TIM_TimeBaseStructure);
    
    // 使能TIM2更新中断
    TIM_ITConfig(TIM2, TIM_IT_Update, ENABLE);
    
    // 配置TIM2中断
    NVIC_InitStructure.NVIC_IRQChannel = TIM2_IRQn;
    NVIC_InitStructure.NVIC_IRQChannelPreemptionPriority = 0x02;  // 优先级低于按键中断
    NVIC_InitStructure.NVIC_IRQChannelSubPriority = 0x00;
    NVIC_InitStructure.NVIC_IRQChannelCmd = ENABLE;
    NVIC_Init(&NVIC_InitStructure);
    
    // 启动定时器
    TIM_Cmd(TIM2, ENABLE);
}




