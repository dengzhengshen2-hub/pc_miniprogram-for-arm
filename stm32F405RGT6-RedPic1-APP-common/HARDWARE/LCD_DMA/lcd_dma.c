#include "lcd_dma.h"
#include "lcd_init.h"
#include "lcd.h"
#include "FreeRTOS.h"
#include "task.h"
#include <string.h>
#include "stdlib.h"
#include "math.h"
#include "sys.h"
#include "app_display_runtime.h"
#include "app_perf_baseline.h"
#include "redpic1_thermal.h"

/* LCD DMA 显示模块
 * 将 24x32 热成像灰度帧插值为 240x320，并通过 DMA 输出到 LCD。 */

#define THERMAL_RENDER_WIDTH  240
#define THERMAL_RENDER_HEIGHT 320
#define THERMAL_OUTPUT_ROWS   (LCD_H - 20U)
#define THERMAL_SRC_WIDTH 24
#define THERMAL_SRC_HEIGHT 32
#define INTERP_STRIDE 10
#define TOP_EDGE_ROWS 5
#define BOTTOM_EDGE_ROWS 4
#define BOTTOM_EDGE_START (THERMAL_RENDER_HEIGHT - BOTTOM_EDGE_ROWS)
#define LINE_BUF_SIZE  (LCD_W * 2)
#define LCD_DMA_THERMAL_CROSS_HALF_SIZE 6U
#define LCD_DMA_THERMAL_CROSS_GAP_SIZE  2U
#define DMA_TRANSFER_WAIT_LOOPS 5000000UL
#define DMA_TRANSFER_WAIT_TIMEOUT_MS 20UL
#define DMA_STREAM_DISABLE_WAIT_LOOPS 100000UL
#define LCD_SPI_IDLE_WAIT_LOOPS 100000UL

#if REDPIC1_THERMAL_STAGE6_ENABLE && REDPIC1_THERMAL_STAGE6_6B_ENABLE
    #define LCD_DMA_STAGE6_6B_ACTIVE 1
#else
    #define LCD_DMA_STAGE6_6B_ACTIVE 0
#endif

#if REDPIC1_THERMAL_STAGE6_ENABLE && REDPIC1_THERMAL_STAGE6_6C_ENABLE
    #define LCD_DMA_STAGE6_6C_ACTIVE 1
#else
    #define LCD_DMA_STAGE6_6C_ACTIVE 0
#endif

/* 双行缓存必须留在系统 SRAM，避免被链接到 DMA 不可达的 CCRAM。 */
__attribute__((section("dma_sram"), aligned(4))) u8 lineBuffer[2][LINE_BUF_SIZE];

volatile uint8_t activeBuffer = 0;
volatile uint8_t transferComplete = 1;
static volatile TaskHandle_t s_dma_wait_task = 0;
static volatile uint8_t s_dma_last_result = 1U;
static volatile app_perf_lcd_dma_status_t s_dma_last_status = APP_PERF_LCD_DMA_STATUS_NONE;
typedef enum {
    LCD_DMA_MODE_IDLE = 0,
    LCD_DMA_MODE_THERMAL = 1
} lcd_dma_mode_t;

static volatile lcd_dma_mode_t s_dma_mode = LCD_DMA_MODE_IDLE;
static CCMRAM uint8_t g_interpRows[THERMAL_SRC_HEIGHT][THERMAL_RENDER_WIDTH];
static CCMRAM uint8_t g_topEdgeRows[TOP_EDGE_ROWS][THERMAL_RENDER_WIDTH];
static CCMRAM uint8_t g_bottomEdgeRows[BOTTOM_EDGE_ROWS][THERMAL_RENDER_WIDTH];
/* 伪彩色 LUT。 */
CCMRAM uint16_t GCM_Pseudo3[256];

#if LCD_DMA_STAGE6_6C_ACTIVE
static CCMRAM uint8_t g_colorHighByteLut[256];
static CCMRAM uint8_t g_colorLowByteLut[256];
#endif

#if LCD_DMA_STAGE6_6B_ACTIVE
typedef enum {
    LCD_DMA_INTERP_ROW_TOP = 0,
    LCD_DMA_INTERP_ROW_BODY = 1,
    LCD_DMA_INTERP_ROW_BOTTOM = 2
} lcd_dma_interp_row_kind_t;

typedef struct {
    uint8_t kind;
    uint8_t base_index;
    uint8_t ratio;
} lcd_dma_interp_row_meta_t;

static CCMRAM lcd_dma_interp_row_meta_t g_interpRowMeta[THERMAL_RENDER_HEIGHT];
static CCMRAM uint16_t g_outputRowToInterpRow[LCD_H];
static CCMRAM uint16_t g_outputColToInterpRow[LCD_W];
static CCMRAM uint16_t g_outputRowToInterpCol[LCD_H];
static CCMRAM uint16_t g_outputColToInterpCol[LCD_W];
static uint8_t s_renderMappingReady = 0U;
#endif

/* 将任意整数裁剪到 0~255。 */
static uint8_t clamp_to_u8(int32_t value)
{
    if (value < 0) {
        return 0;
    }
    if (value > 255) {
        return 255;
    }
    return (uint8_t)value;
}

static void lcd_dma_write_rgb565_pixel(uint8_t *buf, uint16_t out_x, uint16_t color)
{
    if (buf == 0 || out_x >= LCD_W)
    {
        return;
    }

    buf[2U * out_x] = (uint8_t)(color >> 8);
    buf[2U * out_x + 1U] = (uint8_t)(color & 0xFFU);
}

