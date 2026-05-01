// 串口底层管理器，负责端口枚举、收发和异常断开处理。
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace IAPWinForms.Features;

// 定义串口枚举、连接和数据收发能力。
public interface ISerialPortManager
{
    // 串口收到原始数据时触发。
    event EventHandler<byte[]> DataReceived;
    // 串口异常断开时触发。
    event EventHandler<string> ConnectionLost;
    bool IsOpen { get; }
    string CurrentPortName { get; }
    // 获取端口名列表。
    string[] GetPortNames();
    // 获取带显示名的端口信息。
    IReadOnlyList<SerialPortDisplayInfo> GetPortDisplayInfos();
    // 按指定参数打开串口。
    void Open(string portName, int baudRate, int dataBits, StopBits stopBits, Parity parity, Encoding encoding);
    // 关闭当前串口。
    void Close();
    // 发送一段字节数据。
    void Send(byte[] data);
}

// 串口显示项，供下拉框同时保存端口名和显示名。
public sealed record SerialPortDisplayInfo(string PortName, string DisplayName)
{
    // 下拉框默认显示友好名称。
    public override string ToString() => DisplayName;
}

// 串口管理的默认实现。
internal sealed class SerialPortManager : ISerialPortManager, IDisposable
{
    private SerialPort _serialPort;

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<string>? ConnectionLost;

    public bool IsOpen => _serialPort.IsOpen;
    public string CurrentPortName => _serialPort.PortName;

    // 创建底层 SerialPort 实例。
    public SerialPortManager()
    {
        _serialPort = CreateSerialPort();
    }

    // 获取可用端口名，优先走更可靠的 WMI 枚举。
    public string[] GetPortNames()
    {
        if (TryGetActiveSerialPorts(out IReadOnlyList<SerialPortDisplayInfo> ports))
        {
            return ports.Select(x => x.PortName).ToArray();
        }

        return SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();
    }

    // 获取端口和友好显示名，供 UI 下拉框使用。
    public IReadOnlyList<SerialPortDisplayInfo> GetPortDisplayInfos()
    {
        if (TryGetActiveSerialPorts(out IReadOnlyList<SerialPortDisplayInfo> ports))
        {
            return ports;
        }

        return SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .Select(port => new SerialPortDisplayInfo(port, port))
            .ToList();
    }

    // 用指定参数打开串口，已打开时先关闭旧连接。
    public void Open(string portName, int baudRate, int dataBits, StopBits stopBits, Parity parity, Encoding encoding)
    {
        if (_serialPort.IsOpen)
        {
            Close();
        }

        _serialPort.PortName = portName;
        _serialPort.BaudRate = baudRate;
        _serialPort.DataBits = dataBits;
        _serialPort.StopBits = stopBits;
        _serialPort.Parity = parity;
        _serialPort.Encoding = encoding;
        _serialPort.Open();
    }

    // 关闭串口，失败时重建底层实例。
    public void Close()
    {
        try
        {
            if (_serialPort.IsOpen)
            {
                _serialPort.Close();
            }
        }
        catch
        {
            ResetPortInstance();
        }
    }

    // 向串口写入数据，连接异常时按断线处理。
    public void Send(byte[] data)
    {
        if (!_serialPort.IsOpen || data.Length == 0)
        {
            return;
        }

        try
        {
            _serialPort.Write(data, 0, data.Length);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            HandleUnexpectedDisconnect(ex);
        }
    }

    // 释放串口事件和底层资源。
    public void Dispose()
    {
        _serialPort.DataReceived -= OnPortDataReceived;

        try
        {
            if (_serialPort.IsOpen)
            {
                _serialPort.Close();
            }
        }
        catch
        {
        }

        _serialPort.Dispose();
    }

