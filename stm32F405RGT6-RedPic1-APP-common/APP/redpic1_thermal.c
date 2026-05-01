#include "redpic1_thermal.h"

#include <stdio.h>
#include <string.h>

#include "FreeRTOS.h"
#include "semphr.h"
#include "task.h"

#include "app_display_runtime.h"
#include "app_perf_baseline.h"
#include "delay.h"
#include "key.h"
#include "lcd.h"
#include "lcd_utf8.h"
#include "lcd_init.h"
#include "power_manager.h"
#include "MLX90640_I2C_Driver.h"
#include "MLX90640_API.h"
#include "MLX90640.h"
#include "lcd_dma.h"

uint8_t displayPaused = 0U;

#define REDPIC1_THERMAL_ACTIVE_REFRESH_RATE            RefreshRate
#define REDPIC1_THERMAL_IDLE_REFRESH_RATE              FPS1HZ
#define REDPIC1_THERMAL_SRC_ROWS                       24U
#define REDPIC1_THERMAL_SRC_COLS                       32U
#define REDPIC1_THERMAL_PIXEL_COUNT                    768U
#define REDPIC1_THERMAL_VALID_TEMP_MIN_C               (-40.0f)
#define REDPIC1_THERMAL_VALID_TEMP_MAX_C               (300.0f)
#define REDPIC1_THERMAL_VALID_MIN_SPAN_C               (0.5f)
#define REDPIC1_THERMAL_BACKOFF_MS                     20UL
#define REDPIC1_THERMAL_RESTORE_THRESHOLD              3U
#define REDPIC1_THERMAL_OVERLAY_BAR_HEIGHT             20U
#define REDPIC1_THERMAL_VIEWPORT_HEIGHT                (LCD_H - REDPIC1_THERMAL_OVERLAY_BAR_HEIGHT)
#define REDPIC1_THERMAL_OVERLAY_BAR_TEXT_Y_OFFSET      2U
#define REDPIC1_THERMAL_OVERLAY_BAR_TEXT_X             4U
#define REDPIC1_THERMAL_OVERLAY_CROSS_HALF_SIZE        6U
#define REDPIC1_THERMAL_OVERLAY_BAR_REFRESH_MS         250UL
#define REDPIC1_THERMAL_SLOT_INDEX_NONE                0xFFU
#define REDPIC1_THERMAL_TOKEN_SHIFT                    8U
#define REDPIC1_THERMAL_TOKEN_SLOT_MASK                0xFFU
#define REDPIC1_THERMAL_PRESENT_WAIT_TIMEOUT_MS        1000UL
#define REDPIC1_THERMAL_DISPLAY_WINDOW_MIN_SPAN_C      1.5f
#define REDPIC1_THERMAL_DISPLAY_WINDOW_EMA_ALPHA       0.25f
#define REDPIC1_THERMAL_DISPLAY_WINDOW_MAX_STEP_C      0.75f

#if REDPIC1_THERMAL_STAGE3D_ENABLE && \
    (REDPIC1_THERMAL_STAGE3D_BRINGUP_SERIALIZE == 0) && \
    (REDPIC1_THERMAL_STAGE3D_3A_ENABLE != 0)
    #define REDPIC1_THERMAL_STAGE3D_ASYNC_ACTIVE 1
#else
    #define REDPIC1_THERMAL_STAGE3D_ASYNC_ACTIVE 0
#endif

#if REDPIC1_THERMAL_STAGE6V_ENABLE && \
    REDPIC1_THERMAL_STAGE6V_2_ENABLE && \
    REDPIC1_THERMAL_STAGE3D_ASYNC_ACTIVE
    #define REDPIC1_THERMAL_STAGE6V_2_ACTIVE 1
#else
    #define REDPIC1_THERMAL_STAGE6V_2_ACTIVE 0
#endif

#if REDPIC1_THERMAL_STAGE6V_ENABLE && REDPIC1_THERMAL_STAGE6V_3_ENABLE
    #define REDPIC1_THERMAL_STAGE6V_3_ACTIVE 1
#else
    #define REDPIC1_THERMAL_STAGE6V_3_ACTIVE 0
#endif

#if REDPIC1_THERMAL_STAGE6V_ENABLE && REDPIC1_THERMAL_STAGE6V_4_ENABLE
    #define REDPIC1_THERMAL_STAGE6V_4_ACTIVE 1
#else
    #define REDPIC1_THERMAL_STAGE6V_4_ACTIVE 0
#endif

#if REDPIC1_THERMAL_STAGE3D_ENABLE
    #define REDPIC1_THERMAL_SLOT_COUNT 3U
#else
    #define REDPIC1_THERMAL_SLOT_COUNT 2U
#endif

#if REDPIC1_THERMAL_STAGE2_ENABLE
typedef enum
{
    REDPIC1_THERMAL_FRAME_SLOT_FREE = 0,
    REDPIC1_THERMAL_FRAME_SLOT_WRITING,
    REDPIC1_THERMAL_FRAME_SLOT_READY,
    REDPIC1_THERMAL_FRAME_SLOT_INFLIGHT,
    REDPIC1_THERMAL_FRAME_SLOT_FRONT
} redpic1_thermal_frame_slot_state_t;

typedef struct
{
    float temp_frame[REDPIC1_THERMAL_PIXEL_COUNT];
    uint8_t gray_frame[REDPIC1_THERMAL_PIXEL_COUNT];
    float min_temp;
    float max_temp;
    float center_temp;
    uint32_t capture_tick_ms;
    uint32_t frame_seq;
    uint8_t valid;
    redpic1_thermal_frame_slot_state_t slot_state;
} redpic1_thermal_frame_slot_t;

/* Keep thermal frame slots in system SRAM.
 * The 3D pipeline grows slot lifetime and footprint, and system SRAM gives us
 * enough headroom without reintroducing CCM-related bring-up risk. */
static redpic1_thermal_frame_slot_t s_frame_slots[REDPIC1_THERMAL_SLOT_COUNT];
static CCMRAM uint8_t s_diag_pattern_frame[REDPIC1_THERMAL_PIXEL_COUNT];
static uint32_t s_frame_sequence = 0U;
static uint32_t s_backoff_until_ms = 0U;
static uint8_t s_restore_bus_pending = 0U;
static uint8_t s_consecutive_transport_failures = 0U;

#if REDPIC1_THERMAL_STAGE3D_ENABLE
static uint8_t s_front_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
static uint8_t s_ready_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
static uint8_t s_inflight_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
static uintptr_t s_last_submitted_token = 0U;
static uint8_t s_last_submitted_valid = 0U;
typedef struct
{
    uintptr_t token;
    app_display_thermal_done_status_t done_status;
    uint8_t waiting;
    uint8_t done;
} redpic1_thermal_present_wait_ctx_t;
static redpic1_thermal_present_wait_ctx_t s_present_wait_ctx;
static SemaphoreHandle_t s_present_wait_sem = 0;
#else
static uint8_t s_front_slot_index = 0U;
static uint8_t s_back_slot_index = 1U;
#endif
#else
static CCMRAM float s_tempFrame[REDPIC1_THERMAL_PIXEL_COUNT];
static CCMRAM uint8_t s_grayFrame[REDPIC1_THERMAL_PIXEL_COUNT];
#endif

static uint8_t s_overlayHold = 0U;
static uint8_t s_frameReady = 0U;
static uint8_t s_colorMode = 0U;
static uint8_t s_runEnabled = 1U;
static uint8_t s_refreshRate = REDPIC1_THERMAL_ACTIVE_REFRESH_RATE;
static uint8_t s_diag_pattern_ready = 0U;
static uint8_t s_runtime_overlay_visible = 1U;
static char s_overlay_bar_last_line[64];
static char s_overlay_bar_pending_line[64];
static uint32_t s_overlay_bar_last_refresh_ms = 0U;
static uint8_t s_overlay_bar_last_visible = 0U;
static uint8_t s_overlay_bar_last_line_valid = 0U;
static uint8_t s_overlay_bar_pending_dirty = 1U;
#if REDPIC1_THERMAL_STAGE6V_3_ACTIVE
static float s_display_min_temp = 0.0f;
static float s_display_max_temp = 0.0f;
static uint8_t s_display_window_valid = 0U;
#endif
#if REDPIC1_THERMAL_STAGE6V_4_ACTIVE
static CCMRAM float s_previous_filtered_temp_frame[REDPIC1_THERMAL_PIXEL_COUNT];
static CCMRAM float s_current_visual_temp_frame[REDPIC1_THERMAL_PIXEL_COUNT];
static uint8_t s_filter_history_valid = 0U;
#endif

static uint8_t redpic1_thermal_scheduler_running(void)
{
    return (xTaskGetSchedulerState() == taskSCHEDULER_RUNNING) ? 1U : 0U;
}

static void redpic1_thermal_enter_critical(void)
{
    if (redpic1_thermal_scheduler_running() != 0U)
    {
        taskENTER_CRITICAL();
    }
}

static void redpic1_thermal_exit_critical(void)
{
    if (redpic1_thermal_scheduler_running() != 0U)
    {
        taskEXIT_CRITICAL();
    }
}

