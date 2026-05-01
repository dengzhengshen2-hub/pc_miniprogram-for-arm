using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace IAPWinForms.Features;

public enum YModemStatus
{
    Idle,
    Transferring,
    Completed,
    Cancelled,
    Timeout,
    Error
}

public class YModemProgressEventArgs : EventArgs
{
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long Transferred { get; set; }
    public double Rate { get; set; }
    public int Progress { get; set; }
    public YModemStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class YModem
{
    private const byte SOH = 0x01;
    private const byte STX = 0x02;
    private const byte EOT = 0x04;
    private const byte ACK = 0x06;
    private const byte NAK = 0x15;
    private const byte CA = 0x18;
    private const byte CRC16 = 0x43;
    private const int HandshakeTimeoutMs = 5000;
    private const int ReceiveTimeoutRetryLimit = 10;

    private readonly ISerialPortManager _portManager;
    private readonly IFirmwareVerificationService _verificationService;
    private readonly BlockingCollection<byte> _rxQueue = new(new ConcurrentQueue<byte>());
    private bool _isAborted;
    private bool _operationInProgress;
    private bool _isListening;

    public event EventHandler<YModemProgressEventArgs>? ProgressChanged;

    public string LastOperationMessage { get; private set; } = string.Empty;

    public YModem(ISerialPortManager portManager)
        : this(portManager, new FirmwareVerificationService())
    {
    }

    internal YModem(ISerialPortManager portManager, IFirmwareVerificationService verificationService)
    {
        _portManager = portManager;
        _verificationService = verificationService;
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        foreach (byte value in data)
        {
            _rxQueue.Add(value);
        }
    }

    public void Abort()
    {
        _isAborted = true;
        LastOperationMessage = "YModem operation cancelled by user.";
        ClearRxQueue();

        if (_operationInProgress)
        {
            try
            {
                _portManager.Send([CA, CA]);
            }
            catch
            {
            }
        }
    }

    public void StartHandshakeCapture()
    {
        _isAborted = false;
        LastOperationMessage = string.Empty;
        ClearRxQueue();
        EnsureListening();
    }

    public void StopHandshakeCapture()
    {
        if (_operationInProgress)
        {
            return;
        }

        DetachListening();
        ClearRxQueue();
    }

    private void ClearRxQueue()
    {
        while (_rxQueue.TryTake(out _))
        {
        }
    }

    private byte? ReadByte(int timeoutMs)
    {
        if (timeoutMs <= 0)
        {
            return _rxQueue.TryTake(out byte immediateValue, 0)
                ? immediateValue
                : null;
        }

        int remainingMs = timeoutMs;
        while (remainingMs > 0)
        {
            if (_isAborted)
            {
                return null;
            }

            int waitMs = Math.Min(100, remainingMs);
            if (_rxQueue.TryTake(out byte value, waitMs))
            {
                return value;
            }

            remainingMs -= waitMs;
        }

        return null;
    }

    private void SendByte(byte value)
    {
        _portManager.Send([value]);
    }

    private void EnsureListening()
    {
        if (_isListening)
        {
            return;
        }

        _portManager.DataReceived += OnDataReceived;
        _isListening = true;
    }

    private void DetachListening()
    {
        if (!_isListening)
        {
            return;
        }

        _portManager.DataReceived -= OnDataReceived;
        _isListening = false;
    }

    public Task<bool> TransmitAsync(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        byte[] fileData = File.ReadAllBytes(filePath);
        return TransmitAsync(Path.GetFileName(filePath), fileData);
    }

    public async Task<bool> TransmitAsync(string transferFileName, byte[] fileData)
    {
        _isAborted = false;
        _operationInProgress = true;
        LastOperationMessage = string.Empty;
        if (!_isListening)
        {
            ClearRxQueue();
            EnsureListening();
        }

        try
        {
            ArgumentNullException.ThrowIfNull(fileData);
            string fileName = string.IsNullOrWhiteSpace(transferFileName)
                ? "firmware.bin"
                : Path.GetFileName(transferFileName);
            long fileSize = fileData.Length;
            if (fileSize == 0)
            {
                LastOperationMessage = "YModem transmit aborted: firmware payload is empty.";
                PublishStatus(fileName, 0, 0, YModemStatus.Error, LastOperationMessage);
                return false;
            }

            PublishStatus(fileName, fileSize, 0, YModemStatus.Transferring, "Waiting for STM32 'C' handshake...");

            byte? initialChar = null;
            DateTime startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalSeconds < 60 && !_isAborted)
            {
                initialChar = ReadByte(1000);
                if (initialChar == CRC16)
                {
                    break;
                }
            }

            if (_isAborted)
            {
                PublishStatus(fileName, fileSize, 0, YModemStatus.Cancelled, LastOperationMessage);
                return false;
            }

            if (initialChar != CRC16)
            {
                LastOperationMessage = "YModem transmit handshake timed out.";
                PublishStatus(fileName, fileSize, 0, YModemStatus.Timeout, LastOperationMessage);
                return false;
            }

            // Drop pre-handshake 'C' bytes buffered while preparing the package.
            ClearRxQueue();
            PublishStatus(fileName, fileSize, 0, YModemStatus.Transferring, "STM32 handshake received. Sending packet 0...");

            byte[] packet0 = PrepareInitialPacket(fileName, fileSize);
            if (!await SendPacketWithRetry(packet0))
            {
                if (_isAborted)
                {
                    PublishStatus(fileName, fileSize, 0, YModemStatus.Cancelled, LastOperationMessage);
                }

                return false;
            }

            byte? dataStartRequest = ReadByte(HandshakeTimeoutMs);
            if (_isAborted)
            {
                PublishStatus(fileName, fileSize, 0, YModemStatus.Cancelled, LastOperationMessage);
                return false;
            }

            PublishStatus(fileName, fileSize, 0, YModemStatus.Transferring, "Packet 0 acknowledged. Waiting for data-start request...");

            if (dataStartRequest != CRC16)
            {
                LastOperationMessage = "YModem transmit did not receive data-start request after packet 0.";
                PublishStatus(fileName, fileSize, 0, YModemStatus.Timeout, LastOperationMessage);
                return false;
            }

            int packetSize = 1024;
            int totalPackets = (int)Math.Ceiling((double)fileSize / packetSize);
            long transferred = 0;
            DateTime transferStartTime = DateTime.Now;

            for (int i = 1; i <= totalPackets; i++)
            {
                if (_isAborted)
                {
                    SendByte(CA);
                    SendByte(CA);
                    PublishStatus(fileName, fileSize, transferred, YModemStatus.Cancelled, LastOperationMessage);
                    return false;
                }

                int offset = (i - 1) * packetSize;
                int currentChunkSize = (int)Math.Min(packetSize, fileSize - offset);
                byte[] data = new byte[currentChunkSize];
                Array.Copy(fileData, offset, data, 0, currentChunkSize);

                byte[] packet = PrepareDataPacket(data, i, packetSize);
                if (!await SendPacketWithRetry(packet))
                {
                    if (_isAborted)
                    {
                        PublishStatus(fileName, fileSize, transferred, YModemStatus.Cancelled, LastOperationMessage);
                    }

                    return false;
                }

                transferred += currentChunkSize;
                double elapsed = (DateTime.Now - transferStartTime).TotalSeconds;
                double rate = elapsed > 0 ? (transferred / 1024.0) / elapsed : 0;
                int progress = fileSize > 0 ? (int)(transferred * 100 / fileSize) : 0;

                ProgressChanged?.Invoke(this, new YModemProgressEventArgs
                {
                    FileName = fileName,
                    FileSize = fileSize,
                    Transferred = transferred,
                    Rate = rate,
                    Progress = progress,
                    Status = YModemStatus.Transferring
                });
            }

            PublishStatus(fileName, fileSize, transferred, YModemStatus.Transferring, "All data packets sent. Waiting for EOT ACK...");
            SendByte(EOT);
            byte? eotResponse = ReadByte(HandshakeTimeoutMs);
            if (_isAborted)
            {
                PublishStatus(fileName, fileSize, transferred, YModemStatus.Cancelled, LastOperationMessage);
                return false;
            }

            if (eotResponse != ACK)
            {
                LastOperationMessage = "YModem transmit did not receive ACK after EOT.";
                PublishStatus(fileName, fileSize, transferred, YModemStatus.Timeout, LastOperationMessage);
                return false;
            }

            byte[] endPacket = PrepareInitialPacket(string.Empty, 0);
            if (!await SendPacketWithRetry(endPacket))
            {
                if (_isAborted)
                {
                    PublishStatus(fileName, fileSize, transferred, YModemStatus.Cancelled, LastOperationMessage);
                }

                return false;
            }

            LastOperationMessage = "YModem transmit completed.";
            PublishStatus(fileName, fileSize, transferred, YModemStatus.Completed, LastOperationMessage);
            return true;
        }
        finally
        {
            DetachListening();
            _operationInProgress = false;
            ClearRxQueue();
        }
    }