    // 读取串口缓冲区数据并转发给上层。
    private void OnPortDataReceived(object? sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            int length = _serialPort.BytesToRead;
            if (length <= 0)
            {
                return;
            }

            byte[] buffer = new byte[length];
            _serialPort.Read(buffer, 0, length);
            DataReceived?.Invoke(this, buffer);
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            HandleUnexpectedDisconnect(ex);
        }
    }

    // 合并多个来源的端口枚举结果。
    private static bool TryGetActiveSerialPorts(out IReadOnlyList<SerialPortDisplayInfo> ports)
    {
        bool serialClassOk = TryLoadFromSerialPortClass(out List<SerialPortDisplayInfo> serialClassPorts);
        bool pnpOk = TryLoadFromPnPEntity(out List<SerialPortDisplayInfo> pnpPorts);

        if (!serialClassOk && !pnpOk)
        {
            ports = [];
            return false;
        }

        List<SerialPortDisplayInfo> merged = [];

        // 同一端口优先保留更友好的显示名。
        foreach (SerialPortDisplayInfo port in serialClassPorts.Concat(pnpPorts))
        {
            int existingIndex = merged.FindIndex(x => string.Equals(x.PortName, port.PortName, StringComparison.OrdinalIgnoreCase));
            if (existingIndex < 0)
            {
                merged.Add(port);
                continue;
            }

            if (string.Equals(merged[existingIndex].DisplayName, merged[existingIndex].PortName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(port.DisplayName, port.PortName, StringComparison.OrdinalIgnoreCase))
            {
                merged[existingIndex] = port;
            }
        }

        ports = merged
            .OrderBy(x => x.PortName)
            .ToList();

        return true;
    }

    // 从 Win32_SerialPort 获取活动串口。
    private static bool TryLoadFromSerialPortClass(out List<SerialPortDisplayInfo> ports)
    {
        ports = [];

        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT DeviceID, Name, Description, ConfigManagerErrorCode FROM Win32_SerialPort");

            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                int errorCode = Convert.ToInt32(item["ConfigManagerErrorCode"] ?? 0);
                if (errorCode != 0)
                {
                    continue;
                }

                string? portName = item["DeviceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(portName))
                {
                    continue;
                }

                string? name = item["Name"]?.ToString();
                string? description = item["Description"]?.ToString();
                ports.Add(new SerialPortDisplayInfo(portName, BuildDisplayName(portName, name, description)));
            }

            return true;
        }
        catch
        {
            ports = [];
            return false;
        }
    }

    // 从 Win32_PnPEntity 补充带 COM 口的设备信息。
    private static bool TryLoadFromPnPEntity(out List<SerialPortDisplayInfo> ports)
    {
        ports = [];

        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT Name, Description, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE Name LIKE '%(COM%' OR Description LIKE '%(COM%'");

            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                int errorCode = Convert.ToInt32(item["ConfigManagerErrorCode"] ?? 0);
                if (errorCode != 0)
                {
                    continue;
                }

                string? name = item["Name"]?.ToString();
                string? description = item["Description"]?.ToString();
                string source = string.IsNullOrWhiteSpace(name) ? description ?? string.Empty : name;
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                Match match = Regex.Match(source, @"\((COM\d+)\)");
                if (!match.Success)
                {
                    continue;
                }

                string portName = match.Groups[1].Value;
                ports.Add(new SerialPortDisplayInfo(portName, BuildDisplayName(portName, name, description)));
            }

            return true;
        }
        catch
        {
            ports = [];
            return false;
        }
    }

    // 生成统一的下拉框显示名。
    private static string BuildDisplayName(string portName, string? name, string? description)
    {
        string source = !string.IsNullOrWhiteSpace(name) ? name! : description ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            return portName;
        }

        string cleaned = Regex.Replace(source, $@"\s*\({Regex.Escape(portName)}\)\s*", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return portName;
        }

        return $"{portName}:{cleaned}";
    }

    // 创建并初始化新的 SerialPort 实例。
    private SerialPort CreateSerialPort()
    {
        SerialPort port = new()
        {
            ReadBufferSize = 8192,
            WriteBufferSize = 8192,
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
        port.DataReceived += OnPortDataReceived;
        return port;
    }

    // 丢弃异常串口对象并重建一个新实例。
    private void ResetPortInstance()
    {
        try
        {
            _serialPort.DataReceived -= OnPortDataReceived;
            _serialPort.Dispose();
        }
        catch
        {
        }

        _serialPort = CreateSerialPort();
    }

    // 把收发异常统一映射为断线事件。
    private void HandleUnexpectedDisconnect(Exception ex)
    {
        string portName = _serialPort.PortName;
        ResetPortInstance();

        string message = string.IsNullOrWhiteSpace(portName)
            ? "\u4e32\u53e3\u8fde\u63a5\u5f02\u5e38\u4e2d\u65ad: " + ex.Message
            : $"\u4e32\u53e3[{portName}]\u8fde\u63a5\u5df2\u65ad\u5f00: {ex.Message}";

        ConnectionLost?.Invoke(this, message);
    }

    // 判断是否属于典型的串口连接失败异常。
    private static bool IsConnectionFailure(Exception ex)
    {
        return ex is IOException ||
               ex is InvalidOperationException ||
               ex is UnauthorizedAccessException;
    }
}
