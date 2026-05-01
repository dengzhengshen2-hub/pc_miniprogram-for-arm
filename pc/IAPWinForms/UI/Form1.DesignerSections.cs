// Designer 拆分文件，负责各区域控件的静态布局。
namespace IAPWinForms;

// 将大体量 Designer 代码拆成多个初始化方法，便于维护。
public partial class Form1
{
    // 初始化主布局容器和左右区域。
    private void InitializeLayoutShell()
    {
        splitContainerMain.Dock = DockStyle.Fill;
        splitContainerMain.FixedPanel = FixedPanel.Panel1;
        splitContainerMain.IsSplitterFixed = true;
        splitContainerMain.Location = new Point(0, 0);
        splitContainerMain.Name = "splitContainerMain";
        splitContainerMain.Panel1.BackColor = Color.FromArgb(22, 25, 30);
        splitContainerMain.Panel1.Controls.Add(panelLeft);
        splitContainerMain.Panel2.BackColor = Color.FromArgb(20, 23, 27);
        splitContainerMain.Panel2.Controls.Add(panelRight);
        splitContainerMain.Size = new Size(1260, 680);
        splitContainerMain.SplitterDistance = 240;
        splitContainerMain.SplitterWidth = 6;
        splitContainerMain.TabIndex = 0;

        panelLeft.Controls.Add(groupBoxSendSetting);
        panelLeft.Controls.Add(groupBoxReceiveSetting);
        panelLeft.Controls.Add(groupBoxSerial);
        panelLeft.Dock = DockStyle.Fill;
        panelLeft.Location = new Point(0, 0);
        panelLeft.Name = "panelLeft";
        panelLeft.Padding = new Padding(6, 8, 6, 8);
        panelLeft.Size = new Size(240, 680);
        panelLeft.TabIndex = 0;

        panelRight.Controls.Add(buttonStopSend);
        panelRight.Controls.Add(buttonSendFile);
        panelRight.Controls.Add(buttonClearSend);
        panelRight.Controls.Add(buttonSend);
        panelRight.Controls.Add(buttonLoadFile);
        panelRight.Controls.Add(textBoxSend);
        panelRight.Controls.Add(textBoxMainBuffer);
        panelRight.Controls.Add(textBoxReceive);
        panelRight.Dock = DockStyle.Fill;
        panelRight.Location = new Point(0, 0);
        panelRight.Name = "panelRight";
        panelRight.Padding = new Padding(8, 8, 8, 8);
        panelRight.Size = new Size(1014, 680);
        panelRight.TabIndex = 1;
    }