static uint8_t redpic1_thermal_temp_to_gray(float temp, float min_temp, float scale)
{
    int32_t gray_value = (int32_t)((temp - min_temp) * scale);

    if (gray_value < 0)
    {
        return 0U;
    }
    if (gray_value > 255)
    {
        return 255U;
    }

    return (uint8_t)gray_value;
}

static void redpic1_thermal_format_overlay_temp(char *buffer,
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
        snprintf(buffer, buffer_len, "%s", "--.-");
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

    snprintf(buffer, buffer_len, "%ld.%ld", (long)whole, (long)frac);
}

static void redpic1_thermal_build_bottom_bar_line(char *line_text, uint16_t line_text_len)
{
    app_perf_baseline_snapshot_t snapshot;
    char min_text[12];
    char max_text[12];
    char center_text[12];
    uint8_t has_value = 0U;

    if (line_text == 0 || line_text_len == 0U)
    {
        return;
    }

    app_perf_baseline_get_snapshot(&snapshot);
    has_value = (snapshot.thermal_capture_frames != 0U) ? 1U : 0U;

    redpic1_thermal_format_overlay_temp(min_text,
                                        sizeof(min_text),
                                        snapshot.latest_min_temp,
                                        has_value);
    redpic1_thermal_format_overlay_temp(max_text,
                                        sizeof(max_text),
                                        snapshot.latest_max_temp,
                                        has_value);
    redpic1_thermal_format_overlay_temp(center_text,
                                        sizeof(center_text),
                                        snapshot.latest_center_temp,
                                        has_value);

    snprintf(line_text,
             line_text_len,
             "FPS:%lu  "
             "\xE6\x9C\x80\xE4\xBD\x8E:%s  "
             "\xE6\x9C\x80\xE9\xAB\x98:%s  "
             "\xE4\xB8\xAD\xE5\xBF\x83:%s",
             (unsigned long)snapshot.thermal_display_fps,
             min_text,
             max_text,
             center_text);

}

static void redpic1_thermal_draw_bottom_bar_line(const char *line_text)
{
    uint16_t bar_top = 0U;

    if (line_text == 0)
    {
        return;
    }

    if (LCD_H > REDPIC1_THERMAL_OVERLAY_BAR_HEIGHT)
    {
        bar_top = (uint16_t)(LCD_H - REDPIC1_THERMAL_OVERLAY_BAR_HEIGHT);
    }

    LCD_Fill(0U, bar_top, (uint16_t)(LCD_W - 1U), (uint16_t)(LCD_H - 1U), BLACK);
    if (bar_top > 0U)
    {
        LCD_DrawLine(0U,
                     (uint16_t)(bar_top - 1U),
                     (uint16_t)(LCD_W - 1U),
                     (uint16_t)(bar_top - 1U),
                     WHITE);
    }
    LCD_ShowUTF8String(REDPIC1_THERMAL_OVERLAY_BAR_TEXT_X,
                       (uint16_t)(bar_top + REDPIC1_THERMAL_OVERLAY_BAR_TEXT_Y_OFFSET),
                       line_text,
                       YELLOW,
                       BLACK,
                       16,
                       0);
}

static void redpic1_thermal_clear_bottom_bar(void)
{
    uint16_t bar_top = 0U;
    uint16_t clear_top = 0U;

    if (LCD_H > REDPIC1_THERMAL_OVERLAY_BAR_HEIGHT)
    {
        bar_top = (uint16_t)(LCD_H - REDPIC1_THERMAL_OVERLAY_BAR_HEIGHT);
    }

    clear_top = (bar_top > 0U) ? (uint16_t)(bar_top - 1U) : bar_top;
    LCD_Fill(0U, clear_top, (uint16_t)(LCD_W - 1U), (uint16_t)(LCD_H - 1U), BLACK);
}

static void redpic1_thermal_reset_bottom_bar_cache(void)
{
    s_overlay_bar_last_line[0] = '\0';
    s_overlay_bar_pending_line[0] = '\0';
    s_overlay_bar_last_refresh_ms = 0U;
    s_overlay_bar_last_visible = 0U;
    s_overlay_bar_last_line_valid = 0U;
    s_overlay_bar_pending_dirty = 1U;
}

static float redpic1_thermal_center_temp(const float *frame_data)
{
    uint16_t center_row = REDPIC1_THERMAL_SRC_ROWS / 2U;
    uint16_t center_col = REDPIC1_THERMAL_SRC_COLS / 2U;

    if (frame_data == 0)
    {
        return 0.0f;
    }

    return frame_data[(center_row * REDPIC1_THERMAL_SRC_COLS) + center_col];
}

#if REDPIC1_THERMAL_STAGE6V_3_ACTIVE
static void redpic1_thermal_reset_display_window_state(void)
{
    s_display_min_temp = 0.0f;
    s_display_max_temp = 0.0f;
    s_display_window_valid = 0U;
}

static float redpic1_thermal_limit_display_window_step(float current_value, float target_value)
{
    float delta = target_value - current_value;

    if (delta > REDPIC1_THERMAL_DISPLAY_WINDOW_MAX_STEP_C)
    {
        delta = REDPIC1_THERMAL_DISPLAY_WINDOW_MAX_STEP_C;
    }
    else if (delta < -REDPIC1_THERMAL_DISPLAY_WINDOW_MAX_STEP_C)
    {
        delta = -REDPIC1_THERMAL_DISPLAY_WINDOW_MAX_STEP_C;
    }

    return current_value + delta;
}

static void redpic1_thermal_enforce_display_window_min_span(float *window_min_temp,
                                                            float *window_max_temp)
{
    float center_temp = 0.0f;
    float half_span = REDPIC1_THERMAL_DISPLAY_WINDOW_MIN_SPAN_C * 0.5f;

    if (window_min_temp == 0 || window_max_temp == 0)
    {
        return;
    }

    if ((*window_max_temp - *window_min_temp) >= REDPIC1_THERMAL_DISPLAY_WINDOW_MIN_SPAN_C)
    {
        return;
    }

    center_temp = (*window_min_temp + *window_max_temp) * 0.5f;
    *window_min_temp = center_temp - half_span;
    *window_max_temp = center_temp + half_span;
}

static void redpic1_thermal_get_display_window(float raw_min_temp,
                                               float raw_max_temp,
                                               float *out_display_min_temp,
                                               float *out_display_max_temp)
{
    float target_min_temp = raw_min_temp;
    float target_max_temp = raw_max_temp;

    if ((target_max_temp - target_min_temp) < REDPIC1_THERMAL_DISPLAY_WINDOW_MIN_SPAN_C)
    {
        float target_center_temp = (target_min_temp + target_max_temp) * 0.5f;
        float target_half_span = REDPIC1_THERMAL_DISPLAY_WINDOW_MIN_SPAN_C * 0.5f;

        target_min_temp = target_center_temp - target_half_span;
        target_max_temp = target_center_temp + target_half_span;
    }

    if (s_display_window_valid == 0U)
    {
        s_display_min_temp = target_min_temp;
        s_display_max_temp = target_max_temp;
        s_display_window_valid = 1U;
    }
    else
    {
        float ema_min_temp = s_display_min_temp +
                             ((target_min_temp - s_display_min_temp) *
                              REDPIC1_THERMAL_DISPLAY_WINDOW_EMA_ALPHA);
        float ema_max_temp = s_display_max_temp +
                             ((target_max_temp - s_display_max_temp) *
                              REDPIC1_THERMAL_DISPLAY_WINDOW_EMA_ALPHA);

        s_display_min_temp =
            redpic1_thermal_limit_display_window_step(s_display_min_temp, ema_min_temp);
        s_display_max_temp =
            redpic1_thermal_limit_display_window_step(s_display_max_temp, ema_max_temp);
        redpic1_thermal_enforce_display_window_min_span(&s_display_min_temp,
                                                        &s_display_max_temp);

        if (s_display_max_temp <= s_display_min_temp)
        {
            s_display_min_temp = target_min_temp;
            s_display_max_temp = target_max_temp;
        }
    }

    if (out_display_min_temp != 0)
    {
        *out_display_min_temp = s_display_min_temp;
    }
    if (out_display_max_temp != 0)
    {
        *out_display_max_temp = s_display_max_temp;
    }
}
#endif

#if REDPIC1_THERMAL_STAGE6V_4_ACTIVE
static void redpic1_thermal_reset_visual_filter_state(void)
{
    s_filter_history_valid = 0U;
}

static const float *redpic1_thermal_get_visual_frame(const float *raw_frame_data)
{
    uint16_t i = 0U;

    if (raw_frame_data == 0)
    {
        return 0;
    }

    if (s_filter_history_valid == 0U)
    {
        for (i = 0U; i < REDPIC1_THERMAL_PIXEL_COUNT; ++i)
        {
            s_current_visual_temp_frame[i] = raw_frame_data[i];
            s_previous_filtered_temp_frame[i] = raw_frame_data[i];
        }
        s_filter_history_valid = 1U;
        return s_current_visual_temp_frame;
    }

    for (i = 0U; i < REDPIC1_THERMAL_PIXEL_COUNT; ++i)
    {
        float raw_temp = raw_frame_data[i];
        float prev_temp = s_previous_filtered_temp_frame[i];
        float delta = raw_temp - prev_temp;
        float abs_delta = delta;
        float current_weight = 1.0f;
        float filtered_temp = 0.0f;

        if (abs_delta < 0.0f)
        {
            abs_delta = -abs_delta;
        }

        if (abs_delta <= 0.20f)
        {
            current_weight = 0.40f;
        }
        else if (abs_delta < 1.00f)
        {
            current_weight = 0.40f + (((abs_delta - 0.20f) / 0.80f) * 0.60f);
        }

        filtered_temp = prev_temp + ((raw_temp - prev_temp) * current_weight);
        s_current_visual_temp_frame[i] = filtered_temp;
        s_previous_filtered_temp_frame[i] = filtered_temp;
    }

    return s_current_visual_temp_frame;
}

