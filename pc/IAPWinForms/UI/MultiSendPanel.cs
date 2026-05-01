// 发送区主逻辑，负责单发、文件发送和多发面板初始化。
using Microsoft.Data.Sqlite;

namespace IAPWinForms;

// 管理发送区的模式切换和各类发送入口。
public partial class Form1
{
    // 初始化发送相关定时器、面板和本地缓存。
    private void InitializeMultiSendPanel()
    {
        autoSendTimer.Interval = 1000;
        autoSendTimer.Tick += autoSendTimer_Tick;
        fileSendTimer.Interval = 10;
        fileSendTimer.Tick += fileSendTimer_Tick;
        saveDebounceTimer.Interval = 500;
        saveDebounceTimer.Tick += saveDebounceTimer_Tick;
        EnsureSendModeOption(SendModeFirmwareEncryption);
        EnsureSendModeOption(SendModeIapPackage);

        BuildMultiSendPanel();
        BuildYModemPanel();
        BuildFirmwareEncryptionPanel();
        BuildIapPackagePanel();
        BuildOneClickDebugPanel();
        BuildFileSendProgressBar();
        InitializeMultiSendStorage();
        LoadMultiSendCache();
        if (comboBoxSendMode.Items.Count > 0)
        {
            comboBoxSendMode.SelectedIndex = 0;
        }
        if (comboBoxProtocolRecv.Items.Count > 0)
        {
            comboBoxProtocolRecv.SelectedIndex = 0;
        }
        if (comboBoxProtocolSend.Items.Count > 0)
        {
            comboBoxProtocolSend.SelectedIndex = 0;
        }
        if (comboBoxOneClickDebugMode != null && comboBoxOneClickDebugMode.Items.Count > 0)
        {
            comboBoxOneClickDebugMode.SelectedIndex = 0;
        }
        ApplySendModeUI();
    }

    private void EnsureSendModeOption(string optionText)
    {
        if (!comboBoxSendMode.Items.Contains(optionText))
        {
            comboBoxSendMode.Items.Add(optionText);
        }
    }

