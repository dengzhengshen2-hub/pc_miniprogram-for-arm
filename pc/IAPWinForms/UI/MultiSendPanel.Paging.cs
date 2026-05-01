// 多发分页与缓存逻辑，负责 SQLite 持久化和快捷发送。
using Microsoft.Data.Sqlite;

namespace IAPWinForms;

// 管理多页指令缓存、导航和数字键快捷发送。
public partial class Form1
{
    // 初始化多发缓存表。
    private void InitializeMultiSendStorage()
    {
        using SqliteConnection conn = new($"Data Source={cacheDbPath}");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS multi_send_cache (
                page_no INTEGER NOT NULL,
                row_idx INTEGER NOT NULL,
                hex_text TEXT NOT NULL,
                current_page INTEGER NOT NULL,
                keyboard_enabled INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY(page_no, row_idx)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // 从 SQLite 恢复多发内容和当前页。
    private void LoadMultiSendCache()
    {
        multiSendPages.Clear();
        int loadedCurrentPage = 1;
        bool keyboardEnabled = false;
        using SqliteConnection conn = new($"Data Source={cacheDbPath}");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT page_no,row_idx,hex_text,current_page,keyboard_enabled FROM multi_send_cache ORDER BY page_no,row_idx";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int page = reader.GetInt32(0);
            int row = reader.GetInt32(1);
            EnsurePageInitialized(page);
            multiSendPages[page][row] = reader.GetString(2);
            loadedCurrentPage = reader.GetInt32(3);
            keyboardEnabled = reader.GetInt32(4) == 1;
        }
        if (multiSendPages.Count == 0) EnsurePageInitialized(1);
        currentMultiSendPage = Math.Clamp(loadedCurrentPage, 1, 999);
        if (!multiSendPages.ContainsKey(currentMultiSendPage)) currentMultiSendPage = 1;
        if (checkBoxEnableNumberKeyboard != null) checkBoxEnableNumberKeyboard.Checked = keyboardEnabled;
        RenderMultiSendPage(currentMultiSendPage);
    }

    // 确保指定页在内存中已创建。
    private void EnsurePageInitialized(int page)
    {
        if (page is < 1 or > 999) return;
        if (!multiSendPages.ContainsKey(page)) multiSendPages[page] = new string[10];
    }

    // 返回当前最大页号。
    private int GetTotalPages() => multiSendPages.Count == 0 ? 1 : multiSendPages.Keys.Max();

    // 把指定页内容渲染到 UI。
    private void RenderMultiSendPage(int page)
    {
        if (textBoxPageInput == null || labelPageStatus == null) return;
        EnsurePageInitialized(page);
        currentMultiSendPage = page;
        loadingMultiSendUi = true;
        try
        {
            string[] records = multiSendPages[page];
            for (int i = 0; i < 10; i++)
            {
                if (multiSendInputs[i] != null) multiSendInputs[i].Text = records[i] ?? string.Empty;
                ResetRowColor(i);
            }
            textBoxPageInput.Text = page.ToString();
            labelPageStatus.Text = $"{page}/{GetTotalPages()}";
            if (buttonDeletePage != null) buttonDeletePage.Enabled = GetTotalPages() > 1;
        }
        finally
        {
            loadingMultiSendUi = false;
        }
    }

    // 合并短时间内的多次保存请求。
    private void QueueSaveMultiSendCache() { saveDebounceTimer.Stop(); saveDebounceTimer.Start(); }
    // 防抖到期后落盘多发缓存。
    private void saveDebounceTimer_Tick(object? sender, EventArgs e) { saveDebounceTimer.Stop(); SaveMultiSendCache(); }

    // 将当前多发缓存整体写回 SQLite。
    private void SaveMultiSendCache()
    {
        if (loadingMultiSendUi) return;
        using SqliteConnection conn = new($"Data Source={cacheDbPath}");
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        using (SqliteCommand clear = conn.CreateCommand()) { clear.CommandText = "DELETE FROM multi_send_cache"; clear.ExecuteNonQuery(); }
        bool keyboardEnabled = checkBoxEnableNumberKeyboard?.Checked == true;
        foreach ((int page, string[] rows) in multiSendPages.OrderBy(x => x.Key))
        {
            for (int row = 0; row < 10; row++)
            {
                using SqliteCommand insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO multi_send_cache(page_no,row_idx,hex_text,current_page,keyboard_enabled,updated_at) VALUES($p,$r,$h,$c,$k,$t)";
                insert.Parameters.AddWithValue("$p", page);
                insert.Parameters.AddWithValue("$r", row);
                insert.Parameters.AddWithValue("$h", rows[row] ?? string.Empty);
                insert.Parameters.AddWithValue("$c", currentMultiSendPage);
                insert.Parameters.AddWithValue("$k", keyboardEnabled ? 1 : 0);
                insert.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                insert.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    // 输入变化时同步更新当前页缓存。
    private void multiSendInput_TextChanged(object? sender, EventArgs e)
    {
        if (loadingMultiSendUi) return;
        if (sender is not TextBox textBox || textBox.Tag is not int row) return;
        string value = textBox.Text.Length > 128 ? textBox.Text[..128] : textBox.Text;
        if (textBox.Text != value) { textBox.Text = value; textBox.SelectionStart = value.Length; }
        EnsurePageInitialized(currentMultiSendPage);
        multiSendPages[currentMultiSendPage][row] = value;
    }

    // 快捷按钮发送对应行内容。
    private async void quickSendDigit_Click(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.Tag is not int row) return;
        await SendMultiRowAsync(row);
    }

    // 发送多发区指定行的指令。
    private async Task SendMultiRowAsync(int row)
    {
        if (row is < 0 or > 9 || !_serialPortManager.IsOpen) return;
        string raw = multiSendInputs[row]?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return;
        try
        {
            byte[] payload = _commandProcessor.BuildPayload(raw, checkBoxHexSend.Checked, checkBoxNewLine.Checked, GetSelectedEncoding());
            await Task.Run(() => _serialPortManager.Send(payload));
            Interlocked.Add(ref totalTxBytes, payload.Length);
            UpdateByteCounter();
            await FlashRowAsync(row, Color.FromArgb(213, 255, 221));
        }
        catch
        {
            await FlashRowAsync(row, Color.FromArgb(255, 210, 210));
        }
    }

    // 高亮指定行，给发送结果做视觉反馈。
    private void HighlightRow(int row, Color color) { if (multiSendInputs[row] != null) multiSendInputs[row].BackColor = color; if (multiSendButtons[row] != null) multiSendButtons[row].BackColor = color; }
    // 恢复指定行默认颜色。
    private void ResetRowColor(int row) => HighlightRow(row, Color.White);
    // 短暂闪烁行颜色表示发送成功或失败。
    private async Task FlashRowAsync(int row, Color color) { HighlightRow(row, color); await Task.Delay(500); if (!IsDisposed) BeginInvoke(() => ResetRowColor(row)); }
    // 跳到第一页。
    private void navFirst_Click(object? sender, EventArgs e) => RenderMultiSendPage(1);
    // 切到上一页。
    private void navPrev_Click(object? sender, EventArgs e) => RenderMultiSendPage(Math.Max(1, currentMultiSendPage - 1));
    // 切到下一页，必要时自动建页。
    private void navNext_Click(object? sender, EventArgs e) { int t = currentMultiSendPage + 1; if (t > 999) { ShowPageWarning("页数上限为 999"); return; } EnsurePageInitialized(t); RenderMultiSendPage(t); }
    // 跳到最后一页。
    private void navLast_Click(object? sender, EventArgs e) => RenderMultiSendPage(GetTotalPages());
    // 新增一页并切换过去。
    private void navAddPage_Click(object? sender, EventArgs e) { int t = GetTotalPages() + 1; if (t > 999) { ShowPageWarning("页数上限为 999"); return; } EnsurePageInitialized(t); RenderMultiSendPage(t); QueueSaveMultiSendCache(); }
    // 删除当前页并保持页码有效。
    private void navDeletePage_Click(object? sender, EventArgs e) { if (GetTotalPages() <= 1) return; multiSendPages.Remove(currentMultiSendPage); int t = Math.Max(1, Math.Min(currentMultiSendPage, GetTotalPages())); EnsurePageInitialized(t); RenderMultiSendPage(t); QueueSaveMultiSendCache(); }
    // 从页码输入框跳转。
    private void navJump_Click(object? sender, EventArgs e) => JumpToInputPage();
    // 回车时执行页码跳转。
    private void textBoxPageInput_KeyDown(object? sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { JumpToInputPage(); e.SuppressKeyPress = true; } }

    // 校验输入页码后切换页面。
    private void JumpToInputPage()
    {
        if (textBoxPageInput == null) return;
        if (!int.TryParse(textBoxPageInput.Text, out int page) || page is < 1 or > 999) { ShowPageWarning("页码范围 1-999"); return; }
        EnsurePageInitialized(page);
        RenderMultiSendPage(page);
        QueueSaveMultiSendCache();
    }

    // 在页码框附近弹出轻量提示。
    private void ShowPageWarning(string text) { if (textBoxPageInput != null) warningToolTip.Show(text, textBoxPageInput, 0, -20, 1800); }
    // 数字键盘开关变化后保存设置。
    private void checkBoxEnableNumberKeyboard_CheckedChanged(object? sender, EventArgs e) => QueueSaveMultiSendCache();
    // 相关控件失焦时触发缓存保存。
    private void multiSendControl_Leave(object? sender, EventArgs e) => QueueSaveMultiSendCache();

    // 启用数字键盘模式时，按数字直接发送对应行。
    private async void Form1_KeyDown(object? sender, KeyEventArgs e)
    {
        if (checkBoxEnableNumberKeyboard?.Checked != true || e.Control || e.Alt || e.Shift) return;
        int? row = e.KeyCode switch
        {
            Keys.D0 or Keys.NumPad0 => 0, Keys.D1 or Keys.NumPad1 => 1, Keys.D2 or Keys.NumPad2 => 2, Keys.D3 or Keys.NumPad3 => 3, Keys.D4 or Keys.NumPad4 => 4,
            Keys.D5 or Keys.NumPad5 => 5, Keys.D6 or Keys.NumPad6 => 6, Keys.D7 or Keys.NumPad7 => 7, Keys.D8 or Keys.NumPad8 => 8, Keys.D9 or Keys.NumPad9 => 9, _ => null
        };
        if (!row.HasValue) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        await SendMultiRowAsync(row.Value);
    }
}