    public Task<bool> ReceiveAsync(string savePath)
    {
        return ReceiveAsync(savePath, null);
    }

    public async Task<bool> ReceiveAsync(string savePath, FirmwareVerificationOptions? verificationOptions)
    {
        _isAborted = false;
        _operationInProgress = true;
        LastOperationMessage = string.Empty;
        ClearRxQueue();
        EnsureListening();

        try
        {
            SendByte(CRC16);
            PublishStatus(string.Empty, 0, 0, YModemStatus.Transferring, "Receiver ready. Waiting for sender packet 0...");

            string fileName = string.Empty;
            long fileSize = 0;
            long transferred = 0;
            DateTime transferStartTime = DateTime.Now;
            List<byte> fileData = [];
            int expectedPacketNum = 0;
            int timeoutCount = 0;

            while (!_isAborted)
            {
                byte? header = ReadByte(HandshakeTimeoutMs);
                if (header == null)
                {
                    timeoutCount++;
                    if (timeoutCount >= ReceiveTimeoutRetryLimit)
                    {
                        LastOperationMessage = "YModem receive timed out. Serial link may be interrupted.";
                        PublishStatus(fileName, fileSize, transferred, YModemStatus.Timeout, LastOperationMessage);
                        return false;
                    }

                    SendByte(CRC16);
                    continue;
                }

                timeoutCount = 0;

                if (header == EOT)
                {
                    SendByte(ACK);
                    break;
                }

                if (header == CA)
                {
                    LastOperationMessage = "YModem receive cancelled by sender.";
                    PublishStatus(fileName, fileSize, transferred, YModemStatus.Cancelled, LastOperationMessage);
                    return false;
                }

                int packetSize = header == STX ? 1024 : 128;
                byte[] packet = new byte[packetSize + 5];
                packet[0] = header.Value;

                for (int i = 1; i < packet.Length; i++)
                {
                    byte? nextByte = ReadByte(1000);
                    if (nextByte == null)
                    {
                        break;
                    }

                    packet[i] = nextByte.Value;
                }

                if (!VerifyPacket(packet, expectedPacketNum))
                {
                    SendByte(NAK);
                    continue;
                }

                if (expectedPacketNum == 0)
                {
                    string info = Encoding.ASCII.GetString(packet, 3, packetSize).TrimEnd('\0');
                    string[] parts = info.Split('\0');
                    fileName = parts[0];
                    if (parts.Length > 1 && long.TryParse(parts[1].Split(' ')[0], out long parsedSize))
                    {
                        fileSize = parsedSize;
                    }

                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        SendByte(ACK);
                        break;
                    }

                    SendByte(ACK);
                    SendByte(CRC16);
                    PublishStatus(fileName, fileSize, transferred, YModemStatus.Transferring, "Packet 0 accepted. Requesting data packets...");
                    expectedPacketNum = 1;
                    continue;
                }

                int remaining = fileSize > 0 ? (int)Math.Max(0, fileSize - transferred) : packetSize;
                int dataSize = Math.Min(packetSize, remaining);
                if (dataSize < 0)
                {
                    dataSize = 0;
                }

                byte[] data = new byte[dataSize];
                Array.Copy(packet, 3, data, 0, dataSize);
                fileData.AddRange(data);
                transferred += dataSize;

                SendByte(ACK);
                expectedPacketNum = (expectedPacketNum + 1) % 256;

                double elapsed = (DateTime.Now - transferStartTime).TotalSeconds;
                double rate = elapsed > 0 ? (transferred / 1024.0) / elapsed : 0;
                int progress = fileSize > 0 ? (int)(transferred * 100 / fileSize) : 0;

                ProgressChanged?.Invoke(this, new YModemProgressEventArgs
                {
                    FileName = fileName,
                    FileSize = fileSize,
                    Transferred = transferred,
                    Rate = rate,
                    Progress = progress,
                    Status = YModemStatus.Transferring
                });
            }

            if (_isAborted)
            {
                PublishStatus(fileName, fileSize, transferred, YModemStatus.Cancelled, LastOperationMessage);
                return false;
            }

            if (fileData.Count == 0)
            {
                LastOperationMessage = "YModem receive completed without firmware payload.";
                PublishStatus(fileName, fileSize, transferred, YModemStatus.Error, LastOperationMessage);
                return false;
            }

            if (fileSize > 0 && transferred != fileSize)
            {
                LastOperationMessage = $"Firmware length mismatch. Expected {fileSize} bytes, received {transferred} bytes.";
                PublishStatus(fileName, fileSize, transferred, YModemStatus.Error, LastOperationMessage);
                return false;
            }

            byte[] firmwareBytes = fileData.ToArray();
            FirmwareVerificationResult verificationResult;
            try
            {
                verificationResult = _verificationService.Validate(firmwareBytes, savePath, verificationOptions);
            }
            catch (FirmwareValidationException ex)
            {
                LastOperationMessage = ex.Message;
                PublishStatus(fileName, fileSize, transferred, YModemStatus.Error, LastOperationMessage);
                return false;
            }

            try
            {
                string? outputDirectory = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                File.WriteAllBytes(savePath, firmwareBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastOperationMessage = $"Firmware verification passed but saving failed: {ex.Message}";
                PublishStatus(fileName, fileSize, transferred, YModemStatus.Error, LastOperationMessage);
                return false;
            }

            LastOperationMessage = verificationResult.Message;
            PublishStatus(fileName, fileSize, transferred, YModemStatus.Completed, LastOperationMessage);
            return true;
        }
        catch (CryptographicException ex)
        {
            LastOperationMessage = $"Firmware signature verification failed: {ex.Message}";
            PublishStatus(string.Empty, 0, 0, YModemStatus.Error, LastOperationMessage);
            return false;
        }
        finally
        {
            DetachListening();
            _operationInProgress = false;
            ClearRxQueue();
        }
    }

