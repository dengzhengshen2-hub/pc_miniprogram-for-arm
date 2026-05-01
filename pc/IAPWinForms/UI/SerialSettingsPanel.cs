using IAPWinForms.Features;
using System.IO.Ports;
using System.Text;
using System.Threading;

namespace IAPWinForms;

public partial class Form1
{
    private void InitializeSerialSettingsPanel()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _serialPortManager.DataReceived += SerialPortManager_DataReceived;
        _serialPortManager.ConnectionLost += SerialPortManager_ConnectionLost;
        EnsureDefaultSerialParameters();
        portRefreshDebounceTimer.Interval = 150;
        portRefreshDebounceTimer.Tick += portRefreshDebounceTimer_Tick;
        portMonitorTimer.Interval = 1200;
        portMonitorTimer.Tick += portMonitorTimer_Tick;
        portMonitorTimer.Start();
    }

    private void EnsureDefaultSerialParameters()
    {
        if (comboBoxBaudRate.SelectedItem == null && string.IsNullOrWhiteSpace(comboBoxBaudRate.Text))
        {
            comboBoxBaudRate.SelectedItem = "115200";
        }

        if (comboBoxDataBits.SelectedItem == null && string.IsNullOrWhiteSpace(comboBoxDataBits.Text))
        {
            comboBoxDataBits.SelectedItem = "8";
        }

        if (comboBoxStopBits.SelectedItem == null && string.IsNullOrWhiteSpace(comboBoxStopBits.Text))
        {
            comboBoxStopBits.SelectedItem = "1";
        }

        if (comboBoxParity.SelectedItem == null && string.IsNullOrWhiteSpace(comboBoxParity.Text))
        {
            comboBoxParity.SelectedItem = "None";
        }
    }

    private void LoadPorts()
    {
        QueuePortRefresh();
    }

    private async void QueuePortRefresh()
    {
        if (IsDisposed)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _portRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        string selectedPort = GetSelectedPortName();
        string currentOpenPort = _serialPortManager.IsOpen ? _serialPortManager.CurrentPortName : string.Empty;

        try
        {
            IReadOnlyList<SerialPortDisplayInfo> latest = await Task.Run(() => _serialPortManager.GetPortDisplayInfos());
            if (IsDisposed)
            {
                return;
            }

            ApplyRefreshedPorts(latest, selectedPort, currentOpenPort);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                _logger.Error("串口刷新失败: " + ex.Message);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _portRefreshInFlight, 0);
        }
    }

    private void ApplyRefreshedPorts(
        IReadOnlyList<SerialPortDisplayInfo> latest,
        string selectedPort,
        string currentOpenPort)
    {
        string trackedPort = !string.IsNullOrWhiteSpace(currentOpenPort) ? currentOpenPort : selectedPort;
        string[] latestPorts = latest.Select(x => x.PortName).ToArray();

        if (_serialPortManager.IsOpen &&
            !string.IsNullOrWhiteSpace(trackedPort) &&
            !latestPorts.Contains(trackedPort, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _serialPortManager.Close();
            }
            catch
            {
            }

            UpdateConnectControls(false);
            RemovePortFromUi(trackedPort);
        }

        string[] currentPorts = comboBoxPort.Items
            .Cast<object>()
            .Select(x => x is SerialPortDisplayInfo info ? info.PortName : x.ToString() ?? string.Empty)
            .ToArray();

        if (latestPorts.SequenceEqual(currentPorts, StringComparer.OrdinalIgnoreCase))
        {
            if (comboBoxPort.SelectedItem == null)
            {
                TrySelectPortByName(trackedPort);
                if (comboBoxPort.SelectedItem == null)
                {
                    TrySelectPortByName(_preferredPortName);
                }

                EnsureValidPortSelection();
            }

            if (!_serialPortManager.IsOpen)
            {
                UpdateConnectControls(false);
            }

            return;
        }

        comboBoxPort.Items.Clear();
        comboBoxPort.Items.AddRange(latest.Cast<object>().ToArray());
        UpdatePortDropDownWidth(latest);
        TrySelectPortByName(trackedPort);
        if (comboBoxPort.SelectedItem == null)
        {
            TrySelectPortByName(_preferredPortName);
        }

        EnsureValidPortSelection();

        if (!_serialPortManager.IsOpen)
        {
            UpdateConnectControls(false);
        }
    }

    private void buttonRefreshPorts_Click(object? sender, EventArgs e)
    {
        LoadPorts();
        _logger.Info("串口列表已刷新");
    }

    private void portMonitorTimer_Tick(object? sender, EventArgs e)
    {
        RefreshPortsNonDestructive();
    }

    private void portRefreshDebounceTimer_Tick(object? sender, EventArgs e)
    {
        portRefreshDebounceTimer.Stop();
        RefreshPortsNonDestructive();
    }

    internal void NotifyDeviceTopologyChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        portRefreshDebounceTimer.Stop();
        portRefreshDebounceTimer.Start();
    }

    private void RefreshPortsNonDestructive()
    {
        QueuePortRefresh();
    }

    private void buttonConnect_Click(object? sender, EventArgs e)
    {
        if (radioConnectState.Checked != _serialPortManager.IsOpen)
        {
            UpdateConnectControls(_serialPortManager.IsOpen);
        }

        if (_serialPortManager.IsOpen)
        {
            try
            {
                _serialPortManager.Close();
            }
            catch
            {
            }

            UpdateConnectControls(false);
            RefreshPortsNonDestructive();
            return;
        }

        try
        {
            if (comboBoxPort.SelectedItem == null)
            {
                MessageBox.Show("未发现可用串口", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StopBits stopBits = comboBoxStopBits.Text switch
            {
                "1.5" => StopBits.OnePointFive,
                "2" => StopBits.Two,
                _ => StopBits.One
            };

            Parity parity = comboBoxParity.Text switch
            {
                "Odd" => Parity.Odd,
                "Even" => Parity.Even,
                "Mark" => Parity.Mark,
                "Space" => Parity.Space,
                _ => Parity.None
            };

            string selectedPort = GetSelectedPortName();
            if (string.IsNullOrWhiteSpace(selectedPort))
            {
                MessageBox.Show("未发现可用串口", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!_serialPortManager.GetPortNames().Contains(selectedPort, StringComparer.OrdinalIgnoreCase))
            {
                RefreshPortsNonDestructive();
                MessageBox.Show("选择的串口已不存在，请重新选择可用端口", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _serialPortManager.Open(
                selectedPort,
                int.Parse(comboBoxBaudRate.Text),
                int.Parse(comboBoxDataBits.Text),
                stopBits,
                parity,
                GetSelectedEncoding());

            _preferredPortName = selectedPort;
            UpdateConnectControls(true);
        }
        catch (Exception ex)
        {
            string failedPort = GetSelectedPortName();
            UpdateConnectControls(_serialPortManager.IsOpen);
            RefreshPortsNonDestructive();
            if (!_serialPortManager.IsOpen && LooksLikePortMissing(ex))
            {
                RemovePortFromUi(failedPort);
            }

            MessageBox.Show($"串口操作失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdatePortDropDownWidth(IReadOnlyList<SerialPortDisplayInfo> ports)
    {
        int width = comboBoxPort.Width;
        foreach (SerialPortDisplayInfo item in ports)
        {
            int itemWidth = TextRenderer.MeasureText(item.DisplayName, comboBoxPort.Font).Width + 30;
            if (itemWidth > width)
            {
                width = itemWidth;
            }
        }

        comboBoxPort.DropDownWidth = Math.Max(comboBoxPort.Width, width);
    }

    private void UpdateConnectControls(bool connected)
    {
        buttonConnect.Text = connected ? "关闭串口" : "打开串口";
        comboBoxPort.Enabled = !connected;
        comboBoxBaudRate.Enabled = !connected;
        comboBoxDataBits.Enabled = !connected;
        comboBoxStopBits.Enabled = !connected;
        comboBoxParity.Enabled = !connected;
        radioConnectState.Checked = connected;
        UpdateStatusConnection();
    }

    private void UpdateStatusConnection()
    {
        string portName = GetStatusPortName();
        statusConnection.Text = _serialPortManager.IsOpen
            ? $"串口[{portName}]已打开!"
            : $"串口[{portName}]已关闭";
    }

    private void SerialPortManager_ConnectionLost(object? sender, string message)
    {
        if (IsDisposed)
        {
            return;
        }

        BeginInvoke(() =>
        {
            bool wasConnected = radioConnectState.Checked;
            string lostPort = GetSelectedPortName();
            UpdateConnectControls(false);
            RefreshPortsNonDestructive();
            RemovePortFromUi(lostPort);

            if (!wasConnected)
            {
                return;
            }

            _logger.Error(message);
            AppendReceiveLine(message);
        });
    }

    private string GetSelectedPortName()
    {
        if (comboBoxPort.SelectedItem is SerialPortDisplayInfo info)
        {
            return info.PortName;
        }

        string text = comboBoxPort.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        int idx = text.IndexOf(':');
        return idx >= 0 ? text[..idx] : text;
    }

    private void TrySelectPortByName(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        foreach (object item in comboBoxPort.Items)
        {
            if (item is SerialPortDisplayInfo info &&
                string.Equals(info.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                comboBoxPort.SelectedItem = item;
                return;
            }
        }
    }

    private void ClearPortSelection()
    {
        comboBoxPort.SelectedIndex = -1;
        comboBoxPort.SelectedItem = null;
        comboBoxPort.ResetText();
        comboBoxPort.Text = string.Empty;
    }

    private void RemovePortFromUi(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            if (comboBoxPort.Items.Count == 0)
            {
                ClearPortSelection();
                UpdateStatusConnection();
            }

            return;
        }

        for (int i = comboBoxPort.Items.Count - 1; i >= 0; i--)
        {
            if (comboBoxPort.Items[i] is SerialPortDisplayInfo info &&
                string.Equals(info.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                comboBoxPort.Items.RemoveAt(i);
            }
        }

        string currentPort = GetSelectedPortName();
        if (string.Equals(currentPort, portName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(comboBoxPort.Text, portName, StringComparison.OrdinalIgnoreCase) ||
            comboBoxPort.Text.StartsWith(portName + ":", StringComparison.OrdinalIgnoreCase))
        {
            ClearPortSelection();
        }

        EnsureValidPortSelection();
        UpdateStatusConnection();
    }

    private static bool LooksLikePortMissing(Exception ex)
    {
        return ex is FileNotFoundException ||
               ex.Message.Contains("Could not find file", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("找不到", StringComparison.OrdinalIgnoreCase);
    }

    private string GetStatusPortName()
    {
        if (_serialPortManager.IsOpen)
        {
            return string.IsNullOrWhiteSpace(_serialPortManager.CurrentPortName)
                ? "无端口"
                : _serialPortManager.CurrentPortName;
        }

        string selectedPort = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(selectedPort))
        {
            return "无端口";
        }

        bool existsInUi = comboBoxPort.Items
            .Cast<object>()
            .Any(item => item is SerialPortDisplayInfo info &&
                         string.Equals(info.PortName, selectedPort, StringComparison.OrdinalIgnoreCase));

        return existsInUi ? selectedPort : "无端口";
    }

    private void EnsureValidPortSelection()
    {
        if (comboBoxPort.Items.Count == 0)
        {
            ClearPortSelection();
            return;
        }

        if (comboBoxPort.SelectedItem != null)
        {
            return;
        }

        try
        {
            if (comboBoxPort.Items.Count > 0)
            {
                comboBoxPort.SelectedIndex = 0;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            ClearPortSelection();
        }
    }

    private Encoding GetSelectedEncoding()
    {
        return comboBoxEncoding.Text switch
        {
            "ASCII" => Encoding.ASCII,
            "GB2312" => Encoding.GetEncoding("GB2312"),
            _ => Encoding.UTF8
        };
    }
}
