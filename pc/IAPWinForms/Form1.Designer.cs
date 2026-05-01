// 主窗体 Designer 文件，集中声明控件并完成基础布局装配。
namespace IAPWinForms;

// 窗体控件定义与初始化入口。
partial class Form1
{
    private System.ComponentModel.IContainer components = null;
    private SplitContainer splitContainerMain;
    private Panel panelLeft;
    private Panel panelRight;
    private GroupBox groupBoxSerial;
    private GroupBox groupBoxReceiveSetting;
    private GroupBox groupBoxSendSetting;
    private Label labelPort;
    private Label labelBaudRate;
    private Label labelDataBits;
    private Label labelStopBits;
    private Label labelParity;
    private Label labelAction;
    private ComboBox comboBoxPort;
    private ComboBox comboBoxBaudRate;
    private ComboBox comboBoxDataBits;
    private ComboBox comboBoxStopBits;
    private ComboBox comboBoxParity;
    private Button buttonConnect;
    private RadioButton radioConnectState;
    private RadioButton radioRecvAscii;
    private CheckBox checkBoxHexDisplay;
    private CheckBox checkBoxTimestamp;
    private CheckBox checkBoxPauseDisplay;
    private Button buttonClear;
    private Button buttonSaveFile;
    private ComboBox comboBoxSendMode;
    private NumericUpDown numericAutoSendInterval;
    private Label labelSendMode;
    private Label labelAutoPeriod;
    private Label labelMs;
    private Label labelEncodingMode;
    private RadioButton radioSendAscii;
    private CheckBox checkBoxHexSend;
    private CheckBox checkBoxNewLine;
    private CheckBox checkBoxAutoSend;
    private Panel panelRecvHeader;
    private Label labelRecvHeader;
    private TextBox textBoxReceive;
    private TextBox textBoxMainBuffer;
    private TextBox textBoxSend;
    private Button buttonClearSend;
    private Button buttonLoadFile;
    private Button buttonSendFile;
    private Button buttonStopSend;
    private Button buttonSend;
    private StatusStrip statusStripMain;
    private ToolStripStatusLabel statusConnection;
    private ToolStripStatusLabel statusTxBytes;
    private ToolStripStatusLabel statusRxBytes;
    private ToolStripStatusLabel statusClearCounter;
    private ComboBox comboBoxEncoding;
    private Label labelProtocolRecv;
    private ComboBox comboBoxProtocolRecv;
    private Label labelProtocolSend;
    private ComboBox comboBoxProtocolSend;
    private Label labelOneClickDebugMode;
    private ComboBox comboBoxOneClickDebugMode;

    // 释放 Designer 创建的控件资源。
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    // 初始化主窗体控件，并分发到各区域初始化方法。
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        splitContainerMain = new SplitContainer();
        panelLeft = new Panel();
        panelRight = new Panel();
        groupBoxSerial = new GroupBox();
        groupBoxReceiveSetting = new GroupBox();
        groupBoxSendSetting = new GroupBox();
        labelPort = new Label();
        labelBaudRate = new Label();
        labelDataBits = new Label();
        labelStopBits = new Label();
        labelParity = new Label();
        labelAction = new Label();
        comboBoxPort = new ComboBox();
        comboBoxBaudRate = new ComboBox();
        comboBoxDataBits = new ComboBox();
        comboBoxStopBits = new ComboBox();
        comboBoxParity = new ComboBox();
        buttonConnect = new Button();
        radioConnectState = new RadioButton();
        radioRecvAscii = new RadioButton();
        checkBoxHexDisplay = new CheckBox();
        checkBoxTimestamp = new CheckBox();
        checkBoxPauseDisplay = new CheckBox();
        buttonClear = new Button();
        buttonSaveFile = new Button();
        comboBoxSendMode = new ComboBox();
        numericAutoSendInterval = new NumericUpDown();
        labelSendMode = new Label();
        labelAutoPeriod = new Label();
        labelMs = new Label();
        labelEncodingMode = new Label();
        radioSendAscii = new RadioButton();
        checkBoxHexSend = new CheckBox();
        checkBoxNewLine = new CheckBox();
        checkBoxAutoSend = new CheckBox();
        panelRecvHeader = new Panel();
        labelRecvHeader = new Label();
        textBoxReceive = new TextBox();
        textBoxMainBuffer = new TextBox();
        textBoxSend = new TextBox();
        buttonClearSend = new Button();
        buttonLoadFile = new Button();
        buttonSendFile = new Button();
        buttonStopSend = new Button();
        buttonSend = new Button();
        statusStripMain = new StatusStrip();
        statusConnection = new ToolStripStatusLabel();
        statusTxBytes = new ToolStripStatusLabel();
        statusRxBytes = new ToolStripStatusLabel();
        statusClearCounter = new ToolStripStatusLabel();
        comboBoxEncoding = new ComboBox();
        ((System.ComponentModel.ISupportInitialize)splitContainerMain).BeginInit();
        splitContainerMain.Panel1.SuspendLayout();
        splitContainerMain.Panel2.SuspendLayout();
        splitContainerMain.SuspendLayout();
        groupBoxSerial.SuspendLayout();
        groupBoxReceiveSetting.SuspendLayout();
        groupBoxSendSetting.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)numericAutoSendInterval).BeginInit();
        panelRecvHeader.SuspendLayout();
        statusStripMain.SuspendLayout();
        SuspendLayout();
        InitializeLayoutShell();
        InitializeSerialSettingsControls();
        InitializeReceiveSettingsControls();
        InitializeSendSettingsControls();
        InitializeRightAreaControls();
        InitializeStatusBarControls();
        InitializeFormShell();
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
        InitializeSerialSettingsPanel();
        InitializeReceiveViewPanel();
        InitializeMultiSendPanel();
        InitializeStatusBarPanel();
        splitContainerMain.Panel1.ResumeLayout(false);
        splitContainerMain.Panel2.ResumeLayout(false);
        splitContainerMain.Panel2.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)splitContainerMain).EndInit();
        splitContainerMain.ResumeLayout(false);
        groupBoxSerial.ResumeLayout(false);
        groupBoxSerial.PerformLayout();
        groupBoxReceiveSetting.ResumeLayout(false);
        groupBoxReceiveSetting.PerformLayout();
        groupBoxSendSetting.ResumeLayout(false);
        groupBoxSendSetting.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)numericAutoSendInterval).EndInit();
        panelRecvHeader.ResumeLayout(false);
        panelRecvHeader.PerformLayout();
        statusStripMain.ResumeLayout(false);
        statusStripMain.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }
}
