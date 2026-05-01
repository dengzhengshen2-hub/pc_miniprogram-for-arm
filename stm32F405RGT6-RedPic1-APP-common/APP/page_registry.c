#include "page_registry.h"

#include <stdio.h>
#include <string.h>

#include "app_display_runtime.h"
#include "app_perf_baseline.h"
#include "battery_monitor.h"
#include "delay.h"
#include "esp_host_service.h"
#include "key.h"
#include "lcd.h"
#include "lcd_init.h"
#include "lcd_utf8.h"
#include "ota_ctrl_protocol.h"
#include "ota_service.h"
#include "power_manager.h"
#include "redpic1_thermal.h"
#include "redpic1_app.h"
#include "ui_renderer.h"

/*
 * 页面注册表文件同时承载页面回调注册、页面私有状态、异步服务回流和局部刷新逻辑。
 * ui_manager 只负责做轻量调度，真正的页面状态与页面内部协作都集中收口在这里。
 */

/* OTA 页面内部显示模式。 */
typedef enum
{
    OTA_CENTER_MODE_MENU = 0,
    OTA_CENTER_MODE_CONFIRM_WIFI,
    OTA_CENTER_MODE_CONFIRM_UPGRADE,
    OTA_CENTER_MODE_CONFIRM_ROLLBACK,
    OTA_CENTER_MODE_INFO
} ota_center_mode_t;

/* OTA 页面在查询完成后的后续动作。 */
typedef enum
{
    OTA_PENDING_NONE = 0,
    OTA_PENDING_CHECK,
    OTA_PENDING_UPGRADE
} ota_pending_action_t;

/* 页面侧异步命令的本地 pending 状态与超时信息。 */
typedef struct
{
    /* WiFi 开关命令是否仍在等待响应。 */
    uint8_t wifi_set_pending;
    /* WiFi 开关命令的目标状态。 */
    uint8_t wifi_target_enabled;
    /* WiFi 开关命令的超时截止时间。 */
    uint32_t wifi_set_deadline_ms;
    /* 调试屏幕开关命令是否仍在等待响应。 */
    uint8_t debug_screen_pending;
    /* 调试屏幕开关命令的目标状态。 */
    uint8_t debug_screen_target_enabled;
    /* 调试屏幕开关命令的超时截止时间。 */
    uint32_t debug_screen_deadline_ms;
    /* 遥控按键开关命令是否仍在等待响应。 */
    uint8_t remote_keys_pending;
    /* 遥控按键开关命令的目标状态。 */
    uint8_t remote_keys_target_enabled;
    /* 遥控按键开关命令的超时截止时间。 */
    uint32_t remote_keys_deadline_ms;
    /* 主机状态同步命令是否仍在等待响应。 */
    uint8_t host_state_pending;
    /* 主机状态同步命令的目标状态。 */
    power_state_t host_state_target;
    /* 主机状态同步命令的超时截止时间。 */
    uint32_t host_state_deadline_ms;
    /* 电源策略同步命令是否仍在等待响应。 */
    uint8_t power_policy_pending;
    /* 电源策略同步命令的目标策略。 */
    power_policy_t power_policy_target;
    /* 电源策略同步命令的超时截止时间。 */
    uint32_t power_policy_deadline_ms;
    /* 强制深睡命令是否仍在等待响应。 */
    uint8_t forced_deep_sleep_pending;
    /* 强制深睡命令的超时截止时间。 */
    uint32_t forced_deep_sleep_deadline_ms;
    /* OTA 查询命令是否仍在等待响应。 */
    uint8_t ota_query_pending;
    /* OTA 查询命令的超时截止时间。 */
    uint32_t ota_query_deadline_ms;
    /* OTA 查询成功后是否需要弹出成功信息页。 */
    uint8_t ota_query_show_success_info;
    /* OTA 查询结束后需要执行的后续动作。 */
    ota_pending_action_t ota_post_query_action;
    /* 是否处于 OTA 流程触发的 WiFi 自动开启阶段。 */
    uint8_t ota_wifi_enable_pending;
} page_async_state_t;

/* 按索引重绘单个菜单项的回调类型。 */
typedef void (*page_draw_item_fn_t)(uint8_t index);

static void home_on_enter(ui_page_id_t previous_page);
static void home_on_leave(ui_page_id_t next_page);
static void home_on_key(uint8_t key_value);
static void home_on_tick(void);
static void home_render(uint8_t full_refresh);

static void thermal_on_enter(ui_page_id_t previous_page);
static void thermal_on_leave(ui_page_id_t next_page);
static void thermal_on_key(uint8_t key_value);
static void thermal_on_tick(void);
static void thermal_render(uint8_t full_refresh);

static void ota_center_on_enter(ui_page_id_t previous_page);
static void ota_center_on_leave(ui_page_id_t next_page);
static void ota_center_on_key(uint8_t key_value);
static void ota_center_on_tick(void);
static void ota_center_render(uint8_t full_refresh);

static void connectivity_on_enter(ui_page_id_t previous_page);
static void connectivity_on_leave(ui_page_id_t next_page);
static void connectivity_on_key(uint8_t key_value);
static void connectivity_on_tick(void);
static void connectivity_render(uint8_t full_refresh);

static void power_page_on_enter(ui_page_id_t previous_page);
static void power_page_on_leave(ui_page_id_t next_page);
static void power_page_on_key(uint8_t key_value);
static void power_page_on_tick(void);
static void power_page_render(uint8_t full_refresh);

static void system_on_enter(ui_page_id_t previous_page);
static void system_on_leave(ui_page_id_t next_page);
static void system_on_key(uint8_t key_value);
static void system_on_tick(void);
static void system_render(uint8_t full_refresh);

static void engineering_on_enter(ui_page_id_t previous_page);
static void engineering_on_leave(ui_page_id_t next_page);
static void engineering_on_key(uint8_t key_value);
static void engineering_on_tick(void);
static void engineering_render(uint8_t full_refresh);

static void perf_baseline_on_enter(ui_page_id_t previous_page);
static void perf_baseline_on_leave(ui_page_id_t next_page);
static void perf_baseline_on_key(uint8_t key_value);
static void perf_baseline_on_tick(void);
static void perf_baseline_render(uint8_t full_refresh);

static uint8_t page_set_wifi_enabled(uint8_t enabled);
static uint8_t page_set_debug_screen_enabled(uint8_t enabled);
static uint8_t page_set_remote_keys_enabled(uint8_t enabled);
static uint8_t page_refresh_host_status_async(void);
static uint8_t page_set_host_state_async(power_state_t state);
static uint8_t page_set_power_policy_async(power_policy_t policy);
static uint8_t page_enter_forced_deep_sleep_async(uint32_t timeout_ms);
static void ota_center_enter_menu_mode(void);
static void ota_center_enter_confirm_mode(ota_center_mode_t mode);
static void ota_center_reset_menu_state(void);
static void ota_center_clear_query_follow_up(void);
static void ota_center_clear_info_latched_state(void);
static void ota_center_return_to_menu(void);
static void ota_center_exit_info_mode(uint8_t navigate_home);
static void ota_center_show_local_version_info(void);
static uint8_t ota_center_request_latest_async(uint8_t show_success_info, ota_pending_action_t post_action);
static void ota_center_show_task_busy_info(void);
static void ota_center_show_restart_info(const char *detail);
static void ota_center_handle_query_success(const app_service_rsp_t *rsp,
                                            ota_pending_action_t post_action,
                                            uint8_t show_success_info);
static void ota_center_handle_query_failure(const app_service_rsp_t *rsp);
static const char *ota_center_child_title(void);
static void page_handle_service_response(const app_service_rsp_t *rsp);
static void page_async_handle_timeouts(void);
static uint32_t page_async_make_deadline(uint32_t timeout_ms);
static uint8_t page_async_deadline_expired(uint32_t now_ms, uint32_t deadline_ms);
static void ota_center_draw_info_rows(void);
static void home_draw_item(uint8_t index);
static void wifi_draw_status_row(uint8_t force_refresh);
static void wifi_draw_item(uint8_t force_refresh);
static void power_draw_info_rows(void);
static void power_draw_battery_status(void);
static void power_draw_item(uint8_t index);
static void engineering_draw_item(uint8_t index);
static uint8_t perf_baseline_debug_visible(void);
static void page_refresh_host_status_views(ui_page_id_t active_page);
static void page_refresh_timeout_views(ui_page_id_t active_page);
static uint8_t page_cycle_prev_index(uint8_t current_index, uint8_t item_count);
static uint8_t page_cycle_next_index(uint8_t current_index, uint8_t item_count);
static void page_move_selection(uint8_t *selected_index,
                                uint8_t item_count,
                                uint8_t move_previous,
                                page_draw_item_fn_t draw_item);
static uint32_t page_next_u32_option(const uint32_t *option_list,
                                     uint32_t option_count,
                                     uint32_t current_value);

/* 页面回调表，顺序与 ui_page_id_t 枚举保持一致。 */
static const ui_page_ops_t s_page_ops[UI_PAGE_COUNT] =
{
    { home_on_enter, home_on_leave, home_on_key, home_on_tick, home_render },
    { thermal_on_enter, thermal_on_leave, thermal_on_key, thermal_on_tick, thermal_render },
    { ota_center_on_enter, ota_center_on_leave, ota_center_on_key, ota_center_on_tick, ota_center_render },
    { connectivity_on_enter, connectivity_on_leave, connectivity_on_key, connectivity_on_tick, connectivity_render },
    { power_page_on_enter, power_page_on_leave, power_page_on_key, power_page_on_tick, power_page_render },
    { system_on_enter, system_on_leave, system_on_key, system_on_tick, system_render },
    { engineering_on_enter, engineering_on_leave, engineering_on_key, engineering_on_tick, engineering_render },
    { perf_baseline_on_enter, perf_baseline_on_leave, perf_baseline_on_key, perf_baseline_on_tick, perf_baseline_render }
};

/* 各页面的选择状态与轻量刷新缓存都只在本文件内部可见。 */
static uint8_t s_home_selected = 0U;
static uint8_t s_wifi_selected = 0U;
static uint8_t s_power_selected = 0U;
static uint8_t s_system_selected = 0U;
static uint8_t s_engineering_selected = 0U;
static uint8_t s_perf_baseline_subpage = 0U;
static uint32_t s_perf_baseline_next_refresh_ms = 0U;
static uint32_t s_wifi_next_refresh_ms = 0U;
static char s_wifi_status_cache[24];
static uint16_t s_wifi_status_color_cache = 0U;
static uint8_t s_wifi_status_cache_valid = 0U;
static uint8_t s_wifi_item_cache_valid = 0U;
static uint8_t s_wifi_item_cache_enabled = 0U;
static uint8_t s_wifi_item_cache_selected = 0U;
static uint8_t s_wifi_item_cache_forced = 0U;
static uint8_t s_wifi_item_cache_pending = 0U;

static ota_center_mode_t s_ota_mode = OTA_CENTER_MODE_MENU;
static ota_pending_action_t s_ota_pending_action = OTA_PENDING_NONE;
static uint8_t s_ota_selected = 0U;
static char s_ota_latest_version[BOOT_INFO_VERSION_LEN];
static char s_ota_notice_line1[64];
static char s_ota_notice_line2[64];
static char s_ota_info_current_version[BOOT_INFO_VERSION_LEN];
static char s_ota_info_latest_version[BOOT_INFO_VERSION_LEN];
static char s_ota_info_partition[12];
static uint8_t s_ota_show_version_rows = 0U;
static uint8_t s_ota_show_partition_rows = 0U;
static page_async_state_t s_async_state;

/* 首页菜单文本。 */
static const char * const s_home_items[] =
{
    "Thermal",
    "Update",
    "WiFi",
    "Power",
    "System"
};

static const uint16_t s_home_item_colors[] =
{
    RED,
    GBLUE,
    GREEN,
    BROWN,
    DARKBLUE
};

/* OTA 页面菜单文本。 */
static const char * const s_ota_items[] =
{
    "Check Now",
    "Start Update",
    "Restore Previous Version",
    "Version Info"
};

/* 系统页面菜单文本。 */
static const char * const s_system_items[] =
{
    "Debug Mode",
    "Debug Page"
};

/* 工程页面菜单文本。 */
static const char * const s_engineering_items[] =
{
    "Perf Baseline",
    "Debug Screen",
    "Remote Keys"
};

/* 电源页面菜单文本。 */
static const char * const s_power_items[] =
{
    "Power Mode",
    "Screen Off",
    "Standby",
    "ESP Save"
};

static const uint32_t s_power_screen_off_options_ms[] =
{
    15000UL,
    30000UL,
    45000UL,
    60000UL,
    120000UL,
    180000UL,
    300000UL,
    600000UL
};

#define HOME_ITEM_COUNT            5U
#define OTA_ITEM_COUNT             4U
#define POWER_ITEM_COUNT           4U
#define SYSTEM_ITEM_MAX_COUNT      2U
#define ENGINEERING_ITEM_COUNT     3U

#define HOME_LIST_START_Y          70U
#define HOME_BANNER_TOP            38U
#define HOME_BANNER_HEIGHT         24U
#define HOME_CARD_LEFT             12U
#define HOME_CARD_RIGHT            (LCD_W - 12U)
#define HOME_CARD_HEIGHT           28U
#define HOME_CARD_GAP              6U
#define HOME_CARD_ICON_LEFT        22U
#define HOME_CARD_TEXT_LEFT        52U
#define HOME_CARD_CHEVRON_LEFT     (LCD_W - 30U)
#define WIFI_STATUS_Y              UI_CONTENT_TOP
#define WIFI_LIST_START_Y          86U
#define OTA_LIST_START_Y           88U
#define POWER_LIST_START_Y         90U
#define SYSTEM_LIST_START_Y        90U
#define DEBUG_LIST_START_Y         60U