static void lcd_dma_overlay_crosshair_row(uint16_t out_row, uint8_t *buf)
{
    uint16_t center_x = (uint16_t)(LCD_W / 2U);
    uint16_t center_y = (uint16_t)(THERMAL_OUTPUT_ROWS / 2U);
    uint16_t left_start = 0U;
    uint16_t left_end = 0U;
    uint16_t right_start = 0U;
    uint16_t right_end = 0U;
    uint16_t top_start = 0U;
    uint16_t top_end = 0U;
    uint16_t bottom_start = (uint16_t)(center_y + LCD_DMA_THERMAL_CROSS_GAP_SIZE);
    uint16_t bottom_end = (uint16_t)(center_y + LCD_DMA_THERMAL_CROSS_HALF_SIZE);

    if (buf == 0 || out_row >= THERMAL_OUTPUT_ROWS ||
        redpic1_thermal_runtime_overlay_visible() == 0U)
    {
        return;
    }

    left_start = (center_x > LCD_DMA_THERMAL_CROSS_HALF_SIZE) ?
                 (uint16_t)(center_x - LCD_DMA_THERMAL_CROSS_HALF_SIZE) :
                 0U;
    left_end = (center_x > LCD_DMA_THERMAL_CROSS_GAP_SIZE) ?
               (uint16_t)(center_x - LCD_DMA_THERMAL_CROSS_GAP_SIZE) :
               0U;
    right_start = (uint16_t)(center_x + LCD_DMA_THERMAL_CROSS_GAP_SIZE);
    right_end = (uint16_t)(center_x + LCD_DMA_THERMAL_CROSS_HALF_SIZE);
    top_start = (center_y > LCD_DMA_THERMAL_CROSS_HALF_SIZE) ?
                (uint16_t)(center_y - LCD_DMA_THERMAL_CROSS_HALF_SIZE) :
                0U;
    top_end = (center_y > LCD_DMA_THERMAL_CROSS_GAP_SIZE) ?
              (uint16_t)(center_y - LCD_DMA_THERMAL_CROSS_GAP_SIZE) :
              0U;

    if (out_row == center_y)
    {
        uint16_t out_x = 0U;

        for (out_x = left_start; out_x <= left_end && out_x < LCD_W; ++out_x)
        {
            lcd_dma_write_rgb565_pixel(buf, out_x, WHITE);
        }
        for (out_x = right_start; out_x <= right_end && out_x < LCD_W; ++out_x)
        {
            lcd_dma_write_rgb565_pixel(buf, out_x, WHITE);
        }
        lcd_dma_write_rgb565_pixel(buf, center_x, RED);
        return;
    }

    if ((out_row >= top_start && out_row <= top_end) ||
        (out_row >= bottom_start && out_row <= bottom_end))
    {
        lcd_dma_write_rgb565_pixel(buf, center_x, WHITE);
    }
}

static uint16_t lcd_dma_scale_axis(uint16_t index, uint16_t output_count, uint16_t interp_count)
{
    uint32_t numerator = 0U;
    uint32_t denominator = 0U;

    if (output_count <= 1U || interp_count <= 1U)
    {
        return 0U;
    }

    denominator = (uint32_t)(output_count - 1U);
    numerator = ((uint32_t)index * (uint32_t)(interp_count - 1U)) + (denominator / 2U);
    return (uint16_t)(numerator / denominator);
}

/* 启动一次 DMA 行发送。 */
static uint8_t lcd_dma_wait_stream_disabled(void)
{
    uint32_t timeout = DMA_STREAM_DISABLE_WAIT_LOOPS;

    if (DMA_GetCmdStatus(DMA2_Stream3) == DISABLE)
    {
        return 1U;
    }

    DMA_Cmd(DMA2_Stream3, DISABLE);
    while (DMA_GetCmdStatus(DMA2_Stream3) != DISABLE)
    {
        if (timeout-- == 0U)
        {
            s_dma_mode = LCD_DMA_MODE_IDLE;
            transferComplete = 1U;
            s_dma_last_result = 0U;
            s_dma_last_status = APP_PERF_LCD_DMA_STATUS_ERROR;
            return 0U;
        }
    }

    return 1U;
}

static uint8_t lcd_dma_wait_spi_idle(void)
{
    uint32_t timeout = LCD_SPI_IDLE_WAIT_LOOPS;

    while (SPI_I2S_GetFlagStatus(SPI1, SPI_I2S_FLAG_TXE) == RESET)
    {
        if (timeout-- == 0U)
        {
            s_dma_mode = LCD_DMA_MODE_IDLE;
            s_dma_last_result = 0U;
            s_dma_last_status = APP_PERF_LCD_DMA_STATUS_ERROR;
            return 0U;
        }
    }

    timeout = LCD_SPI_IDLE_WAIT_LOOPS;
    while (SPI_I2S_GetFlagStatus(SPI1, SPI_I2S_FLAG_BSY) == SET)
    {
        if (timeout-- == 0U)
        {
            s_dma_mode = LCD_DMA_MODE_IDLE;
            s_dma_last_result = 0U;
            s_dma_last_status = APP_PERF_LCD_DMA_STATUS_ERROR;
            return 0U;
        }
    }

    return 1U;
}

static uint8_t start_dma_line_transfer(uint8_t *buf)
{
    if (buf == 0 || lcd_dma_wait_stream_disabled() == 0U)
    {
        return 0U;
    }

    s_dma_mode = LCD_DMA_MODE_THERMAL;
    transferComplete = 0U;
    s_dma_last_result = 0U;
    s_dma_last_status = APP_PERF_LCD_DMA_STATUS_TIMEOUT;
    DMA_ClearFlag(DMA2_Stream3,
                  DMA_FLAG_FEIF3 |
                  DMA_FLAG_DMEIF3 |
                  DMA_FLAG_TEIF3 |
                  DMA_FLAG_HTIF3 |
                  DMA_FLAG_TCIF3);
    DMA2_Stream3->M0AR = (uint32_t)buf;
    DMA_SetCurrDataCounter(DMA2_Stream3, LINE_BUF_SIZE);
    DMA_Cmd(DMA2_Stream3, ENABLE);
    return 1U;
}

static uint8_t lcd_dma_scheduler_running(void)
{
    return (xTaskGetSchedulerState() == taskSCHEDULER_RUNNING) ? 1U : 0U;
}