    // 发送主输入框里的内容。
    private void buttonSend_Click(object? sender, EventArgs e)
    {
        if (!_serialPortManager.IsOpen)
        {
            MessageBox.Show("串口未打开", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            // 先构造负载，再走统一发送队列。
            string raw = textBoxMainBuffer.Text.Trim();
            if (string.IsNullOrEmpty(raw)) return;
            byte[] payload = _commandProcessor.BuildPayload(raw, checkBoxHexSend.Checked, checkBoxNewLine.Checked, GetSelectedEncoding());
            _dataSender.Enqueue(payload);
            _dataSender.ProcessQueue(data =>
            {
                _serialPortManager.Send(data);
                return true;
            });
            Interlocked.Add(ref totalTxBytes, payload.Length);
            UpdateByteCounter();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"发送失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // 清空主发送输入区。
    private void buttonClearSend_Click(object? sender, EventArgs e) => textBoxMainBuffer.Clear();

    // 选择待发送文件并加载到发送区。
    private void buttonLoadFile_Click(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new() { Filter = "所有文件|*.*" };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        LoadFileIntoSendArea(dialog.FileName, previewTextContent: true);
    }

    // 启动分块文件发送。
    private void buttonSendFile_Click(object? sender, EventArgs e)
    {
        if (!_serialPortManager.IsOpen)
        {
            MessageBox.Show("串口未打开", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (fileSendBuffer.Length == 0)
        {
            MessageBox.Show("请先打开文件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        fileSendOffset = 0;
        UpdateFileSendProgress(0);
        fileSendTimer.Start();
    }

    // 停止当前文件发送并保留进度。
    private void buttonStopSend_Click(object? sender, EventArgs e)
    {
        fileSendTimer.Stop();
        int percent = fileSendBuffer.Length == 0 ? 0 : (int)Math.Round(fileSendOffset * 100.0 / fileSendBuffer.Length);
        UpdateFileSendProgress(percent);
    }

    // 自动发送定时触发入口。
    private void autoSendTimer_Tick(object? sender, EventArgs e)
    {
        if (_serialPortManager.IsOpen && checkBoxAutoSend.Checked) buttonSend_Click(sender, EventArgs.Empty);
    }

    // 按固定块大小持续发送文件内容。
    private void fileSendTimer_Tick(object? sender, EventArgs e)
    {
        if (!_serialPortManager.IsOpen || fileSendBuffer.Length == 0)
        {
            fileSendTimer.Stop();
            return;
        }
        const int chunk = 256;
        int remain = fileSendBuffer.Length - fileSendOffset;
        if (remain <= 0)
        {
            fileSendTimer.Stop();
            UpdateFileSendProgress(100);
            return;
        }
        int count = Math.Min(chunk, remain);
        byte[] payload = new byte[count];
        Array.Copy(fileSendBuffer, fileSendOffset, payload, 0, count);
        _serialPortManager.Send(payload);
        fileSendOffset += count;
        Interlocked.Add(ref totalTxBytes, count);
        UpdateByteCounter();
        UpdateFileSendProgress((int)Math.Round(fileSendOffset * 100.0 / fileSendBuffer.Length));
    }

    // 发送模式变化时切换右侧面板。
    private void comboBoxSendMode_SelectedIndexChanged(object? sender, EventArgs e) => ApplySendModeUI();
    // 实时同步自动发送周期。
    private void numericAutoSendInterval_ValueChanged(object? sender, EventArgs e) => autoSendTimer.Interval = (int)numericAutoSendInterval.Value;
    // 自动发送开关。
    private void checkBoxAutoSend_CheckedChanged(object? sender, EventArgs e) { if (checkBoxAutoSend.Checked) autoSendTimer.Start(); else autoSendTimer.Stop(); }
    // ASCII 模式选中时关闭 HEX 发送。
    private void radioSendAscii_CheckedChanged(object? sender, EventArgs e) { if (radioSendAscii.Checked) checkBoxHexSend.Checked = false; }
    // HEX 模式选中时取消 ASCII 单选。
    private void checkBoxHexSend_CheckedChanged(object? sender, EventArgs e) { if (checkBoxHexSend.Checked) radioSendAscii.Checked = false; }

    // 构建多条发送面板和页脚导航。
    private void BuildMultiSendPanel()
    {
        panelMultiSendMode = new Panel { Name = "panelMultiSendMode", Location = new Point(8, 436), Size = new Size(998, 205), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Visible = false, BackColor = Color.White };
        Panel leftRows = new() { Location = new Point(0, 0), Size = new Size(495, 154), BackColor = Color.White };
        Panel rightRows = new() { Location = new Point(503, 0), Size = new Size(495, 154), BackColor = Color.White };
        for (int i = 0; i < 10; i++) CreateMultiSendRow(i < 5 ? leftRows : rightRows, i, i < 5 ? i : i - 5);
        panelMultiSendMode.Controls.Add(leftRows);
        panelMultiSendMode.Controls.Add(rightRows);

        Panel footer = new() { Location = new Point(0, 160), Size = new Size(998, 30), Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
        buttonDeletePage = CreateNavButton("删除此页", 84, navDeletePage_Click);
        buttonAddPage = CreateNavButton("添加页码", 154, navAddPage_Click);
        Button btnFirst = CreateNavButton("首页", 236, navFirst_Click);
        Button btnPrev = CreateNavButton("上页", 288, navPrev_Click);
        Button btnNext = CreateNavButton("下页", 348, navNext_Click);
        Button btnLast = CreateNavButton("尾页", 408, navLast_Click);
        footer.Controls.AddRange([buttonDeletePage, buttonAddPage, btnFirst, btnPrev, btnNext, btnLast]);
        Label labelPage = new() { Text = "页码", Location = new Point(472, 5), Size = new Size(40, 20) };
        textBoxPageInput = new TextBox { Location = new Point(512, 2), Size = new Size(48, 27), Text = "1" };
        textBoxPageInput.KeyDown += textBoxPageInput_KeyDown;
        textBoxPageInput.Leave += multiSendControl_Leave;
        Button btnJump = new() { Text = "跳转", Location = new Point(564, 1), Size = new Size(50, 27), FlatStyle = FlatStyle.Flat };
        btnJump.Click += navJump_Click;
        labelPageStatus = new Label { Text = "1/1", Location = new Point(8, 5), Size = new Size(70, 20), TextAlign = ContentAlignment.MiddleLeft };
        checkBoxEnableNumberKeyboard = new CheckBox { Text = "启用数字键盘", Location = new Point(624, 4), Size = new Size(120, 24) };
        checkBoxEnableNumberKeyboard.CheckedChanged += checkBoxEnableNumberKeyboard_CheckedChanged;
        checkBoxEnableNumberKeyboard.Leave += multiSendControl_Leave;
        footer.Controls.AddRange([labelPage, textBoxPageInput, btnJump, labelPageStatus, checkBoxEnableNumberKeyboard]);
        panelMultiSendMode.Controls.Add(footer);
        panelRight.Controls.Add(panelMultiSendMode);
        panelMultiSendMode.BringToFront();
        EnsurePageInitialized(1);
        RenderMultiSendPage(1);
        KeyPreview = true;
        KeyDown += Form1_KeyDown;
    }

    // 创建一行快捷发送输入框和发送按钮。
    private void CreateMultiSendRow(Panel parent, int rowIndex, int row)
    {
        int y = row * 30;
        Label label = new() { Text = rowIndex.ToString(), Location = new Point(0, y + 5), Size = new Size(24, 20) };
        TextBox input = new() { Location = new Point(26, y + 2), Size = new Size(378, 27), Tag = rowIndex };
        input.TextChanged += multiSendInput_TextChanged;
        input.Leave += multiSendControl_Leave;
        Button send = new() { Text = rowIndex.ToString(), Location = new Point(410, y + 1), Size = new Size(38, 28), Tag = rowIndex, FlatStyle = FlatStyle.Flat };
        send.Click += quickSendDigit_Click;
        multiSendInputs[rowIndex] = input;
        multiSendButtons[rowIndex] = send;
        parent.Controls.AddRange([label, input, send]);
    }

    // 创建分页导航按钮。
    private Button CreateNavButton(string text, int x, EventHandler click)
    {
        Button button = new() { Text = text, Location = new Point(x, 0), Size = new Size(60, 28), FlatStyle = FlatStyle.Flat };
        button.Click += click;
        return button;
    }
}
