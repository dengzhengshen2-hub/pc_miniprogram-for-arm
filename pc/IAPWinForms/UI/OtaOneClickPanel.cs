using System.Diagnostics;

namespace IAPWinForms;

public partial class Form1
{
    private const string OneClickSetupKey = "setup";
    private const string OneClickPublishKey = "publish";
    private const string OneClickNegativeTestsKey = "negative-tests";
    private const string OneClickCleanKey = "clean";

    private static readonly IReadOnlyDictionary<string, OneClickDebugCommand> OneClickDebugCommands =
        new Dictionary<string, OneClickDebugCommand>(StringComparer.Ordinal)
        {
            [OneClickSetupKey] = new OneClickDebugCommand(
                "Setup",
                @"scripts\Setup-OtaProductionEnv.cmd",
                "这是 OTA 生产环境初始化入口。\r\n\r\n" +
                "执行内容：\r\n" +
                "1. 生成或轮换 OTA RSA 私钥，并导出公钥。\r\n" +
                "2. 刷新协议公钥头文件（供 STM32/ESP32 校验使用）。\r\n" +
                "3. 生成 ESP32 与 STM32 Bootloader 侧的 ota_security_secrets.h。\r\n" +
                "4. 写入发布相关环境变量（如 IAP_SECURITY_PROFILE / IAP_SIGNING_PRIVATE_KEY_PEM / IAP_AES_KEY_HEX）。\r\n\r\n" +
                "建议使用场景：\r\n" +
                "- 新电脑首次搭建 OTA 发布环境；\r\n" +
                "- 更换签名密钥或 AES Key 后；\r\n" +
                "- 怀疑本机密钥或发布环境变量不一致时。"),

            [OneClickPublishKey] = new OneClickDebugCommand(
                "Publish",
                @"scripts\Publish-OtaRelease.cmd",
                "这是 OTA 正式发布入口。\r\n\r\n" +
                "执行内容：\r\n" +
                "1. 读取 APP1/APP2 固件输入文件。\r\n" +
                "2. 生成 .iap 升级包与 release-manifest.v2.json。\r\n" +
                "3. 使用 RSA 私钥进行签名。\r\n" +
                "4. 输出 release-summary.txt 与 upload-list.txt。\r\n" +
                "5. 当配置 UPLOAD_TO_OSS=true 时自动上传 OSS。\r\n\r\n" +
                "建议使用场景：\r\n" +
                "- APP1/APP2 新版本编译完成后；\r\n" +
                "- 准备执行一次正式 OTA 发布时。"),

            [OneClickNegativeTestsKey] = new OneClickDebugCommand(
                "Negative Tests",
                @"scripts\Run-NegativeSecurityTests.cmd",
                "这是 OTA 负向安全测试总入口。\r\n\r\n" +
                "执行内容：\r\n" +
                "1. 按顺序准备并执行 manifest_tamper / iap_tamper / old_test_key 等测试。\r\n" +
                "2. 测试过程中会暂停，等待人工上板验证现象后继续。\r\n" +
                "3. 用于确认系统在异常包、篡改包、旧测试密钥等情况下具备拒绝能力。\r\n\r\n" +
                "建议使用场景：\r\n" +
                "- 正向 OTA 功能全部通过后；\r\n" +
                "- 交接、验收、答辩时出具安全验证证据。"),

            [OneClickCleanKey] = new OneClickDebugCommand(
                "Clean",
                @"scripts\Clean-BackupArtifacts.cmd",
                "这是工程备份前清理入口。\r\n\r\n" +
                "执行内容：\r\n" +
                "1. 清理可重建的发布产物目录。\r\n" +
                "2. 清理 tamper / negative tests 相关临时文件。\r\n" +
                "3. 保留工程源码与关键配置，降低备份体积。\r\n\r\n" +
                "建议使用场景：\r\n" +
                "- 打包工程归档前；\r\n" +
                "- 发工程给他人前；\r\n" +
                "- 需要清掉历史测试产物时。")
        };

    private sealed record OneClickDebugCommand(string DisplayName, string ScriptRelativePath, string Description);

    private void BuildOneClickDebugPanel()
    {
        panelOneClickDebug = new Panel
        {
            Name = "panelOneClickDebug",
            Location = new Point(8, 436),
            Size = new Size(998, 228),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Visible = false,
            BackColor = Color.White
        };

        labelOneClickDebugTitle = new Label
        {
            Text = "一键调试",
            Location = new Point(20, 12),
            Size = new Size(760, 26),
            Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point)
        };