static uint8_t lcd_dma_wait_busy_loop(void)
{
    uint32_t timeout = DMA_TRANSFER_WAIT_LOOPS;

    while (!transferComplete)
    {
        if (timeout-- == 0U)
        {
            DMA_Cmd(DMA2_Stream3, DISABLE);
            DMA_ClearFlag(DMA2_Stream3,
                          DMA_FLAG_FEIF3 |
                          DMA_FLAG_DMEIF3 |
                          DMA_FLAG_TEIF3 |
                          DMA_FLAG_HTIF3 |
                          DMA_FLAG_TCIF3);
            s_dma_mode = LCD_DMA_MODE_IDLE;
            transferComplete = 1U;
            s_dma_last_result = 0U;
            s_dma_last_status = APP_PERF_LCD_DMA_STATUS_TIMEOUT;
            return 0U;
        }
    }

    if (s_dma_last_result != 0U)
    {
        return lcd_dma_wait_spi_idle();
    }

    return 0U;
}

/* 等待 DMA 发送完成。
 * 超时后主动终止，避免主循环长期卡死。 */
static uint8_t wait_for_dma_transfer_complete(void)
{
#if APP_DISPLAY_STAGE3_ENABLE
    TickType_t wait_ticks = pdMS_TO_TICKS(DMA_TRANSFER_WAIT_TIMEOUT_MS);
    TaskHandle_t current_task = 0;

    if (lcd_dma_scheduler_running() == 0U)
    {
        return lcd_dma_wait_busy_loop();
    }

    if (transferComplete != 0U)
    {
        if (s_dma_last_result != 0U)
        {
            return lcd_dma_wait_spi_idle();
        }

        return 0U;
    }

    current_task = xTaskGetCurrentTaskHandle();
    (void)ulTaskNotifyTake(pdTRUE, 0U);

    taskENTER_CRITICAL();
    if (transferComplete == 0U)
    {
        s_dma_wait_task = current_task;
    }
    taskEXIT_CRITICAL();

    if (transferComplete != 0U)
    {
        taskENTER_CRITICAL();
        if (s_dma_wait_task == current_task)
        {
            s_dma_wait_task = 0;
        }
        taskEXIT_CRITICAL();
        if (s_dma_last_result != 0U)
        {
            return lcd_dma_wait_spi_idle();
        }

        return 0U;
    }

    if (ulTaskNotifyTake(pdTRUE, wait_ticks) == 0U)
    {
        taskENTER_CRITICAL();
        if (s_dma_wait_task == current_task)
        {
            s_dma_wait_task = 0;
        }
        taskEXIT_CRITICAL();

        DMA_Cmd(DMA2_Stream3, DISABLE);
        DMA_ClearFlag(DMA2_Stream3,
                      DMA_FLAG_FEIF3 |
                      DMA_FLAG_DMEIF3 |
                      DMA_FLAG_TEIF3 |
                      DMA_FLAG_HTIF3 |
                      DMA_FLAG_TCIF3);
        s_dma_mode = LCD_DMA_MODE_IDLE;
        transferComplete = 1U;
        s_dma_last_result = 0U;
        s_dma_last_status = APP_PERF_LCD_DMA_STATUS_TIMEOUT;
        return 0U;
    }

    app_perf_baseline_record_dma_wait_take();
    if (s_dma_last_result != 0U)
    {
        return lcd_dma_wait_spi_idle();
    }

    return 0U;
#else
    return lcd_dma_wait_busy_loop();
#endif
}

#if LCD_DMA_STAGE6_6B_ACTIVE
static void lcd_dma_init_render_mappings(void)
{
    uint16_t out_row = 0U;
    uint16_t out_col = 0U;

    if (s_renderMappingReady != 0U)
    {
        return;
    }

    for (uint16_t interp_row = 0U; interp_row < THERMAL_RENDER_HEIGHT; ++interp_row)
    {
        lcd_dma_interp_row_meta_t *meta = &g_interpRowMeta[interp_row];

        if (interp_row < TOP_EDGE_ROWS)
        {
            meta->kind = LCD_DMA_INTERP_ROW_TOP;
            meta->base_index = (uint8_t)interp_row;
            meta->ratio = 0U;
        }
        else if (interp_row >= BOTTOM_EDGE_START)
        {
            meta->kind = LCD_DMA_INTERP_ROW_BOTTOM;
            meta->base_index = (uint8_t)(interp_row - BOTTOM_EDGE_START);
            meta->ratio = 0U;
        }
        else
        {
            uint16_t offset = (uint16_t)(interp_row - TOP_EDGE_ROWS);
            meta->kind = LCD_DMA_INTERP_ROW_BODY;
            meta->base_index = (uint8_t)(offset / INTERP_STRIDE);
            meta->ratio = (uint8_t)(offset % INTERP_STRIDE);
        }
    }

#if USE_HORIZONTAL==0 || USE_HORIZONTAL==1
    for (out_row = 0U; out_row < LCD_H; ++out_row)
    {
        if (out_row < THERMAL_OUTPUT_ROWS)
        {
            uint16_t mapped_row = lcd_dma_scale_axis(out_row,
                                                     THERMAL_OUTPUT_ROWS,
                                                     THERMAL_RENDER_HEIGHT);
            g_outputRowToInterpRow[out_row] = (uint16_t)(THERMAL_RENDER_HEIGHT - 1U - mapped_row);
        }
        else
        {
            g_outputRowToInterpRow[out_row] = 0U;
        }
    }
    for (out_col = 0U; out_col < LCD_W; ++out_col)
    {
        g_outputColToInterpCol[out_col] = (uint16_t)(THERMAL_RENDER_WIDTH - 1U - out_col);
    }
#elif USE_HORIZONTAL==2
    for (out_row = 0U; out_row < LCD_H; ++out_row)
    {
        if (out_row < THERMAL_OUTPUT_ROWS)
        {
            uint16_t mapped_col = lcd_dma_scale_axis(out_row,
                                                     THERMAL_OUTPUT_ROWS,
                                                     THERMAL_RENDER_WIDTH);
            g_outputRowToInterpCol[out_row] = (uint16_t)(THERMAL_RENDER_WIDTH - 1U - mapped_col);
        }
        else
        {
            g_outputRowToInterpCol[out_row] = 0U;
        }
    }
    for (out_col = 0U; out_col < LCD_W; ++out_col)
    {
        g_outputColToInterpRow[out_col] = out_col;
    }
#else
    for (out_row = 0U; out_row < LCD_H; ++out_row)
    {
        if (out_row < THERMAL_OUTPUT_ROWS)
        {
            g_outputRowToInterpCol[out_row] = lcd_dma_scale_axis(out_row,
                                                                 THERMAL_OUTPUT_ROWS,
                                                                 THERMAL_RENDER_WIDTH);
        }
        else
        {
            g_outputRowToInterpCol[out_row] = 0U;
        }
    }
    for (out_col = 0U; out_col < LCD_W; ++out_col)
    {
        g_outputColToInterpRow[out_col] = (uint16_t)(THERMAL_RENDER_HEIGHT - 1U - out_col);
    }
#endif

    s_renderMappingReady = 1U;
}

