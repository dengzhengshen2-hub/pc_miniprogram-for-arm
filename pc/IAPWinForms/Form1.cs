using IAPWinForms.Features;

namespace IAPWinForms;

public partial class Form1 : Form
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    public Form1()
    {
        _ymodem = new YModem(_serialPortManager);
        InitializeComponent();
        InitializeModules();
    }

    private void InitializeModules()
    {
        _mediator.Subscribe("log", payload =>
        {
            if (payload is string line)
            {
                BeginInvoke(() => AppendReceiveLine(line));
            }
        });

        _logger.Logged += (_, line) => _mediator.Publish("log", line);
    }

    private void Form1_Load(object? sender, EventArgs e)
    {
        try
        {
            ApplyLightTheme();
            _configManager.StartWatch();

            Dictionary<string, string> cfg = _configManager.Load();
            if (cfg.TryGetValue("DefaultPort", out string? port) && !string.IsNullOrWhiteSpace(port))
            {
                _preferredPortName = port;
                TrySelectPortByName(port);
            }

            if (cfg.TryGetValue("BaudRate", out string? baud))
            {
                comboBoxBaudRate.Text = baud;
            }

            if (cfg.TryGetValue("EncodingName", out string? enc))
            {
                comboBoxEncoding.Text = enc;
            }

            textBoxMainBuffer.Clear();
            BeginInvoke(new Action(LoadPorts));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        try
        {
            autoSendTimer.Stop();
            fileSendTimer.Stop();
            receiveFlushTimer.Stop();
            saveDebounceTimer.Stop();
            portMonitorTimer.Stop();
            portRefreshDebounceTimer.Stop();
            SaveMultiSendCache();
            _dataReceiver.Flush(GetSelectedEncoding(), checkBoxHexDisplay.Checked, true);
            _configManager.Save(new Dictionary<string, string>
            {
                ["DefaultPort"] = GetSelectedPortName(),
                ["BaudRate"] = int.TryParse(comboBoxBaudRate.Text, out int baud) ? baud.ToString() : "115200",
                ["EncodingName"] = comboBoxEncoding.Text
            });
            _configManager.StopWatch();
            _serialPortManager.Close();
            if (_serialPortManager is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"关闭过程异常: {ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE)
        {
            int eventCode = m.WParam.ToInt32();
            if (eventCode == DBT_DEVICEARRIVAL ||
                eventCode == DBT_DEVICEREMOVECOMPLETE ||
                eventCode == DBT_DEVNODES_CHANGED)
            {
                NotifyDeviceTopologyChanged();
            }
        }

        base.WndProc(ref m);
    }
}
