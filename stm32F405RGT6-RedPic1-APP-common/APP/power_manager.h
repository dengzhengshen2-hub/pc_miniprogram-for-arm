#ifndef POWER_MANAGER_H
#define POWER_MANAGER_H

#include <stdint.h>

typedef enum
{
    POWER_STATE_ACTIVE_THERMAL = 0,
    POWER_STATE_ACTIVE_UI,
    POWER_STATE_SCREEN_OFF_IDLE
} power_state_t;

typedef enum
{
    POWER_POLICY_PERFORMANCE = 0,
    POWER_POLICY_BALANCED,
    POWER_POLICY_ECO,
    POWER_POLICY_COUNT
} power_policy_t;

#define POWER_STATE_ACTIVE_MENU   POWER_STATE_ACTIVE_UI
#define POWER_STATE_IDLE_SLEEP    POWER_STATE_SCREEN_OFF_IDLE

typedef uint32_t power_lock_mask_t;

#define POWER_LOCK_THERMAL      ((power_lock_mask_t)(1UL << 0))
#define POWER_LOCK_OTA          ((power_lock_mask_t)(1UL << 1))
#define POWER_LOCK_DISPLAY_DMA  ((power_lock_mask_t)(1UL << 2))
#define POWER_LOCK_ESP_HOST     ((power_lock_mask_t)(1UL << 3))
#define POWER_LOCK_UI_MODAL     ((power_lock_mask_t)(1UL << 4))
#define POWER_LOCK_USER         ((power_lock_mask_t)(1UL << 5))

void power_manager_init(void);
void power_manager_notify_activity(void);
void power_manager_acquire_lock(power_lock_mask_t mask);
void power_manager_release_lock(power_lock_mask_t mask);
power_lock_mask_t power_manager_get_lock_mask(void);
void power_manager_set_policy(power_policy_t policy);
power_policy_t power_manager_get_policy(void);
void power_manager_set_low_power_enabled(uint8_t enabled);
uint8_t power_manager_is_low_power_enabled(void);
void power_manager_set_screen_off_timeout_ms(uint32_t timeout_ms);
uint32_t power_manager_get_screen_off_timeout_ms(void);
void power_manager_step(void);
power_state_t power_manager_get_state(void);
uint32_t power_manager_get_tick_ms(void);
void power_manager_advance_sleep_time(uint32_t elapsed_ms);
void power_manager_reconfigure_timebase(void);

#endif