        textBoxOneClickDebugDescription = new TextBox
        {
            Location = new Point(20, 44),
            Size = new Size(760, 144),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle
        };

        labelOneClickDebugScript = new Label
        {
            Location = new Point(20, 196),
            Size = new Size(760, 24),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };

        buttonOneClickDebugRun = new Button
        {
            Text = "执行",
            Location = new Point(802, 78),
            Size = new Size(178, 88),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(44, 50, 57),
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point)
        };
        buttonOneClickDebugRun.Click += buttonOneClickDebugRun_Click;

        panelOneClickDebug.Controls.AddRange(
        [
            labelOneClickDebugTitle,
            textBoxOneClickDebugDescription,
            labelOneClickDebugScript,
            buttonOneClickDebugRun
        ]);

        panelRight.Controls.Add(panelOneClickDebug);
        panelOneClickDebug.BringToFront();
    }

    private void comboBoxOneClickDebugMode_SelectedIndexChanged(object? sender, EventArgs e)
    {
        ApplySendModeUI();
    }

    private string GetSelectedOneClickDebugCommandKey()
    {
        string selected = comboBoxOneClickDebugMode?.SelectedItem?.ToString() ?? "无";
        if (string.Equals(selected, "Setup", StringComparison.OrdinalIgnoreCase))
        {
            return OneClickSetupKey;
        }

        if (string.Equals(selected, "Publish", StringComparison.OrdinalIgnoreCase))
        {
            return OneClickPublishKey;
        }

        if (string.Equals(selected, "Negative Tests", StringComparison.OrdinalIgnoreCase))
        {
            return OneClickNegativeTestsKey;
        }

        if (string.Equals(selected, "Clean", StringComparison.OrdinalIgnoreCase))
        {
            return OneClickCleanKey;
        }

        return string.Empty;
    }

    private void SelectOneClickDebugCommand(string commandKey)
    {
        if (!OneClickDebugCommands.TryGetValue(commandKey, out OneClickDebugCommand? selected))
        {
            return;
        }

        _oneClickDebugSelectedKey = commandKey;

        if (labelOneClickDebugTitle != null)
        {
            labelOneClickDebugTitle.Text = $"一键调试 - {selected.DisplayName}";
        }

        if (textBoxOneClickDebugDescription != null)
        {
            textBoxOneClickDebugDescription.Text = selected.Description;
            textBoxOneClickDebugDescription.SelectionStart = 0;
            textBoxOneClickDebugDescription.SelectionLength = 0;
        }

        if (labelOneClickDebugScript != null)
        {
            labelOneClickDebugScript.Text = $"命令：cmd /c {selected.ScriptRelativePath}";
        }

        if (buttonOneClickDebugRun != null)
        {
            buttonOneClickDebugRun.Text = $"执行{selected.DisplayName}";
        }
    }

    private void buttonOneClickDebugRun_Click(object? sender, EventArgs e)
    {
        if (!OneClickDebugCommands.TryGetValue(_oneClickDebugSelectedKey, out OneClickDebugCommand? selected))
        {
            return;
        }

        string repositoryRoot = ResolveRepositoryRoot();
        string scriptPath = Path.Combine(repositoryRoot, selected.ScriptRelativePath.Replace('\\', Path.DirectorySeparatorChar));
        if (!File.Exists(scriptPath))
        {
            string message = $"未找到脚本文件：{scriptPath}";
            AppendReceiveLine($"[一键调试] {message}");
            MessageBox.Show(message, "一键调试", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"{scriptPath}\"",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = true
            };

            Process? process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("命令进程未成功启动。");
            }

            AppendReceiveLine($"[一键调试] 已启动 {selected.DisplayName}: {selected.ScriptRelativePath}");
        }
        catch (Exception ex)
        {
            AppendReceiveLine($"[一键调试] 执行失败: {ex.Message}");
            MessageBox.Show($"执行失败: {ex.Message}", "一键调试", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            bool hasScripts = Directory.Exists(Path.Combine(current.FullName, "scripts"));
            bool hasPc = Directory.Exists(Path.Combine(current.FullName, "pc"));
            if (hasScripts && hasPc)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
