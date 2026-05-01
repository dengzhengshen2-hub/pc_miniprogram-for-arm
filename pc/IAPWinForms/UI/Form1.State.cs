using IAPWinForms.Features;
using System.IO.Ports;
using System.Text;

namespace IAPWinForms;

public partial class Form1
{
    private const string SendModeSingle = "单项发送";
    private const string SendModeMulti = "多项发送";
    private const string SendModeFirmwareEncryption = "固件加密";
    private const string SendModeIapPackage = "IAP升级打包";

    private const int CurrentStm32YModemBaudRate = 38400;

    private readonly ISerialPortManager _serialPortManager = new SerialPortManager();
    private readonly IDataReceiver _dataReceiver = new DataReceiver();
    private readonly IDataSender _dataSender = new DataSender();
    private readonly ICommandProcessor _commandProcessor = new CommandProcessor();
    private readonly IFirmwareEncryptionService _firmwareEncryptionService = new FirmwareEncryptionService();
    private readonly IIapPackageService _iapPackageService = new IapPackageService();
    private readonly IFirmwareUpgradePreparationService _firmwareUpgradePreparationService = new FirmwareUpgradePreparationService();
    private readonly ILogger _logger = new Logger();
    private readonly IConfigManager _configManager = new ConfigManager();
    private readonly Mediator _mediator = new();
    private readonly YModem _ymodem;

    private readonly System.Windows.Forms.Timer autoSendTimer = new();
    private readonly System.Windows.Forms.Timer fileSendTimer = new();
    private readonly System.Windows.Forms.Timer receiveFlushTimer = new();
    private readonly System.Windows.Forms.Timer saveDebounceTimer = new();
    private readonly System.Windows.Forms.Timer portMonitorTimer = new();
    private readonly System.Windows.Forms.Timer portRefreshDebounceTimer = new();
    private readonly Dictionary<int, string[]> multiSendPages = new();
    private readonly ToolTip warningToolTip = new();
    private readonly string cacheDbPath = Path.Combine(Application.StartupPath, "multi_send_cache.db");

    private long totalRxBytes;
    private long totalTxBytes;
    private byte[] fileSendBuffer = [];
    private int fileSendOffset;
    private int currentMultiSendPage = 1;
    private bool loadingMultiSendUi;
    private volatile bool _isYModemModeSelected;
    private volatile bool _isYModemBusy;
    private volatile bool _suppressRawReceiveDisplay;
    private int _portRefreshInFlight;
    private string? _preferredPortName;
    private CancellationTokenSource? _yModemOperationCts;

    private readonly TextBox[] multiSendInputs = new TextBox[10];
    private readonly Button[] multiSendButtons = new Button[10];
    private TextBox? textBoxPageInput;
    private Label? labelPageStatus;
    private CheckBox? checkBoxEnableNumberKeyboard;
    private Panel? panelMultiSendMode;
    private Button? buttonAddPage;
    private Button? buttonDeletePage;
    private ProgressBar? progressBarFileSend;
    private Label? labelFileSendPercent;

    private Panel? panelYModem;
    private ProgressBar? progressBarYModem;
    private Label? labelYFileName;
    private Label? labelYFileSize;
    private Label? labelYTransferred;
    private Label? labelYRate;
    private TextBox? textBoxYModemPath;
    private Button? buttonYBrowse;
    private Button? buttonYStart;
    private Button? buttonYCancel;
    private string? _yModemSelectedFilePath;

    private Panel? panelFirmwareEncryption;
    private TextBox? textBoxFirmwareInputPath;
    private TextBox? textBoxFirmwareOutputPath;
    private Label? labelFirmwareSummary;
    private Label? labelFirmwareStatus;

    private Panel? panelIapPackage;
    private TextBox? textBoxIapFirmwarePath;
    private TextBox? textBoxIapPrivateKeyPath;
    private TextBox? textBoxIapOutputPath;
    private Label? labelIapPackageSummary;
    private Label? labelIapPackageStatus;

    private Panel? panelOneClickDebug;
    private Label? labelOneClickDebugTitle;
    private Label? labelOneClickDebugScript;
    private TextBox? textBoxOneClickDebugDescription;
    private Button? buttonOneClickDebugRun;
    private string _oneClickDebugSelectedKey = "setup";
}