static uint8_t lcd_dma_sample_interp_row(const lcd_dma_interp_row_meta_t *meta, uint16_t col)
{
    if (meta == 0)
    {
        return 0U;
    }

    if (meta->kind == LCD_DMA_INTERP_ROW_TOP)
    {
        return g_topEdgeRows[meta->base_index][col];
    }

    if (meta->kind == LCD_DMA_INTERP_ROW_BOTTOM)
    {
        return g_bottomEdgeRows[meta->base_index][col];
    }

    if (meta->ratio == 0U)
    {
        return g_interpRows[meta->base_index][col];
    }

    return (uint8_t)((g_interpRows[meta->base_index][col] * (INTERP_STRIDE - meta->ratio)
                    + g_interpRows[meta->base_index + 1U][col] * meta->ratio) / INTERP_STRIDE);
}
#endif

/* 水平方向插值：将每行 24 列扩展为 240 列。 */
static void build_horizontal_interp_rows(const uint8_t *frameData)
{
    for (int row = 0; row < THERMAL_SRC_HEIGHT; row++) {
        uint8_t *dst = g_interpRows[row];
        const uint8_t *src = &frameData[row * THERMAL_SRC_WIDTH];

        for (int i = 0; i < (THERMAL_SRC_WIDTH - 1); i++) {
            int base = TOP_EDGE_ROWS + i * INTERP_STRIDE;
            uint8_t p0 = src[i];
            uint8_t p1 = src[i + 1];

            dst[base] = p0;
            dst[base + 1] = (uint8_t)((p0 * 9 + p1) / INTERP_STRIDE);
            dst[base + 2] = (uint8_t)((p0 * 8 + p1 * 2) / INTERP_STRIDE);
            dst[base + 3] = (uint8_t)((p0 * 7 + p1 * 3) / INTERP_STRIDE);
            dst[base + 4] = (uint8_t)((p0 * 6 + p1 * 4) / INTERP_STRIDE);
            dst[base + 5] = (uint8_t)((p0 * 5 + p1 * 5) / INTERP_STRIDE);
            dst[base + 6] = (uint8_t)((p0 * 4 + p1 * 6) / INTERP_STRIDE);
            dst[base + 7] = (uint8_t)((p0 * 3 + p1 * 7) / INTERP_STRIDE);
            dst[base + 8] = (uint8_t)((p0 * 2 + p1 * 8) / INTERP_STRIDE);
            dst[base + 9] = (uint8_t)((p0 + p1 * 9) / INTERP_STRIDE);
        }

        dst[THERMAL_RENDER_WIDTH - TOP_EDGE_ROWS] = src[THERMAL_SRC_WIDTH - 1];

        for (int i = TOP_EDGE_ROWS - 1; i >= 0; i--) {
            dst[i] = clamp_to_u8(2 * dst[i + 1] - dst[i + 2]);
        }

        for (int i = THERMAL_RENDER_WIDTH - TOP_EDGE_ROWS + 1; i < THERMAL_RENDER_WIDTH; i++) {
            dst[i] = clamp_to_u8(2 * dst[i - 1] - dst[i - 2]);
        }
    }
}

/* 计算顶部与底部边缘外推行。 */
static void build_vertical_edge_rows(void)
{
    for (int x = 0; x < THERMAL_RENDER_WIDTH; x++) {
        uint8_t row5 = g_interpRows[0][x];
        uint8_t row6 = (uint8_t)((g_interpRows[0][x] * 9 + g_interpRows[1][x]) / INTERP_STRIDE);
        g_topEdgeRows[TOP_EDGE_ROWS - 1][x] = clamp_to_u8(2 * row5 - row6);
    }

    for (int row = TOP_EDGE_ROWS - 2; row >= 0; row--) {
        const uint8_t *next1 = g_topEdgeRows[row + 1];
        const uint8_t *next2 = (row == TOP_EDGE_ROWS - 2) ? g_interpRows[0] : g_topEdgeRows[row + 2];

        for (int x = 0; x < THERMAL_RENDER_WIDTH; x++) {
            g_topEdgeRows[row][x] = clamp_to_u8(2 * next1[x] - next2[x]);
        }
    }

    for (int x = 0; x < THERMAL_RENDER_WIDTH; x++) {
        uint8_t row314 = (uint8_t)((g_interpRows[30][x] + g_interpRows[31][x] * 9) / INTERP_STRIDE);
        uint8_t row315 = g_interpRows[31][x];
        g_bottomEdgeRows[0][x] = clamp_to_u8(2 * row315 - row314);
    }

    for (int row = 1; row < BOTTOM_EDGE_ROWS; row++) {
        const uint8_t *prev1 = g_bottomEdgeRows[row - 1];
        const uint8_t *prev2 = (row == 1) ? g_interpRows[31] : g_bottomEdgeRows[row - 2];

        for (int x = 0; x < THERMAL_RENDER_WIDTH; x++) {
            g_bottomEdgeRows[row][x] = clamp_to_u8(2 * prev1[x] - prev2[x]);
        }
    }
}