    private async Task<bool> SendPacketWithRetry(byte[] packet)
    {
        for (int retry = 0; retry < 5; retry++)
        {
            if (_isAborted)
            {
                LastOperationMessage = "YModem operation cancelled by user.";
                return false;
            }

            _portManager.Send(packet);
            byte? response = ReadByte(HandshakeTimeoutMs);
            if (_isAborted)
            {
                LastOperationMessage = "YModem operation cancelled by user.";
                return false;
            }

            if (response == ACK)
            {
                return true;
            }

            if (response == CA)
            {
                LastOperationMessage = "YModem peer cancelled the transfer.";
                PublishStatus(string.Empty, 0, 0, YModemStatus.Cancelled, LastOperationMessage);
                return false;
            }
        }

        LastOperationMessage = "YModem packet retry limit reached.";
        PublishStatus(string.Empty, 0, 0, YModemStatus.Timeout, LastOperationMessage);
        return false;
    }

    private byte[] PrepareInitialPacket(string fileName, long fileSize)
    {
        byte[] packet = new byte[128 + 5];
        packet[0] = SOH;
        packet[1] = 0;
        packet[2] = 0xFF;

        string info = fileName + "\0" + fileSize + " ";
        byte[] infoBytes = Encoding.ASCII.GetBytes(info);
        Array.Copy(infoBytes, 0, packet, 3, Math.Min(infoBytes.Length, 128));

        ushort crc = CalculateCRC(packet, 3, 128);
        packet[131] = (byte)(crc >> 8);
        packet[132] = (byte)(crc & 0xFF);
        return packet;
    }