#define WIFI_STATUS_REFRESH_MS     1500UL
#define POWER_PAGE_HOST_PREP_TIMEOUT_MS 400UL
#define PAGE_ASYNC_TIMEOUT_SHORT_MS 2000UL
#define PAGE_ASYNC_TIMEOUT_WIFI_MS  3000UL
#define PAGE_ASYNC_TIMEOUT_OTA_MS   7000UL
#define PERF_BASELINE_REFRESH_MS    250UL
#define PERF_SUBPAGE_COUNT          4U
#define PERF_LABEL_X                12U
#define PERF_VALUE_X                180U
#define PERF_TIMING_VALUE_X         106U
#define PERF_VALUE_PAD_CHARS        18U
#define PERF_TIMING_VALUE_PAD_CHARS 22U
#define PERF_VALUE_FONT_SIZE        16U
#define PERF_TIMING_VALUE_FONT_SIZE 12U
#define PERF_FOOTER_PAD_CHARS       40U

/* 格式化电池状态文本，供页面信息行显示。 */
static void page_format_battery(char *buffer, uint16_t buffer_len)
{
    snprintf(buffer,
             buffer_len,
             "%u%%",
             battery_monitor_get_percent());
}

/* 根据当前设置和主机状态生成人机可读的 WiFi 状态文本。 */
static void page_format_wifi_status(char *buffer, uint16_t buffer_len)
{
    esp_host_status_t status;
    device_settings_t settings;

    esp_host_get_status_copy(&status);
    app_rtos_settings_copy(&settings);

    if (esp_host_is_forced_deep_sleep() != 0U)
    {
        snprintf(buffer, buffer_len, "%s", "PRESS KEY6");
        return;
    }

    if (s_async_state.wifi_set_pending != 0U)
    {
        snprintf(buffer, buffer_len, "%s", "CONNECTING");
        return;
    }

    if (settings.wifi_enabled != 0U)
    {
        if (status.online == 0U)
        {
            snprintf(buffer, buffer_len, "%s", "NOT CONNECTED");
            return;
        }

        if (status.has_credentials == 0U && status.last_seen_ms != 0U)
        {
            snprintf(buffer, buffer_len, "%s", "NOT CONNECTED");
            return;
        }

        snprintf(buffer,
                 buffer_len,
                 "%s",
                 (status.wifi_connected != 0U) ? "CONNECTED" : "CONNECTING");
        return;
    }

    snprintf(buffer, buffer_len, "%s", "NOT CONNECTED");
}

/* 返回当前试运行状态的简短文案。 */
/* 根据起始 Y 坐标和索引计算列表项的绘制位置。 */
static uint16_t page_list_item_y(uint16_t start_y, uint8_t index)
{
    return (uint16_t)(start_y + ((uint16_t)index * UI_ROW_HEIGHT));
}

/* 计算循环菜单中上一个焦点索引。 */
static uint8_t page_cycle_prev_index(uint8_t current_index, uint8_t item_count)
{
    if (item_count == 0U)
    {
        return current_index;
    }

    return (uint8_t)((current_index + item_count - 1U) % item_count);
}

/* 计算循环菜单中下一个焦点索引。 */
static uint8_t page_cycle_next_index(uint8_t current_index, uint8_t item_count)
{
    if (item_count == 0U)
    {
        return current_index;
    }

    return (uint8_t)((current_index + 1U) % item_count);
}

/*
 * 更新菜单焦点并重绘前后两个索引位置。
 * 即使前后索引相同，也保持原有的双次重绘路径，避免改变局部刷新行为。
 */
static void page_move_selection(uint8_t *selected_index,
                                uint8_t item_count,
                                uint8_t move_previous,
                                page_draw_item_fn_t draw_item)
{
    uint8_t previous_index = 0U;

    if (selected_index == 0 || item_count == 0U || draw_item == 0)
    {
        return;
    }

    previous_index = *selected_index;
    *selected_index = (move_previous != 0U) ?
                      page_cycle_prev_index(*selected_index, item_count) :
                      page_cycle_next_index(*selected_index, item_count);

    draw_item(previous_index);
    draw_item(*selected_index);
}

/* 以当前系统时基生成异步命令的截止时间。 */
static uint32_t page_async_make_deadline(uint32_t timeout_ms)
{
    return power_manager_get_tick_ms() + timeout_ms;
}

/* 判断某个异步截止时间是否已经超时。 */
static uint8_t page_async_deadline_expired(uint32_t now_ms, uint32_t deadline_ms)
{
    if (deadline_ms == 0U)
    {
        return 0U;
    }

    return (((int32_t)(now_ms - deadline_ms)) >= 0) ? 1U : 0U;
}

/* 将超时时间格式化为页面显示字符串。 */
static void page_format_timeout_ms(char *buffer, uint16_t buffer_len, uint32_t timeout_ms)
{
    if ((timeout_ms % 60000UL) == 0UL)
    {
        snprintf(buffer, buffer_len, "%lu min", (unsigned long)(timeout_ms / 60000UL));
    }
    else if ((timeout_ms % 1000UL) == 0UL)
    {
        snprintf(buffer, buffer_len, "%lu s", (unsigned long)(timeout_ms / 1000UL));
    }
    else
    {
        snprintf(buffer, buffer_len, "%lu ms", (unsigned long)timeout_ms);
    }
}

/* 在无符号整型选项表中轮转到下一个配置值。 */
static uint32_t page_next_u32_option(const uint32_t *option_list,
                                     uint32_t option_count,
                                     uint32_t current_value)
{
    uint32_t index = 0U;

    if (option_list == 0 || option_count == 0U)
    {
        return current_value;
    }

    for (index = 0U; index < option_count; ++index)
    {
        if (option_list[index] == current_value)
        {
            return option_list[(index + 1U) % option_count];
        }
    }

    return option_list[0];
}

/* 轮转得到下一个 Stop 唤醒超时选项。 */
/* 轮转得到下一个熄屏超时选项。 */
static uint32_t page_next_screen_off_timeout_ms(uint32_t current_ms)
{
    return page_next_u32_option(s_power_screen_off_options_ms,
                                (uint32_t)(sizeof(s_power_screen_off_options_ms) /
                                           sizeof(s_power_screen_off_options_ms[0])),
                                current_ms);
}

/* 解析最近一次复位原因，用于系统页面显示。 */
/* 根据调试模式状态计算系统页面当前可见项数量。 */
static uint8_t system_item_count(void)
{
    device_settings_t settings;

    app_rtos_settings_copy(&settings);
    return (settings.debug_mode_enabled != 0U) ? SYSTEM_ITEM_MAX_COUNT : 1U;
}

/* 设置写入成功后，需要同步更新当前运行时电源策略。 */
static void page_apply_settings(const device_settings_t *settings)
{
    if (settings == 0)
    {
        return;
    }

    power_manager_set_policy(settings->power_policy);
    power_manager_set_screen_off_timeout_ms(settings->screen_off_timeout_ms);
}

/* 写入设置并在成功后同步应用运行时副作用。 */
static uint8_t page_store_settings(device_settings_t *updated)
{
    if (updated == 0)
    {
        return 0U;
    }

    if (app_rtos_settings_update(updated) == 0U)
    {
        return 0U;
    }

    page_apply_settings(updated);
    return 1U;
}

/* 异步提交 WiFi 开关命令，并维护页面侧 pending 状态。 */
static uint8_t page_set_wifi_enabled(uint8_t enabled)
{
    app_service_cmd_t cmd;
    uint8_t normalized_enabled = (enabled != 0U) ? 1U : 0U;

    memset(&cmd, 0, sizeof(cmd));
    cmd.cmd_id = APP_SERVICE_CMD_SET_WIFI;
    cmd.arg0 = normalized_enabled;
    cmd.value = (normalized_enabled != 0U) ? 800UL : 250UL;

    if (s_async_state.wifi_set_pending != 0U)
    {
        s_async_state.wifi_target_enabled = normalized_enabled;
        s_async_state.wifi_set_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_WIFI_MS);
        (void)app_service_submit_async(&cmd);
        return 1U;
    }

    if (app_service_submit_async(&cmd) == 0U)
    {
        return 0U;
    }

    s_async_state.wifi_set_pending = 1U;
    s_async_state.wifi_target_enabled = normalized_enabled;
    s_async_state.wifi_set_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_WIFI_MS);
    ui_manager_request_render();
    return 1U;
}

/* 异步提交调试屏幕开关命令。 */
static uint8_t page_set_debug_screen_enabled(uint8_t enabled)
{
    app_service_cmd_t cmd;
    uint8_t normalized_enabled = (enabled != 0U) ? 1U : 0U;

    memset(&cmd, 0, sizeof(cmd));
    cmd.cmd_id = APP_SERVICE_CMD_SET_DEBUG_SCREEN;
    cmd.arg0 = normalized_enabled;

    if (s_async_state.debug_screen_pending != 0U)
    {
        s_async_state.debug_screen_target_enabled = normalized_enabled;
        s_async_state.debug_screen_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_SHORT_MS);
        (void)app_service_submit_async(&cmd);
        return 1U;
    }

    if (app_service_submit_async(&cmd) == 0U)
    {
        return 0U;
    }

    s_async_state.debug_screen_pending = 1U;
    s_async_state.debug_screen_target_enabled = normalized_enabled;
    s_async_state.debug_screen_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_SHORT_MS);
    ui_manager_request_render();
    return 1U;
}

/* 异步提交遥控按键开关命令。 */
static uint8_t page_set_remote_keys_enabled(uint8_t enabled)
{
    app_service_cmd_t cmd;
    uint8_t normalized_enabled = (enabled != 0U) ? 1U : 0U;

    memset(&cmd, 0, sizeof(cmd));
    cmd.cmd_id = APP_SERVICE_CMD_SET_REMOTE_KEYS;
    cmd.arg0 = normalized_enabled;

    if (s_async_state.remote_keys_pending != 0U)
    {
        s_async_state.remote_keys_target_enabled = normalized_enabled;
        s_async_state.remote_keys_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_SHORT_MS);
        (void)app_service_submit_async(&cmd);
        return 1U;
    }

    if (app_service_submit_async(&cmd) == 0U)
    {
        return 0U;
    }

    s_async_state.remote_keys_pending = 1U;
    s_async_state.remote_keys_target_enabled = normalized_enabled;
    s_async_state.remote_keys_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_SHORT_MS);
    ui_manager_request_render();
    return 1U;
}

/* 异步请求 ESP 主机刷新最新状态。 */
static uint8_t page_refresh_host_status_async(void)
{
    app_service_cmd_t cmd;

    memset(&cmd, 0, sizeof(cmd));
    cmd.cmd_id = APP_SERVICE_CMD_ESP_REFRESH_STATUS;
    return app_service_submit_async(&cmd);
}

/* 异步将当前电源状态同步给主机侧。 */
static uint8_t page_set_host_state_async(power_state_t state)
{
    app_service_cmd_t cmd;

    memset(&cmd, 0, sizeof(cmd));
    cmd.cmd_id = APP_SERVICE_CMD_SET_HOST_STATE;
    cmd.arg0 = (uint8_t)state;

    if (s_async_state.host_state_pending != 0U)
    {
        s_async_state.host_state_target = state;
        s_async_state.host_state_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_SHORT_MS);
        (void)app_service_submit_async(&cmd);
        return 1U;
    }

    if (app_service_submit_async(&cmd) == 0U)
    {
        return 0U;
    }

    s_async_state.host_state_pending = 1U;
    s_async_state.host_state_target = state;
    s_async_state.host_state_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_SHORT_MS);
    return 1U;
}

/* 异步将电源策略同步给主机侧。 */
static uint8_t page_set_power_policy_async(power_policy_t policy)
{
    app_service_cmd_t cmd;

    memset(&cmd, 0, sizeof(cmd));
    cmd.cmd_id = APP_SERVICE_CMD_SET_POWER_POLICY;
    cmd.arg0 = (uint8_t)policy;

    if (s_async_state.power_policy_pending != 0U)
    {
        s_async_state.power_policy_target = policy;
        s_async_state.power_policy_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_SHORT_MS);
        (void)app_service_submit_async(&cmd);
        return 1U;
    }

    if (app_service_submit_async(&cmd) == 0U)
    {
        return 0U;
    }

    s_async_state.power_policy_pending = 1U;
    s_async_state.power_policy_target = policy;
    s_async_state.power_policy_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_SHORT_MS);
    return 1U;
}

/* 异步请求主机进入强制深睡准备流程。 */
static uint8_t page_enter_forced_deep_sleep_async(uint32_t timeout_ms)
{
    app_service_cmd_t cmd;

    memset(&cmd, 0, sizeof(cmd));
    cmd.cmd_id = APP_SERVICE_CMD_ENTER_FORCED_DEEP_SLEEP;
    cmd.value = timeout_ms;

    if (s_async_state.forced_deep_sleep_pending != 0U)
    {
        s_async_state.forced_deep_sleep_deadline_ms =
            page_async_make_deadline(timeout_ms + PAGE_ASYNC_TIMEOUT_SHORT_MS);
        (void)app_service_submit_async(&cmd);
        return 1U;
    }
    if (app_service_submit_async(&cmd) == 0U)
    {
        return 0U;
    }

    s_async_state.forced_deep_sleep_pending = 1U;
    s_async_state.forced_deep_sleep_deadline_ms =
        page_async_make_deadline(timeout_ms + PAGE_ASYNC_TIMEOUT_SHORT_MS);
    return 1U;
}

/* 设置 OTA 信息页的两行提示文本。 */
static void ota_center_set_notice(const char *line1, const char *line2)
{
    memset(s_ota_notice_line1, 0, sizeof(s_ota_notice_line1));
    memset(s_ota_notice_line2, 0, sizeof(s_ota_notice_line2));
    memset(s_ota_info_current_version, 0, sizeof(s_ota_info_current_version));
    memset(s_ota_info_latest_version, 0, sizeof(s_ota_info_latest_version));
    memset(s_ota_info_partition, 0, sizeof(s_ota_info_partition));
    s_ota_show_version_rows = 0U;
    s_ota_show_partition_rows = 0U;

    if (line1 != 0)
    {
        snprintf(s_ota_notice_line1, sizeof(s_ota_notice_line1), "%s", line1);
    }
    if (line2 != 0)
    {
        snprintf(s_ota_notice_line2, sizeof(s_ota_notice_line2), "%s", line2);
    }
}

