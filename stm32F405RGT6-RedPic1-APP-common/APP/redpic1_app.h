#ifndef REDPIC1_APP_H
#define REDPIC1_APP_H

#include <stdint.h>

#include "settings_service.h"

typedef struct
{
    uint8_t key_value;
    uint32_t tick_ms;
} app_key_event_t;

typedef enum
{
    APP_SERVICE_CMD_NONE = 0,
    APP_SERVICE_CMD_ESP_REFRESH_STATUS,
    APP_SERVICE_CMD_SET_WIFI,
    APP_SERVICE_CMD_SET_DEBUG_SCREEN,
    APP_SERVICE_CMD_SET_REMOTE_KEYS,
    APP_SERVICE_CMD_SET_POWER_POLICY,
    APP_SERVICE_CMD_SET_HOST_STATE,
    APP_SERVICE_CMD_ENTER_FORCED_DEEP_SLEEP,
    APP_SERVICE_CMD_PREPARE_STOP,
    APP_SERVICE_CMD_PREPARE_STANDBY,
    APP_SERVICE_CMD_OTA_QUERY_LATEST
} app_service_cmd_id_t;

typedef struct
{
    app_service_cmd_id_t cmd_id;
    uint8_t arg0;
    uint8_t arg1;
    uint32_t value;
} app_service_cmd_t;

#define APP_SERVICE_TEXT_LEN 24U

typedef struct
{
    app_service_cmd_id_t cmd_id;
    uint8_t ok;
    uint8_t reserved;
    uint16_t reason;
    uint32_t value;
    char text[APP_SERVICE_TEXT_LEN];
} app_service_rsp_t;

void app_rtos_runtime_init(void);
void app_rtos_runtime_start(void);
uint8_t app_service_submit(const app_service_cmd_t *cmd,
                           app_service_rsp_t *rsp,
                           uint32_t timeout_ms);
uint8_t app_service_submit_async(const app_service_cmd_t *cmd);
void app_rtos_lcd_lock(void);
void app_rtos_lcd_unlock(void);
void app_rtos_settings_lock(void);
void app_rtos_settings_unlock(void);
void app_rtos_settings_copy(device_settings_t *out_settings);
uint8_t app_rtos_settings_update(const device_settings_t *settings);

void redpic1_app_main(void);

#endif
