using IAPWinForms.Features;

namespace IAPWinForms;

public partial class Form1
{
    private bool _firmwareSelfTestPassed = true;
    private bool _firmwareEncryptionReady = true;

    private const string FirmwareModeText = "固件加密";
    private const string SelfTestPassedText = "自检通过";
    private const string SelfTestFailedText = "自检失败，请检查 AES 参数";
    private const string EncryptionCompletedText = "加密完成，结果已同步到发送文件栏";
    private const string PromptTitleText = "提示";
    private const string ErrorTitleText = "错误";

    private void BuildFirmwareEncryptionPanel()
    {
        _firmwareSelfTestPassed = _firmwareEncryptionService.SelfTest();
        string keyDisplayText = ResolveFirmwareKeyDisplay(out string statusText, out Color statusColor, out bool keyAvailable);
        _firmwareEncryptionReady = keyAvailable && _firmwareSelfTestPassed;

        panelFirmwareEncryption = new Panel
        {
            Name = "panelFirmwareEncryption",
            Location = new Point(8, 436),
            Size = new Size(998, 228),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Visible = false,
            BackColor = Color.White
        };

        Label labelTitle = new()
        {
            AutoSize = true,
            Text = "AES256-CTR " + FirmwareModeText,
            Location = new Point(20, 14),
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point)
        };