/* 在 240x320 插值空间中按坐标采样灰度值。 */
static uint8_t sample_interpolated_gray(uint16_t row, uint16_t col)
{
#if LCD_DMA_STAGE6_6B_ACTIVE
    return lcd_dma_sample_interp_row(&g_interpRowMeta[row], col);
#else
    if (row < TOP_EDGE_ROWS) {
        return g_topEdgeRows[row][col];
    }

    if (row >= BOTTOM_EDGE_START) {
        return g_bottomEdgeRows[row - BOTTOM_EDGE_START][col];
    }

    uint16_t offset = (uint16_t)(row - TOP_EDGE_ROWS);
    uint16_t block = (uint16_t)(offset / INTERP_STRIDE);
    uint16_t ratio = (uint16_t)(offset % INTERP_STRIDE);

    if (ratio == 0U) {
        return g_interpRows[block][col];
    }

    return (uint8_t)((g_interpRows[block][col] * (INTERP_STRIDE - ratio)
                    + g_interpRows[block + 1U][col] * ratio) / INTERP_STRIDE);
#endif
}

/* 兼容旧版方向：按旧坐标定义采样灰度值。 */
static uint8_t sample_legacy_portrait_gray(uint16_t x, uint16_t y)
{
    uint16_t interpRow = (uint16_t)(THERMAL_RENDER_HEIGHT - 1U - y);
    uint16_t interpCol = (uint16_t)(THERMAL_RENDER_WIDTH - 1U - x);
    return sample_interpolated_gray(interpRow, interpCol);
}

/* 将当前输出坐标映射到旧版 portrait 逻辑坐标。 */
static void map_output_to_legacy_portrait(uint16_t outX, uint16_t outY, uint16_t *portraitX, uint16_t *portraitY)
{
#if USE_HORIZONTAL==0 || USE_HORIZONTAL==1
    *portraitX = outX;
    *portraitY = lcd_dma_scale_axis(outY, THERMAL_OUTPUT_ROWS, THERMAL_RENDER_HEIGHT);
#elif USE_HORIZONTAL==2
    *portraitX = lcd_dma_scale_axis(outY, THERMAL_OUTPUT_ROWS, THERMAL_RENDER_WIDTH);
    *portraitY = (uint16_t)(THERMAL_RENDER_HEIGHT - 1U - outX);
#else
    *portraitX = (uint16_t)(THERMAL_RENDER_WIDTH - 1U -
                            lcd_dma_scale_axis(outY, THERMAL_OUTPUT_ROWS, THERMAL_RENDER_WIDTH));
    *portraitY = outX;
#endif
}

/* 生成指定输出行的 RGB565 数据。 */
static void render_output_row_to_buffer(uint16_t outRow, uint8_t *buf)
{
    if (buf == 0)
    {
        return;
    }

    if (outRow >= THERMAL_OUTPUT_ROWS)
    {
        memset(buf, 0, LINE_BUF_SIZE);
        return;
    }

#if LCD_DMA_STAGE6_6B_ACTIVE && (USE_HORIZONTAL==0 || USE_HORIZONTAL==1)
    const lcd_dma_interp_row_meta_t *row_meta = &g_interpRowMeta[g_outputRowToInterpRow[outRow]];

    for (uint16_t outX = 0U; outX < LCD_W; outX++) {
        uint8_t pixel = lcd_dma_sample_interp_row(row_meta, g_outputColToInterpCol[outX]);
#if LCD_DMA_STAGE6_6C_ACTIVE
        buf[2U * outX] = g_colorHighByteLut[pixel];
        buf[2U * outX + 1U] = g_colorLowByteLut[pixel];
#else
        uint16_t color = GCM_Pseudo3[pixel];
        buf[2U * outX] = (uint8_t)(color >> 8);
        buf[2U * outX + 1U] = (uint8_t)(color & 0xFFU);
#endif
    }
#elif LCD_DMA_STAGE6_6B_ACTIVE
    uint16_t interp_col = g_outputRowToInterpCol[outRow];

    for (uint16_t outX = 0U; outX < LCD_W; outX++) {
        const lcd_dma_interp_row_meta_t *row_meta = &g_interpRowMeta[g_outputColToInterpRow[outX]];
        uint8_t pixel = lcd_dma_sample_interp_row(row_meta, interp_col);
#if LCD_DMA_STAGE6_6C_ACTIVE
        buf[2U * outX] = g_colorHighByteLut[pixel];
        buf[2U * outX + 1U] = g_colorLowByteLut[pixel];
#else
        uint16_t color = GCM_Pseudo3[pixel];
        buf[2U * outX] = (uint8_t)(color >> 8);
        buf[2U * outX + 1U] = (uint8_t)(color & 0xFFU);
#endif
    }
#elif LCD_DMA_STAGE6_6C_ACTIVE
    for (uint16_t outX = 0U; outX < LCD_W; outX++) {
        uint16_t portraitX = 0U;
        uint16_t portraitY = 0U;
        map_output_to_legacy_portrait(outX, outRow, &portraitX, &portraitY);

        uint8_t pixel = sample_legacy_portrait_gray(portraitX, portraitY);
        buf[2U * outX] = g_colorHighByteLut[pixel];
        buf[2U * outX + 1U] = g_colorLowByteLut[pixel];
    }
#else
    for (uint16_t outX = 0U; outX < LCD_W; outX++) {
        uint16_t portraitX = 0U;
        uint16_t portraitY = 0U;
        map_output_to_legacy_portrait(outX, outRow, &portraitX, &portraitY);

        uint8_t pixel = sample_legacy_portrait_gray(portraitX, portraitY);
        uint16_t color = GCM_Pseudo3[pixel];
        buf[2U * outX] = (uint8_t)(color >> 8);
        buf[2U * outX + 1U] = (uint8_t)(color & 0xFFU);
    }
#endif

    lcd_dma_overlay_crosshair_row(outRow, buf);
}