    private byte[] PrepareDataPacket(byte[] data, int packetNum, int packetSize)
    {
        byte[] packet = new byte[packetSize + 5];
        packet[0] = packetSize == 1024 ? STX : SOH;
        packet[1] = (byte)(packetNum % 256);
        packet[2] = (byte)(~(packetNum % 256));

        Array.Copy(data, 0, packet, 3, data.Length);
        for (int i = 3 + data.Length; i < 3 + packetSize; i++)
        {
            packet[i] = 0x1A;
        }

        ushort crc = CalculateCRC(packet, 3, packetSize);
        packet[packetSize + 3] = (byte)(crc >> 8);
        packet[packetSize + 4] = (byte)(crc & 0xFF);
        return packet;
    }

    private bool VerifyPacket(byte[] packet, int expectedNum)
    {
        if (packet[1] != (byte)(expectedNum % 256))
        {
            return false;
        }

        if (packet[2] != (byte)(~(expectedNum % 256)))
        {
            return false;
        }

        int dataSize = packet.Length - 5;
        ushort received = (ushort)((packet[^2] << 8) | packet[^1]);
        ushort calculated = CalculateCRC(packet, 3, dataSize);
        return received == calculated;
    }

    private ushort CalculateCRC(byte[] data, int offset, int length)
    {
        ushort crc = 0;
        for (int i = offset; i < offset + length; i++)
        {
            crc = (ushort)(crc ^ (data[i] << 8));
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }

        return crc;
    }

    private void PublishStatus(string fileName, long fileSize, long transferred, YModemStatus status, string message)
    {
        ProgressChanged?.Invoke(this, new YModemProgressEventArgs
        {
            FileName = fileName,
            FileSize = fileSize,
            Transferred = transferred,
            Progress = fileSize > 0 ? (int)(transferred * 100 / fileSize) : 0,
            Status = status,
            Message = message
        });
    }
}