static const float *redpic1_thermal_get_gray_source_frame(const float *raw_frame_data)
{
    const float *visual_frame = redpic1_thermal_get_visual_frame(raw_frame_data);

    return (visual_frame != 0) ? visual_frame : raw_frame_data;
}
#endif

static void redpic1_thermal_prepare_gray_frame(const float *raw_frame_data,
                                               const float *display_frame_data,
                                               uint8_t *gray_frame,
                                               float *out_min_temp,
                                               float *out_max_temp)
{
    float raw_min_temp = 300.0f;
    float raw_max_temp = -40.0f;
    float display_min_temp = 0.0f;
    float display_max_temp = 0.0f;
    float scale = 0.0f;
    uint16_t i = 0U;

    if (raw_frame_data == 0 || display_frame_data == 0 || gray_frame == 0)
    {
        return;
    }

    for (i = 0U; i < REDPIC1_THERMAL_PIXEL_COUNT; ++i)
    {
        float temp = raw_frame_data[i];

        if (temp > raw_max_temp)
        {
            raw_max_temp = temp;
        }
        if (temp < raw_min_temp)
        {
            raw_min_temp = temp;
        }
    }

    if (raw_max_temp <= raw_min_temp)
    {
        for (i = 0U; i < REDPIC1_THERMAL_PIXEL_COUNT; ++i)
        {
            gray_frame[i] = 0U;
        }
        if (out_min_temp != 0)
        {
            *out_min_temp = raw_min_temp;
        }
        if (out_max_temp != 0)
        {
            *out_max_temp = raw_max_temp;
        }
        return;
    }

#if REDPIC1_THERMAL_STAGE6V_3_ACTIVE
    redpic1_thermal_get_display_window(raw_min_temp,
                                       raw_max_temp,
                                       &display_min_temp,
                                       &display_max_temp);
#else
    display_min_temp = raw_min_temp;
    display_max_temp = raw_max_temp;
#endif

    scale = 255.0f / (display_max_temp - display_min_temp);

#if REDPIC1_THERMAL_STAGE6_ENABLE && REDPIC1_THERMAL_STAGE6_6A_ENABLE
    for (uint16_t src_row = 0U; src_row < REDPIC1_THERMAL_SRC_ROWS; ++src_row)
    {
        const float *src = display_frame_data + ((uint32_t)src_row * REDPIC1_THERMAL_SRC_COLS);
        uint8_t *dst = gray_frame + src_row;

        for (uint16_t src_col = 0U; src_col < REDPIC1_THERMAL_SRC_COLS; ++src_col)
        {
            int32_t gray_value = (int32_t)(((*src++) - display_min_temp) * scale);

            if (gray_value < 0)
            {
                gray_value = 0;
            }
            else if (gray_value > 255)
            {
                gray_value = 255;
            }

            *dst = (uint8_t)gray_value;
            dst += REDPIC1_THERMAL_SRC_ROWS;
        }
    }
#else
    for (uint16_t src_row = 0U; src_row < REDPIC1_THERMAL_SRC_ROWS; ++src_row)
    {
        uint16_t src_base = (uint16_t)(src_row * REDPIC1_THERMAL_SRC_COLS);

        for (uint16_t src_col = 0U; src_col < REDPIC1_THERMAL_SRC_COLS; ++src_col)
        {
            uint16_t dst_index = (uint16_t)(src_col * REDPIC1_THERMAL_SRC_ROWS + src_row);
            gray_frame[dst_index] =
                redpic1_thermal_temp_to_gray(display_frame_data[src_base + src_col],
                                             display_min_temp,
                                             scale);
        }
    }
#endif

    if (out_min_temp != 0)
    {
        *out_min_temp = raw_min_temp;
    }
    if (out_max_temp != 0)
    {
        *out_max_temp = raw_max_temp;
    }
}

static uint32_t redpic1_thermal_refresh_rate_to_period_ms(uint8_t refresh_rate)
{
    switch (refresh_rate)
    {
    case FPS1HZ:
        return 1000UL;
    case FPS2HZ:
        return 500UL;
    case FPS4HZ:
        return 250UL;
    case FPS8HZ:
        return 125UL;
    case FPS16HZ:
        return 63UL;
    case FPS32HZ:
        return 32UL;
    default:
        return 63UL;
    }
}

static void redpic1_thermal_apply_refresh_rate_internal(uint8_t refresh_rate, uint8_t force_write)
{
    if (force_write == 0U && s_refreshRate == refresh_rate)
    {
        return;
    }

    if (MLX90640_SetRefreshRate(MLX90640_ADDR, refresh_rate) == 0)
    {
        s_refreshRate = refresh_rate;
    }
}

static void redpic1_thermal_apply_refresh_rate(uint8_t refresh_rate)
{
    redpic1_thermal_apply_refresh_rate_internal(refresh_rate, 0U);
}

#if REDPIC1_THERMAL_STAGE2_ENABLE
static void redpic1_thermal_build_diag_pattern(void)
{
    uint16_t row = 0U;

    for (row = 0U; row < REDPIC1_THERMAL_SRC_COLS; ++row)
    {
        uint16_t col = 0U;

        for (col = 0U; col < REDPIC1_THERMAL_SRC_ROWS; ++col)
        {
            uint16_t index = (uint16_t)(row * REDPIC1_THERMAL_SRC_ROWS + col);
            uint8_t gray = (uint8_t)((col * 255U) / (REDPIC1_THERMAL_SRC_ROWS - 1U));

            if ((row & 0x04U) != 0U)
            {
                gray = (uint8_t)(255U - gray);
            }

            if (row == (REDPIC1_THERMAL_SRC_COLS / 2U) ||
                col == (REDPIC1_THERMAL_SRC_ROWS / 2U))
            {
                gray = 255U;
            }

            if (((row + col) & 0x07U) == 0U)
            {
                gray = 32U;
            }

            s_diag_pattern_frame[index] = gray;
        }
    }

    s_diag_pattern_ready = 1U;
}

static uint8_t redpic1_thermal_deadline_reached(uint32_t now_ms, uint32_t deadline_ms)
{
    return (((int32_t)(now_ms - deadline_ms)) >= 0) ? 1U : 0U;
}

static uint8_t redpic1_thermal_present_gray_frame(const uint8_t *gray_frame)
{
    uint8_t ok = 0U;

    if (gray_frame == 0 ||
        s_runEnabled == 0U ||
        displayPaused != 0U ||
        s_overlayHold != 0U)
    {
        return 0U;
    }

    power_manager_acquire_lock(POWER_LOCK_DISPLAY_DMA);
    ok = app_display_runtime_present_thermal_frame((uint8_t *)gray_frame);
    power_manager_release_lock(POWER_LOCK_DISPLAY_DMA);
    return ok;
}

static void redpic1_thermal_present_diag_pattern(void)
{
    if (s_diag_pattern_ready == 0U)
    {
        redpic1_thermal_build_diag_pattern();
    }

    (void)redpic1_thermal_present_gray_frame(s_diag_pattern_frame);
}

static uint8_t redpic1_thermal_temp_in_range(float temp)
{
    if (temp != temp)
    {
        return 0U;
    }

    if (temp < REDPIC1_THERMAL_VALID_TEMP_MIN_C)
    {
        return 0U;
    }
    if (temp > REDPIC1_THERMAL_VALID_TEMP_MAX_C)
    {
        return 0U;
    }

    return 1U;
}

static uint8_t redpic1_thermal_frame_data_is_valid(const float *frame_data)
{
    uint16_t i = 0U;

    if (frame_data == 0)
    {
        return 0U;
    }

    for (i = 0U; i < REDPIC1_THERMAL_PIXEL_COUNT; ++i)
    {
        if (redpic1_thermal_temp_in_range(frame_data[i]) == 0U)
        {
            return 0U;
        }
    }

    return 1U;
}

static uint8_t redpic1_thermal_frame_is_valid(float min_temp,
                                              float max_temp,
                                              float center_temp)
{
    if (redpic1_thermal_temp_in_range(min_temp) == 0U ||
        redpic1_thermal_temp_in_range(max_temp) == 0U ||
        redpic1_thermal_temp_in_range(center_temp) == 0U)
    {
        return 0U;
    }

    if (max_temp < min_temp)
    {
        return 0U;
    }

    if ((max_temp - min_temp) < REDPIC1_THERMAL_VALID_MIN_SPAN_C)
    {
        return 0U;
    }

    return 1U;
}