/* 初始化 SPI1 -> DMA2_Stream3 发送链路。 */
void MYDMA_Config(void)
{
    RCC_AHB1PeriphClockCmd(RCC_AHB1Periph_DMA2, ENABLE);
    
    DMA_InitTypeDef DMA_InitStructure;
    DMA_DeInit(DMA2_Stream3);
    
    DMA_InitStructure.DMA_Channel = DMA_Channel_3;
    DMA_InitStructure.DMA_Memory0BaseAddr = (u32)lineBuffer[0];
    DMA_InitStructure.DMA_PeripheralBaseAddr = (u32)&SPI1->DR;
    DMA_InitStructure.DMA_DIR = DMA_DIR_MemoryToPeripheral;
    DMA_InitStructure.DMA_BufferSize = LINE_BUF_SIZE;
    DMA_InitStructure.DMA_PeripheralInc = DMA_PeripheralInc_Disable;
    DMA_InitStructure.DMA_MemoryInc = DMA_MemoryInc_Enable;
    DMA_InitStructure.DMA_PeripheralDataSize = DMA_PeripheralDataSize_Byte;
    DMA_InitStructure.DMA_MemoryDataSize = DMA_MemoryDataSize_Byte;
    DMA_InitStructure.DMA_Mode = DMA_Mode_Normal; // 单次传输模式
    DMA_InitStructure.DMA_Priority = DMA_Priority_VeryHigh;
    DMA_InitStructure.DMA_FIFOMode = DMA_FIFOMode_Enable;
    DMA_InitStructure.DMA_FIFOThreshold = DMA_FIFOThreshold_HalfFull;
    DMA_InitStructure.DMA_MemoryBurst = DMA_MemoryBurst_INC4;
    DMA_InitStructure.DMA_PeripheralBurst = DMA_PeripheralBurst_Single;
    DMA_Init(DMA2_Stream3, &DMA_InitStructure);
    
    SPI_I2S_DMACmd(SPI1, SPI_I2S_DMAReq_Tx, ENABLE);

    NVIC_InitTypeDef NVIC_InitStructure;
    NVIC_InitStructure.NVIC_IRQChannel = DMA2_Stream3_IRQn;
#if APP_DISPLAY_STAGE3_ENABLE
    NVIC_InitStructure.NVIC_IRQChannelPreemptionPriority = 5;
#else
    NVIC_InitStructure.NVIC_IRQChannelPreemptionPriority = 0;
#endif
    NVIC_InitStructure.NVIC_IRQChannelSubPriority = 0;
    NVIC_InitStructure.NVIC_IRQChannelCmd = ENABLE;
    NVIC_Init(&NVIC_InitStructure);
    
    DMA_ITConfig(DMA2_Stream3, DMA_IT_TC | DMA_IT_TE, ENABLE);

    DMA_ClearFlag(DMA2_Stream3,
                  DMA_FLAG_FEIF3 |
                  DMA_FLAG_DMEIF3 |
                  DMA_FLAG_TEIF3 |
                  DMA_FLAG_HTIF3 |
                  DMA_FLAG_TCIF3);

    transferComplete = 1;
    s_dma_wait_task = 0;
    s_dma_last_result = 1U;
    s_dma_last_status = APP_PERF_LCD_DMA_STATUS_NONE;
    activeBuffer = 0;
    s_dma_mode = LCD_DMA_MODE_IDLE;
#if LCD_DMA_STAGE6_6B_ACTIVE
    s_renderMappingReady = 0U;
    lcd_dma_init_render_mappings();
#endif
}
/* DMA2 Stream3 中断处理：
 * 处理热成像逐行发送。 */
void DMA2_Stream3_IRQHandler(void)
{
    BaseType_t higher_priority_task_woken = pdFALSE;
    TaskHandle_t waiting_task = 0;

    if (DMA_GetITStatus(DMA2_Stream3, DMA_IT_TCIF3) != RESET)
    {
        app_perf_baseline_record_dma_irq_tc();
        DMA_ClearITPendingBit(DMA2_Stream3, DMA_IT_TCIF3);
        transferComplete = 1U;
        s_dma_last_result = 1U;
        s_dma_last_status = APP_PERF_LCD_DMA_STATUS_OK;
        waiting_task = (TaskHandle_t)s_dma_wait_task;
        s_dma_wait_task = 0;
    }

    if (DMA_GetITStatus(DMA2_Stream3, DMA_IT_TEIF3) != RESET)
    {
        app_perf_baseline_record_dma_irq_te();
        DMA_ClearFlag(DMA2_Stream3,
                      DMA_FLAG_FEIF3 |
                      DMA_FLAG_DMEIF3 |
                      DMA_FLAG_TEIF3 |
                      DMA_FLAG_HTIF3 |
                      DMA_FLAG_TCIF3);
        DMA_Cmd(DMA2_Stream3, DISABLE);
        transferComplete = 1U;
        s_dma_last_result = 0U;
        s_dma_last_status = APP_PERF_LCD_DMA_STATUS_ERROR;
        s_dma_mode = LCD_DMA_MODE_IDLE;
        LCD_CS_Set();
        waiting_task = (TaskHandle_t)s_dma_wait_task;
        s_dma_wait_task = 0;
    }

#if APP_DISPLAY_STAGE3_ENABLE
    if (waiting_task != 0)
    {
        vTaskNotifyGiveFromISR(waiting_task, &higher_priority_task_woken);
        portYIELD_FROM_ISR(higher_priority_task_woken);
    }
#endif
}

