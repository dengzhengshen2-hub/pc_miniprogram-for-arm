#ifndef RTC_LP_SERVICE_H
#define RTC_LP_SERVICE_H

#include <stdint.h>

typedef enum
{
    RTC_LP_WAKE_NONE = 0,
    RTC_LP_WAKE_TIMER,
    RTC_LP_WAKE_STANDBY_RESET
} rtc_lp_wakeup_reason_t;

void rtc_lp_service_init(void);
void rtc_lp_arm_ms(uint32_t period_ms);
void rtc_lp_disarm(void);
void rtc_lp_handle_irq(void);
uint8_t rtc_lp_consume_wakeup_event(void);
uint32_t rtc_lp_get_last_elapsed_ms(void);
uint32_t rtc_lp_get_last_programmed_ms(void);
rtc_lp_wakeup_reason_t rtc_lp_get_wakeup_reason(void);
uint8_t rtc_lp_woke_from_standby(void);
void rtc_lp_backup_write(uint32_t index, uint32_t value);
uint32_t rtc_lp_backup_read(uint32_t index);

#endif