static void ota_center_set_version_rows(const char *current_version, const char *latest_version)
{
    memset(s_ota_info_current_version, 0, sizeof(s_ota_info_current_version));
    memset(s_ota_info_latest_version, 0, sizeof(s_ota_info_latest_version));
    memset(s_ota_info_partition, 0, sizeof(s_ota_info_partition));

    snprintf(s_ota_info_current_version,
             sizeof(s_ota_info_current_version),
             "%s",
             (current_version != 0 && current_version[0] != '\0') ? current_version : "--");
    snprintf(s_ota_info_latest_version,
             sizeof(s_ota_info_latest_version),
             "%s",
             (latest_version != 0 && latest_version[0] != '\0') ? latest_version : "--");
    s_ota_show_version_rows = 1U;
    s_ota_show_partition_rows = 0U;
}

static void ota_center_set_local_info_rows(const char *current_version, const char *partition_name)
{
    memset(s_ota_info_current_version, 0, sizeof(s_ota_info_current_version));
    memset(s_ota_info_latest_version, 0, sizeof(s_ota_info_latest_version));
    memset(s_ota_info_partition, 0, sizeof(s_ota_info_partition));

    snprintf(s_ota_info_current_version,
             sizeof(s_ota_info_current_version),
             "%s",
             (current_version != 0 && current_version[0] != '\0') ? current_version : "--");
    snprintf(s_ota_info_partition,
             sizeof(s_ota_info_partition),
             "%s",
             (partition_name != 0 && partition_name[0] != '\0') ? partition_name : "--");
    s_ota_show_version_rows = 0U;
    s_ota_show_partition_rows = 1U;
}

/* 将 OTA 页面切回普通菜单模式。 */
static void ota_center_enter_menu_mode(void)
{
    s_ota_mode = OTA_CENTER_MODE_MENU;
}

/* 切换 OTA 页面到确认类子状态，并保持原有整页刷新路径不变。 */
static void ota_center_enter_confirm_mode(ota_center_mode_t mode)
{
    s_ota_mode = mode;
    ui_manager_force_full_refresh();
}

/* 复位 OTA 页面菜单态及其挂起动作。 */
static void ota_center_reset_menu_state(void)
{
    ota_center_enter_menu_mode();
    s_ota_pending_action = OTA_PENDING_NONE;
    ota_center_set_notice(0, 0);
}

/* 清理 OTA 查询完成后的后续动作标记。 */
static void ota_center_clear_query_follow_up(void)
{
    s_async_state.ota_post_query_action = OTA_PENDING_NONE;
    s_async_state.ota_query_show_success_info = 0U;
}

/* 清理 OTA 信息页依赖的临时挂起状态。 */
static void ota_center_clear_info_latched_state(void)
{
    s_async_state.ota_wifi_enable_pending = 0U;
    ota_center_clear_query_follow_up();
    s_ota_pending_action = OTA_PENDING_NONE;
    ota_center_set_notice(0, 0);
}

/* 从 OTA 确认类子状态返回普通菜单，并沿用原来的整页刷新时机。 */
static void ota_center_return_to_menu(void)
{
    ota_center_enter_menu_mode();
    ui_manager_force_full_refresh();
}

/* 退出 OTA 信息页，可选择返回首页或回到 OTA 菜单。 */
static void ota_center_exit_info_mode(uint8_t navigate_home)
{
    ota_center_clear_info_latched_state();

    if (navigate_home != 0U)
    {
        ui_manager_navigate_home();
        return;
    }

    ota_center_return_to_menu();
}

/* 切换 OTA 页面到信息展示模式，并立即请求整页刷新。 */
static void ota_center_show_info_mode(const char *line1, const char *line2)
{
    s_ota_mode = OTA_CENTER_MODE_INFO;
    ota_center_set_notice(line1, line2);
    ui_manager_force_full_refresh();
}

static void ota_center_present_info_now(void)
{
    if (ui_manager_get_active_page() != UI_PAGE_OTA_CENTER)
    {
        return;
    }

    if (app_display_runtime_is_awake() == 0U)
    {
        return;
    }

    (void)app_display_runtime_request_ui_render(ota_center_render, 1U);
}

static void ota_center_show_version_status(const char *status_text,
                                           const char *current_version,
                                           const char *latest_version)
{
    s_ota_mode = OTA_CENTER_MODE_INFO;
    ota_center_set_notice(status_text, 0);
    ota_center_set_version_rows(current_version, latest_version);
    ui_manager_force_full_refresh();
}

static void ota_center_show_local_version_info(void)
{
    s_ota_mode = OTA_CENTER_MODE_INFO;
    ota_center_set_notice("Version Info", 0);
    ota_center_set_local_info_rows(ota_service_get_display_version(),
                                   ota_service_get_partition_name(ota_service_get_active_partition()));
    ui_manager_force_full_refresh();
}

/* 统一显示 OTA 页面“任务忙”提示，避免固定文案分散在多个分支中。 */
static void ota_center_show_task_busy_info(void)
{
    ota_center_show_info_mode("Please wait", "Task busy");
}

/* 统一显示 OTA 重启类提示，保持升级和回滚分支的提示路径一致。 */
static void ota_center_show_restart_info(const char *detail)
{
    ota_center_show_info_mode("Restarting", detail);
}

/* 绘制 OTA 页面顶部的信息行区域。 */
static void ota_center_draw_info_rows(void)
{
    char wifi_buffer[24];

    page_format_wifi_status(wifi_buffer, sizeof(wifi_buffer));
    ui_renderer_draw_value_row(UI_CONTENT_TOP,
                               "WiFi Status",
                               wifi_buffer,
                               BLACK,
                               WHITE);
}

/* 绘制单个 OTA 菜单项。 */
static void ota_center_draw_menu_item(uint8_t index)
{
    if (index >= OTA_ITEM_COUNT)
    {
        return;
    }

    ui_renderer_draw_list_item(page_list_item_y(OTA_LIST_START_Y, index),
                               s_ota_items[index],
                               (s_ota_selected == index) ? 1U : 0U,
                               1U,
                               WHITE);
}

/* 重绘全部 OTA 菜单项。 */
static void ota_center_draw_menu_items(void)
{
    uint8_t index = 0U;

    for (index = 0U; index < OTA_ITEM_COUNT; ++index)
    {
        ota_center_draw_menu_item(index);
    }
}

/* 异步查询最新 OTA 版本信息。 */
static uint8_t ota_center_request_latest_async(uint8_t show_success_info, ota_pending_action_t post_action)
{
    app_service_cmd_t cmd;

    memset(&cmd, 0, sizeof(cmd));
    cmd.cmd_id = APP_SERVICE_CMD_OTA_QUERY_LATEST;

    if (s_async_state.ota_query_pending != 0U)
    {
        s_async_state.ota_query_show_success_info = (show_success_info != 0U) ? 1U : 0U;
        s_async_state.ota_post_query_action = post_action;
        s_async_state.ota_query_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_OTA_MS);
        (void)app_service_submit_async(&cmd);
        return 1U;
    }
    ota_center_show_info_mode("Checking", "Please wait");
    ota_center_present_info_now();

    if (app_service_submit_async(&cmd) == 0U)
    {
        return 0U;
    }

    memset(s_ota_latest_version, 0, sizeof(s_ota_latest_version));
    s_async_state.ota_query_pending = 1U;
    s_async_state.ota_query_deadline_ms = page_async_make_deadline(PAGE_ASYNC_TIMEOUT_OTA_MS);
    s_async_state.ota_query_show_success_info = (show_success_info != 0U) ? 1U : 0U;
    s_async_state.ota_post_query_action = post_action;
    return 1U;
}

/* 根据当前版本信息推进 OTA 升级确认流程。 */
static uint8_t ota_center_start_upgrade_flow(void)
{
    if ((s_ota_latest_version[0] == '\0') ||
        (ota_service_compare_version(s_ota_latest_version,
                                     ota_service_get_display_version()) <= 0))
    {
        if (ota_center_request_latest_async(0U, OTA_PENDING_UPGRADE) == 0U)
        {
            ota_center_show_task_busy_info();
            return 0U;
        }

        return 0U;
    }

    ota_center_enter_confirm_mode(OTA_CENTER_MODE_CONFIRM_UPGRADE);
    return 1U;
}

/* 统一处理 OTA 查询成功后的版本比较、确认流程和信息页跳转路径。 */
static void ota_center_handle_query_success(const app_service_rsp_t *rsp,
                                            ota_pending_action_t post_action,
                                            uint8_t show_success_info)
{
    snprintf(s_ota_latest_version, sizeof(s_ota_latest_version), "%s", rsp->text);

    if (ota_service_compare_version(s_ota_latest_version,
                                    ota_service_get_display_version()) > 0)
    {
        if (post_action == OTA_PENDING_UPGRADE)
        {
            ota_center_enter_confirm_mode(OTA_CENTER_MODE_CONFIRM_UPGRADE);
            return;
        }

        if (show_success_info != 0U)
        {
            ota_center_show_version_status("Found new version",
                                           ota_service_get_display_version(),
                                           s_ota_latest_version);
        }
        else
        {
            ota_center_return_to_menu();
        }
        return;
    }

    ota_center_show_version_status("Up to date version",
                                   ota_service_get_display_version(),
                                   ota_service_get_display_version());
}

/* 统一处理 OTA 查询失败后的错误文案映射，保持原有错误码到提示文本的对应关系。 */
static void ota_center_handle_query_failure(const app_service_rsp_t *rsp)
{
    if (rsp->reason == OTA_CTRL_ERR_NO_UPDATE)
    {
        ota_center_show_version_status("Up to date version",
                                       ota_service_get_display_version(),
                                       ota_service_get_display_version());
    }
    else if (rsp->reason == OTA_CTRL_ERR_NO_WIFI)
    {
        ota_center_show_info_mode("WiFi not ready", "Try again");
    }
    else if (rsp->reason == OTA_CTRL_ERR_BUSY)
    {
        ota_center_show_info_mode("Please wait", "Device busy");
    }
    else
    {
        ota_center_show_info_mode("Check failed", ota_service_reason_text(rsp->reason));
    }
}

static const char *ota_center_child_title(void)
{
    if (s_ota_mode == OTA_CENTER_MODE_CONFIRM_UPGRADE)
    {
        return "Start Update";
    }

    if (s_ota_mode == OTA_CENTER_MODE_CONFIRM_ROLLBACK)
    {
        return "Restore Previous Version";
    }

    if (s_ota_pending_action == OTA_PENDING_UPGRADE)
    {
        return "Start Update";
    }

    if (s_ota_pending_action == OTA_PENDING_CHECK)
    {
        return "Check Now";
    }

    if (s_ota_selected < OTA_ITEM_COUNT)
    {
        return s_ota_items[s_ota_selected];
    }

    return "Version Info";
}

/* 按当前活动页面刷新受主机状态影响的界面区域。 */
static void page_refresh_host_status_views(ui_page_id_t active_page)
{
    if (active_page == UI_PAGE_CONNECTIVITY)
    {
        wifi_draw_status_row(0U);
        wifi_draw_item(0U);
    }
    else if (active_page == UI_PAGE_OTA_CENTER && s_ota_mode == OTA_CENTER_MODE_MENU)
    {
        ota_center_draw_info_rows();
    }
    else if (active_page == UI_PAGE_POWER)
    {
        power_draw_item(3U);
    }
}

/* 处理 WiFi 开关命令的异步响应。 */
static void page_handle_wifi_set_response(const app_service_rsp_t *rsp)
{
    device_settings_t updated;
    ota_pending_action_t pending_action = s_ota_pending_action;
    ui_page_id_t active_page = ui_manager_get_active_page();

    s_async_state.wifi_set_pending = 0U;
    s_async_state.wifi_set_deadline_ms = 0U;
    if (rsp->ok != 0U)
    {
        app_rtos_settings_copy(&updated);
        updated.wifi_enabled = s_async_state.wifi_target_enabled;
        (void)page_store_settings(&updated);
        (void)page_refresh_host_status_async();
    }

    if (s_async_state.ota_wifi_enable_pending != 0U)
    {
        s_async_state.ota_wifi_enable_pending = 0U;
        s_ota_pending_action = OTA_PENDING_NONE;

        if (rsp->ok == 0U)
        {
            ota_center_show_info_mode("WiFi error", "Try again");
            return;
        }

        if (pending_action == OTA_PENDING_CHECK)
        {
            if (ota_center_request_latest_async(1U, OTA_PENDING_CHECK) == 0U)
            {
                ota_center_show_task_busy_info();
            }
            return;
        }

        if (pending_action == OTA_PENDING_UPGRADE)
        {
            (void)ota_center_start_upgrade_flow();
            return;
        }

        ota_center_return_to_menu();
        return;
    }

    page_refresh_host_status_views(active_page);
}

/* 处理调试屏幕开关命令的异步响应。 */
static void page_handle_debug_screen_response(const app_service_rsp_t *rsp)
{
    device_settings_t updated;

    s_async_state.debug_screen_pending = 0U;
    s_async_state.debug_screen_deadline_ms = 0U;
    if (rsp->ok == 0U)
    {
        return;
    }

    app_rtos_settings_copy(&updated);
    updated.esp32_debug_screen_enabled = s_async_state.debug_screen_target_enabled;
    (void)page_store_settings(&updated);

    if (ui_manager_get_active_page() == UI_PAGE_ENGINEERING)
    {
        engineering_draw_item(1U);
    }
}