    // 初始化串口配置区域的静态控件。
    private void InitializeSerialSettingsControls()
    {
        groupBoxSerial.Controls.Add(buttonConnect);
        groupBoxSerial.Controls.Add(radioConnectState);
        groupBoxSerial.Controls.Add(comboBoxParity);
        groupBoxSerial.Controls.Add(comboBoxStopBits);
        groupBoxSerial.Controls.Add(comboBoxDataBits);
        groupBoxSerial.Controls.Add(comboBoxBaudRate);
        groupBoxSerial.Controls.Add(comboBoxPort);
        groupBoxSerial.Controls.Add(labelAction);
        groupBoxSerial.Controls.Add(labelParity);
        groupBoxSerial.Controls.Add(labelStopBits);
        groupBoxSerial.Controls.Add(labelDataBits);
        groupBoxSerial.Controls.Add(labelBaudRate);
        groupBoxSerial.Controls.Add(labelPort);
        groupBoxSerial.ForeColor = Color.White;
        groupBoxSerial.Location = new Point(8, 8);
        groupBoxSerial.Name = "groupBoxSerial";
        groupBoxSerial.Size = new Size(224, 238);
        groupBoxSerial.TabIndex = 0;
        groupBoxSerial.TabStop = false;
        groupBoxSerial.Text = "串口配置";

        labelPort.AutoSize = true;
        labelPort.Location = new Point(12, 31);
        labelPort.Name = "labelPort";
        labelPort.Size = new Size(39, 20);
        labelPort.TabIndex = 0;
        labelPort.Text = "端口";
        labelBaudRate.AutoSize = true;
        labelBaudRate.Location = new Point(12, 63);
        labelBaudRate.Name = "labelBaudRate";
        labelBaudRate.Size = new Size(54, 20);
        labelBaudRate.TabIndex = 1;
        labelBaudRate.Text = "波特率";
        labelDataBits.AutoSize = true;
        labelDataBits.Location = new Point(12, 95);
        labelDataBits.Name = "labelDataBits";
        labelDataBits.Size = new Size(54, 20);
        labelDataBits.TabIndex = 2;
        labelDataBits.Text = "数据位";
        labelStopBits.AutoSize = true;
        labelStopBits.Location = new Point(12, 127);
        labelStopBits.Name = "labelStopBits";
        labelStopBits.Size = new Size(54, 20);
        labelStopBits.TabIndex = 3;
        labelStopBits.Text = "停止位";
        labelParity.AutoSize = true;
        labelParity.Location = new Point(12, 159);
        labelParity.Name = "labelParity";
        labelParity.Size = new Size(54, 20);
        labelParity.TabIndex = 4;
        labelParity.Text = "校验位";
        labelAction.AutoSize = true;
        labelAction.Location = new Point(12, 191);
        labelAction.Name = "labelAction";
        labelAction.Size = new Size(39, 20);
        labelAction.TabIndex = 5;
        labelAction.Text = "操作";

        comboBoxPort.BackColor = Color.FromArgb(44, 50, 57);
        comboBoxPort.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxPort.IntegralHeight = false;
        comboBoxPort.FlatStyle = FlatStyle.Standard;
        comboBoxPort.ForeColor = Color.White;
        comboBoxPort.FormattingEnabled = true;
        comboBoxPort.Location = new Point(74, 27);
        comboBoxPort.Name = "comboBoxPort";
        comboBoxPort.Size = new Size(150, 28);
        comboBoxPort.TabIndex = 6;

        comboBoxBaudRate.BackColor = Color.FromArgb(44, 50, 57);
        comboBoxBaudRate.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxBaudRate.FlatStyle = FlatStyle.Standard;
        comboBoxBaudRate.ForeColor = Color.White;
        comboBoxBaudRate.FormattingEnabled = true;
        comboBoxBaudRate.Items.AddRange(new object[] { "2400", "4800", "9600", "19200", "38400", "57600", "115200", "128000", "230400", "256000", "460800", "921600" });
        comboBoxBaudRate.Location = new Point(74, 59);
        comboBoxBaudRate.Name = "comboBoxBaudRate";
        comboBoxBaudRate.Size = new Size(136, 28);
        comboBoxBaudRate.TabIndex = 7;

        comboBoxDataBits.BackColor = Color.FromArgb(44, 50, 57);
        comboBoxDataBits.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxDataBits.FlatStyle = FlatStyle.Standard;
        comboBoxDataBits.ForeColor = Color.White;
        comboBoxDataBits.FormattingEnabled = true;
        comboBoxDataBits.Items.AddRange(new object[] { "7", "8" });
        comboBoxDataBits.Location = new Point(74, 91);
        comboBoxDataBits.Name = "comboBoxDataBits";
        comboBoxDataBits.Size = new Size(136, 28);
        comboBoxDataBits.TabIndex = 8;

        comboBoxStopBits.BackColor = Color.FromArgb(44, 50, 57);
        comboBoxStopBits.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxStopBits.FlatStyle = FlatStyle.Standard;
        comboBoxStopBits.ForeColor = Color.White;
        comboBoxStopBits.FormattingEnabled = true;
        comboBoxStopBits.Items.AddRange(new object[] { "1", "1.5", "2" });
        comboBoxStopBits.Location = new Point(74, 123);
        comboBoxStopBits.Name = "comboBoxStopBits";
        comboBoxStopBits.Size = new Size(136, 28);
        comboBoxStopBits.TabIndex = 9;

        comboBoxParity.BackColor = Color.FromArgb(44, 50, 57);
        comboBoxParity.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxParity.FlatStyle = FlatStyle.Standard;
        comboBoxParity.ForeColor = Color.White;
        comboBoxParity.FormattingEnabled = true;
        comboBoxParity.Items.AddRange(new object[] { "None", "Odd", "Even", "Mark", "Space" });
        comboBoxParity.Location = new Point(74, 155);
        comboBoxParity.Name = "comboBoxParity";
        comboBoxParity.Size = new Size(136, 28);
        comboBoxParity.TabIndex = 10;

        buttonConnect.BackColor = Color.FromArgb(44, 50, 57);
        buttonConnect.FlatStyle = FlatStyle.Flat;
        buttonConnect.ForeColor = Color.White;
        buttonConnect.Location = new Point(74, 187);
        buttonConnect.Name = "buttonConnect";
        buttonConnect.Size = new Size(136, 30);
        buttonConnect.TabIndex = 12;
        buttonConnect.Text = "打开串口";
        buttonConnect.UseVisualStyleBackColor = false;
        buttonConnect.Click += buttonConnect_Click;

        radioConnectState.AutoCheck = false;
        radioConnectState.AutoSize = true;
        radioConnectState.Location = new Point(55, 194);
        radioConnectState.Name = "radioConnectState";
        radioConnectState.Size = new Size(17, 16);
        radioConnectState.TabIndex = 11;
        radioConnectState.TabStop = true;
        radioConnectState.UseVisualStyleBackColor = true;
    }

