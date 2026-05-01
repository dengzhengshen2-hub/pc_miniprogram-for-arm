#ifndef LOW_POWER_RUNTIME_H
#define LOW_POWER_RUNTIME_H

#include <stdint.h>

typedef enum
{
    LP_RUNTIME_STATE_RUN = 0,
    LP_RUNTIME_STATE_STOP_IDLE,
    LP_RUNTIME_STATE_STANDBY_PROTECT
} lp_runtime_state_t;

void low_power_runtime_init(void);
void low_power_runtime_step(void);
lp_runtime_state_t low_power_runtime_get_state(void);
uint8_t low_power_runtime_handle_early_boot(void);
void low_power_runtime_request_manual_standby(void);

#endif