/* 处理遥控按键开关命令的异步响应。 */
static void page_handle_remote_keys_response(const app_service_rsp_t *rsp)
{
    device_settings_t updated;

    s_async_state.remote_keys_pending = 0U;
    s_async_state.remote_keys_deadline_ms = 0U;
    if (rsp->ok == 0U)
    {
        return;
    }

    app_rtos_settings_copy(&updated);
    updated.esp32_remote_keys_enabled = s_async_state.remote_keys_target_enabled;
    (void)page_store_settings(&updated);

    if (ui_manager_get_active_page() == UI_PAGE_ENGINEERING)
    {
        engineering_draw_item(2U);
    }
}

/* 处理主机状态同步命令的异步响应。 */
static void page_handle_host_state_response(const app_service_rsp_t *rsp)
{
    (void)rsp;
    s_async_state.host_state_pending = 0U;
    s_async_state.host_state_deadline_ms = 0U;
}

/* 处理电源策略同步命令的异步响应。 */
static void page_handle_power_policy_response(const app_service_rsp_t *rsp)
{
    if (s_async_state.power_policy_pending == 0U)
    {
        return;
    }

    s_async_state.power_policy_pending = 0U;
    s_async_state.power_policy_deadline_ms = 0U;
    if (rsp->ok == 0U)
    {
        return;
    }

    (void)page_set_host_state_async(power_manager_get_state());
    if (ui_manager_get_active_page() == UI_PAGE_POWER)
    {
        power_draw_item(0U);
    }
}

/* 处理强制深睡命令的异步响应。 */
static void page_handle_forced_deep_sleep_response(const app_service_rsp_t *rsp)
{
    (void)rsp;
    s_async_state.forced_deep_sleep_pending = 0U;
    s_async_state.forced_deep_sleep_deadline_ms = 0U;

    if (ui_manager_get_active_page() == UI_PAGE_POWER)
    {
        power_draw_item(4U);
    }
}

/* 处理 OTA 最新版本查询命令的异步响应。 */
static void page_handle_ota_query_response(const app_service_rsp_t *rsp)
{
    ota_pending_action_t post_action = s_async_state.ota_post_query_action;
    uint8_t show_success_info = s_async_state.ota_query_show_success_info;

    s_async_state.ota_query_pending = 0U;
    s_async_state.ota_query_deadline_ms = 0U;
    ota_center_clear_query_follow_up();

    if (rsp->ok != 0U)
    {
        ota_center_handle_query_success(rsp, post_action, show_success_info);
        return;
    }

    ota_center_handle_query_failure(rsp);
}

/* 按命令类型把服务响应分发给各页面内部处理器。 */
static void page_handle_service_response(const app_service_rsp_t *rsp)
{
    if (rsp == 0)
    {
        return;
    }

    switch (rsp->cmd_id)
    {
    case APP_SERVICE_CMD_SET_WIFI:
        page_handle_wifi_set_response(rsp);
        break;

    case APP_SERVICE_CMD_SET_DEBUG_SCREEN:
        page_handle_debug_screen_response(rsp);
        break;

    case APP_SERVICE_CMD_SET_REMOTE_KEYS:
        page_handle_remote_keys_response(rsp);
        break;

    case APP_SERVICE_CMD_SET_HOST_STATE:
        page_handle_host_state_response(rsp);
        break;

    case APP_SERVICE_CMD_SET_POWER_POLICY:
        page_handle_power_policy_response(rsp);
        break;

    case APP_SERVICE_CMD_ENTER_FORCED_DEEP_SLEEP:
        page_handle_forced_deep_sleep_response(rsp);
        break;

    case APP_SERVICE_CMD_OTA_QUERY_LATEST:
        page_handle_ota_query_response(rsp);
        break;

    case APP_SERVICE_CMD_ESP_REFRESH_STATUS:
        page_refresh_host_status_views(ui_manager_get_active_page());
        break;

    case APP_SERVICE_CMD_NONE:
    default:
        break;
    }
}

/*
 * 超时处理只负责释放页面侧的 pending/UI 等待状态。
 * 后台服务任务仍可能稍后完成，这里不能把“界面超时”误当成“任务被取消”。
 */
static void page_refresh_timeout_views(ui_page_id_t active_page)
{
    if (active_page == UI_PAGE_CONNECTIVITY)
    {
        wifi_draw_status_row(0U);
        wifi_draw_item(0U);
    }
    else if (active_page == UI_PAGE_ENGINEERING)
    {
        engineering_draw_item(0U);
        engineering_draw_item(1U);
        engineering_draw_item(2U);
    }
    else if (active_page == UI_PAGE_POWER)
    {
        power_draw_item(3U);
    }
}

/* 扫描所有页面侧异步 pending 项，并在超时后恢复页面显示。 */
static void page_async_handle_timeouts(void)
{
    uint32_t now_ms = power_manager_get_tick_ms();
    ui_page_id_t active_page = ui_manager_get_active_page();
    uint8_t timed_out = 0U;

    if (s_async_state.wifi_set_pending != 0U &&
        page_async_deadline_expired(now_ms, s_async_state.wifi_set_deadline_ms) != 0U)
    {
        s_async_state.wifi_set_pending = 0U;
        s_async_state.wifi_set_deadline_ms = 0U;
        timed_out = 1U;

        if (s_async_state.ota_wifi_enable_pending != 0U)
        {
            s_async_state.ota_wifi_enable_pending = 0U;
            s_ota_pending_action = OTA_PENDING_NONE;
            if (active_page == UI_PAGE_OTA_CENTER)
            {
                ota_center_show_info_mode("WiFi timeout", "Try again");
            }
        }
    }

    if (s_async_state.debug_screen_pending != 0U &&
        page_async_deadline_expired(now_ms, s_async_state.debug_screen_deadline_ms) != 0U)
    {
        s_async_state.debug_screen_pending = 0U;
        s_async_state.debug_screen_deadline_ms = 0U;
        timed_out = 1U;
    }

    if (s_async_state.remote_keys_pending != 0U &&
        page_async_deadline_expired(now_ms, s_async_state.remote_keys_deadline_ms) != 0U)
    {
        s_async_state.remote_keys_pending = 0U;
        s_async_state.remote_keys_deadline_ms = 0U;
        timed_out = 1U;
    }

    if (s_async_state.host_state_pending != 0U &&
        page_async_deadline_expired(now_ms, s_async_state.host_state_deadline_ms) != 0U)
    {
        s_async_state.host_state_pending = 0U;
        s_async_state.host_state_deadline_ms = 0U;
        timed_out = 1U;
    }

    if (s_async_state.power_policy_pending != 0U &&
        page_async_deadline_expired(now_ms, s_async_state.power_policy_deadline_ms) != 0U)
    {
        s_async_state.power_policy_pending = 0U;
        s_async_state.power_policy_deadline_ms = 0U;
        timed_out = 1U;
    }

    if (s_async_state.forced_deep_sleep_pending != 0U &&
        page_async_deadline_expired(now_ms, s_async_state.forced_deep_sleep_deadline_ms) != 0U)
    {
        s_async_state.forced_deep_sleep_pending = 0U;
        s_async_state.forced_deep_sleep_deadline_ms = 0U;
        timed_out = 1U;
    }

    if (s_async_state.ota_query_pending != 0U &&
        page_async_deadline_expired(now_ms, s_async_state.ota_query_deadline_ms) != 0U)
    {
        s_async_state.ota_query_pending = 0U;
        s_async_state.ota_query_deadline_ms = 0U;
        ota_center_clear_query_follow_up();
        if (active_page == UI_PAGE_OTA_CENTER)
        {
            ota_center_show_info_mode("Check timeout", "Try again");
        }
        timed_out = 1U;
    }

    if (timed_out == 0U)
    {
        return;
    }

    page_refresh_timeout_views(active_page);
    ui_manager_request_render();
}

/* 重绘首页全部菜单项。 */
static uint16_t home_card_y(uint8_t index)
{
    return (uint16_t)(HOME_LIST_START_Y + ((uint16_t)index * (HOME_CARD_HEIGHT + HOME_CARD_GAP)));
}

static void home_draw_chevron(uint16_t x, uint16_t y, uint16_t color)
{
    LCD_DrawLine(x, y, (uint16_t)(x + 6U), (uint16_t)(y + 6U), color);
    LCD_DrawLine((uint16_t)(x + 6U), (uint16_t)(y + 6U), x, (uint16_t)(y + 12U), color);
}

static void home_draw_icon(uint8_t index, uint16_t x, uint16_t y, uint16_t color)
{
    switch (index)
    {
    case 0U:
        Draw_Circle((uint16_t)(x + 8U), (uint16_t)(y + 8U), 6U, color);
        LCD_DrawLine((uint16_t)(x + 8U), (uint16_t)(y + 2U), (uint16_t)(x + 8U), (uint16_t)(y + 14U), color);
        LCD_DrawLine((uint16_t)(x + 2U), (uint16_t)(y + 8U), (uint16_t)(x + 14U), (uint16_t)(y + 8U), color);
        break;

    case 1U:
        LCD_DrawRectangle((uint16_t)(x + 2U), (uint16_t)(y + 3U), (uint16_t)(x + 14U), (uint16_t)(y + 13U), color);
        LCD_DrawLine((uint16_t)(x + 8U), (uint16_t)(y + 4U), (uint16_t)(x + 8U), (uint16_t)(y + 11U), color);
        LCD_DrawLine((uint16_t)(x + 5U), (uint16_t)(y + 8U), (uint16_t)(x + 8U), (uint16_t)(y + 11U), color);
        LCD_DrawLine((uint16_t)(x + 11U), (uint16_t)(y + 8U), (uint16_t)(x + 8U), (uint16_t)(y + 11U), color);
        break;

    case 2U:
        LCD_DrawLine((uint16_t)(x + 2U), (uint16_t)(y + 11U), (uint16_t)(x + 8U), (uint16_t)(y + 5U), color);
        LCD_DrawLine((uint16_t)(x + 14U), (uint16_t)(y + 11U), (uint16_t)(x + 8U), (uint16_t)(y + 5U), color);
        LCD_DrawLine((uint16_t)(x + 5U), (uint16_t)(y + 11U), (uint16_t)(x + 8U), (uint16_t)(y + 8U), color);
        LCD_DrawLine((uint16_t)(x + 11U), (uint16_t)(y + 11U), (uint16_t)(x + 8U), (uint16_t)(y + 8U), color);
        LCD_DrawPoint((uint16_t)(x + 8U), (uint16_t)(y + 13U), color);
        break;

    case 3U:
        Draw_Circle((uint16_t)(x + 8U), (uint16_t)(y + 8U), 6U, color);
        LCD_DrawLine((uint16_t)(x + 8U), (uint16_t)(y + 1U), (uint16_t)(x + 8U), (uint16_t)(y + 7U), color);
        break;

    case 4U:
    default:
        LCD_DrawRectangle((uint16_t)(x + 2U), (uint16_t)(y + 2U), (uint16_t)(x + 14U), (uint16_t)(y + 14U), color);
        LCD_DrawLine((uint16_t)(x + 8U), (uint16_t)(y + 4U), (uint16_t)(x + 8U), (uint16_t)(y + 12U), color);
        LCD_DrawLine((uint16_t)(x + 4U), (uint16_t)(y + 8U), (uint16_t)(x + 12U), (uint16_t)(y + 8U), color);
        break;
    }
}

static void home_draw_banner(void)
{
    app_display_runtime_lock();
    LCD_Fill(HOME_CARD_LEFT,
             HOME_BANNER_TOP,
             HOME_CARD_RIGHT,
             (uint16_t)(HOME_BANNER_TOP + HOME_BANNER_HEIGHT),
             LIGHTBLUE);
    LCD_Fill(HOME_CARD_LEFT,
             HOME_BANNER_TOP,
             (uint16_t)(HOME_CARD_LEFT + 5U),
             (uint16_t)(HOME_BANNER_TOP + HOME_BANNER_HEIGHT),
             BLUE);
    LCD_DrawRectangle(HOME_CARD_LEFT,
                      HOME_BANNER_TOP,
                      HOME_CARD_RIGHT,
                      (uint16_t)(HOME_BANNER_TOP + HOME_BANNER_HEIGHT),
                      BLUE);
    LCD_ShowUTF8String((uint16_t)(HOME_CARD_LEFT + 14U),
                       (uint16_t)(HOME_BANNER_TOP + 4U),
                       ui_renderer_localize("Function Home"),
                       DARKBLUE,
                       LIGHTBLUE,
                       16,
                       0);
    Draw_Circle((uint16_t)(HOME_CARD_RIGHT - 16U),
                (uint16_t)(HOME_BANNER_TOP + 12U),
                4U,
                RED);
    LCD_DrawPoint((uint16_t)(HOME_CARD_RIGHT - 16U),
                  (uint16_t)(HOME_BANNER_TOP + 12U),
                  RED);
    app_display_runtime_unlock();
}

static void home_draw_items(void)
{
    uint8_t index = 0U;

    for (index = 0U; index < HOME_ITEM_COUNT; ++index)
    {
        home_draw_item(index);
    }
}