static uint8_t redpic1_thermal_gray_frame_has_contrast(const uint8_t *gray_frame)
{
    uint8_t gray_min = 255U;
    uint8_t gray_max = 0U;
    uint16_t i = 0U;

    if (gray_frame == 0)
    {
        return 0U;
    }

    for (i = 0U; i < REDPIC1_THERMAL_PIXEL_COUNT; ++i)
    {
        if (gray_frame[i] < gray_min)
        {
            gray_min = gray_frame[i];
        }
        if (gray_frame[i] > gray_max)
        {
            gray_max = gray_frame[i];
        }
    }

    return (gray_max > gray_min) ? 1U : 0U;
}

static void redpic1_thermal_note_backoff(uint8_t transport_related)
{
    app_perf_baseline_record_thermal_capture_failure();
    app_perf_baseline_record_thermal_backoff();
    s_backoff_until_ms = power_manager_get_tick_ms() + REDPIC1_THERMAL_BACKOFF_MS;

    if (transport_related != 0U)
    {
        s_consecutive_transport_failures++;
        if (s_consecutive_transport_failures >= REDPIC1_THERMAL_RESTORE_THRESHOLD)
        {
            s_restore_bus_pending = 1U;
        }
    }
    else
    {
        s_consecutive_transport_failures = 0U;
    }
}

static void redpic1_thermal_restore_bus_now(void)
{
    MLX90640_I2CInit();
    redpic1_thermal_apply_refresh_rate_internal(s_refreshRate, 1U);
    s_restore_bus_pending = 0U;
    s_consecutive_transport_failures = 0U;
}

#if REDPIC1_THERMAL_STAGE3D_ENABLE
static uintptr_t redpic1_thermal_make_slot_token(uint8_t slot_index, uint32_t frame_seq)
{
    return ((((uintptr_t)frame_seq) << REDPIC1_THERMAL_TOKEN_SHIFT) |
            (uintptr_t)slot_index);
}

static uint8_t redpic1_thermal_token_slot_index(uintptr_t token)
{
    return (uint8_t)(token & REDPIC1_THERMAL_TOKEN_SLOT_MASK);
}

static uint32_t redpic1_thermal_token_frame_seq(uintptr_t token)
{
    return (uint32_t)(token >> REDPIC1_THERMAL_TOKEN_SHIFT);
}

static void redpic1_thermal_reset_present_wait_locked(void)
{
    s_present_wait_ctx.token = 0U;
    s_present_wait_ctx.done_status = APP_DISPLAY_THERMAL_DONE_ERROR;
    s_present_wait_ctx.waiting = 0U;
    s_present_wait_ctx.done = 0U;
}

static void redpic1_thermal_clear_submitted_token_locked(void)
{
    s_last_submitted_token = 0U;
    s_last_submitted_valid = 0U;
}

static void redpic1_thermal_clear_submitted_token(void)
{
    redpic1_thermal_enter_critical();
    redpic1_thermal_clear_submitted_token_locked();
    redpic1_thermal_exit_critical();
}

static uint8_t redpic1_thermal_prepare_present_wait(uintptr_t token)
{
    if (s_present_wait_sem == 0)
    {
        s_present_wait_sem = xSemaphoreCreateBinary();
        if (s_present_wait_sem == 0)
        {
            return 0U;
        }
    }

    while (xSemaphoreTake(s_present_wait_sem, 0U) == pdPASS)
    {
    }

    redpic1_thermal_enter_critical();
    s_present_wait_ctx.token = token;
    s_present_wait_ctx.done_status = APP_DISPLAY_THERMAL_DONE_ERROR;
    s_present_wait_ctx.waiting = 1U;
    s_present_wait_ctx.done = 0U;
    redpic1_thermal_exit_critical();
    return 1U;
}

static void redpic1_thermal_signal_present_done(uintptr_t token,
                                                app_display_thermal_done_status_t status)
{
    uint8_t should_signal = 0U;

    redpic1_thermal_enter_critical();
    if (s_present_wait_ctx.waiting != 0U &&
        s_present_wait_ctx.token == token)
    {
        s_present_wait_ctx.done_status = status;
        s_present_wait_ctx.done = 1U;
        should_signal = 1U;
    }
    redpic1_thermal_exit_critical();

    if (should_signal != 0U && s_present_wait_sem != 0)
    {
        (void)xSemaphoreGive(s_present_wait_sem);
    }
}

static app_display_thermal_done_status_t redpic1_thermal_wait_present_done(uintptr_t token)
{
    app_display_thermal_done_status_t status = APP_DISPLAY_THERMAL_DONE_ERROR;

    if (s_present_wait_sem == 0)
    {
        return APP_DISPLAY_THERMAL_DONE_ERROR;
    }

    if (xSemaphoreTake(s_present_wait_sem, pdMS_TO_TICKS(REDPIC1_THERMAL_PRESENT_WAIT_TIMEOUT_MS)) != pdPASS)
    {
        redpic1_thermal_enter_critical();
        if (s_present_wait_ctx.waiting != 0U &&
            s_present_wait_ctx.token == token)
        {
            redpic1_thermal_reset_present_wait_locked();
        }
        redpic1_thermal_exit_critical();
        app_perf_baseline_record_thermal_3d_wait_timeout();
        return APP_DISPLAY_THERMAL_DONE_ERROR;
    }

    redpic1_thermal_enter_critical();
    if (s_present_wait_ctx.waiting != 0U &&
        s_present_wait_ctx.token == token &&
        s_present_wait_ctx.done != 0U)
    {
        status = s_present_wait_ctx.done_status;
        redpic1_thermal_reset_present_wait_locked();
    }
    else if (s_present_wait_ctx.waiting == 0U)
    {
        status = s_present_wait_ctx.done_status;
    }
    redpic1_thermal_exit_critical();

    return status;
}

static void redpic1_thermal_free_slot_locked(uint8_t slot_index)
{
    if (slot_index >= REDPIC1_THERMAL_SLOT_COUNT)
    {
        return;
    }

    s_frame_slots[slot_index].valid = 0U;
    s_frame_slots[slot_index].slot_state = REDPIC1_THERMAL_FRAME_SLOT_FREE;

    if (s_front_slot_index == slot_index)
    {
        s_front_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
    }
    if (s_ready_slot_index == slot_index)
    {
        s_ready_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
    }
    if (s_inflight_slot_index == slot_index)
    {
        s_inflight_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
    }
    if (s_last_submitted_valid != 0U &&
        redpic1_thermal_token_slot_index(s_last_submitted_token) == slot_index)
    {
        redpic1_thermal_clear_submitted_token_locked();
    }
}