    // 初始化接收设置区域的静态控件。
    private void InitializeReceiveSettingsControls()
    {
        labelProtocolRecv = new Label();
        comboBoxProtocolRecv = new ComboBox();
        groupBoxReceiveSetting.Controls.Add(labelProtocolRecv);
        groupBoxReceiveSetting.Controls.Add(comboBoxProtocolRecv);
        groupBoxReceiveSetting.Controls.Add(buttonSaveFile);
        groupBoxReceiveSetting.Controls.Add(buttonClear);
        groupBoxReceiveSetting.Controls.Add(checkBoxPauseDisplay);
        groupBoxReceiveSetting.Controls.Add(checkBoxTimestamp);
        groupBoxReceiveSetting.Controls.Add(checkBoxHexDisplay);
        groupBoxReceiveSetting.Controls.Add(radioRecvAscii);
        groupBoxReceiveSetting.ForeColor = Color.White;
        groupBoxReceiveSetting.Location = new Point(8, 254);
        groupBoxReceiveSetting.Name = "groupBoxReceiveSetting";
        groupBoxReceiveSetting.Size = new Size(224, 180);
        groupBoxReceiveSetting.TabIndex = 1;
        groupBoxReceiveSetting.TabStop = false;
        groupBoxReceiveSetting.Text = "接收设置";

        radioRecvAscii.AutoSize = true;
        radioRecvAscii.Checked = true;
        radioRecvAscii.ForeColor = Color.White;
        radioRecvAscii.Location = new Point(12, 27);
        radioRecvAscii.Name = "radioRecvAscii";
        radioRecvAscii.Size = new Size(69, 24);
        radioRecvAscii.TabIndex = 0;
        radioRecvAscii.TabStop = true;
        radioRecvAscii.Text = "ASCII";
        radioRecvAscii.UseVisualStyleBackColor = true;
        radioRecvAscii.CheckedChanged += radioRecvAscii_CheckedChanged;

        checkBoxHexDisplay.AutoSize = true;
        checkBoxHexDisplay.ForeColor = Color.White;
        checkBoxHexDisplay.Location = new Point(113, 27);
        checkBoxHexDisplay.Name = "checkBoxHexDisplay";
        checkBoxHexDisplay.Size = new Size(59, 24);
        checkBoxHexDisplay.TabIndex = 1;
        checkBoxHexDisplay.Text = "HEX";
        checkBoxHexDisplay.UseVisualStyleBackColor = true;
        checkBoxHexDisplay.CheckedChanged += checkBoxHexDisplay_CheckedChanged;

        checkBoxTimestamp.AutoSize = true;
        checkBoxTimestamp.ForeColor = Color.White;
        checkBoxTimestamp.Location = new Point(113, 62);
        checkBoxTimestamp.Name = "checkBoxTimestamp";
        checkBoxTimestamp.Size = new Size(91, 24);
        checkBoxTimestamp.TabIndex = 3;
        checkBoxTimestamp.Text = "时间戳";
        checkBoxTimestamp.UseVisualStyleBackColor = true;

        checkBoxPauseDisplay.AutoSize = true;
        checkBoxPauseDisplay.ForeColor = Color.White;
        checkBoxPauseDisplay.Location = new Point(12, 62);
        checkBoxPauseDisplay.Name = "checkBoxPauseDisplay";
        checkBoxPauseDisplay.Size = new Size(91, 24);
        checkBoxPauseDisplay.TabIndex = 2;
        checkBoxPauseDisplay.Text = "暂停显示";
        checkBoxPauseDisplay.UseVisualStyleBackColor = true;
        checkBoxPauseDisplay.CheckedChanged += checkBoxPauseDisplay_CheckedChanged;

        buttonClear.BackColor = Color.FromArgb(44, 50, 57);
        buttonClear.FlatStyle = FlatStyle.Flat;
        buttonClear.ForeColor = Color.White;
        buttonClear.Location = new Point(12, 97);
        buttonClear.Name = "buttonClear";
        buttonClear.Size = new Size(94, 32);
        buttonClear.TabIndex = 4;
        buttonClear.Text = "清空接收区";
        buttonClear.UseVisualStyleBackColor = false;
        buttonClear.Click += buttonClear_Click;

        buttonSaveFile.BackColor = Color.FromArgb(44, 50, 57);
        buttonSaveFile.FlatStyle = FlatStyle.Flat;
        buttonSaveFile.ForeColor = Color.White;
        buttonSaveFile.Location = new Point(112, 97);
        buttonSaveFile.Name = "buttonSaveFile";
        buttonSaveFile.Size = new Size(94, 32);
        buttonSaveFile.TabIndex = 5;
        buttonSaveFile.Text = "保存文件";
        buttonSaveFile.UseVisualStyleBackColor = false;
        buttonSaveFile.Click += buttonSaveFile_Click;

        labelProtocolRecv.AutoSize = true;
        labelProtocolRecv.Location = new Point(12, 141);
        labelProtocolRecv.Name = "labelProtocolRecv";
        labelProtocolRecv.Size = new Size(69, 20);
        labelProtocolRecv.TabIndex = 6;
        labelProtocolRecv.Text = "协议接收";

        comboBoxProtocolRecv.BackColor = Color.FromArgb(44, 50, 57);
        comboBoxProtocolRecv.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxProtocolRecv.FlatStyle = FlatStyle.Standard;
        comboBoxProtocolRecv.ForeColor = Color.White;
        comboBoxProtocolRecv.FormattingEnabled = true;
        comboBoxProtocolRecv.Items.AddRange(new object[] { "无", "ymodem" });
        comboBoxProtocolRecv.Location = new Point(88, 137);
        comboBoxProtocolRecv.Name = "comboBoxProtocolRecv";
        comboBoxProtocolRecv.Size = new Size(122, 28);
        comboBoxProtocolRecv.TabIndex = 7;
        comboBoxProtocolRecv.SelectedIndexChanged += comboBoxProtocolRecv_SelectedIndexChanged;
    }