/* 8 位 RGB 合成为 RGB565。 */
uint16_t rgb_565(uint16_t COLOR_R,uint16_t COLOR_G,uint16_t COLOR_B){
	uint16_t RGB565=0;
	RGB565=((COLOR_R&0XF8)<<8)+((COLOR_G&0XFC)<<3)+((COLOR_B&0XF8)>>3);
	return RGB565;
}


/* 将灰度值按指定模式映射为伪彩色 RGB565。 */
uint16_t color_code(uint16_t grayValue,uint16_t mode){
	uint16_t colorR,colorG,colorB;
    colorR=0;
    colorG=0;
    colorB=0;
    if (mode==0){
        colorR=abs(0-grayValue);
        colorG=abs(127-grayValue);
        colorB=abs(255-grayValue);
		}
    else if (mode==1){
        if ((grayValue>0) && (grayValue<=63)){
            colorR=0;
            colorG=0;
            colorB=round(grayValue/64.0*255.0);
			}
        else if ((grayValue>=64) && (grayValue<=127)){
            colorR=0;
            colorG=round((grayValue-64)/64.0*255.0);
            colorB=round((127-grayValue)/64.0*255.0);
			}
        else if ((grayValue>=128) && (grayValue<=191)){
            colorR=round((grayValue-128)/64.0*255.0);
            colorG=255;
            colorB=0;
			}
        else if ((grayValue>=192) && (grayValue<=255)){
            colorR=255;
            colorG=round((255-grayValue)/64.0*255.0);
            colorB=0;
			}
		}
    else if (mode==2){ 
        if ((grayValue>0) && (grayValue<=63)){
            colorR=0; 
            colorG=0; 
            colorB=round(grayValue/64.0*255.0); 
			}
        else if ((grayValue>=64) && (grayValue<=95)){
        
            colorR=round((grayValue-63)/32.0*127.0); 
            colorG=round((grayValue-63)/32.0*127.0); 
            colorB=255; 
			}
        else if ((grayValue>=96) && (grayValue<=127)){
        
            colorR=round((grayValue-95)/32.0*127.0)+128; 
            colorG=round((grayValue-95)/32.0*127.0)+128; 
            colorB=round((127-grayValue)/32.0*255.0); 
			}
        else if ((grayValue>=128) && (grayValue<=191)){
            colorR=255; 
            colorG=255; 
            colorB=0;
			}
        else if ((grayValue>=192) && (grayValue<=255)){
        
            colorR=255; 
            colorG=255; 
            colorB=round((grayValue-192)/64*255.0);
			}
		}
    else if (mode==3){  
        colorR=0; 
        colorG=0;
        colorB=0;
        if ((grayValue>0) && (grayValue<=16)){
            colorR=0;} 
        else if ((grayValue>=17) && (grayValue<=140)){ 
            colorR=round((grayValue-16)/124.0*255.0);
			}
        else if ((grayValue>=141) && (grayValue<=255)){  
            colorR=255; 
			}
		
        if ((grayValue>0) && (grayValue<=101)){
            colorG=0;
			}
        else if ((grayValue>=102) && (grayValue<=218)){
            colorG=round((grayValue-101)/117.0*255.0);
			}
        else if ((grayValue>=219) && (grayValue<=255)){  
            colorG=255; 
			}
        if ((grayValue>0) && (grayValue<=91)){
            colorB=28+round((grayValue-0)/91.0*100.0);
			}
        else if ((grayValue>=92) && (grayValue<=120)){
            colorB=round((120-grayValue)/29.0*128.0);
			}
        else if ((grayValue>=129) && (grayValue<=214)){
            colorB=0;
			}			
        else if ((grayValue>=215) && (grayValue<=255)){
            colorB=round((grayValue-214)/41.0*255.0);
			}
		}
    else if (mode==4){ 
        if ((grayValue>0) && (grayValue<=31)){
            colorR=0; 
            colorG=0; 
            colorB=round(grayValue/32.0*255.0);
			}			
        else if ((grayValue>=32) && (grayValue<=63)){
            colorR=0; 
            colorG=round((grayValue-32)/32.0*255.0); 
            colorB=255;
			}			
        else if ((grayValue>=64) && (grayValue<=95)){
            colorR=0; 
            colorG=255; 
            colorB=round((95-grayValue)/32.0*255.0);
			}
        else if ((grayValue>=96) && (grayValue<=127)){
            colorR=round((grayValue-96)/32.0*255.0); 
            colorG=255;
            colorB=0;
			}
        else if ((grayValue>=128) && (grayValue<=191)){
            colorR=255; 
            colorG=round((191-grayValue)/64.0*255.0); 
            colorB=0;
			}
        else if ((grayValue>=192) && (grayValue<=255)){
            colorR=255;
            colorG=round((grayValue-192)/64.0*255.0);
            colorB=round((grayValue-192)/64.0*255.0); 
			}
		}
    else if (mode==5){
        if ((grayValue>0) && (grayValue<=63)){
            colorR=0;
            colorG=round((grayValue-0)/64.0*255.0);
            colorB=255;
			}
        else if ((grayValue>=64) && (grayValue<=95)){
            colorR=0; 
            colorG=255; 
            colorB=round((95-grayValue)/32.0*255.0);
			}
        else if ((grayValue>=96) && (grayValue<=127)){
            colorR=round((grayValue-96)/32.0*255.0); 
            colorG=255; 
            colorB=0;
			}
        else if ((grayValue>=128) && (grayValue<=191)){
            colorR=255 ;
            colorG=round((191-grayValue)/64.0*255.0); 
            colorB=0;
			}
        else if ((grayValue>=192) && (grayValue<=255)){
            colorR=255;
            colorG=round((grayValue-192)/64.0*255.0);
            colorB=round((grayValue-192)/64.0*255.0);
			}
		}
    else if (mode==6){
        if ((grayValue>0) && (grayValue<=51)){
            colorR=0;
            colorG=grayValue*5;
            colorB=255;
			}
        else if ((grayValue>=52) && (grayValue<=102)){
            colorR=0;
            colorG=255;
            colorB=255-(grayValue-51)*5;
			}
        else if ((grayValue>=103) && (grayValue<=153)){
            colorR=(grayValue-102)*5;
            colorG=255;
            colorB=0;
			}
        else if ((grayValue>=154) && (grayValue<=204)){
            colorR=255;
            colorG=round(255.0-128.0*(grayValue-153.0)/51.0);
            colorB=0;
			}
        else if ((grayValue>=205) && (grayValue<=255)){
            colorR=255;
            colorG=round(127.0-127.0*(grayValue-204.0)/51.0);
            colorB=0;
			}
		}
    else if (mode==7){
        if ((grayValue>0) && (grayValue<=63)){
            colorR=0;
            colorG=round((64-grayValue)/64.0*255.0);
            colorB=255;
			}
        else if ((grayValue>=64) && (grayValue<=127)){
            colorR=0 ;
            colorG=round((grayValue-64)/64.0*255.0);
            colorB=round((127-grayValue)/64.0*255.0);
			}
        else if ((grayValue>=128) && (grayValue<=191)){
            colorR=round((grayValue-128)/64.0*255.0);
            colorG=255;
            colorB=0;
			}
        else if ((grayValue>=192) && (grayValue<=255)){
            colorR=255;
            colorG=round((255-grayValue)/64.0*255.0);
            colorB=0;
			}
		}
    else if (mode==8){
        if ((grayValue>0) && (grayValue<=63)){
            colorR=0;
            colorG=254-4*grayValue;
            colorB=255;
			}
        else if ((grayValue>=64) && (grayValue<=127)){
            colorR=0;
            colorG=4*grayValue-254;
            colorB=510-4*grayValue;
			}
        else if ((grayValue>=128) && (grayValue<=191)){
            colorR=4*grayValue-510;
            colorG=255;
            colorB=0;
			}
        else if ((grayValue>=192) && (grayValue<=255)){
            colorR=255;
            colorG=1022-4*grayValue;
            colorB=0;
			}
		}
    else{
        colorR=grayValue;
        colorG=grayValue; 
        colorB=grayValue;
	}
	return rgb_565(colorR,colorG,colorB);
}