/* 根据索引重绘首页中的单个菜单项。 */
static void home_draw_item(uint8_t index)
{
    uint16_t y = 0U;
    uint16_t x1 = HOME_CARD_LEFT;
    uint16_t x2 = HOME_CARD_RIGHT;
    uint16_t y2 = 0U;
    uint16_t accent = 0U;
    uint16_t shadow = 0xD69AU;
    uint16_t fill = WHITE;
    uint16_t border = GRAYBLUE;
    uint16_t text_color = DARKBLUE;

    if (index >= HOME_ITEM_COUNT)
    {
        return;
    }

    y = home_card_y(index);
    y2 = (uint16_t)(y + HOME_CARD_HEIGHT);
    accent = s_home_item_colors[index];

    if (s_home_selected == index)
    {
        fill = accent;
        border = accent;
        text_color = WHITE;
    }

    app_display_runtime_lock();
    LCD_Fill((uint16_t)(x1 + 2U),
             (uint16_t)(y + 2U),
             (uint16_t)(x2 + 1U),
             (uint16_t)(y2 + 1U),
             shadow);
    LCD_Fill(x1, y, x2, y2, fill);
    LCD_Fill(x1, y, (uint16_t)(x1 + 5U), y2, accent);
    LCD_DrawRectangle(x1, y, x2, y2, border);
    home_draw_icon(index, HOME_CARD_ICON_LEFT, (uint16_t)(y + 5U), text_color);
    LCD_ShowUTF8String(HOME_CARD_TEXT_LEFT,
                       (uint16_t)(y + 6U),
                       ui_renderer_localize(s_home_items[index]),
                       text_color,
                       fill,
                       16,
                       0);
    home_draw_chevron(HOME_CARD_CHEVRON_LEFT, (uint16_t)(y + 8U), text_color);
    app_display_runtime_unlock();
}

/* 绘制 WiFi 状态行，并利用缓存避免重复重绘。 */
static void wifi_draw_status_row(uint8_t force_refresh)
{
    char value_buffer[24];
    uint16_t value_color = BLACK;

    page_format_wifi_status(value_buffer, sizeof(value_buffer));
    if (strcmp(value_buffer, "CONNECTED") == 0)
    {
        value_color = GREEN;
    }
    else if (strcmp(value_buffer, "NOT CONNECTED") == 0)
    {
        value_color = RED;
    }
    else if (strcmp(value_buffer, "OFFLINE") == 0)
    {
        value_color = RED;
    }
    else if (strcmp(value_buffer, "OFF") == 0)
    {
        value_color = RED;
    }

    if (force_refresh == 0U &&
        s_wifi_status_cache_valid != 0U &&
        s_wifi_status_color_cache == value_color &&
        strcmp(s_wifi_status_cache, value_buffer) == 0)
    {
        return;
    }

    ui_renderer_draw_value_row(WIFI_STATUS_Y, "Connection", value_buffer, value_color, WHITE);
    snprintf(s_wifi_status_cache, sizeof(s_wifi_status_cache), "%s", value_buffer);
    s_wifi_status_color_cache = value_color;
    s_wifi_status_cache_valid = 1U;
}

/* 绘制 WiFi 开关项，并利用缓存避免重复重绘。 */
static void wifi_draw_item(uint8_t force_refresh)
{
    device_settings_t settings;
    const char *label = "WiFi";
    uint8_t forced_deep_sleep = (esp_host_is_forced_deep_sleep() != 0U) ? 1U : 0U;
    uint8_t pending = (s_async_state.wifi_set_pending != 0U) ? 1U : 0U;
    uint8_t selected = (s_wifi_selected == 0U) ? 1U : 0U;
    uint8_t display_enabled = 0U;

    app_rtos_settings_copy(&settings);
    display_enabled = settings.wifi_enabled;
    if (pending != 0U)
    {
        display_enabled = s_async_state.wifi_target_enabled;
    }

    if (forced_deep_sleep != 0U)
    {
        label = "WiFi(KEY6)";
    }
    else if (pending != 0U)
    {
        label = "WiFi...";
    }

    if (force_refresh == 0U &&
        s_wifi_item_cache_valid != 0U &&
        s_wifi_item_cache_enabled == display_enabled &&
        s_wifi_item_cache_selected == selected &&
        s_wifi_item_cache_forced == forced_deep_sleep &&
        s_wifi_item_cache_pending == pending)
    {
        return;
    }

    ui_renderer_draw_toggle_item(WIFI_LIST_START_Y,
                                 label,
                                 display_enabled,
                                 selected,
                                 WHITE);
    s_wifi_item_cache_enabled = display_enabled;
    s_wifi_item_cache_selected = selected;
    s_wifi_item_cache_forced = forced_deep_sleep;
    s_wifi_item_cache_pending = pending;
    s_wifi_item_cache_valid = 1U;
}

/* 绘制电源页面顶部信息行。 */
static void power_draw_info_rows(void)
{
    power_draw_battery_status();
}

static void power_draw_battery_status(void)
{
    char value_buffer[12];
    uint8_t percent = battery_monitor_get_percent();
    uint16_t fill_color = GREEN;
    uint16_t outline_color = BLACK;
    uint16_t row_top = UI_CONTENT_TOP;
    uint16_t row_bottom = (uint16_t)(UI_CONTENT_TOP + UI_ROW_HEIGHT - 2U);
    uint16_t icon_left = 208U;
    uint16_t icon_top = (uint16_t)(UI_CONTENT_TOP + 4U);
    uint16_t icon_right = 232U;
    uint16_t icon_bottom = (uint16_t)(icon_top + 12U);
    uint16_t inner_left = (uint16_t)(icon_left + 2U);
    uint16_t inner_top = (uint16_t)(icon_top + 2U);
    uint16_t inner_right = (uint16_t)(icon_right - 2U);
    uint16_t inner_bottom = (uint16_t)(icon_bottom - 2U);
    uint16_t inner_width = (uint16_t)(inner_right - inner_left + 1U);
    uint16_t fill_width = 0U;

    if (percent < 30U)
    {
        fill_color = RED;
    }
    else if (percent < 60U)
    {
        fill_color = YELLOW;
    }

    page_format_battery(value_buffer, sizeof(value_buffer));
    if (percent > 0U)
    {
        fill_width = (uint16_t)(((uint32_t)inner_width * percent + 99UL) / 100UL);
        if (fill_width == 0U)
        {
            fill_width = 1U;
        }
        if (fill_width > inner_width)
        {
            fill_width = inner_width;
        }
    }

    app_display_runtime_lock();
    LCD_Fill(8U, row_top, LCD_W - 8U, row_bottom, WHITE);
    LCD_ShowUTF8String(12U,
                       row_top,
                       ui_renderer_localize("Battery Level"),
                       BLACK,
                       WHITE,
                       16,
                       0);
    LCD_ShowUTF8String(88U,
                       row_top,
                       value_buffer,
                       fill_color,
                       WHITE,
                       16,
                       0);

    LCD_DrawRectangle(icon_left, icon_top, icon_right, icon_bottom, outline_color);
    LCD_DrawRectangle((uint16_t)(icon_right + 1U),
                      (uint16_t)(icon_top + 3U),
                      (uint16_t)(icon_right + 3U),
                      (uint16_t)(icon_bottom - 3U),
                      outline_color);
    LCD_Fill(inner_left, inner_top, inner_right, inner_bottom, WHITE);
    if (fill_width > 0U)
    {
        LCD_Fill(inner_left,
                 inner_top,
                 (uint16_t)(inner_left + fill_width - 1U),
                 inner_bottom,
                 fill_color);
    }
    app_display_runtime_unlock();
}

/* 根据索引绘制电源页面中的单个菜单项。 */
static void power_draw_item(uint8_t index)
{
    char value_buffer[24];
    device_settings_t settings;
    uint16_t item_y = page_list_item_y(POWER_LIST_START_Y, index);

    app_rtos_settings_copy(&settings);

    if (index >= POWER_ITEM_COUNT)
    {
        return;
    }

    if (index == 0U)
    {
        ui_renderer_draw_option_item(item_y,
                                     s_power_items[0],
                                     ui_renderer_power_policy_text(settings.power_policy),
                                     (s_power_selected == 0U) ? 1U : 0U,
                                     WHITE);
    }
    else if (index == 1U)
    {
        page_format_timeout_ms(value_buffer, sizeof(value_buffer), settings.screen_off_timeout_ms);
        ui_renderer_draw_option_item(item_y,
                                     s_power_items[1],
                                     value_buffer,
                                     (s_power_selected == 1U) ? 1U : 0U,
                                     WHITE);
    }
    else if (index == 2U)
    {
        ui_renderer_draw_toggle_item(item_y,
                                     s_power_items[2],
                                     settings.standby_enabled,
                                     (s_power_selected == 2U) ? 1U : 0U,
                                     WHITE);
    }
    else if (index == 3U)
    {
        const char *value_text = "Off";

        if (s_async_state.forced_deep_sleep_pending != 0U)
        {
            value_text = "WAIT";
        }
        else if (esp_host_is_forced_deep_sleep() != 0U)
        {
            value_text = "KEY6";
        }

        ui_renderer_draw_option_item(item_y,
                                     s_power_items[3],
                                     value_text,
                                     (s_power_selected == 3U) ? 1U : 0U,
                                     WHITE);
    }
}

/* 重绘电源页面全部菜单项。 */
static void power_draw_items(void)
{
    uint8_t index = 0U;

    for (index = 0U; index < POWER_ITEM_COUNT; ++index)
    {
        power_draw_item(index);
    }
}

/* 绘制系统页面顶部信息行。 */
/* 根据索引绘制系统页面中的单个菜单项。 */
static void system_draw_item(uint8_t index)
{
    device_settings_t settings;

    app_rtos_settings_copy(&settings);

    if (index == 0U)
    {
        ui_renderer_draw_toggle_item(SYSTEM_LIST_START_Y,
                                     s_system_items[0],
                                     settings.debug_mode_enabled,
                                     (s_system_selected == 0U) ? 1U : 0U,
                                     WHITE);
    }
    else if (index == 1U)
    {
        if (settings.debug_mode_enabled != 0U)
        {
            ui_renderer_draw_list_item((uint16_t)(SYSTEM_LIST_START_Y + UI_ROW_HEIGHT),
                                       s_system_items[1],
                                       (s_system_selected == 1U) ? 1U : 0U,
                                       1U,
                                       WHITE);
        }
        else
        {
            ui_renderer_clear_row((uint16_t)(SYSTEM_LIST_START_Y + UI_ROW_HEIGHT), WHITE);
        }
    }
}

/* 重绘系统页面全部菜单项。 */
static void system_draw_items(void)
{
    system_draw_item(0U);
    system_draw_item(1U);
}

/* 根据索引绘制工程页面中的单个菜单项。 */
static void engineering_draw_item(uint8_t index)
{
    device_settings_t settings;
    uint16_t item_y = page_list_item_y(DEBUG_LIST_START_Y, index);

    app_rtos_settings_copy(&settings);

    if (index >= ENGINEERING_ITEM_COUNT)
    {
        return;
    }

    if (index == 0U)
    {
        ui_renderer_draw_list_item(item_y,
                                   s_engineering_items[index],
                                   (s_engineering_selected == index) ? 1U : 0U,
                                   1U,
                                   WHITE);
        return;
    }

    ui_renderer_draw_toggle_item(item_y,
                                 s_engineering_items[index],
                                 (index == 1U) ? settings.esp32_debug_screen_enabled :
                                                 settings.esp32_remote_keys_enabled,
                                 (s_engineering_selected == index) ? 1U : 0U,
                                 WHITE);
}

/* 重绘工程页面全部菜单项。 */
static void engineering_draw_items(void)
{
    uint8_t index = 0U;

    for (index = 0U; index < ENGINEERING_ITEM_COUNT; ++index)
    {
        engineering_draw_item(index);
    }
}

/* 首页进入回调，目前不需要额外状态初始化。 */
static void home_on_enter(ui_page_id_t previous_page)
{
    (void)previous_page;
}

/* 首页离开回调，目前仅保留统一接口。 */
static void home_on_leave(ui_page_id_t next_page)
{
    (void)next_page;
}

/* 处理首页按键：移动焦点或进入子页面。 */
static void home_on_key(uint8_t key_value)
{
    if (key_value == KEY1_PRES)
    {
        page_move_selection(&s_home_selected, HOME_ITEM_COUNT, 1U, home_draw_item);
    }
    else if (key_value == KEY3_PRES)
    {
        page_move_selection(&s_home_selected, HOME_ITEM_COUNT, 0U, home_draw_item);
    }
    else if (key_value == KEY2_PRES)
    {
        switch (s_home_selected)
        {
        case 0U:
            ui_manager_navigate_to(UI_PAGE_THERMAL);
            break;
        case 1U:
            ui_manager_navigate_to(UI_PAGE_OTA_CENTER);
            break;
        case 2U:
            ui_manager_navigate_to(UI_PAGE_CONNECTIVITY);
            break;
        case 3U:
            ui_manager_navigate_to(UI_PAGE_POWER);
            break;
        default:
            ui_manager_navigate_to(UI_PAGE_SYSTEM);
            break;
        }
    }
}

/* 首页周期回调，仅负责处理异步超时。 */
static void home_on_tick(void)
{
    page_async_handle_timeouts();
}

/* 渲染首页静态布局与菜单项。 */
static void home_render(uint8_t full_refresh)
{
    if (full_refresh == 0U)
    {
        return;
    }

    ui_renderer_draw_header_status("Main Menu", BLUE);
    ui_renderer_clear_body(LGRAY);
    home_draw_banner();
    home_draw_items();
}

/* 热成像页面进入回调，恢复热成像运行并强制整页刷新。 */
static void thermal_on_enter(ui_page_id_t previous_page)
{
    (void)previous_page;
    redpic1_thermal_resume();
    ui_manager_force_full_refresh();
}

/* 热成像页面离开回调，暂停热成像运行。 */
static void thermal_on_leave(ui_page_id_t next_page)
{
    (void)next_page;
    redpic1_thermal_suspend();
}

/* 处理热成像页面按键，长按返回，其余交给热成像子模块。 */
static void thermal_on_key(uint8_t key_value)
{
    if (key_value == UI_KEY_KEY2_LONG)
    {
        ui_manager_navigate_home();
        return;
    }

    redpic1_thermal_handle_key(key_value);
}

/* 热成像页面周期回调，仅负责处理异步超时。 */
static void thermal_on_tick(void)
{
    page_async_handle_timeouts();
}

/* 渲染热成像页面背景，并触发热成像模块自行刷新。 */
static void thermal_render(uint8_t full_refresh)
{
    if (full_refresh != 0U)
    {
        app_display_runtime_lock();
        LCD_Fill(0, 0, LCD_W - 1U, LCD_H - 1U, BLACK);
        app_display_runtime_unlock();
        redpic1_thermal_force_refresh();
    }
}