    // 初始化发送设置区域的静态控件。
    private void InitializeSendSettingsControls()
    {
        labelProtocolSend = new Label();
        comboBoxProtocolSend = new ComboBox();
        labelOneClickDebugMode = new Label();
        comboBoxOneClickDebugMode = new ComboBox();
        groupBoxSendSetting.Controls.Add(comboBoxEncoding);
        groupBoxSendSetting.Controls.Add(labelEncodingMode);
        groupBoxSendSetting.Controls.Add(comboBoxOneClickDebugMode);
        groupBoxSendSetting.Controls.Add(labelOneClickDebugMode);
        groupBoxSendSetting.Controls.Add(checkBoxAutoSend);
        groupBoxSendSetting.Controls.Add(checkBoxNewLine);
        groupBoxSendSetting.Controls.Add(checkBoxHexSend);
        groupBoxSendSetting.Controls.Add(radioSendAscii);
        groupBoxSendSetting.Controls.Add(labelMs);
        groupBoxSendSetting.Controls.Add(labelAutoPeriod);
        groupBoxSendSetting.Controls.Add(labelSendMode);
        groupBoxSendSetting.Controls.Add(numericAutoSendInterval);
        groupBoxSendSetting.Controls.Add(comboBoxSendMode);
        groupBoxSendSetting.ForeColor = Color.White;
        groupBoxSendSetting.Location = new Point(8, 444);
        groupBoxSendSetting.Name = "groupBoxSendSetting";
        groupBoxSendSetting.Size = new Size(224, 224);
        groupBoxSendSetting.TabIndex = 2;
        groupBoxSendSetting.TabStop = false;
        groupBoxSendSetting.Text = "发送设置";

        comboBoxSendMode.BackColor = Color.FromArgb(44, 50, 57);
        comboBoxSendMode.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxSendMode.FlatStyle = FlatStyle.Standard;
        comboBoxSendMode.ForeColor = Color.White;
        comboBoxSendMode.FormattingEnabled = true;
        comboBoxSendMode.Items.AddRange(new object[] { "单项发送", "多项发送" });
        comboBoxSendMode.Location = new Point(88, 25);
        comboBoxSendMode.Name = "comboBoxSendMode";
        comboBoxSendMode.Size = new Size(122, 28);
        comboBoxSendMode.TabIndex = 1;
        comboBoxSendMode.SelectedIndexChanged += comboBoxSendMode_SelectedIndexChanged;

        numericAutoSendInterval.BackColor = Color.FromArgb(44, 50, 57);
        numericAutoSendInterval.BorderStyle = BorderStyle.FixedSingle;
        numericAutoSendInterval.ForeColor = Color.White;
        numericAutoSendInterval.Location = new Point(102, 57);
        numericAutoSendInterval.Maximum = new decimal(new int[] { 100000, 0, 0, 0 });
        numericAutoSendInterval.Minimum = new decimal(new int[] { 10, 0, 0, 0 });
        numericAutoSendInterval.Name = "numericAutoSendInterval";
        numericAutoSendInterval.Size = new Size(72, 27);
        numericAutoSendInterval.TabIndex = 3;
        numericAutoSendInterval.Value = new decimal(new int[] { 1000, 0, 0, 0 });
        numericAutoSendInterval.ValueChanged += numericAutoSendInterval_ValueChanged;

        labelSendMode.AutoSize = true;
        labelSendMode.Location = new Point(12, 29);
        labelSendMode.Name = "labelSendMode";
        labelSendMode.Size = new Size(69, 20);
        labelSendMode.TabIndex = 0;
        labelSendMode.Text = "发送选项";
        labelAutoPeriod.AutoSize = true;
        labelAutoPeriod.Location = new Point(12, 60);
        labelAutoPeriod.Name = "labelAutoPeriod";
        labelAutoPeriod.Size = new Size(84, 20);
        labelAutoPeriod.TabIndex = 2;
        labelAutoPeriod.Text = "发送周期";
        labelMs.AutoSize = true;
        labelMs.Location = new Point(180, 60);
        labelMs.Name = "labelMs";
        labelMs.Size = new Size(30, 20);
        labelMs.TabIndex = 4;
        labelMs.Text = "ms";

        radioSendAscii.AutoSize = true;
        radioSendAscii.Checked = true;
        radioSendAscii.Location = new Point(12, 91);
        radioSendAscii.Name = "radioSendAscii";
        radioSendAscii.Size = new Size(69, 24);
        radioSendAscii.TabIndex = 5;
        radioSendAscii.TabStop = true;
        radioSendAscii.Text = "ASCII";
        radioSendAscii.UseVisualStyleBackColor = true;
        radioSendAscii.CheckedChanged += radioSendAscii_CheckedChanged;

        checkBoxHexSend.AutoSize = true;
        checkBoxHexSend.Location = new Point(113, 91);
        checkBoxHexSend.Name = "checkBoxHexSend";
        checkBoxHexSend.Size = new Size(59, 24);
        checkBoxHexSend.TabIndex = 6;
        checkBoxHexSend.Text = "HEX";
        checkBoxHexSend.UseVisualStyleBackColor = true;
        checkBoxHexSend.CheckedChanged += checkBoxHexSend_CheckedChanged;

        checkBoxNewLine.AutoSize = true;
        checkBoxNewLine.Checked = true;
        checkBoxNewLine.CheckState = CheckState.Checked;
        checkBoxNewLine.Location = new Point(12, 126);
        checkBoxNewLine.Name = "checkBoxNewLine";
        checkBoxNewLine.Size = new Size(91, 24);
        checkBoxNewLine.TabIndex = 7;
        checkBoxNewLine.Text = "发送换行";
        checkBoxNewLine.UseVisualStyleBackColor = true;

        checkBoxAutoSend.AutoSize = true;
        checkBoxAutoSend.Location = new Point(113, 126);
        checkBoxAutoSend.Name = "checkBoxAutoSend";
        checkBoxAutoSend.Size = new Size(91, 24);
        checkBoxAutoSend.TabIndex = 8;
        checkBoxAutoSend.Text = "自动发送";
        checkBoxAutoSend.UseVisualStyleBackColor = true;
        checkBoxAutoSend.CheckedChanged += checkBoxAutoSend_CheckedChanged;

        labelEncodingMode.AutoSize = true;
        labelEncodingMode.Location = new Point(12, 157);
        labelEncodingMode.Name = "labelEncodingMode";
        labelEncodingMode.Size = new Size(69, 20);
        labelEncodingMode.TabIndex = 9;
        labelEncodingMode.Text = "编码方式";
        comboBoxEncoding.BackColor = Color.FromArgb(44, 50, 57);
        comboBoxEncoding.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxEncoding.FlatStyle = FlatStyle.Standard;
        comboBoxEncoding.ForeColor = Color.White;
        comboBoxEncoding.FormattingEnabled = true;
        comboBoxEncoding.Items.AddRange(new object[] { "UTF-8", "ASCII", "GB2312" });
        comboBoxEncoding.Location = new Point(88, 153);
        comboBoxEncoding.Name = "comboBoxEncoding";
        comboBoxEncoding.Size = new Size(122, 28);
        comboBoxEncoding.TabIndex = 9;

        labelOneClickDebugMode.AutoSize = true;
        labelOneClickDebugMode.Location = new Point(12, 188);
        labelOneClickDebugMode.Name = "labelOneClickDebugMode";
        labelOneClickDebugMode.Size = new Size(69, 20);
        labelOneClickDebugMode.TabIndex = 10;
        labelOneClickDebugMode.Text = "一键调试";

        comboBoxOneClickDebugMode.BackColor = Color.FromArgb(44, 50, 57);
        comboBoxOneClickDebugMode.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxOneClickDebugMode.FlatStyle = FlatStyle.Standard;
        comboBoxOneClickDebugMode.ForeColor = Color.White;
        comboBoxOneClickDebugMode.FormattingEnabled = true;
        comboBoxOneClickDebugMode.Items.AddRange(new object[] { "无", "Setup", "Publish", "Negative Tests", "Clean" });
        comboBoxOneClickDebugMode.Location = new Point(88, 184);
        comboBoxOneClickDebugMode.Name = "comboBoxOneClickDebugMode";
        comboBoxOneClickDebugMode.Size = new Size(122, 28);
        comboBoxOneClickDebugMode.TabIndex = 11;
        comboBoxOneClickDebugMode.SelectedIndexChanged += comboBoxOneClickDebugMode_SelectedIndexChanged;

        // Keep this hidden selector for legacy YModem send compatibility.
        labelProtocolSend.Visible = false;
        comboBoxProtocolSend.Visible = false;
        labelProtocolSend.AutoSize = true;
        labelProtocolSend.Location = new Point(12, 220);
        labelProtocolSend.Name = "labelProtocolSend";
        labelProtocolSend.Size = new Size(69, 20);
        labelProtocolSend.TabIndex = 10;
        labelProtocolSend.Text = "协议发送";

        comboBoxProtocolSend.BackColor = Color.FromArgb(44, 50, 57);
        comboBoxProtocolSend.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxProtocolSend.FlatStyle = FlatStyle.Standard;
        comboBoxProtocolSend.ForeColor = Color.White;
        comboBoxProtocolSend.FormattingEnabled = true;
        comboBoxProtocolSend.Items.AddRange(new object[] { "无", "ymodem" });
        comboBoxProtocolSend.Location = new Point(88, 216);
        comboBoxProtocolSend.Name = "comboBoxProtocolSend";
        comboBoxProtocolSend.Size = new Size(122, 28);
        comboBoxProtocolSend.TabIndex = 11;
        comboBoxProtocolSend.SelectedIndexChanged += comboBoxProtocolSend_SelectedIndexChanged;
    }