/* 生成整套 256 色伪彩色查找表。 */
void color_listcode(uint16_t *color_list,uint16_t mode ){
	uint16_t i;
	for (i=0;i<256;i++){
        uint16_t color = color_code(i,mode);
		color_list[i]=color;
#if LCD_DMA_STAGE6_6C_ACTIVE
        g_colorHighByteLut[i] = (uint8_t)(color >> 8);
        g_colorLowByteLut[i] = (uint8_t)(color & 0xFFU);
#endif
	}
}
/* 设置伪彩色模式并重建 LUT。 */
void set_color_mode(uint16_t mode) {
    color_listcode(GCM_Pseudo3, mode); // 重建 LUT
}

/* 热成像显示主入口：
 * 输入 24x32 灰度帧，插值后逐行 DMA 输出到 LCD。 */
uint8_t LCD_Disp_Thermal_Interpolated_DMA(uint8_t *data24x32)
{
    uint32_t start_cycle = app_perf_baseline_cycle_now();
    uint8_t tx_buffer_index = 0U;
    uint8_t fill_buffer_index = 1U;

    if (data24x32 == 0)
    {
        return 0U;
    }

    app_perf_baseline_record_lcd_dma_enter();
#if LCD_DMA_STAGE6_6B_ACTIVE
    lcd_dma_init_render_mappings();
#endif
    build_horizontal_interp_rows(data24x32);
    build_vertical_edge_rows();

    LCD_Address_Set(0, 0, (u16)(LCD_W - 1), (u16)(THERMAL_OUTPUT_ROWS - 1U));
    LCD_DC_Set();
    LCD_CS_Clr();

    activeBuffer = tx_buffer_index;
    render_output_row_to_buffer(0U, lineBuffer[tx_buffer_index]);
    if (start_dma_line_transfer(lineBuffer[tx_buffer_index]) == 0U) {
        LCD_CS_Set();
        s_dma_mode = LCD_DMA_MODE_IDLE;
        app_perf_baseline_record_lcd_dma_result(app_perf_baseline_elapsed_us(start_cycle),
                                                s_dma_last_status);
        return 0U;
    }

    for (uint16_t row = 1U; row < THERMAL_OUTPUT_ROWS; row++) {
        render_output_row_to_buffer(row, lineBuffer[fill_buffer_index]);

        if (wait_for_dma_transfer_complete() == 0U) {
            LCD_CS_Set();
            s_dma_mode = LCD_DMA_MODE_IDLE;
            app_perf_baseline_record_lcd_dma_result(app_perf_baseline_elapsed_us(start_cycle),
                                                    s_dma_last_status);
            return 0U;
        }

        tx_buffer_index = fill_buffer_index;
        fill_buffer_index ^= 1U;
        activeBuffer = tx_buffer_index;
        if (start_dma_line_transfer(lineBuffer[tx_buffer_index]) == 0U) {
            LCD_CS_Set();
            s_dma_mode = LCD_DMA_MODE_IDLE;
            app_perf_baseline_record_lcd_dma_result(app_perf_baseline_elapsed_us(start_cycle),
                                                    s_dma_last_status);
            return 0U;
        }
    }

    if (wait_for_dma_transfer_complete() == 0U) {
        LCD_CS_Set();
        s_dma_mode = LCD_DMA_MODE_IDLE;
        app_perf_baseline_record_lcd_dma_result(app_perf_baseline_elapsed_us(start_cycle),
                                                s_dma_last_status);
        return 0U;
    }

    LCD_CS_Set();
    s_dma_mode = LCD_DMA_MODE_IDLE;
    app_perf_baseline_record_lcd_dma_result(app_perf_baseline_elapsed_us(start_cycle),
                                            APP_PERF_LCD_DMA_STATUS_OK);
    return 1U;
}