/* OTA 页面进入回调，刷新基础信息并复位页面模式。 */
static void ota_center_on_enter(ui_page_id_t previous_page)
{
    (void)previous_page;
    ota_service_refresh_info();
    ota_center_reset_menu_state();
}

/* OTA 页面离开回调，清理页面模式与挂起动作。 */
static void ota_center_on_leave(ui_page_id_t next_page)
{
    (void)next_page;
    ota_center_reset_menu_state();
}

/* 处理 OTA 页面按键与确认流程状态机。 */
static void ota_center_on_key(uint8_t key_value)
{
    device_settings_t settings;

    app_rtos_settings_copy(&settings);

    if (s_ota_mode == OTA_CENTER_MODE_CONFIRM_WIFI)
    {
        if (key_value == UI_KEY_KEY2_LONG)
        {
            ui_manager_navigate_home();
        }
        else if (key_value == KEY1_PRES)
        {
            ota_center_reset_menu_state();
            ui_manager_force_full_refresh();
        }
        else if (key_value == KEY2_PRES)
        {
            if (page_set_wifi_enabled(1U) == 0U)
            {
                ota_center_show_task_busy_info();
                return;
            }
            s_async_state.ota_wifi_enable_pending = 1U;
            ota_center_show_info_mode("Enabling WiFi", "Please wait");
            ota_center_present_info_now();
        }
        return;
    }

    if (s_ota_mode == OTA_CENTER_MODE_CONFIRM_UPGRADE)
    {
        if (key_value == UI_KEY_KEY2_LONG)
        {
            ui_manager_navigate_home();
        }
        else if (key_value == KEY1_PRES)
        {
            ota_center_return_to_menu();
        }
        else if (key_value == KEY2_PRES)
        {
            ota_center_show_restart_info("Start update");
            delay_ms(200U);
            ota_service_request_upgrade();
        }
        return;
    }

    if (s_ota_mode == OTA_CENTER_MODE_CONFIRM_ROLLBACK)
    {
        if (key_value == UI_KEY_KEY2_LONG)
        {
            ui_manager_navigate_home();
        }
        else if (key_value == KEY1_PRES)
        {
            ota_center_return_to_menu();
        }
        else if (key_value == KEY2_PRES)
        {
            ota_center_show_restart_info("Restore Previous Version");
            delay_ms(200U);
            ota_service_request_rollback();
        }
        return;
    }

    if (s_ota_mode == OTA_CENTER_MODE_INFO)
    {
        if (key_value == UI_KEY_KEY2_LONG)
        {
            ota_center_exit_info_mode(1U);
        }
        else if (key_value == KEY1_PRES || key_value == KEY2_PRES)
        {
            ota_center_exit_info_mode(0U);
        }
        return;
    }

    if (key_value == KEY1_PRES)
    {
        page_move_selection(&s_ota_selected, OTA_ITEM_COUNT, 1U, ota_center_draw_menu_item);
    }
    else if (key_value == KEY3_PRES)
    {
        page_move_selection(&s_ota_selected, OTA_ITEM_COUNT, 0U, ota_center_draw_menu_item);
    }
    else if (key_value == UI_KEY_KEY2_LONG)
    {
        ui_manager_navigate_home();
    }
    else if (key_value == KEY2_PRES)
    {
        if ((s_ota_selected == 0U || s_ota_selected == 1U) && settings.wifi_enabled == 0U)
        {
            s_ota_pending_action = (s_ota_selected == 0U) ? OTA_PENDING_CHECK : OTA_PENDING_UPGRADE;
            ota_center_enter_confirm_mode(OTA_CENTER_MODE_CONFIRM_WIFI);
            return;
        }

        switch (s_ota_selected)
        {
        case 0U:
            if (ota_center_request_latest_async(1U, OTA_PENDING_CHECK) == 0U)
            {
                ota_center_show_task_busy_info();
            }
            break;
        case 1U:
            (void)ota_center_start_upgrade_flow();
            break;
        case 2U:
            ota_center_enter_confirm_mode(OTA_CENTER_MODE_CONFIRM_ROLLBACK);
            break;
        case 3U:
            ota_center_show_local_version_info();
            break;
        }
    }
}

/* OTA 页面周期回调，仅负责处理异步超时。 */
static void ota_center_on_tick(void)
{
    page_async_handle_timeouts();
}

/* 根据当前 OTA 子状态渲染页面内容。 */
static void ota_center_render(uint8_t full_refresh)
{
    if (full_refresh == 0U)
    {
        return;
    }

    if (s_ota_mode == OTA_CENTER_MODE_MENU)
    {
        ui_renderer_draw_header_status("Update", GBLUE);
        ui_renderer_clear_body(WHITE);
        ota_center_draw_info_rows();
        ota_center_draw_menu_items();
        return;
    }

    if (s_ota_mode == OTA_CENTER_MODE_CONFIRM_WIFI)
    {
        ui_renderer_draw_header_path_status("Update", ota_center_child_title(), GBLUE);
        ui_renderer_clear_body(WHITE);
        ui_renderer_draw_value_row(72,
                                   "Need WiFi",
                                   (s_async_state.ota_wifi_enable_pending != 0U) ? "Enabling..." : "Turn on now?",
                                   BLACK,
                                   WHITE);
        ui_renderer_draw_value_row(96,
                                   "Reason",
                                   (s_ota_pending_action == OTA_PENDING_UPGRADE) ? "Required to update" : "Required to check",
                                   BLACK,
                                   WHITE);
        return;
    }

    if (s_ota_mode == OTA_CENTER_MODE_CONFIRM_UPGRADE)
    {
        ui_renderer_draw_header_path_status("Update", ota_center_child_title(), GBLUE);
        ui_renderer_clear_body(WHITE);
        ui_renderer_draw_value_row(72, "Latest Version", s_ota_latest_version, GREEN, WHITE);
        ui_renderer_draw_value_row(96,
                                   "Target",
                                   ota_service_get_partition_name(ota_service_get_inactive_partition()),
                                   BLACK,
                                   WHITE);
        return;
    }

    if (s_ota_mode == OTA_CENTER_MODE_CONFIRM_ROLLBACK)
    {
        ui_renderer_draw_header_path_status("Update", ota_center_child_title(), GBLUE);
        ui_renderer_clear_body(WHITE);
        ui_renderer_draw_value_row(72,
                                   "Current Partition",
                                   ota_service_get_partition_name(ota_service_get_active_partition()),
                                   BLACK,
                                   WHITE);
        ui_renderer_draw_value_row(96,
                                   "Old Partition",
                                   ota_service_get_partition_name(ota_service_get_inactive_partition()),
                                   YELLOW,
                                   WHITE);
        ui_renderer_draw_value_row(120,
                                   "Current Version",
                                   ota_service_get_display_version(),
                                   BLACK,
                                   WHITE);
        ui_renderer_draw_value_row(144,
                                   "Previous Version",
                                   ota_service_get_partition_version(ota_service_get_inactive_partition()),
                                   BLACK,
                                   WHITE);
        return;
    }

    ui_renderer_draw_header_path_status("Update", ota_center_child_title(), GBLUE);
    ui_renderer_clear_body(WHITE);
    if (s_ota_show_partition_rows != 0U)
    {
        ui_renderer_draw_value_row(72, "Current Version", s_ota_info_current_version, BLACK, WHITE);
        ui_renderer_draw_value_row(96, "Current Partition", s_ota_info_partition, BLACK, WHITE);
    }
    else if (s_ota_show_version_rows != 0U)
    {
        ui_renderer_draw_value_row(72, "Version Info", s_ota_notice_line1, BLACK, WHITE);
        ui_renderer_draw_value_row(96, "Current Version", s_ota_info_current_version, BLACK, WHITE);
        ui_renderer_draw_value_row(120, "Latest Version", s_ota_info_latest_version, GREEN, WHITE);
    }
    else
    {
        ui_renderer_draw_value_row(72, "Info", s_ota_notice_line1, BLACK, WHITE);
        ui_renderer_draw_value_row(96, "Detail", s_ota_notice_line2, BLACK, WHITE);
    }
}

/* WiFi 页面进入回调，初始化选择状态并按需拉取主机状态。 */
static void connectivity_on_enter(ui_page_id_t previous_page)
{
    device_settings_t settings;

    (void)previous_page;
    s_wifi_selected = 0U;
    s_wifi_next_refresh_ms = 0U;
    s_wifi_status_cache_valid = 0U;
    s_wifi_item_cache_valid = 0U;

    app_rtos_settings_copy(&settings);
    if (settings.wifi_enabled != 0U)
    {
        (void)page_refresh_host_status_async();
    }
}

/* WiFi 页面离开回调，清理本页缓存。 */
static void connectivity_on_leave(ui_page_id_t next_page)
{
    (void)next_page;
    s_wifi_status_cache_valid = 0U;
    s_wifi_item_cache_valid = 0U;
}

/* 处理 WiFi 页面按键，负责开关 WiFi 或唤醒主机状态同步。 */
static void connectivity_on_key(uint8_t key_value)
{
    device_settings_t settings;

    app_rtos_settings_copy(&settings);

    if (key_value == UI_KEY_KEY2_LONG)
    {
        ui_manager_navigate_home();
    }
    else if (key_value == KEY2_PRES)
    {
        if (esp_host_is_forced_deep_sleep() != 0U)
        {
            (void)page_refresh_host_status_async();
            (void)page_set_host_state_async(power_manager_get_state());
            return;
        }

        if (page_set_wifi_enabled((uint8_t)!settings.wifi_enabled) != 0U)
        {
            wifi_draw_status_row(0U);
            wifi_draw_item(0U);
        }
        else
        {
            wifi_draw_status_row(0U);
        }
    }
}

/* WiFi 页面周期回调，按节流周期刷新连接状态。 */
static void connectivity_on_tick(void)
{
    device_settings_t settings;

    page_async_handle_timeouts();
    app_rtos_settings_copy(&settings);

    if (settings.wifi_enabled != 0U)
    {
        uint32_t now_ms = power_manager_get_tick_ms();

        if (now_ms >= s_wifi_next_refresh_ms)
        {
            s_wifi_next_refresh_ms = now_ms + WIFI_STATUS_REFRESH_MS;
            (void)page_refresh_host_status_async();
        }
    }
    else if (esp_host_is_forced_deep_sleep() != 0U)
    {
        uint32_t now_ms = power_manager_get_tick_ms();

        if (now_ms >= s_wifi_next_refresh_ms)
        {
            s_wifi_next_refresh_ms = now_ms + WIFI_STATUS_REFRESH_MS;
            (void)page_refresh_host_status_async();
            (void)page_set_host_state_async(power_manager_get_state());
        }
    }
}

/* 渲染 WiFi 页面静态布局与当前连接状态。 */
static void connectivity_render(uint8_t full_refresh)
{
    if (full_refresh == 0U)
    {
        return;
    }

    ui_renderer_draw_header_status("WiFi", GREEN);
    ui_renderer_clear_body(WHITE);
    wifi_draw_status_row(1U);
    wifi_draw_item(1U);
}

/* 电源页面进入回调，复位菜单焦点。 */
static void power_page_on_enter(ui_page_id_t previous_page)
{
    (void)previous_page;
    s_power_selected = 0U;
}

/* 电源页面离开回调，目前仅保留统一接口。 */
static void power_page_on_leave(ui_page_id_t next_page)
{
    (void)next_page;
}

/* 处理电源页面按键，负责轮转配置、触发深睡或手动待机。 */
static void power_page_on_key(uint8_t key_value)
{
    device_settings_t updated;

    app_rtos_settings_copy(&updated);

    if (key_value == KEY1_PRES)
    {
        page_move_selection(&s_power_selected, POWER_ITEM_COUNT, 1U, power_draw_item);
    }
    else if (key_value == KEY3_PRES)
    {
        page_move_selection(&s_power_selected, POWER_ITEM_COUNT, 0U, power_draw_item);
    }
    else if (key_value == UI_KEY_KEY2_LONG)
    {
        ui_manager_navigate_home();
    }
    else if (key_value == KEY2_PRES)
    {
        if (s_power_selected == 0U)
        {
            updated.power_policy =
                (power_policy_t)(((uint32_t)updated.power_policy + 1U) % (uint32_t)POWER_POLICY_COUNT);
        }
        else if (s_power_selected == 1U)
        {
            updated.screen_off_timeout_ms = page_next_screen_off_timeout_ms(updated.screen_off_timeout_ms);
        }
        else if (s_power_selected == 2U)
        {
            updated.standby_enabled = (uint8_t)!updated.standby_enabled;
        }
        else if (s_power_selected == 3U)
        {
            if (esp_host_is_forced_deep_sleep() == 0U)
            {
                if (page_enter_forced_deep_sleep_async(POWER_PAGE_HOST_PREP_TIMEOUT_MS) != 0U)
                {
                    power_draw_item(s_power_selected);
                }
            }
            else
            {
                if (page_refresh_host_status_async() != 0U)
                {
                    (void)page_set_host_state_async(power_manager_get_state());
                    power_draw_item(s_power_selected);
                }
            }
            return;
        }

        if (page_store_settings(&updated) != 0U)
        {
            if (s_power_selected == 0U)
            {
                (void)page_set_power_policy_async(updated.power_policy);
                (void)page_set_host_state_async(power_manager_get_state());
            }
            power_draw_item(s_power_selected);
        }
    }
}

/* 电源页面周期回调，仅负责处理异步超时。 */
static void power_page_on_tick(void)
{
    page_async_handle_timeouts();
}

/* 渲染电源页面信息行和全部菜单项。 */
static void power_page_render(uint8_t full_refresh)
{
    if (full_refresh == 0U)
    {
        return;
    }

    ui_renderer_draw_header_status("Power", BROWN);
    ui_renderer_clear_body(WHITE);
    power_draw_info_rows();
    power_draw_items();
}