    // 初始化右侧收发区和文件操作按钮。
    private void InitializeRightAreaControls()
    {
        panelRecvHeader.BackColor = Color.FromArgb(50, 54, 60);
        panelRecvHeader.Controls.Add(labelRecvHeader);
        panelRecvHeader.Dock = DockStyle.Top;
        panelRecvHeader.Location = new Point(8, 8);
        panelRecvHeader.Name = "panelRecvHeader";
        panelRecvHeader.Size = new Size(998, 32);
        panelRecvHeader.TabIndex = 0;
        panelRecvHeader.Visible = false;
        labelRecvHeader.AutoSize = true;
        labelRecvHeader.ForeColor = Color.FromArgb(160, 168, 176);
        labelRecvHeader.Location = new Point(12, 6);
        labelRecvHeader.Name = "labelRecvHeader";
        labelRecvHeader.Size = new Size(99, 20);
        labelRecvHeader.TabIndex = 0;
        labelRecvHeader.Text = "";

        textBoxReceive.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        textBoxReceive.BackColor = Color.FromArgb(50, 54, 60);
        textBoxReceive.BorderStyle = BorderStyle.FixedSingle;
        textBoxReceive.Font = new Font("Consolas", 11F, FontStyle.Regular, GraphicsUnit.Point);
        textBoxReceive.ForeColor = Color.FromArgb(230, 230, 230);
        textBoxReceive.Location = new Point(8, 8);
        textBoxReceive.Multiline = true;
        textBoxReceive.Name = "textBoxReceive";
        textBoxReceive.ReadOnly = true;
        textBoxReceive.ScrollBars = ScrollBars.Vertical;
        textBoxReceive.Size = new Size(998, 422);
        textBoxReceive.TabIndex = 1;

        textBoxMainBuffer.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        textBoxMainBuffer.BackColor = Color.White;
        textBoxMainBuffer.BorderStyle = BorderStyle.FixedSingle;
        textBoxMainBuffer.Font = new Font("Consolas", 11F, FontStyle.Regular, GraphicsUnit.Point);
        textBoxMainBuffer.ForeColor = Color.Black;
        textBoxMainBuffer.Location = new Point(8, 436);
        textBoxMainBuffer.Multiline = true;
        textBoxMainBuffer.Name = "textBoxMainBuffer";
        textBoxMainBuffer.ScrollBars = ScrollBars.Vertical;
        textBoxMainBuffer.Size = new Size(879, 160);
        textBoxMainBuffer.TabIndex = 2;
        textBoxMainBuffer.Text = "";

        textBoxSend.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        textBoxSend.BackColor = Color.White;
        textBoxSend.BorderStyle = BorderStyle.FixedSingle;
        textBoxSend.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);
        textBoxSend.ForeColor = Color.Black;
        textBoxSend.Location = new Point(8, 612);
        textBoxSend.Name = "textBoxSend";
        textBoxSend.ReadOnly = true;
        textBoxSend.Size = new Size(672, 27);
        textBoxSend.TabIndex = 3;

