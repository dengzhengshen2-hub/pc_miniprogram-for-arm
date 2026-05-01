using IAPWinForms.Features;

namespace IAPWinForms;

public partial class Form1
{
    private const string IapPackageTitleText = "IAP升级打包";
    private const string IapPackageReadyText = "请选择原始固件。开发态可指定 PEM 私钥；生产态请通过环境变量或密钥提供器完成签名。";
    private const string IapPackageSuccessText = "升级包生成完成，结果已同步到 YModem 路径。";

    private void BuildIapPackagePanel()
    {
        panelIapPackage = new Panel
        {
            Name = "panelIapPackage",
            Location = new Point(8, 436),
            Size = new Size(998, 228),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Visible = false,
            BackColor = Color.White
        };

        Label labelTitle = new()
        {
            AutoSize = true,
            Text = IapPackageTitleText,
            Location = new Point(20, 14),
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point)
        };

        Label labelSubtitle = new()
        {
            Text = "生成单文件 .iap 升级包，默认输出 manifest v2、随机 IV、固件签名与 manifest 签名。",
            Location = new Point(145, 16),
            Size = new Size(800, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = Color.DimGray
        };

        labelIapPackageStatus = new Label
        {
            Text = IapPackageReadyText,
            Location = new Point(20, 42),
            Size = new Size(928, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };

        Label labelFirmware = new()
        {
            Text = "原始固件",
            Location = new Point(20, 76),
            Size = new Size(78, 24),
            TextAlign = ContentAlignment.MiddleLeft
        };

        textBoxIapFirmwarePath = new TextBox
        {
            Location = new Point(104, 74),
            Size = new Size(720, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        Button buttonBrowseFirmware = new()
        {
            Text = "浏览...",
            Location = new Point(836, 72),
            Size = new Size(120, 31),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat
        };
        buttonBrowseFirmware.Click += buttonBrowseIapFirmware_Click;

        Label labelPrivateKey = new()
        {
            Text = "开发态私钥",
            Location = new Point(20, 112),
            Size = new Size(78, 24),
            TextAlign = ContentAlignment.MiddleLeft
        };

        textBoxIapPrivateKeyPath = new TextBox
        {
            Location = new Point(104, 110),
            Size = new Size(720, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        Button buttonBrowsePrivateKey = new()
        {
            Text = "选择PEM",
            Location = new Point(836, 108),
            Size = new Size(120, 31),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat
        };
        buttonBrowsePrivateKey.Click += buttonBrowseIapPrivateKey_Click;

        Label labelOutput = new()
        {
            Text = "输出升级包",
            Location = new Point(20, 148),
            Size = new Size(78, 24),
            TextAlign = ContentAlignment.MiddleLeft
        };

        textBoxIapOutputPath = new TextBox
        {
            Location = new Point(104, 146),
            Size = new Size(720, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        Button buttonBrowseOutput = new()
        {
            Text = "另存为...",
            Location = new Point(836, 144),
            Size = new Size(120, 31),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat
        };
        buttonBrowseOutput.Click += buttonBrowseIapOutput_Click;

        labelIapPackageSummary = new Label
        {
            Text = BuildIapPackageSummaryText(-1, string.Empty),
            Location = new Point(20, 186),
            Size = new Size(804, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleLeft
        };

        Button buttonCreatePackage = new()
        {
            Text = "生成 .iap",
            Location = new Point(836, 180),
            Size = new Size(120, 31),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat
        };
        buttonCreatePackage.Click += buttonCreateIapPackage_Click;

        panelIapPackage.Controls.AddRange(
        [
            labelTitle,
            labelSubtitle,
            labelIapPackageStatus,
            labelFirmware,
            textBoxIapFirmwarePath,
            buttonBrowseFirmware,
            labelPrivateKey,
            textBoxIapPrivateKeyPath,
            buttonBrowsePrivateKey,
            labelOutput,
            textBoxIapOutputPath,
            buttonBrowseOutput,
            labelIapPackageSummary,
            buttonCreatePackage
        ]);

        panelRight.Controls.Add(panelIapPackage);
        panelIapPackage.BringToFront();
        UpdateIapPackageSummary();
    }

    private void buttonBrowseIapFirmware_Click(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new()
        {
            Filter = "BIN 固件|*.bin|所有文件|*.*",
            Title = "选择原始固件"
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        string previousInputPath = textBoxIapFirmwarePath?.Text ?? string.Empty;
        string previousSuggestedOutput = _iapPackageService.BuildDefaultPackagePath(previousInputPath);
        string newSuggestedOutput = _iapPackageService.BuildDefaultPackagePath(dialog.FileName);

        if (textBoxIapFirmwarePath != null)
        {
            textBoxIapFirmwarePath.Text = dialog.FileName;
        }

        if (textBoxIapOutputPath != null &&
            (string.IsNullOrWhiteSpace(textBoxIapOutputPath.Text) ||
             string.Equals(textBoxIapOutputPath.Text, previousSuggestedOutput, StringComparison.OrdinalIgnoreCase)))
        {
            textBoxIapOutputPath.Text = newSuggestedOutput;
        }

        UpdateIapPackageSummary();
    }

    private void buttonBrowseIapPrivateKey_Click(object? sender, EventArgs e)
    {
        using OpenFileDialog dialog = new()
        {
            Filter = "PEM 私钥|*.pem|所有文件|*.*",
            Title = "选择开发态 RSA 私钥 PEM"
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        if (textBoxIapPrivateKeyPath != null)
        {
            textBoxIapPrivateKeyPath.Text = dialog.FileName;
        }
    }

    private void buttonBrowseIapOutput_Click(object? sender, EventArgs e)
    {
        string firmwarePath = textBoxIapFirmwarePath?.Text ?? string.Empty;
        string defaultOutput = _iapPackageService.BuildDefaultPackagePath(firmwarePath);
        string currentOutput = textBoxIapOutputPath?.Text ?? string.Empty;
        string dialogPath = string.IsNullOrWhiteSpace(currentOutput) ? defaultOutput : currentOutput;

        using SaveFileDialog dialog = new()
        {
            Filter = "IAP 升级包|*.iap|所有文件|*.*",
            Title = "选择 .iap 输出位置",
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

        if (textBoxIapOutputPath != null)
        {
            textBoxIapOutputPath.Text = dialog.FileName;
        }
    }

    private void buttonCreateIapPackage_Click(object? sender, EventArgs e)
    {
        string firmwarePath = textBoxIapFirmwarePath?.Text.Trim() ?? string.Empty;
        string privateKeyPath = textBoxIapPrivateKeyPath?.Text.Trim() ?? string.Empty;
        string outputPath = textBoxIapOutputPath?.Text.Trim() ?? string.Empty;

        try
        {
            IapPackageBuildResult result = _iapPackageService.CreatePackage(new IapPackageBuildRequest
            {
                FirmwarePath = firmwarePath,
                PrivateKeyPath = privateKeyPath,
                OutputPackagePath = outputPath
            });

            SetYModemSelectedPath(result.OutputPackagePath);
            UpdateIapPackageSummary(result);

            string message = $"IAP package created: {Path.GetFileName(result.FirmwarePath)} -> {Path.GetFileName(result.OutputPackagePath)}";
            _logger.Info(message);
            MessageBox.Show(
                $".iap 生成完成\r\n输出文件: {result.OutputPackagePath}\r\nCRC32: {result.FirmwareCrc32}\r\n签名: {result.SignatureAlgorithm}-{result.HashAlgorithm}",
                "提示",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            if (labelIapPackageStatus != null)
            {
                labelIapPackageStatus.Text = ex.Message;
                labelIapPackageStatus.ForeColor = Color.Firebrick;
            }

            _logger.Error($"IAP package create failed: {ex.Message}");
            MessageBox.Show($"生成 .iap 失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateIapPackageSummary(IapPackageBuildResult? result = null)
    {
        long firmwareSize = result?.FirmwareSize ?? GetFileSize(textBoxIapFirmwarePath?.Text);
        string crc32 = result?.FirmwareCrc32 ?? TryComputeCrc32(textBoxIapFirmwarePath?.Text);

        if (labelIapPackageSummary != null)
        {
            labelIapPackageSummary.Text = BuildIapPackageSummaryText(firmwareSize, crc32);
        }

        if (labelIapPackageStatus == null)
        {
            return;
        }

        if (result != null)
        {
            labelIapPackageStatus.Text = IapPackageSuccessText;
            labelIapPackageStatus.ForeColor = Color.FromArgb(0, 120, 80);
            return;
        }

        labelIapPackageStatus.Text = IapPackageReadyText;
        labelIapPackageStatus.ForeColor = Color.DimGray;
    }

    private string BuildIapPackageSummaryText(long firmwareSize, string crc32)
    {
        string sizeText = firmwareSize >= 0 ? FormatFileSize(firmwareSize) : "未选择";
        string crcText = string.IsNullOrWhiteSpace(crc32) ? "-" : crc32;
        return $"固件: {sizeText} | CRC32: {crcText} | 签名: RSA-SHA256 | 输出: .iap(zip)";
    }

    private static string TryComputeCrc32(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return string.Empty;
        }

        byte[] bytes = File.ReadAllBytes(filePath);
        return bytes.Length == 0 ? string.Empty : Crc32Utility.ComputeHex(bytes);
    }
}
