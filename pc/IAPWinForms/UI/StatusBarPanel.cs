// 状态栏逻辑，负责连接状态、字节计数和文件发送进度显示。
namespace IAPWinForms;

// 管理底部状态栏和文件发送进度控件。
public partial class Form1
{
    // 初始化状态栏默认文本。
    private void InitializeStatusBarPanel()
    {
        statusConnection.Text = "\u4e32\u53e3[\u65e0\u7aef\u53e3]\u5df2\u5173\u95ed";
        statusTxBytes.Text = "\u53d1\u9001\u5b57\u8282: 0";
        statusRxBytes.Text = "\u63a5\u6536\u5b57\u8282: 0";
    }

    // 刷新收发字节统计。
    private void UpdateByteCounter()
    {
        statusTxBytes.Text = $"\u53d1\u9001\u5b57\u8282: {totalTxBytes}";
        statusRxBytes.Text = $"\u63a5\u6536\u5b57\u8282: {totalRxBytes}";
    }

    // 清零收发计数。
    private void statusClearCounter_Click(object? sender, EventArgs e)
    {
        totalRxBytes = 0;
        totalTxBytes = 0;
        UpdateByteCounter();
    }

    // 创建文件发送进度条和百分比标签。
    private void BuildFileSendProgressBar()
    {
        progressBarFileSend = new ProgressBar
        {
            Location = new Point(686, 645),
            Size = new Size(280, 16),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Minimum = 0,
            Maximum = 100,
            Value = 0
        };

        labelFileSendPercent = new Label
        {
            Location = new Point(970, 643),
            Size = new Size(36, 20),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Text = "0%"
        };

        panelRight.Controls.Add(progressBarFileSend);
        panelRight.Controls.Add(labelFileSendPercent);
    }

    // 更新文件发送进度显示。
    private void UpdateFileSendProgress(int percent)
    {
        if (progressBarFileSend == null || labelFileSendPercent == null)
        {
            return;
        }

        int value = Math.Clamp(percent, 0, 100);
        progressBarFileSend.Value = value;
        labelFileSendPercent.Text = $"{value}%";
    }
}