/* 系统页面进入回调，校正当前选择项范围。 */
static void system_on_enter(ui_page_id_t previous_page)
{
    (void)previous_page;

    if (s_system_selected >= system_item_count())
    {
        s_system_selected = 0U;
    }
}

/* 系统页面离开回调，目前仅保留统一接口。 */
static void system_on_leave(ui_page_id_t next_page)
{
    (void)next_page;
}

/* 处理系统页面按键，负责调试模式开关和工程页面跳转。 */
static void system_on_key(uint8_t key_value)
{
    uint8_t item_count = system_item_count();
    device_settings_t updated;

    app_rtos_settings_copy(&updated);

    if (key_value == KEY1_PRES)
    {
        page_move_selection(&s_system_selected, item_count, 1U, system_draw_item);
    }
    else if (key_value == KEY3_PRES)
    {
        page_move_selection(&s_system_selected, item_count, 0U, system_draw_item);
    }
    else if (key_value == UI_KEY_KEY2_LONG)
    {
        ui_manager_navigate_home();
    }
    else if (key_value == KEY2_PRES)
    {
        if (s_system_selected == 0U)
        {
            if (updated.debug_mode_enabled != 0U)
            {
                if (updated.esp32_remote_keys_enabled != 0U)
                {
                    (void)page_set_remote_keys_enabled(0U);
                }
                if (updated.esp32_debug_screen_enabled != 0U)
                {
                    (void)page_set_debug_screen_enabled(0U);
                }
                app_rtos_settings_copy(&updated);
                updated.esp32_remote_keys_enabled = 0U;
                updated.esp32_debug_screen_enabled = 0U;
            }

            updated.debug_mode_enabled = (uint8_t)!updated.debug_mode_enabled;
            if (page_store_settings(&updated) != 0U)
            {
                s_system_selected = 0U;
                system_draw_items();
            }
        }
        else if (updated.debug_mode_enabled != 0U)
        {
            ui_manager_navigate_to(UI_PAGE_PERF_BASELINE);
        }
    }
}

/* 系统页面周期回调，仅负责处理异步超时。 */
static void system_on_tick(void)
{
    page_async_handle_timeouts();
}

/* 渲染系统页面信息行和菜单项。 */
static void system_render(uint8_t full_refresh)
{
    if (full_refresh == 0U)
    {
        return;
    }

    ui_renderer_draw_header_status("System", DARKBLUE);
    ui_renderer_clear_body(WHITE);
    system_draw_items();
}

/* 工程页面进入回调，校验调试模式并初始化焦点。 */
static void engineering_on_enter(ui_page_id_t previous_page)
{
    device_settings_t settings;

    (void)previous_page;
    app_rtos_settings_copy(&settings);

    if (settings.debug_mode_enabled == 0U)
    {
        ui_manager_navigate_home();
        return;
    }

    s_engineering_selected = 0U;
    page_refresh_host_status_async();
}

/* 工程页面离开回调，目前仅保留统一接口。 */
static void engineering_on_leave(ui_page_id_t next_page)
{
    (void)next_page;
}

/* 处理工程页面按键，负责子页跳转和工程开关项切换。 */
static void engineering_on_key(uint8_t key_value)
{
    device_settings_t settings;

    app_rtos_settings_copy(&settings);

    if (key_value == KEY1_PRES)
    {
        page_move_selection(&s_engineering_selected, ENGINEERING_ITEM_COUNT, 1U, engineering_draw_item);
    }
    else if (key_value == KEY3_PRES)
    {
        page_move_selection(&s_engineering_selected, ENGINEERING_ITEM_COUNT, 0U, engineering_draw_item);
    }
    else if (key_value == UI_KEY_KEY2_LONG)
    {
        ui_manager_navigate_home();
    }
    else if (key_value == KEY2_PRES)
    {
        uint8_t ok = 0U;

        if (s_engineering_selected == 0U)
        {
            ui_manager_navigate_to(UI_PAGE_PERF_BASELINE);
            return;
        }
        else if (s_engineering_selected == 1U)
        {
            ok = page_set_debug_screen_enabled((uint8_t)!settings.esp32_debug_screen_enabled);
        }
        else
        {
            ok = page_set_remote_keys_enabled((uint8_t)!settings.esp32_remote_keys_enabled);
        }

        if (ok != 0U)
        {
            engineering_draw_item(s_engineering_selected);
        }
    }
}

/* 工程页面周期回调，若调试模式关闭则直接返回首页。 */
static void engineering_on_tick(void)
{
    if (perf_baseline_debug_visible() == 0U)
    {
        ui_manager_navigate_home();
        return;
    }

    page_async_handle_timeouts();
}

/* 渲染工程页面菜单内容。 */
static void engineering_render(uint8_t full_refresh)
{
    if (full_refresh == 0U)
    {
        return;
    }

    ui_renderer_draw_header("Debug Page", GRAYBLUE);
    ui_renderer_clear_body(WHITE);
    engineering_draw_items();
}

/* 判断性能基线页面是否允许显示。 */
static uint8_t perf_baseline_debug_visible(void)
{
    device_settings_t settings;

    app_rtos_settings_copy(&settings);
    return (settings.debug_mode_enabled != 0U) ? 1U : 0U;
}

/* 根据当前子页索引返回性能基线页面标题。 */
/* 将文本补空格对齐到固定长度，便于局部刷新覆盖旧内容。 */
static void perf_baseline_pad_text(char *buffer,
                                   uint16_t buffer_len,
                                   const char *value,
                                   uint8_t pad_chars)
{
    uint16_t i = 0U;
    uint16_t copy_len = 0U;

    if (buffer == 0 || buffer_len == 0U)
    {
        return;
    }

    memset(buffer, 0, buffer_len);
    if (pad_chars >= buffer_len)
    {
        pad_chars = (uint8_t)(buffer_len - 1U);
    }

    if (value != 0)
    {
        copy_len = (uint16_t)strlen(value);
        if (copy_len > pad_chars)
        {
            copy_len = pad_chars;
        }

        if (copy_len > 0U)
        {
            memcpy(buffer, value, copy_len);
        }
    }

    for (i = copy_len; i < pad_chars; ++i)
    {
        buffer[i] = ' ';
    }
    buffer[pad_chars] = '\0';
}

/* 绘制性能基线页面中的单行布局底板和标签。 */
static void perf_baseline_draw_layout_row(uint16_t y, const char *label)
{
    app_display_runtime_lock();
    LCD_Fill(8U, y, LCD_W - 8U, (uint16_t)(y + UI_ROW_HEIGHT - 2U), WHITE);
    if (label != 0 && label[0] != '\0')
    {
        LCD_ShowUTF8String(PERF_LABEL_X, y, ui_renderer_localize(label), BLACK, WHITE, 16, 0);
    }
    app_display_runtime_unlock();
}

/* 在性能基线页面指定位置绘制一个对齐后的值文本。 */
static void perf_baseline_draw_value_text_ex(uint16_t y,
                                             const char *value,
                                             uint16_t value_color,
                                             uint16_t value_x,
                                             uint8_t pad_chars,
                                             uint8_t font_size)
{
    char padded[32];
    const char *display_value = ui_renderer_localize(value);

    perf_baseline_pad_text(padded, sizeof(padded), display_value, pad_chars);

    app_display_runtime_lock();
    LCD_ShowUTF8String(value_x, y, padded, value_color, WHITE, font_size, 0);
    app_display_runtime_unlock();
}

/* 以默认布局参数绘制性能基线值文本。 */
static void perf_baseline_draw_value_text(uint16_t y, const char *value, uint16_t value_color)
{
    perf_baseline_draw_value_text_ex(y,
                                     value,
                                     value_color,
                                     PERF_VALUE_X,
                                     PERF_VALUE_PAD_CHARS,
                                     PERF_VALUE_FONT_SIZE);
}

/* 绘制性能基线时序统计值文本。 */
static void perf_baseline_draw_timing_value_text(uint16_t y, const char *value, uint16_t value_color)
{
    perf_baseline_draw_value_text_ex(y,
                                     value,
                                     value_color,
                                     PERF_TIMING_VALUE_X,
                                     PERF_TIMING_VALUE_PAD_CHARS,
                                     PERF_TIMING_VALUE_FONT_SIZE);
}

static uint8_t perf_baseline_text_has_non_ascii(const char *text)
{
    const unsigned char *cursor = (const unsigned char *)text;

    if (cursor == 0)
    {
        return 0U;
    }

    while (*cursor != '\0')
    {
        if (*cursor >= 0x80U)
        {
            return 1U;
        }
        ++cursor;
    }

    return 0U;
}

/* 绘制性能基线页面底部两行说明文本。 */
static void perf_baseline_draw_footer_text(const char *line1, const char *line2)
{
    char padded1[48];
    char padded2[48];
    const char *display_line1 = ui_renderer_localize(line1);
    const char *display_line2 = ui_renderer_localize(line2);
    uint8_t use_utf8 = 0U;

    if (perf_baseline_text_has_non_ascii(display_line1) != 0U ||
        perf_baseline_text_has_non_ascii(display_line2) != 0U)
    {
        use_utf8 = 1U;
    }

    app_display_runtime_lock();
    LCD_Fill(8U, UI_FOOTER_LINE1_Y, LCD_W - 8U, LCD_H - 1U, WHITE);
    if (use_utf8 != 0U)
    {
        if (display_line1 != 0 && display_line1[0] != '\0')
        {
            LCD_ShowUTF8String(8U, UI_FOOTER_LINE1_Y, display_line1, DARKBLUE, WHITE, 16, 0);
        }
        if (display_line2 != 0 && display_line2[0] != '\0')
        {
            LCD_ShowUTF8String(8U, UI_FOOTER_LINE2_Y, display_line2, DARKBLUE, WHITE, 16, 0);
        }
    }
    else
    {
        perf_baseline_pad_text(padded1, sizeof(padded1), display_line1, PERF_FOOTER_PAD_CHARS);
        perf_baseline_pad_text(padded2, sizeof(padded2), display_line2, PERF_FOOTER_PAD_CHARS);
        LCD_ShowString(8U, UI_FOOTER_LINE1_Y, (const u8 *)padded1, DARKBLUE, WHITE, UI_FOOTER_FONT_SIZE, 0);
        LCD_ShowString(8U, UI_FOOTER_LINE2_Y, (const u8 *)padded2, DARKBLUE, WHITE, UI_FOOTER_FONT_SIZE, 0);
    }
    app_display_runtime_unlock();
}

/* 按当前子页模式绘制性能基线页面的固定布局。 */
static void perf_baseline_draw_layout(uint8_t enabled)
{
    static const char *s_snapshot_labels[4] = { "FPS", "MinT", "MaxT", "CtrT" };
    static const char *s_timing_labels[4] = { "Frame L/A/M", "Temp  L/A/M", "Gray  L/A/M", "DMA   L/A/M" };
    static const char *s_counter_labels[4] = { "KeyQ Drop", "UIQ Drop", "SvcQ Fail", "UART Err" };
    static const char *s_health_labels[4] = { "Wdg Fault", "Miss Prog", "Therm Act", "Screen" };
    static const char *s_disabled_labels[4] = { "Status", "Switch", "Action", "Scope" };
    const char **labels = s_snapshot_labels;
    uint8_t index = 0U;

    if (enabled == 0U)
    {
        labels = s_disabled_labels;
    }
    else
    {
        switch (s_perf_baseline_subpage)
        {
        case 1U:
            labels = s_timing_labels;
            break;
        case 2U:
            labels = s_counter_labels;
            break;
        case 3U:
            labels = s_health_labels;
            break;
        case 0U:
        default:
            labels = s_snapshot_labels;
            break;
        }
    }

    for (index = 0U; index < 4U; ++index)
    {
        perf_baseline_draw_layout_row(page_list_item_y(UI_CONTENT_TOP, index), labels[index]);
    }

    app_display_runtime_lock();
    LCD_Fill(0U, UI_FOOTER_LINE1_Y, LCD_W - 1U, LCD_H - 1U, WHITE);
    app_display_runtime_unlock();
}

/* 将温度值格式化为一位小数文本。 */
static void perf_baseline_format_temp(char *buffer,
                                      uint16_t buffer_len,
                                      float temp,
                                      uint8_t has_value)
{
    int32_t scaled = 0;
    int32_t whole = 0;
    int32_t frac = 0;

    if (buffer == 0 || buffer_len == 0U)
    {
        return;
    }

    if (has_value == 0U)
    {
        snprintf(buffer, buffer_len, "%s", "--.-C");
        return;
    }

    scaled = (temp >= 0.0f) ?
             (int32_t)(temp * 10.0f + 0.5f) :
             (int32_t)(temp * 10.0f - 0.5f);
    whole = scaled / 10;
    frac = scaled % 10;
    if (frac < 0)
    {
        frac = -frac;
    }

    snprintf(buffer, buffer_len, "%ld.%ldC", (long)whole, (long)frac);
}

/* 将 last/avg/max 三元统计值格式化为文本。 */
static void perf_baseline_format_triplet(char *buffer,
                                         uint16_t buffer_len,
                                         uint32_t last,
                                         uint32_t avg,
                                         uint32_t max,
                                         uint8_t has_value)
{
    if (buffer == 0 || buffer_len == 0U)
    {
        return;
    }

    if (has_value == 0U)
    {
        snprintf(buffer, buffer_len, "%s", "-/-/-");
        return;
    }

    snprintf(buffer,
             buffer_len,
             "%lu/%lu/%lu",
             (unsigned long)last,
             (unsigned long)avg,
             (unsigned long)max);
}

/* 将 32 位数值格式化为十六进制文本。 */
static void perf_baseline_format_hex32(char *buffer, uint16_t buffer_len, uint32_t value)
{
    if (buffer == 0 || buffer_len == 0U)
    {
        return;
    }

    snprintf(buffer, buffer_len, "0x%lX", (unsigned long)value);
}

