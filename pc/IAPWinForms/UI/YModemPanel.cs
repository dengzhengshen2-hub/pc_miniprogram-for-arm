using IAPWinForms.Features;

namespace IAPWinForms;

public partial class Form1
{
    private void BuildYModemPanel()
    {
        panelYModem = new Panel
        {
            Name = "panelYModem",
            Location = new Point(8, 436),
            Size = new Size(998, 205),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Visible = false,
            BackColor = Color.White
        };

        Label labelMode = new()
        {
            Text = "Legacy YModem Path",
            Location = new Point(16, 18),
            Size = new Size(95, 24)
        };

        textBoxYModemPath = new TextBox
        {
            Location = new Point(118, 16),
            Size = new Size(610, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ReadOnly = true
        };

        buttonYBrowse = new Button
        {
            Text = "Browse",
            Location = new Point(738, 14),
            Size = new Size(88, 31),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat
        };
        buttonYBrowse.Click += buttonYBrowse_Click;

        buttonYStart = new Button
        {
            Text = "Start",
            Location = new Point(832, 14),
            Size = new Size(78, 31),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat
        };
        buttonYStart.Click += buttonYStart_Click;

        buttonYCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(916, 14),
            Size = new Size(70, 31),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        buttonYCancel.Click += buttonYCancel_Click;

        progressBarYModem = new ProgressBar
        {
            Location = new Point(20, 62),
            Size = new Size(966, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        labelYFileName = new Label
        {
            Text = "File: -",
            Location = new Point(20, 96),
            Size = new Size(966, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        labelYFileSize = new Label
        {
            Text = "Size: -",
            Location = new Point(20, 124),
            Size = new Size(300, 24)
        };

        labelYTransferred = new Label
        {
            Text = "Transferred: -",
            Location = new Point(340, 124),
            Size = new Size(300, 24)
        };

        labelYRate = new Label
        {
            Text = "Rate: -",
            Location = new Point(660, 124),
            Size = new Size(326, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        Label labelHint = new()
        {
            Text = $"Current STM32 bootloader YModem baud is {CurrentStm32YModemBaudRate}. This panel is only for legacy YModem workflows. New RAW_CTR custom-package transports must use the ESP32 OTA service path instead of this manual tool.",
            Location = new Point(20, 158),
            Size = new Size(966, 32),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };

        panelYModem.Controls.AddRange([
            labelMode,
            textBoxYModemPath,
            buttonYBrowse,
            buttonYStart,
            buttonYCancel,
            progressBarYModem,
            labelYFileName,
            labelYFileSize,
            labelYTransferred,
            labelYRate,
            labelHint
        ]);

        panelRight.Controls.Add(panelYModem);
        panelYModem.BringToFront();

        _ymodem.ProgressChanged -= YModem_ProgressChanged;
        _ymodem.ProgressChanged += YModem_ProgressChanged;
        ResetYModemDisplay();
    }

    private void comboBoxProtocolRecv_SelectedIndexChanged(object? sender, EventArgs e)
    {
        ApplySendModeUI();
        ResetYModemDisplay();
    }

    private void comboBoxProtocolSend_SelectedIndexChanged(object? sender, EventArgs e)
    {
        ApplySendModeUI();
        ResetYModemDisplay();
    }

    private void buttonYBrowse_Click(object? sender, EventArgs e)
    {
        bool isSendMode = IsYModemSendMode();
        if (isSendMode)
        {
            using OpenFileDialog dialog = new()
            {
                Filter = "IAP packages|*.iap|Firmware files|*.bin;*.hex;*.srec;*.mot|All files|*.*"
            };
            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            SetYModemSelectedPath(dialog.FileName);
            return;
        }

        using SaveFileDialog saveDialog = new()
        {
            Filter = "Firmware files|*.bin|All files|*.*",
            FileName = $"firmware_{DateTime.Now:yyyyMMdd_HHmmss}.bin"
        };
        if (saveDialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        SetYModemSelectedPath(saveDialog.FileName);
    }

    private async void buttonYStart_Click(object? sender, EventArgs e)
    {
        if (!_serialPortManager.IsOpen)
        {
            MessageBox.Show("Please open the serial port first.", "YModem", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!ValidateYModemBaudRate())
        {
            return;
        }

        bool isSendMode = IsYModemSendMode();
        string? targetPath = _yModemSelectedFilePath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            buttonYBrowse_Click(sender, e);
            targetPath = _yModemSelectedFilePath;
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        if (isSendMode && !IsIapPackagePath(targetPath) && !ConfirmLegacyYModemSend(targetPath))
        {
            return;
        }

        _yModemOperationCts?.Dispose();
        using CancellationTokenSource operationCts = new();
        _yModemOperationCts = operationCts;
        SetYModemBusyState(true);
        ResetYModemDisplay();
        AppendReceiveLine(isSendMode
            ? $"YModem send started: {targetPath}"
            : $"YModem receive started: {targetPath}");

        bool success;
        string failureMessage = string.Empty;
        bool cancelledByUser = false;
        bool handshakeCaptureArmed = false;
        try
        {
            if (isSendMode)
            {
                _ymodem.StartHandshakeCapture();
                handshakeCaptureArmed = true;

                if (IsIapPackagePath(targetPath))
                {
                    AppendReceiveLine("Preparing .iap package: verifying signature, CRC32, and AES encryption...");
                    FirmwareUpgradePreparationResult prepared = await Task.Run(() =>
                        _firmwareUpgradePreparationService.PrepareForYModemSend(new FirmwareUpgradePreparationRequest
                        {
                            PackagePath = targetPath
                        }, operationCts.Token), operationCts.Token);
                    operationCts.Token.ThrowIfCancellationRequested();

                    AppendReceiveLine(prepared.VerificationMessage);
                    if (!string.Equals(prepared.TransferEncoding, "IV_PREFIXED", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"The selected package uses {prepared.TransferEncoding} custom transport and cannot be sent with the legacy YModem panel.");
                    }

                    AppendReceiveLine(
                        $"AES encryption completed. Sending {prepared.TransferFileName} ({prepared.TransferBytes.Length} bytes).");
                    success = await Task.Run(
                        () => _ymodem.TransmitAsync(prepared.TransferFileName, prepared.TransferBytes),
                        operationCts.Token);
                    handshakeCaptureArmed = false;
                }
                else
                {
                    AppendReceiveLine("Legacy YModem send confirmed. Package signature/CRC32 verification skipped.");
                    success = await Task.Run(
                        () => _ymodem.TransmitAsync(targetPath),
                        operationCts.Token);
                    handshakeCaptureArmed = false;
                }
            }
            else
            {
                FirmwareVerificationOptions verificationOptions = BuildFirmwareVerificationOptions(targetPath);
                success = await Task.Run(
                    () => _ymodem.ReceiveAsync(targetPath, verificationOptions),
                    operationCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            cancelledByUser = true;
            success = false;
            failureMessage = "YModem operation cancelled by user.";
            AppendReceiveLine(failureMessage);
        }
        catch (Exception ex)
        {
            success = false;
            failureMessage = ex.Message;
            AppendReceiveLine($"YModem failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_yModemOperationCts, operationCts))
            {
                _yModemOperationCts = null;
            }

            if (handshakeCaptureArmed)
            {
                _ymodem.StopHandshakeCapture();
            }

            SetYModemBusyState(false);
        }

        if (!success)
        {
            string message = !string.IsNullOrWhiteSpace(failureMessage)
                ? failureMessage
                : _ymodem.LastOperationMessage;
            if (!cancelledByUser &&
                !message.Contains("cancelled by user", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(message, "YModem", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void buttonYCancel_Click(object? sender, EventArgs e)
    {
        _yModemOperationCts?.Cancel();
        _ymodem.Abort();
        buttonYCancel!.Enabled = false;
        AppendReceiveLine("Cancelling YModem operation...");
    }

    private void YModem_ProgressChanged(object? sender, YModemProgressEventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateYModemUI(e));
            return;
        }

        UpdateYModemUI(e);
    }

    private void UpdateYModemUI(YModemProgressEventArgs e)
    {
        if (progressBarYModem != null)
        {
            progressBarYModem.Value = Math.Max(progressBarYModem.Minimum, Math.Min(progressBarYModem.Maximum, e.Progress));
        }

        if (labelYFileName != null)
        {
            labelYFileName.Text = $"File: {(!string.IsNullOrWhiteSpace(e.FileName) ? e.FileName : "-")}";
        }

        if (labelYFileSize != null)
        {
            labelYFileSize.Text = $"Size: {(e.FileSize > 0 ? $"{e.FileSize} bytes" : "-")}";
        }

        if (labelYTransferred != null)
        {
            labelYTransferred.Text = $"Transferred: {e.Transferred} bytes ({e.Progress}%)";
        }

        if (labelYRate != null)
        {
            labelYRate.Text = $"Rate: {e.Rate:F2} KB/s";
        }

        if (!string.IsNullOrWhiteSpace(e.Message))
        {
            AppendReceiveLine($"YModem [{e.Status}]: {e.Message}");
        }
    }

    private void ResetYModemDisplay()
    {
        if (progressBarYModem != null)
        {
            progressBarYModem.Value = 0;
        }

        if (labelYFileName != null)
        {
            labelYFileName.Text = "File: -";
        }

        if (labelYFileSize != null)
        {
            labelYFileSize.Text = "Size: -";
        }

        if (labelYTransferred != null)
        {
            labelYTransferred.Text = "Transferred: -";
        }

        if (labelYRate != null)
        {
            labelYRate.Text = "Rate: -";
        }
    }

    private void SetYModemBusyState(bool isBusy)
    {
        _isYModemBusy = isBusy;
        UpdateRawReceiveDisplaySuppression();

        if (buttonYBrowse != null)
        {
            buttonYBrowse.Enabled = !isBusy;
        }

        if (buttonYStart != null)
        {
            buttonYStart.Enabled = !isBusy;
        }

        if (buttonYCancel != null)
        {
            buttonYCancel.Enabled = isBusy;
        }
    }

    private void SetYModemSelectedPath(string filePath)
    {
        _yModemSelectedFilePath = filePath;
        if (textBoxYModemPath != null)
        {
            textBoxYModemPath.Text = filePath;
        }
    }

    private bool ValidateYModemBaudRate()
    {
        if (!int.TryParse(comboBoxBaudRate.Text, out int selectedBaudRate))
        {
            MessageBox.Show("Please select a valid baud rate before starting YModem.", "YModem", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (selectedBaudRate == CurrentStm32YModemBaudRate)
        {
            return true;
        }

        string message =
            $"Current STM32 bootloader source uses uart_init({CurrentStm32YModemBaudRate}) in firmware/stm32f4VET6-boot/USER/main.c.{Environment.NewLine}{Environment.NewLine}" +
            $"Your serial port is currently open at {selectedBaudRate}.{Environment.NewLine}" +
            $"Please close the port, switch the baud rate to {CurrentStm32YModemBaudRate}, then reopen the port before starting YModem.";

        AppendReceiveLine($"YModem blocked: baud rate mismatch. PC={selectedBaudRate}, STM32={CurrentStm32YModemBaudRate}.");
        MessageBox.Show(message, "YModem", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private bool IsYModemSendMode()
    {
        return string.Equals(comboBoxProtocolSend.SelectedItem?.ToString(), "ymodem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIapPackagePath(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".iap", StringComparison.OrdinalIgnoreCase);
    }

    private bool ConfirmLegacyYModemSend(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        DialogResult result = MessageBox.Show(
            $"当前选择的是非 .iap 文件:\r\n{fileName}\r\n\r\n此模式会跳过升级包签名和 CRC32 校验，并默认该文件已经按 STM32 当前规则完成 AES 加密。\r\n\r\n是否继续按旧流程直发？",
            "Legacy YModem Send",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        return result == DialogResult.Yes;
    }

    private FirmwareVerificationOptions BuildFirmwareVerificationOptions(string outputPath)
    {
        string companionManifestPath = outputPath + ".verify.json";
        return new FirmwareVerificationOptions
        {
            ManifestPath = File.Exists(companionManifestPath) ? companionManifestPath : null,
            RequireManifest = false,
            RequireSignature = false
        };
    }
}
