#ifndef CLOCK_PROFILE_SERVICE_H
#define CLOCK_PROFILE_SERVICE_H

#include <stdint.h>

typedef enum
{
    CLOCK_PROFILE_HIGH = 0,
    CLOCK_PROFILE_MEDIUM
} clock_profile_t;

typedef enum
{
    CLOCK_PROFILE_POLICY_AUTO = 0,
    CLOCK_PROFILE_POLICY_HIGH_ONLY
} clock_profile_policy_t;

void clock_profile_service_init(void);
void clock_profile_set(clock_profile_t profile);
clock_profile_t clock_profile_get(void);
void clock_profile_restore_after_stop(void);
void clock_profile_restore_after_stop_keep_uart_sleep(void);

#endif