        Label labelSubtitle = new()
        {
            Text = "使用 AES-256-CTR；每包随机 IV；传输输出为 RAW_CTR 密文，不再附带 IV 前缀，也不做 PKCS7 填充。",
            Location = new Point(235, 16),
            Size = new Size(710, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = Color.DimGray
        };

        labelFirmwareStatus = new Label
        {
            Text = statusText,
            Location = new Point(20, 42),
            Size = new Size(928, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = statusColor
        };

        Label labelInput = new()
        {
            Text = "输入固件",
            Location = new Point(20, 76),
            Size = new Size(78, 24),
            TextAlign = ContentAlignment.MiddleLeft
        };

        textBoxFirmwareInputPath = new TextBox
        {
            Location = new Point(104, 74),
            Size = new Size(720, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        Button buttonBrowseInput = new()
        {
            Text = "浏览...",
            Location = new Point(836, 72),
            Size = new Size(120, 31),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat
        };
        buttonBrowseInput.Click += buttonBrowseInput_Click;

        Label labelOutput = new()
        {
            Text = "输出文件",
            Location = new Point(20, 112),
            Size = new Size(78, 24),
            TextAlign = ContentAlignment.MiddleLeft
        };

        textBoxFirmwareOutputPath = new TextBox
        {
            Location = new Point(104, 110),
            Size = new Size(720, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        Button buttonBrowseOutput = new()
        {
            Text = "另存为...",
            Location = new Point(836, 108),
            Size = new Size(120, 31),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat
        };
        buttonBrowseOutput.Click += buttonBrowseOutput_Click;

        Label labelKey = new()
        {
            Text = "Key",
            Location = new Point(20, 148),
            Size = new Size(78, 24),
            TextAlign = ContentAlignment.MiddleLeft
        };

        TextBox textBoxKey = new()
        {
            Location = new Point(104, 146),
            Size = new Size(852, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ReadOnly = true,
            Text = keyDisplayText
        };

        Label labelIv = new()
        {
            Text = "IV 策略",
            Location = new Point(20, 184),
            Size = new Size(78, 24),
            TextAlign = ContentAlignment.MiddleLeft
        };

        TextBox textBoxIv = new()
        {
            Location = new Point(104, 182),
            Size = new Size(250, 27),
            ReadOnly = true,
            Text = _firmwareEncryptionService.IvHex
        };

        labelFirmwareSummary = new Label
        {
            Text = BuildSummaryText(-1, -1),
            Location = new Point(370, 184),
            Size = new Size(448, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleLeft
        };

        Button buttonEncrypt = new()
        {
            Text = "开始加密",
            Location = new Point(836, 180),
            Size = new Size(120, 31),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            Enabled = _firmwareEncryptionReady
        };
        buttonEncrypt.Click += buttonFirmwareEncrypt_Click;

        panelFirmwareEncryption.Controls.AddRange(
        [
            labelTitle,
            labelSubtitle,
            labelFirmwareStatus,
            labelInput,
            textBoxFirmwareInputPath,
            buttonBrowseInput,
            labelOutput,
            textBoxFirmwareOutputPath,
            buttonBrowseOutput,
            labelKey,
            textBoxKey,
            labelIv,
            textBoxIv,
            labelFirmwareSummary,
            buttonEncrypt
        ]);

        panelRight.Controls.Add(panelFirmwareEncryption);
        panelFirmwareEncryption.BringToFront();
    }

    private string ResolveFirmwareKeyDisplay(out string statusText, out Color statusColor, out bool keyAvailable)
    {
        try
        {
            string keyHex = _firmwareEncryptionService.KeyHex;
            keyAvailable = true;
            statusText = _firmwareSelfTestPassed ? SelfTestPassedText : SelfTestFailedText;
            statusColor = _firmwareSelfTestPassed ? Color.FromArgb(0, 120, 80) : Color.Firebrick;
            return keyHex;
        }
        catch (Exception ex)
        {
            keyAvailable = false;
            statusText = BuildFirmwareSecurityHint(ex.Message);
            statusColor = Color.DarkOrange;
            return "[未配置]";
        }
    }

    private static string BuildFirmwareSecurityHint(string message)
    {
        return $"生产 AES Key 未配置。请设置 {OtaSecurityEnvironment.ProductionAesKeyEnvironmentVariable}，或临时设置 {OtaSecurityEnvironment.SecurityProfileEnvironmentVariable}=development。详细原因: {message}";
    }

    private void buttonBrowseInput_Click(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new()
        {
            Filter = "BIN 固件|*.bin|所有文件|*.*",
            Title = "选择待加密的固件"
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        string previousInputPath = textBoxFirmwareInputPath?.Text ?? string.Empty;
        string previousSuggestedOutput = _firmwareEncryptionService.BuildDefaultOutputPath(previousInputPath);
        string newSuggestedOutput = _firmwareEncryptionService.BuildDefaultOutputPath(dialog.FileName);

        if (textBoxFirmwareInputPath != null)
        {
            textBoxFirmwareInputPath.Text = dialog.FileName;
        }

        if (textBoxFirmwareOutputPath != null &&
            (string.IsNullOrWhiteSpace(textBoxFirmwareOutputPath.Text) ||
             string.Equals(textBoxFirmwareOutputPath.Text, previousSuggestedOutput, StringComparison.OrdinalIgnoreCase)))
        {
            textBoxFirmwareOutputPath.Text = newSuggestedOutput;
        }

        UpdateFirmwareEncryptionSummary();
    }

    private void buttonBrowseOutput_Click(object? sender, EventArgs e)
    {
        string inputPath = textBoxFirmwareInputPath?.Text ?? string.Empty;
        string defaultOutput = _firmwareEncryptionService.BuildDefaultOutputPath(inputPath);
        string currentOutput = textBoxFirmwareOutputPath?.Text ?? string.Empty;
        string dialogPath = string.IsNullOrWhiteSpace(currentOutput) ? defaultOutput : currentOutput;

        using SaveFileDialog dialog = new()
        {
            Filter = "BIN 固件|*.bin|所有文件|*.*",
            Title = "选择加密固件输出位置",
            FileName = Path.GetFileName(dialogPath)
        };

        string initialDirectory = Path.GetDirectoryName(dialogPath) ?? string.Empty;
        if (Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        if (textBoxFirmwareOutputPath != null)
        {
            textBoxFirmwareOutputPath.Text = dialog.FileName;
        }

        UpdateFirmwareEncryptionSummary();
    }

    private void buttonFirmwareEncrypt_Click(object? sender, EventArgs e)
    {
        if (!_firmwareEncryptionReady)
        {
            string message = labelFirmwareStatus?.Text ?? BuildFirmwareSecurityHint("AES key is unavailable.");
            MessageBox.Show(message, ErrorTitleText, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string inputPath = textBoxFirmwareInputPath?.Text.Trim() ?? string.Empty;
        string outputPath = textBoxFirmwareOutputPath?.Text.Trim() ?? string.Empty;

        try
        {
            FirmwareEncryptionResult result = _firmwareEncryptionService.EncryptFile(inputPath, outputPath);
            LoadFileIntoSendArea(result.OutputPath, previewTextContent: false);
            UpdateFirmwareEncryptionSummary(result);

            string message = $"AES {FirmwareModeText}完成: {Path.GetFileName(result.InputPath)} -> {Path.GetFileName(result.OutputPath)}";
            _logger.Info(message);
            MessageBox.Show($"加密完成\r\n输出文件: {result.OutputPath}", PromptTitleText, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            if (labelFirmwareStatus != null)
            {
                labelFirmwareStatus.Text = ex.Message;
                labelFirmwareStatus.ForeColor = Color.Firebrick;
            }

            _logger.Error($"AES {FirmwareModeText}失败: {ex.Message}");
            MessageBox.Show($"加密失败: {ex.Message}", ErrorTitleText, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateFirmwareEncryptionSummary(FirmwareEncryptionResult? result = null)
    {
        long inputSize = result?.InputSize ?? GetFileSize(textBoxFirmwareInputPath?.Text);
        long outputSize = result?.OutputSize ?? GetFileSize(textBoxFirmwareOutputPath?.Text);

        if (labelFirmwareSummary != null)
        {
            labelFirmwareSummary.Text = BuildSummaryText(inputSize, outputSize);
        }

        if (labelFirmwareStatus == null)
        {
            return;
        }

        if (result != null)
        {
            labelFirmwareStatus.Text = EncryptionCompletedText;
            labelFirmwareStatus.ForeColor = Color.FromArgb(0, 120, 80);
            return;
        }

        if (!_firmwareEncryptionReady)
        {
            labelFirmwareStatus.ForeColor = Color.DarkOrange;
            return;
        }

        labelFirmwareStatus.Text = _firmwareSelfTestPassed ? SelfTestPassedText : SelfTestFailedText;
        labelFirmwareStatus.ForeColor = _firmwareSelfTestPassed ? Color.FromArgb(0, 120, 80) : Color.Firebrick;
    }

    private void LoadFileIntoSendArea(string filePath, bool previewTextContent)
    {
        textBoxSend.Text = filePath;
        fileSendBuffer = File.ReadAllBytes(filePath);
        fileSendOffset = 0;

        if (previewTextContent && Path.GetExtension(filePath).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            textBoxMainBuffer.Text = File.ReadAllText(filePath, GetSelectedEncoding());
        }

        UpdateFileSendProgress(0);
    }

    private string BuildSummaryText(long inputSize, long outputSize)
    {
        string inputText = inputSize >= 0 ? FormatFileSize(inputSize) : "未选择";
        string outputText = outputSize >= 0 ? FormatFileSize(outputSize) : "未生成";
        return $"输入: {inputText} | 输出: {outputText}";
    }

    private static long GetFileSize(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return -1;
        }

        return new FileInfo(filePath).Length;
    }

    private static string FormatFileSize(long size)
    {
        if (size < 1024)
        {
            return $"{size} B";
        }

        if (size < 1024 * 1024)
        {
            return $"{size / 1024.0:F2} KB";
        }

        return $"{size / 1024.0 / 1024.0:F2} MB";
    }
}