        buttonSend.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        buttonSend.BackColor = Color.FromArgb(44, 50, 57);
        buttonSend.FlatStyle = FlatStyle.Flat;
        buttonSend.ForeColor = Color.White;
        buttonSend.Location = new Point(893, 436);
        buttonSend.Name = "buttonSend";
        buttonSend.Size = new Size(113, 35);
        buttonSend.TabIndex = 4;
        buttonSend.Text = "发送";
        buttonSend.UseVisualStyleBackColor = false;
        buttonSend.Click += buttonSend_Click;

        buttonClearSend.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        buttonClearSend.BackColor = Color.FromArgb(44, 50, 57);
        buttonClearSend.FlatStyle = FlatStyle.Flat;
        buttonClearSend.ForeColor = Color.White;
        buttonClearSend.Location = new Point(893, 477);
        buttonClearSend.Name = "buttonClearSend";
        buttonClearSend.Size = new Size(113, 35);
        buttonClearSend.TabIndex = 5;
        buttonClearSend.Text = "娓呴櫎发送";
        buttonClearSend.UseVisualStyleBackColor = false;
        buttonClearSend.Click += buttonClearSend_Click;

        buttonLoadFile.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        buttonLoadFile.BackColor = Color.FromArgb(44, 50, 57);
        buttonLoadFile.FlatStyle = FlatStyle.Flat;
        buttonLoadFile.ForeColor = Color.White;
        buttonLoadFile.Location = new Point(686, 610);
        buttonLoadFile.Name = "buttonLoadFile";
        buttonLoadFile.Size = new Size(95, 31);
        buttonLoadFile.TabIndex = 6;
        buttonLoadFile.Text = "打开文件";
        buttonLoadFile.UseVisualStyleBackColor = false;
        buttonLoadFile.Click += buttonLoadFile_Click;