static redpic1_thermal_frame_slot_t *redpic1_thermal_get_back_slot(void)
{
    uint8_t slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;

    redpic1_thermal_enter_critical();
    for (slot_index = 0U; slot_index < REDPIC1_THERMAL_SLOT_COUNT; ++slot_index)
    {
        if (s_frame_slots[slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_FREE)
        {
            break;
        }
    }

    if (slot_index >= REDPIC1_THERMAL_SLOT_COUNT &&
        s_ready_slot_index < REDPIC1_THERMAL_SLOT_COUNT)
    {
        slot_index = s_ready_slot_index;
        redpic1_thermal_free_slot_locked(slot_index);
        app_perf_baseline_record_thermal_ready_replace();
    }

    if (slot_index < REDPIC1_THERMAL_SLOT_COUNT)
    {
        s_frame_slots[slot_index].valid = 0U;
        s_frame_slots[slot_index].slot_state = REDPIC1_THERMAL_FRAME_SLOT_WRITING;
    }
    redpic1_thermal_exit_critical();

    if (slot_index >= REDPIC1_THERMAL_SLOT_COUNT)
    {
        return 0;
    }

    return &s_frame_slots[slot_index];
}

static void redpic1_thermal_release_back_slot(redpic1_thermal_frame_slot_t *slot)
{
    uint8_t slot_index = 0U;

    if (slot == 0)
    {
        return;
    }

    slot_index = (uint8_t)(slot - &s_frame_slots[0]);
    if (slot_index >= REDPIC1_THERMAL_SLOT_COUNT)
    {
        return;
    }

    redpic1_thermal_enter_critical();
    if (s_frame_slots[slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_WRITING)
    {
        redpic1_thermal_free_slot_locked(slot_index);
    }
    redpic1_thermal_exit_critical();
}

static redpic1_thermal_frame_slot_t *redpic1_thermal_get_front_slot(void)
{
    uint8_t front_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
    redpic1_thermal_frame_slot_t *slot = 0;

    redpic1_thermal_enter_critical();
    front_index = s_front_slot_index;
    if (front_index < REDPIC1_THERMAL_SLOT_COUNT &&
        s_frame_slots[front_index].valid != 0U &&
        s_frame_slots[front_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_FRONT)
    {
        slot = &s_frame_slots[front_index];
    }
    redpic1_thermal_exit_critical();

    return slot;
}

static void redpic1_thermal_publish_back_slot(redpic1_thermal_frame_slot_t *slot)
{
    uint8_t publish_index = 0U;
    uint8_t old_ready = REDPIC1_THERMAL_SLOT_INDEX_NONE;
    uint8_t note_replace = 0U;

    if (slot == 0)
    {
        return;
    }

    publish_index = (uint8_t)(slot - &s_frame_slots[0]);
    if (publish_index >= REDPIC1_THERMAL_SLOT_COUNT)
    {
        return;
    }

    redpic1_thermal_enter_critical();
    if (s_frame_slots[publish_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_WRITING)
    {
        old_ready = s_ready_slot_index;
        if (old_ready < REDPIC1_THERMAL_SLOT_COUNT &&
            old_ready != publish_index &&
            s_frame_slots[old_ready].slot_state == REDPIC1_THERMAL_FRAME_SLOT_READY)
        {
            redpic1_thermal_free_slot_locked(old_ready);
            note_replace = 1U;
        }
        s_frame_slots[publish_index].valid = 1U;
        s_frame_slots[publish_index].slot_state = REDPIC1_THERMAL_FRAME_SLOT_READY;
        s_ready_slot_index = publish_index;
    }
    redpic1_thermal_exit_critical();

    if (note_replace != 0U)
    {
        app_perf_baseline_record_thermal_ready_replace();
    }
}

static void redpic1_thermal_present_front_slot(void)
{
    redpic1_thermal_frame_slot_t *slot = 0;

    if (s_runEnabled == 0U || displayPaused != 0U || s_overlayHold != 0U || s_frameReady == 0U)
    {
        return;
    }

    slot = redpic1_thermal_get_front_slot();
    if (slot == 0)
    {
        return;
    }

    (void)redpic1_thermal_present_gray_frame(slot->gray_frame);
}

#if REDPIC1_THERMAL_STAGE6V_2_ACTIVE
static void redpic1_thermal_try_submit_latest_ready_after_done(void);
static void redpic1_thermal_try_submit_latest_ready_after_resume(void);
#endif

static uint8_t redpic1_thermal_try_claim_present(uintptr_t token, uint8_t **gray_frame)
{
    uint8_t slot_index = redpic1_thermal_token_slot_index(token);
    uint32_t frame_seq = redpic1_thermal_token_frame_seq(token);
    uint8_t claimed = 0U;

    if (gray_frame == 0)
    {
        return 0U;
    }

    *gray_frame = 0;

    redpic1_thermal_enter_critical();
    if (s_runEnabled != 0U &&
        slot_index < REDPIC1_THERMAL_SLOT_COUNT &&
        s_ready_slot_index == slot_index &&
        s_inflight_slot_index == REDPIC1_THERMAL_SLOT_INDEX_NONE &&
        s_frame_slots[slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_READY &&
        s_frame_slots[slot_index].frame_seq == frame_seq)
    {
        s_frame_slots[slot_index].slot_state = REDPIC1_THERMAL_FRAME_SLOT_INFLIGHT;
        s_ready_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
        s_inflight_slot_index = slot_index;
        *gray_frame = s_frame_slots[slot_index].gray_frame;
        claimed = 1U;
    }
    redpic1_thermal_exit_critical();

    if (claimed != 0U)
    {
        app_perf_baseline_record_thermal_3d_claim();
    }

    return claimed;
}

static void redpic1_thermal_handle_display_done(uintptr_t token,
                                                app_display_thermal_done_status_t status)
{
    uint8_t slot_index = redpic1_thermal_token_slot_index(token);
    uint32_t frame_seq = redpic1_thermal_token_frame_seq(token);
    uint8_t note_cancel = 0U;
    uint8_t note_done_ok = 0U;
    uint8_t note_done_error = 0U;
    uint8_t note_done_cancel = 0U;
    uint8_t notify_waiter = 0U;

    redpic1_thermal_enter_critical();
    if (s_present_wait_ctx.waiting != 0U &&
        s_present_wait_ctx.token == token)
    {
        notify_waiter = 1U;
    }

    if (slot_index < REDPIC1_THERMAL_SLOT_COUNT &&
        s_frame_slots[slot_index].frame_seq == frame_seq)
    {
        switch (status)
        {
        case APP_DISPLAY_THERMAL_DONE_OK:
            if (s_inflight_slot_index == slot_index &&
                s_frame_slots[slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_INFLIGHT)
            {
                s_inflight_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
                if (s_runEnabled != 0U)
                {
                    uint8_t old_front = s_front_slot_index;

                    if (old_front < REDPIC1_THERMAL_SLOT_COUNT && old_front != slot_index)
                    {
                        redpic1_thermal_free_slot_locked(old_front);
                    }

                    s_frame_slots[slot_index].valid = 1U;
                    s_frame_slots[slot_index].slot_state = REDPIC1_THERMAL_FRAME_SLOT_FRONT;
                    s_front_slot_index = slot_index;
                    s_frameReady = 1U;
                }
                else
                {
                    redpic1_thermal_free_slot_locked(slot_index);
                }
                note_done_ok = 1U;
            }
            break;

        case APP_DISPLAY_THERMAL_DONE_CANCELLED:
            if ((s_ready_slot_index == slot_index &&
                 s_frame_slots[slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_READY) ||
                (s_inflight_slot_index == slot_index &&
                 s_frame_slots[slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_INFLIGHT))
            {
                redpic1_thermal_free_slot_locked(slot_index);
                note_cancel = 1U;
                note_done_cancel = 1U;
            }
            break;

        case APP_DISPLAY_THERMAL_DONE_ERROR:
        default:
            if ((s_inflight_slot_index == slot_index &&
                 s_frame_slots[slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_INFLIGHT) ||
                (s_ready_slot_index == slot_index &&
                 s_frame_slots[slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_READY))
            {
                redpic1_thermal_free_slot_locked(slot_index);
                note_done_error = 1U;
            }
            break;
        }
    }

    if (s_front_slot_index >= REDPIC1_THERMAL_SLOT_COUNT ||
        s_frame_slots[s_front_slot_index].slot_state != REDPIC1_THERMAL_FRAME_SLOT_FRONT ||
        s_frame_slots[s_front_slot_index].valid == 0U)
    {
        s_frameReady = 0U;
    }
    redpic1_thermal_exit_critical();

    if (note_cancel != 0U)
    {
        app_perf_baseline_record_thermal_display_cancel();
    }
    else if (notify_waiter != 0U && status == APP_DISPLAY_THERMAL_DONE_CANCELLED)
    {
        app_perf_baseline_record_thermal_display_cancel();
    }

    if (note_done_ok != 0U)
    {
        app_perf_baseline_record_thermal_3d_done_ok();
    }
    else if (notify_waiter != 0U && status == APP_DISPLAY_THERMAL_DONE_OK)
    {
        app_perf_baseline_record_thermal_3d_done_ok();
    }
    if (note_done_error != 0U)
    {
        app_perf_baseline_record_thermal_3d_done_error();
    }
    else if (notify_waiter != 0U && status == APP_DISPLAY_THERMAL_DONE_ERROR)
    {
        app_perf_baseline_record_thermal_3d_done_error();
    }
    if (note_done_cancel != 0U)
    {
        app_perf_baseline_record_thermal_3d_done_cancel();
    }
    else if (notify_waiter != 0U && status == APP_DISPLAY_THERMAL_DONE_CANCELLED)
    {
        app_perf_baseline_record_thermal_3d_done_cancel();
    }

    if (notify_waiter != 0U || note_done_ok != 0U || note_done_error != 0U || note_done_cancel != 0U)
    {
        redpic1_thermal_signal_present_done(token, status);
    }

    redpic1_thermal_enter_critical();
    if (s_last_submitted_valid != 0U && s_last_submitted_token == token)
    {
        redpic1_thermal_clear_submitted_token_locked();
    }
    redpic1_thermal_exit_critical();

#if REDPIC1_THERMAL_STAGE6V_2_ACTIVE
    if (note_done_ok != 0U)
    {
        redpic1_thermal_try_submit_latest_ready_after_done();
    }
#endif
}

static uint8_t redpic1_thermal_submit_ready_slot(redpic1_thermal_frame_slot_t *slot,
                                                 uintptr_t *out_token)
{
    uint8_t slot_index = 0U;
    uintptr_t token = 0U;

    if (slot == 0)
    {
        return 0U;
    }

    slot_index = (uint8_t)(slot - &s_frame_slots[0]);
    if (slot_index >= REDPIC1_THERMAL_SLOT_COUNT)
    {
        return 0U;
    }

    token = redpic1_thermal_make_slot_token(slot_index, slot->frame_seq);
    if (out_token != 0)
    {
        *out_token = token;
    }
    return app_display_runtime_request_thermal_present_async(slot->gray_frame, token);
}

#if REDPIC1_THERMAL_STAGE3D_ASYNC_ACTIVE
static uint8_t redpic1_thermal_submit_latest_ready_slot(void)
{
    redpic1_thermal_frame_slot_t *slot = 0;
    uintptr_t token = 0U;
    uint8_t submit_needed = 0U;
    uint8_t ok = 0U;

    redpic1_thermal_enter_critical();
    if (s_ready_slot_index < REDPIC1_THERMAL_SLOT_COUNT &&
        s_frame_slots[s_ready_slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_READY)
    {
        slot = &s_frame_slots[s_ready_slot_index];
        token = redpic1_thermal_make_slot_token(s_ready_slot_index, slot->frame_seq);
        if (s_last_submitted_valid == 0U || s_last_submitted_token != token)
        {
            submit_needed = 1U;
        }
    }
    redpic1_thermal_exit_critical();

    if (slot == 0)
    {
        return 0U;
    }

    if (submit_needed == 0U)
    {
        return 1U;
    }

    ok = app_display_runtime_request_thermal_present_async(slot->gray_frame, token);
    if (ok != 0U)
    {
        redpic1_thermal_enter_critical();
        s_last_submitted_token = token;
        s_last_submitted_valid = 1U;
        redpic1_thermal_exit_critical();
    }

    return ok;
}
#endif

#if REDPIC1_THERMAL_STAGE6V_2_ACTIVE
static uint8_t redpic1_thermal_can_gap_close_submit_locked(void)
{
    if (s_runEnabled == 0U ||
        displayPaused != 0U ||
        s_overlayHold != 0U ||
        s_inflight_slot_index != REDPIC1_THERMAL_SLOT_INDEX_NONE)
    {
        return 0U;
    }

    if (s_ready_slot_index >= REDPIC1_THERMAL_SLOT_COUNT ||
        s_frame_slots[s_ready_slot_index].slot_state != REDPIC1_THERMAL_FRAME_SLOT_READY)
    {
        return 0U;
    }

    return 1U;
}

static void redpic1_thermal_try_submit_latest_ready_after_gap_close(void)
{
    uint8_t can_submit = 0U;

    redpic1_thermal_enter_critical();
    can_submit = redpic1_thermal_can_gap_close_submit_locked();
    redpic1_thermal_exit_critical();

    if (can_submit != 0U)
    {
        (void)redpic1_thermal_submit_latest_ready_slot();
    }
}

static void redpic1_thermal_try_submit_latest_ready_after_done(void)
{
    redpic1_thermal_try_submit_latest_ready_after_gap_close();
}

static void redpic1_thermal_try_submit_latest_ready_after_resume(void)
{
    redpic1_thermal_try_submit_latest_ready_after_gap_close();
}
#endif

static void redpic1_thermal_cancel_pending_present_and_clear_submit(void)
{
    app_display_runtime_cancel_thermal_present_async();
    redpic1_thermal_clear_submitted_token();
}

static void redpic1_thermal_commit_writing_slot(redpic1_thermal_frame_slot_t *slot, uint8_t to_front)
{
    uint8_t slot_index = 0U;
    uint32_t frame_seq = 0U;

    if (slot == 0)
    {
        return;
    }

    slot_index = (uint8_t)(slot - &s_frame_slots[0]);
    if (slot_index >= REDPIC1_THERMAL_SLOT_COUNT)
    {
        return;
    }

    frame_seq = slot->frame_seq;

    redpic1_thermal_enter_critical();
    if (slot_index < REDPIC1_THERMAL_SLOT_COUNT &&
        s_frame_slots[slot_index].frame_seq == frame_seq &&
        s_frame_slots[slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_WRITING)
    {
        if (to_front != 0U)
        {
            uint8_t old_front = s_front_slot_index;

            if (old_front < REDPIC1_THERMAL_SLOT_COUNT && old_front != slot_index)
            {
                redpic1_thermal_free_slot_locked(old_front);
            }

            s_frame_slots[slot_index].valid = 1U;
            s_frame_slots[slot_index].slot_state = REDPIC1_THERMAL_FRAME_SLOT_FRONT;
            s_front_slot_index = slot_index;
            s_ready_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
            s_frameReady = 1U;
        }
        else
        {
            redpic1_thermal_free_slot_locked(slot_index);
        }
    }

    if (s_front_slot_index >= REDPIC1_THERMAL_SLOT_COUNT ||
        s_frame_slots[s_front_slot_index].slot_state != REDPIC1_THERMAL_FRAME_SLOT_FRONT ||
        s_frame_slots[s_front_slot_index].valid == 0U)
    {
        s_frameReady = 0U;
    }
    redpic1_thermal_exit_critical();
}

static uint8_t redpic1_thermal_present_writing_slot_sync(redpic1_thermal_frame_slot_t *slot)
{
    uintptr_t token = 0U;
    app_display_thermal_done_status_t done_status = APP_DISPLAY_THERMAL_DONE_ERROR;

    if (slot == 0)
    {
        return 0U;
    }

    redpic1_thermal_publish_back_slot(slot);
    token = redpic1_thermal_make_slot_token((uint8_t)(slot - &s_frame_slots[0]), slot->frame_seq);
    if (redpic1_thermal_prepare_present_wait(token) == 0U)
    {
        redpic1_thermal_handle_display_done(token, APP_DISPLAY_THERMAL_DONE_ERROR);
        return 0U;
    }

    if (redpic1_thermal_submit_ready_slot(slot, &token) == 0U)
    {
        redpic1_thermal_handle_display_done(token, APP_DISPLAY_THERMAL_DONE_ERROR);
        (void)redpic1_thermal_wait_present_done(token);
        return 0U;
    }

    done_status = redpic1_thermal_wait_present_done(token);
    return (done_status == APP_DISPLAY_THERMAL_DONE_OK) ? 1U : 0U;
}

static void redpic1_thermal_promote_writing_slot_to_front(redpic1_thermal_frame_slot_t *slot)
{
    redpic1_thermal_commit_writing_slot(slot, 1U);
}

static void redpic1_thermal_drop_non_inflight_slots(void)
{
    uint8_t slot_index = 0U;

    redpic1_thermal_enter_critical();
    s_front_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
    s_ready_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;

    for (slot_index = 0U; slot_index < REDPIC1_THERMAL_SLOT_COUNT; ++slot_index)
    {
        if (slot_index == s_inflight_slot_index &&
            s_frame_slots[slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_INFLIGHT)
        {
            continue;
        }

        s_frame_slots[slot_index].valid = 0U;
        s_frame_slots[slot_index].frame_seq = 0U;
        s_frame_slots[slot_index].slot_state = REDPIC1_THERMAL_FRAME_SLOT_FREE;
    }

    if (s_inflight_slot_index >= REDPIC1_THERMAL_SLOT_COUNT ||
        s_frame_slots[s_inflight_slot_index].slot_state != REDPIC1_THERMAL_FRAME_SLOT_INFLIGHT)
    {
        s_inflight_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
        s_frame_sequence = 0U;
    }
    redpic1_thermal_clear_submitted_token_locked();
    redpic1_thermal_exit_critical();

    s_backoff_until_ms = 0U;
    s_restore_bus_pending = 0U;
    s_consecutive_transport_failures = 0U;
    s_frameReady = 0U;
#if REDPIC1_THERMAL_STAGE6V_3_ACTIVE
    redpic1_thermal_reset_display_window_state();
#endif
#if REDPIC1_THERMAL_STAGE6V_4_ACTIVE
    redpic1_thermal_reset_visual_filter_state();
#endif
}

static uint8_t redpic1_thermal_display_busy(void)
{
    uint8_t busy = 0U;

    redpic1_thermal_enter_critical();
    if ((s_ready_slot_index < REDPIC1_THERMAL_SLOT_COUNT &&
         s_frame_slots[s_ready_slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_READY) ||
        (s_inflight_slot_index < REDPIC1_THERMAL_SLOT_COUNT &&
         s_frame_slots[s_inflight_slot_index].slot_state == REDPIC1_THERMAL_FRAME_SLOT_INFLIGHT))
    {
        busy = 1U;
    }
    redpic1_thermal_exit_critical();

    return busy;
}

#else
static void redpic1_thermal_reset_dual_slot_state_locked(void)
{
    s_front_slot_index = 0U;
    s_back_slot_index = 1U;
}

static redpic1_thermal_frame_slot_t *redpic1_thermal_get_back_slot(void)
{
    uint8_t back_index = 1U;

    redpic1_thermal_enter_critical();
    back_index = s_back_slot_index;
    redpic1_thermal_exit_critical();

    if (back_index >= REDPIC1_THERMAL_SLOT_COUNT)
    {
        return 0;
    }

    return &s_frame_slots[back_index];
}

static void redpic1_thermal_release_back_slot(redpic1_thermal_frame_slot_t *slot)
{
    (void)slot;
}

static redpic1_thermal_frame_slot_t *redpic1_thermal_get_front_slot(void)
{
    uint8_t front_index = 0U;
    redpic1_thermal_frame_slot_t *slot = 0;

    redpic1_thermal_enter_critical();
    front_index = s_front_slot_index;
    if (front_index < REDPIC1_THERMAL_SLOT_COUNT &&
        s_frame_slots[front_index].valid != 0U)
    {
        slot = &s_frame_slots[front_index];
    }
    redpic1_thermal_exit_critical();

    return slot;
}

static void redpic1_thermal_publish_back_slot(redpic1_thermal_frame_slot_t *slot)
{
    uint8_t publish_index = 0U;
    uint8_t next_back_index = 0U;

    if (slot == 0)
    {
        return;
    }

    publish_index = (uint8_t)(slot - &s_frame_slots[0]);
    if (publish_index >= REDPIC1_THERMAL_SLOT_COUNT)
    {
        return;
    }

    next_back_index = (uint8_t)(publish_index ^ 1U);
    slot->valid = 1U;

    redpic1_thermal_enter_critical();
    s_front_slot_index = publish_index;
    s_back_slot_index = next_back_index;
    s_frame_slots[next_back_index].valid = 0U;
    redpic1_thermal_exit_critical();

    s_frameReady = 1U;
}

static void redpic1_thermal_present_front_slot(void)
{
    redpic1_thermal_frame_slot_t *slot = 0;

    if (s_runEnabled == 0U || displayPaused != 0U || s_overlayHold != 0U || s_frameReady == 0U)
    {
        return;
    }

    slot = redpic1_thermal_get_front_slot();
    if (slot == 0)
    {
        return;
    }

    (void)redpic1_thermal_present_gray_frame(slot->gray_frame);
}
#endif

static void redpic1_thermal_reset_slots(void)
{
    uint8_t slot_index = 0U;

    redpic1_thermal_enter_critical();
#if REDPIC1_THERMAL_STAGE3D_ENABLE
    s_front_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
    s_ready_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
    s_inflight_slot_index = REDPIC1_THERMAL_SLOT_INDEX_NONE;
    redpic1_thermal_clear_submitted_token_locked();
    redpic1_thermal_reset_present_wait_locked();
    if (s_present_wait_sem != 0)
    {
        while (xSemaphoreTake(s_present_wait_sem, 0U) == pdPASS)
        {
        }
    }
#else
    redpic1_thermal_reset_dual_slot_state_locked();
#endif
    for (slot_index = 0U; slot_index < REDPIC1_THERMAL_SLOT_COUNT; ++slot_index)
    {
        s_frame_slots[slot_index].valid = 0U;
        s_frame_slots[slot_index].frame_seq = 0U;
        s_frame_slots[slot_index].slot_state = REDPIC1_THERMAL_FRAME_SLOT_FREE;
    }
    redpic1_thermal_exit_critical();

    s_frame_sequence = 0U;
    s_backoff_until_ms = 0U;
    s_restore_bus_pending = 0U;
    s_consecutive_transport_failures = 0U;
    s_frameReady = 0U;
#if REDPIC1_THERMAL_STAGE6V_3_ACTIVE
    redpic1_thermal_reset_display_window_state();
#endif
#if REDPIC1_THERMAL_STAGE6V_4_ACTIVE
    redpic1_thermal_reset_visual_filter_state();
#endif
}
#endif

void redpic1_thermal_init(void)
{
    while (mlx90640_init() != 0)
    {
        delay_ms(200);
    }

    s_colorMode = 3U;
    s_runEnabled = 1U;
    s_refreshRate = REDPIC1_THERMAL_ACTIVE_REFRESH_RATE;
    s_overlayHold = 0U;
    displayPaused = 0U;
    s_runtime_overlay_visible = 1U;
    redpic1_thermal_reset_bottom_bar_cache();
    set_color_mode(s_colorMode);

#if REDPIC1_THERMAL_STAGE6V_3_ACTIVE
    redpic1_thermal_reset_display_window_state();
#endif
#if REDPIC1_THERMAL_STAGE6V_4_ACTIVE
    redpic1_thermal_reset_visual_filter_state();
#endif

#if REDPIC1_THERMAL_STAGE2_ENABLE
    redpic1_thermal_reset_slots();
    redpic1_thermal_build_diag_pattern();
#else
    s_frameReady = 0U;
#endif
}

void redpic1_thermal_bind_display_runtime(void)
{
#if REDPIC1_THERMAL_STAGE2_ENABLE && REDPIC1_THERMAL_STAGE3D_ENABLE
    app_display_runtime_set_thermal_present_claim_callback(redpic1_thermal_try_claim_present);
    app_display_runtime_set_thermal_present_done_callback(redpic1_thermal_handle_display_done);
#else
    app_display_runtime_set_thermal_present_claim_callback(0);
    app_display_runtime_set_thermal_present_done_callback(0);
#endif
}

uint32_t redpic1_thermal_get_active_period_ms(void)
{
    return redpic1_thermal_refresh_rate_to_period_ms(REDPIC1_THERMAL_ACTIVE_REFRESH_RATE);
}

void redpic1_thermal_step(void)
{
    uint32_t step_start_cycle = app_perf_baseline_cycle_now();
    uint32_t get_temp_start_cycle = 0U;
    uint32_t gray_start_cycle = 0U;
    float ta = 0.0f;
    float frame_min_temp = 0.0f;
    float frame_max_temp = 0.0f;
    float frame_center_temp = 0.0f;
    uint16_t state = 0U;

    if (s_runEnabled == 0U)
    {
        app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
        return;
    }

#if REDPIC1_THERMAL_STAGE2_ENABLE
    {
        redpic1_thermal_frame_slot_t *back_slot = 0;
        const float *gray_source_frame = 0;
        uint32_t capture_tick_ms = 0U;
        uint32_t now_ms = power_manager_get_tick_ms();

#if REDPIC1_THERMAL_DIAG_MODE == REDPIC1_THERMAL_DIAG_MODE_TEST_PATTERN
        redpic1_thermal_present_diag_pattern();
        app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
        return;
#endif

        if (s_backoff_until_ms != 0U &&
            redpic1_thermal_deadline_reached(now_ms, s_backoff_until_ms) == 0U)
        {
            app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
            return;
        }

#if REDPIC1_THERMAL_STAGE3D_ENABLE && REDPIC1_THERMAL_STAGE3D_BRINGUP_SERIALIZE
        if (redpic1_thermal_display_busy() != 0U)
        {
            app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
            return;
        }
#endif

        s_backoff_until_ms = 0U;
        if (s_restore_bus_pending != 0U)
        {
            redpic1_thermal_restore_bus_now();
        }

        if (MLX90640_I2CRead(MLX90640_ADDR, 0x8000, 1, &state) != 0)
        {
            app_perf_baseline_record_i2c_failure();
            redpic1_thermal_note_backoff(1U);
            app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
            return;
        }

        if ((state & 0x0008U) == 0U)
        {
            app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
            return;
        }

        back_slot = redpic1_thermal_get_back_slot();
        if (back_slot == 0)
        {
            redpic1_thermal_note_backoff(0U);
            app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
            return;
        }

        get_temp_start_cycle = app_perf_baseline_cycle_now();
        if (get_temp(back_slot->temp_frame, &ta) < 0)
        {
            app_perf_baseline_record_get_temp_us(app_perf_baseline_elapsed_us(get_temp_start_cycle));
            app_perf_baseline_record_i2c_failure();
            redpic1_thermal_release_back_slot(back_slot);
            redpic1_thermal_note_backoff(1U);
            app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
            return;
        }
        app_perf_baseline_record_get_temp_us(app_perf_baseline_elapsed_us(get_temp_start_cycle));

        if (redpic1_thermal_frame_data_is_valid(back_slot->temp_frame) == 0U)
        {
            redpic1_thermal_release_back_slot(back_slot);
            redpic1_thermal_note_backoff(0U);
            app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
            return;
        }

        gray_source_frame = back_slot->temp_frame;
#if REDPIC1_THERMAL_STAGE6V_4_ACTIVE
        gray_source_frame = redpic1_thermal_get_gray_source_frame(back_slot->temp_frame);
#endif

        gray_start_cycle = app_perf_baseline_cycle_now();
        redpic1_thermal_prepare_gray_frame(back_slot->temp_frame,
                                           gray_source_frame,
                                           back_slot->gray_frame,
                                           &frame_min_temp,
                                           &frame_max_temp);
        app_perf_baseline_record_gray_us(app_perf_baseline_elapsed_us(gray_start_cycle));
        frame_center_temp = redpic1_thermal_center_temp(back_slot->temp_frame);

        if (redpic1_thermal_frame_is_valid(frame_min_temp,
                                           frame_max_temp,
                                           frame_center_temp) == 0U)
        {
            redpic1_thermal_release_back_slot(back_slot);
            redpic1_thermal_note_backoff(0U);
            app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
            return;
        }

        if (redpic1_thermal_gray_frame_has_contrast(back_slot->gray_frame) == 0U)
        {
            redpic1_thermal_release_back_slot(back_slot);
            redpic1_thermal_note_backoff(0U);
            app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
            return;
        }

        if (s_runEnabled == 0U)
        {
            redpic1_thermal_release_back_slot(back_slot);
            app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
            return;
        }

        capture_tick_ms = power_manager_get_tick_ms();
        back_slot->min_temp = frame_min_temp;
        back_slot->max_temp = frame_max_temp;
        back_slot->center_temp = frame_center_temp;
        back_slot->capture_tick_ms = capture_tick_ms;
        back_slot->frame_seq = ++s_frame_sequence;

        app_perf_baseline_record_thermal_capture_success(capture_tick_ms,
                                                         frame_min_temp,
                                                         frame_max_temp,
                                                         frame_center_temp);

#if REDPIC1_THERMAL_STAGE3D_ENABLE
    #if REDPIC1_THERMAL_STAGE3D_BRINGUP_SERIALIZE
        if (displayPaused == 0U && s_overlayHold == 0U)
        {
            app_perf_baseline_record_thermal_3d_sync_present_attempt();
            if (redpic1_thermal_present_writing_slot_sync(back_slot) != 0U)
            {
                app_perf_baseline_record_thermal_3d_sync_present_ok();
            }
            else
            {
                app_perf_baseline_record_thermal_3d_sync_present_fail();
            }
        }
        else
        {
            redpic1_thermal_promote_writing_slot_to_front(back_slot);
        }
    #else
        redpic1_thermal_publish_back_slot(back_slot);
        if (displayPaused == 0U && s_overlayHold == 0U)
        {
            (void)redpic1_thermal_submit_latest_ready_slot();
        }
    #endif
#else
        (void)redpic1_thermal_present_gray_frame(back_slot->gray_frame);
        redpic1_thermal_publish_back_slot(back_slot);
#endif
        s_consecutive_transport_failures = 0U;
    }
#else
    {
        const float *gray_source_frame = s_tempFrame;

    if (MLX90640_I2CRead(MLX90640_ADDR, 0x8000, 1, &state) != 0)
    {
        app_perf_baseline_record_i2c_failure();
        app_perf_baseline_record_thermal_capture_failure();
        delay_ms(5);
        app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
        return;
    }

    if ((state & 0x0008U) == 0U)
    {
        app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
        return;
    }

    get_temp_start_cycle = app_perf_baseline_cycle_now();
    if (get_temp(s_tempFrame, &ta) < 0)
    {
        app_perf_baseline_record_get_temp_us(app_perf_baseline_elapsed_us(get_temp_start_cycle));
        app_perf_baseline_record_i2c_failure();
        app_perf_baseline_record_thermal_capture_failure();
        delay_ms(5);
        app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
        return;
    }
    app_perf_baseline_record_get_temp_us(app_perf_baseline_elapsed_us(get_temp_start_cycle));

#if REDPIC1_THERMAL_STAGE6V_4_ACTIVE
    gray_source_frame = redpic1_thermal_get_gray_source_frame(s_tempFrame);
#endif

    gray_start_cycle = app_perf_baseline_cycle_now();
    redpic1_thermal_prepare_gray_frame(s_tempFrame,
                                       gray_source_frame,
                                       s_grayFrame,
                                       &frame_min_temp,
                                       &frame_max_temp);
    app_perf_baseline_record_gray_us(app_perf_baseline_elapsed_us(gray_start_cycle));
    frame_center_temp = redpic1_thermal_center_temp(s_tempFrame);
    s_frameReady = 1U;
    app_perf_baseline_record_thermal_capture_success(power_manager_get_tick_ms(),
                                                     frame_min_temp,
                                                     frame_max_temp,
                                                     frame_center_temp);

    if (displayPaused == 0U && s_overlayHold == 0U)
    {
        (void)app_display_runtime_present_thermal_frame(s_grayFrame);
    }
    }
#endif

    app_perf_baseline_record_thermal_step_us(app_perf_baseline_elapsed_us(step_start_cycle));
}

void redpic1_thermal_force_refresh(void)
{
#if REDPIC1_THERMAL_STAGE2_ENABLE
#if REDPIC1_THERMAL_DIAG_MODE == REDPIC1_THERMAL_DIAG_MODE_TEST_PATTERN
    redpic1_thermal_present_diag_pattern();
#else
    redpic1_thermal_present_front_slot();
#endif
#else
    if (s_runEnabled != 0U && s_frameReady != 0U && displayPaused == 0U && s_overlayHold == 0U)
    {
        (void)app_display_runtime_present_thermal_frame(s_grayFrame);
    }
#endif
}

void redpic1_thermal_render_runtime_overlay(void)
{
    char line_text[64];
    uint32_t now_ms = power_manager_get_tick_ms();

    if (s_runtime_overlay_visible == 0U)
    {
        if (s_overlay_bar_last_visible != 0U || s_overlay_bar_last_line_valid != 0U)
        {
            redpic1_thermal_clear_bottom_bar();
            s_overlay_bar_last_visible = 0U;
            s_overlay_bar_last_line_valid = 0U;
            s_overlay_bar_pending_dirty = 1U;
        }
        return;
    }

    redpic1_thermal_build_bottom_bar_line(line_text, sizeof(line_text));
    if (strcmp(s_overlay_bar_pending_line, line_text) != 0)
    {
        snprintf(s_overlay_bar_pending_line, sizeof(s_overlay_bar_pending_line), "%s", line_text);
        s_overlay_bar_pending_dirty = 1U;
    }

    if (s_overlay_bar_last_visible == 0U || s_overlay_bar_last_line_valid == 0U)
    {
        redpic1_thermal_draw_bottom_bar_line(s_overlay_bar_pending_line);
        snprintf(s_overlay_bar_last_line, sizeof(s_overlay_bar_last_line), "%s", s_overlay_bar_pending_line);
        s_overlay_bar_last_visible = 1U;
        s_overlay_bar_last_line_valid = 1U;
        s_overlay_bar_pending_dirty = 0U;
        s_overlay_bar_last_refresh_ms = now_ms;
        return;
    }

    if (s_overlay_bar_pending_dirty == 0U)
    {
        return;
    }

    if ((uint32_t)(now_ms - s_overlay_bar_last_refresh_ms) < REDPIC1_THERMAL_OVERLAY_BAR_REFRESH_MS)
    {
        return;
    }

    redpic1_thermal_draw_bottom_bar_line(s_overlay_bar_pending_line);
    snprintf(s_overlay_bar_last_line, sizeof(s_overlay_bar_last_line), "%s", s_overlay_bar_pending_line);
    s_overlay_bar_last_visible = 1U;
    s_overlay_bar_last_line_valid = 1U;
    s_overlay_bar_pending_dirty = 0U;
    s_overlay_bar_last_refresh_ms = now_ms;
}

uint8_t redpic1_thermal_runtime_overlay_visible(void)
{
    return s_runtime_overlay_visible;
}

void redpic1_thermal_handle_key(uint8_t key_value)
{
    switch (key_value)
    {
    case KEY1_PRES:
        s_colorMode++;
        if (s_colorMode > 4U)
        {
            s_colorMode = 0U;
        }
        set_color_mode(s_colorMode);
        break;

    case KEY2_PRES:
        s_runtime_overlay_visible = (uint8_t)!s_runtime_overlay_visible;
        redpic1_thermal_reset_bottom_bar_cache();
        redpic1_thermal_force_refresh();
        break;

    case KEY3_PRES:
    {
        uint8_t resume_submit = (displayPaused != 0U) ? 1U : 0U;

        displayPaused = (uint8_t)!displayPaused;
#if REDPIC1_THERMAL_STAGE3D_ASYNC_ACTIVE && REDPIC1_THERMAL_STAGE3D_3B_ENABLE
        if (displayPaused != 0U)
        {
            redpic1_thermal_cancel_pending_present_and_clear_submit();
        }
#endif
#if REDPIC1_THERMAL_STAGE6V_2_ACTIVE
        if (resume_submit != 0U && displayPaused == 0U)
        {
            redpic1_thermal_try_submit_latest_ready_after_resume();
        }
#else
        (void)resume_submit;
#endif
    }
        break;

    default:
        break;
    }
}

void redpic1_thermal_suspend(void)
{
    s_runEnabled = 0U;
    redpic1_thermal_apply_refresh_rate(REDPIC1_THERMAL_IDLE_REFRESH_RATE);
    redpic1_thermal_reset_bottom_bar_cache();

#if REDPIC1_THERMAL_STAGE2_ENABLE && REDPIC1_THERMAL_STAGE3D_ENABLE
    redpic1_thermal_cancel_pending_present_and_clear_submit();
#endif

#if REDPIC1_THERMAL_STAGE2_ENABLE
#if REDPIC1_THERMAL_STAGE3D_ENABLE
    redpic1_thermal_drop_non_inflight_slots();
#else
    redpic1_thermal_reset_slots();
#endif
    if (s_diag_pattern_ready == 0U)
    {
        redpic1_thermal_build_diag_pattern();
    }
#else
    s_frameReady = 0U;
#endif

    power_manager_release_lock(POWER_LOCK_THERMAL);
}

void redpic1_thermal_resume(void)
{
    redpic1_thermal_apply_refresh_rate(REDPIC1_THERMAL_ACTIVE_REFRESH_RATE);
    s_runEnabled = 1U;
    s_overlayHold = 0U;
    displayPaused = 0U;
    redpic1_thermal_reset_bottom_bar_cache();

#if REDPIC1_THERMAL_STAGE2_ENABLE
#if REDPIC1_THERMAL_STAGE3D_ENABLE
    redpic1_thermal_drop_non_inflight_slots();
#else
    redpic1_thermal_reset_slots();
#endif
#else
    s_frameReady = 0U;
#endif

    power_manager_acquire_lock(POWER_LOCK_THERMAL);
    power_manager_notify_activity();

#if REDPIC1_THERMAL_STAGE6V_2_ACTIVE
    redpic1_thermal_try_submit_latest_ready_after_resume();
#endif
}

void redpic1_thermal_restore_bus_after_stop(void)
{
#if REDPIC1_THERMAL_STAGE2_ENABLE
    if (redpic1_thermal_scheduler_running() != 0U)
    {
        s_restore_bus_pending = 1U;
        s_backoff_until_ms = 0U;
        return;
    }

    MLX90640_I2CInit();
    redpic1_thermal_apply_refresh_rate_internal(s_refreshRate, 1U);
#else
    MLX90640_I2CInit();
#endif
}

void redpic1_thermal_set_overlay_hold(uint8_t enabled)
{
    s_overlayHold = enabled;
#if REDPIC1_THERMAL_STAGE3D_ASYNC_ACTIVE && REDPIC1_THERMAL_STAGE3D_3B_ENABLE
    if (enabled != 0U)
    {
        redpic1_thermal_cancel_pending_present_and_clear_submit();
    }
#endif
}