/* 将 LCD DMA 状态枚举转换为页面显示文本。 */
static const char *perf_baseline_dma_status_text(uint8_t status)
{
    switch ((app_perf_lcd_dma_status_t)status)
    {
    case APP_PERF_LCD_DMA_STATUS_OK:
        return "OK";
    case APP_PERF_LCD_DMA_STATUS_ERROR:
        return "ERR";
    case APP_PERF_LCD_DMA_STATUS_TIMEOUT:
    case APP_PERF_LCD_DMA_STATUS_NONE:
    default:
        return "WAIT";
    }
}

/* 绘制性能基线快照页。 */
static void perf_baseline_draw_snapshot(const app_perf_baseline_snapshot_t *snapshot)
{
    char value[24];
    char footer1[32];
    char footer2[32];
    uint8_t has_frame = 0U;

    if (snapshot == 0)
    {
        return;
    }

    has_frame = (snapshot->thermal_capture_frames != 0U) ? 1U : 0U;

    snprintf(value, sizeof(value), "%lu", (unsigned long)snapshot->thermal_fps);
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 0U), value, DARKBLUE);

    perf_baseline_format_temp(value, sizeof(value), snapshot->latest_min_temp, has_frame);
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 1U), value, BLUE);

    perf_baseline_format_temp(value, sizeof(value), snapshot->latest_max_temp, has_frame);
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 2U), value, RED);

    perf_baseline_format_temp(value, sizeof(value), snapshot->latest_center_temp, has_frame);
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 3U), value, GREEN);

    snprintf(footer1,
             sizeof(footer1),
             "Cap/Disp %lu/%lu",
             (unsigned long)snapshot->thermal_capture_frames,
             (unsigned long)snapshot->thermal_display_frames);
    snprintf(footer2,
             sizeof(footer2),
             "Fail:%lu DMA:%s",
             (unsigned long)snapshot->thermal_capture_failures,
             perf_baseline_dma_status_text(snapshot->last_dma_status));
    perf_baseline_draw_footer_text(footer1, footer2);
}

/* 绘制性能基线时序统计页。 */
static void perf_baseline_draw_timing(const app_perf_baseline_snapshot_t *snapshot)
{
    char value[24];
    char footer1[32];
    char footer2[32];

    if (snapshot == 0)
    {
        return;
    }

    perf_baseline_format_triplet(value,
                                 sizeof(value),
                                 snapshot->thermal_frame_period_last_ms,
                                 snapshot->thermal_frame_period_avg_ms,
                                 snapshot->thermal_frame_period_max_ms,
                                 (snapshot->thermal_frame_period_samples != 0U) ? 1U : 0U);
    perf_baseline_draw_timing_value_text(page_list_item_y(UI_CONTENT_TOP, 0U), value, DARKBLUE);

    perf_baseline_format_triplet(value,
                                 sizeof(value),
                                 snapshot->get_temp_last_us,
                                 snapshot->get_temp_avg_us,
                                 snapshot->get_temp_max_us,
                                 (snapshot->get_temp_samples != 0U) ? 1U : 0U);
    perf_baseline_draw_timing_value_text(page_list_item_y(UI_CONTENT_TOP, 1U), value, DARKBLUE);

    perf_baseline_format_triplet(value,
                                 sizeof(value),
                                 snapshot->gray_last_us,
                                 snapshot->gray_avg_us,
                                 snapshot->gray_max_us,
                                 (snapshot->gray_samples != 0U) ? 1U : 0U);
    perf_baseline_draw_timing_value_text(page_list_item_y(UI_CONTENT_TOP, 2U), value, DARKBLUE);

    perf_baseline_format_triplet(value,
                                 sizeof(value),
                                 snapshot->lcd_dma_last_us,
                                 snapshot->lcd_dma_avg_us,
                                 snapshot->lcd_dma_max_us,
                                 (snapshot->lcd_dma_samples != 0U) ? 1U : 0U);
    perf_baseline_draw_timing_value_text(page_list_item_y(UI_CONTENT_TOP, 3U), value, DARKBLUE);

    snprintf(footer1,
             sizeof(footer1),
             "Power:%s",
             ui_renderer_power_state_text(snapshot->power_state));
    snprintf(footer2,
             sizeof(footer2),
             "Clock:%s",
             ui_renderer_clock_profile_text(snapshot->clock_profile));
    perf_baseline_draw_footer_text(footer1, footer2);
}

/* 绘制性能基线计数器页。 */
static void perf_baseline_draw_counters(const app_perf_baseline_snapshot_t *snapshot)
{
    char value[24];
    char footer1[64];
    char footer2[64];

    if (snapshot == 0)
    {
        return;
    }

    snprintf(value, sizeof(value), "%lu", (unsigned long)snapshot->key_queue_drop_count);
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 0U),
                                  value,
                                  (snapshot->key_queue_drop_count != 0U) ? RED : DARKBLUE);

    snprintf(value, sizeof(value), "%lu", (unsigned long)snapshot->ui_msg_drop_count);
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 1U),
                                  value,
                                  (snapshot->ui_msg_drop_count != 0U) ? RED : DARKBLUE);

    snprintf(value, sizeof(value), "%lu", (unsigned long)snapshot->service_queue_fail_count);
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 2U),
                                  value,
                                  (snapshot->service_queue_fail_count != 0U) ? RED : DARKBLUE);

    snprintf(value, sizeof(value), "%lu", (unsigned long)snapshot->uart_error_count);
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 3U),
                                  value,
                                  (snapshot->uart_error_count != 0U) ? RED : DARKBLUE);

    #if REDPIC1_THERMAL_STAGE3D_ENABLE
    snprintf(footer1,
             sizeof(footer1),
             "DT:%lu I2:%lu B:%lu DE:%lu T:%lu/%lu/%lu",
             (unsigned long)snapshot->dma_timeout_count,
             (unsigned long)snapshot->i2c_failure_count,
             (unsigned long)snapshot->thermal_backoff_count,
             (unsigned long)snapshot->lcd_dma_enter_count,
             (unsigned long)snapshot->dma_irq_tc_count,
             (unsigned long)snapshot->dma_irq_te_count,
             (unsigned long)snapshot->dma_wait_take_count);
    snprintf(footer2,
             sizeof(footer2),
             "3D:%lu/%lu/%lu C:%lu D:%lu/%lu/%lu W:%lu",
             (unsigned long)snapshot->thermal_3d_sync_present_attempt_count,
             (unsigned long)snapshot->thermal_3d_sync_present_ok_count,
             (unsigned long)snapshot->thermal_3d_sync_present_fail_count,
             (unsigned long)snapshot->thermal_3d_claim_count,
             (unsigned long)snapshot->thermal_3d_done_ok_count,
             (unsigned long)snapshot->thermal_3d_done_error_count,
             (unsigned long)snapshot->thermal_3d_done_cancel_count,
             (unsigned long)snapshot->thermal_3d_wait_timeout_count);
    #else
    snprintf(footer1,
             sizeof(footer1),
             "N:%lu/%lu/%lu/%lu DQ:%lu",
             (unsigned long)snapshot->input_notify_count,
             (unsigned long)snapshot->ui_notify_count,
             (unsigned long)snapshot->service_notify_count,
             (unsigned long)snapshot->display_notify_count,
             (unsigned long)snapshot->display_queue_fail_count);
    snprintf(footer2,
             sizeof(footer2),
             "DT:%lu I2:%lu B:%lu R:%lu C:%lu",
             (unsigned long)snapshot->dma_timeout_count,
             (unsigned long)snapshot->i2c_failure_count,
             (unsigned long)snapshot->thermal_backoff_count,
             (unsigned long)snapshot->thermal_ready_replace_count,
             (unsigned long)snapshot->thermal_display_cancel_count);
    #endif
    perf_baseline_draw_footer_text(footer1, footer2);
}

/* 绘制性能基线健康状态页。 */
static void perf_baseline_draw_health(const app_perf_baseline_snapshot_t *snapshot)
{
    char value[24];
    char footer1[48];
    char footer2[48];

    if (snapshot == 0)
    {
        return;
    }

    perf_baseline_format_hex32(value, sizeof(value), snapshot->watchdog_fault_flags);
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 0U),
                                  value,
                                  (snapshot->watchdog_fault_flags != 0U) ? RED : DARKBLUE);

    perf_baseline_format_hex32(value, sizeof(value), snapshot->watchdog_missing_progress_mask);
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 1U),
                                  value,
                                  (snapshot->watchdog_missing_progress_mask != 0U) ? RED : DARKBLUE);

    snprintf(value, sizeof(value), "%s", (snapshot->thermal_active != 0U) ? "ON" : "OFF");
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 2U),
                                  value,
                                  (snapshot->thermal_active != 0U) ? GREEN : GRAYBLUE);

    snprintf(value, sizeof(value), "%s", (snapshot->screen_off != 0U) ? "OFF" : "ON");
    perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 3U),
                                  value,
                                  (snapshot->screen_off != 0U) ? BROWN : GREEN);

    snprintf(footer1,
             sizeof(footer1),
             "I:%lu S:%lu U:%lu",
             (unsigned long)snapshot->input_stack_words,
             (unsigned long)snapshot->service_stack_words,
             (unsigned long)snapshot->ui_stack_words);
    snprintf(footer2,
             sizeof(footer2),
             "D:%lu T:%lu P:%lu",
             (unsigned long)snapshot->display_stack_words,
             (unsigned long)snapshot->thermal_stack_words,
             (unsigned long)snapshot->power_stack_words);
    perf_baseline_draw_footer_text(footer1, footer2);
}

/* 性能基线页面进入回调，校验可见性并初始化子页状态。 */
static void perf_baseline_on_enter(ui_page_id_t previous_page)
{
    (void)previous_page;

    if (perf_baseline_debug_visible() == 0U)
    {
        ui_manager_navigate_home();
        return;
    }

    s_perf_baseline_subpage = 0U;
    s_perf_baseline_next_refresh_ms = 0U;
    ui_manager_force_full_refresh();
}

/* 性能基线页面离开回调，清理下一次刷新时间。 */
static void perf_baseline_on_leave(ui_page_id_t next_page)
{
    (void)next_page;
    s_perf_baseline_next_refresh_ms = 0U;
}

/* 处理性能基线页面按键：翻页、清零或返回首页。 */
static void perf_baseline_on_key(uint8_t key_value)
{
    if (key_value == KEY1_PRES)
    {
        s_perf_baseline_subpage = page_cycle_prev_index(s_perf_baseline_subpage, PERF_SUBPAGE_COUNT);
        ui_manager_force_full_refresh();
    }
    else if (key_value == KEY3_PRES)
    {
        s_perf_baseline_subpage = page_cycle_next_index(s_perf_baseline_subpage, PERF_SUBPAGE_COUNT);
        ui_manager_force_full_refresh();
    }
    else if (key_value == KEY2_PRES)
    {
        app_perf_baseline_reset();
        ui_manager_force_full_refresh();
    }
    else if (key_value == UI_KEY_KEY2_LONG)
    {
        ui_manager_navigate_home();
    }
}

/* 性能基线页面周期回调，按节流周期触发刷新。 */
static void perf_baseline_on_tick(void)
{
    uint32_t now_ms = 0U;

    if (perf_baseline_debug_visible() == 0U)
    {
        ui_manager_navigate_home();
        return;
    }

    now_ms = power_manager_get_tick_ms();
    if (s_perf_baseline_next_refresh_ms == 0U ||
        now_ms >= s_perf_baseline_next_refresh_ms)
    {
        s_perf_baseline_next_refresh_ms = now_ms + PERF_BASELINE_REFRESH_MS;
        ui_manager_request_render();
    }
}

/* 根据当前子页绘制性能基线页面内容。 */
static void perf_baseline_render(uint8_t full_refresh)
{
    app_perf_baseline_snapshot_t snapshot;

    app_perf_baseline_get_snapshot(&snapshot);

    if (full_refresh != 0U)
    {
        ui_renderer_draw_header_path("System", "Debug Page", GRAYBLUE);
        ui_renderer_clear_body(WHITE);
        perf_baseline_draw_layout(snapshot.enabled);
    }

    if (snapshot.enabled == 0U)
    {
        perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 0U), "DISABLED", RED);
        perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 1U), "APP_PERF=0", DARKBLUE);
        perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 2U), "Enable build", DARKBLUE);
        perf_baseline_draw_value_text(page_list_item_y(UI_CONTENT_TOP, 3U), "Screen only", DARKBLUE);
        perf_baseline_draw_footer_text("Perf baseline off", "Screen only");
        return;
    }

    switch (s_perf_baseline_subpage)
    {
    case 1U:
        perf_baseline_draw_timing(&snapshot);
        break;
    case 2U:
        perf_baseline_draw_counters(&snapshot);
        break;
    case 3U:
        perf_baseline_draw_health(&snapshot);
        break;
    case 0U:
    default:
        perf_baseline_draw_snapshot(&snapshot);
        break;
    }
}

/* 根据页面编号返回页面回调表。 */
const ui_page_ops_t *page_registry_get_ops(ui_page_id_t page_id)
{
    if (page_id >= UI_PAGE_COUNT)
    {
        return 0;
    }

    return &s_page_ops[page_id];
}

/* 返回指定页面的逻辑父页面。 */
ui_page_id_t page_registry_get_parent(ui_page_id_t page_id)
{
    switch (page_id)
    {
    case UI_PAGE_PERF_BASELINE:
        return UI_PAGE_SYSTEM;
    case UI_PAGE_ENGINEERING:
        return UI_PAGE_SYSTEM;
    case UI_PAGE_HOME:
        return UI_PAGE_HOME;
    default:
        return UI_PAGE_HOME;
    }
}

/* 将服务响应回流给页面内部处理逻辑。 */
void page_registry_on_service_response(const app_service_rsp_t *rsp)
{
    page_handle_service_response(rsp);
}