        buttonSendFile.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        buttonSendFile.BackColor = Color.FromArgb(44, 50, 57);
        buttonSendFile.FlatStyle = FlatStyle.Flat;
        buttonSendFile.ForeColor = Color.White;
        buttonSendFile.Location = new Point(792, 610);
        buttonSendFile.Name = "buttonSendFile";
        buttonSendFile.Size = new Size(102, 31);
        buttonSendFile.TabIndex = 7;
        buttonSendFile.Text = "发送文件";
        buttonSendFile.UseVisualStyleBackColor = false;
        buttonSendFile.Click += buttonSendFile_Click;

        buttonStopSend.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        buttonStopSend.BackColor = Color.FromArgb(44, 50, 57);
        buttonStopSend.FlatStyle = FlatStyle.Flat;
        buttonStopSend.ForeColor = Color.White;
        buttonStopSend.Location = new Point(900, 610);
        buttonStopSend.Name = "buttonStopSend";
        buttonStopSend.Size = new Size(106, 31);
        buttonStopSend.TabIndex = 8;
        buttonStopSend.Text = "鍋滄发送";
        buttonStopSend.UseVisualStyleBackColor = false;
        buttonStopSend.Click += buttonStopSend_Click;
    }

    // 初始化底部状态栏。
    private void InitializeStatusBarControls()
    {
        statusStripMain.BackColor = Color.FromArgb(164, 192, 208);
        statusStripMain.ImageScalingSize = new Size(20, 20);
        statusStripMain.Items.AddRange(new ToolStripItem[] { statusConnection, statusTxBytes, statusRxBytes, statusClearCounter });
        statusStripMain.Location = new Point(0, 680);
        statusStripMain.Name = "statusStripMain";
        statusStripMain.Size = new Size(1260, 27);
        statusStripMain.TabIndex = 1;
        statusConnection.AutoSize = false;
        statusConnection.ForeColor = Color.Black;
        statusConnection.Name = "statusConnection";
        statusConnection.Size = new Size(320, 21);
        statusConnection.Text = "串口[无端口]已关闭";
        statusConnection.TextAlign = ContentAlignment.MiddleLeft;
        statusTxBytes.AutoSize = false;
        statusTxBytes.ForeColor = Color.Black;
        statusTxBytes.Name = "statusTxBytes";
        statusTxBytes.Size = new Size(260, 21);
        statusTxBytes.Text = "发送字节: 0";
        statusTxBytes.TextAlign = ContentAlignment.MiddleCenter;
        statusRxBytes.AutoSize = false;
        statusRxBytes.ForeColor = Color.Black;
        statusRxBytes.Name = "statusRxBytes";
        statusRxBytes.Size = new Size(260, 21);
        statusRxBytes.Text = "接收字节: 0";
        statusRxBytes.TextAlign = ContentAlignment.MiddleCenter;
        statusRxBytes.Spring = true;
        statusClearCounter.BackColor = Color.FromArgb(231, 244, 255);
        statusClearCounter.BorderSides = ToolStripStatusLabelBorderSides.All;
        statusClearCounter.Name = "statusClearCounter";
        statusClearCounter.Size = new Size(74, 22);
        statusClearCounter.Text = "清空计数";
        statusClearCounter.Click += statusClearCounter_Click;
    }

    // 初始化窗体自身属性和生命周期事件。
    private void InitializeFormShell()
    {
        AutoScaleDimensions = new SizeF(9F, 20F);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.FromArgb(20, 23, 27);
        ClientSize = new Size(1260, 707);
        Controls.Add(splitContainerMain);
        Controls.Add(statusStripMain);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.Sizable;
        Name = "Form1";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "dzs串口助手";
        FormClosing += Form1_FormClosing;
        Load += Form1_Load;
    }
}
