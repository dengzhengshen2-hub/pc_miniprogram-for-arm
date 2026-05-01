using System.Text;

namespace IAPWinForms;

public partial class Form1
{
    private void InitializeReceiveViewPanel()
    {
        _dataReceiver.BlockReady += DataReceiver_BlockReady;
        receiveFlushTimer.Interval = 80;
        receiveFlushTimer.Tick += receiveFlushTimer_Tick;
    }

    private void SerialPortManager_DataReceived(object? sender, byte[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        Interlocked.Add(ref totalRxBytes, data.Length);
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            UpdateByteCounter();
            if (_suppressRawReceiveDisplay)
            {
                return;
            }

            _dataReceiver.Push(data, GetReceiveEncoding(), checkBoxHexDisplay.Checked);
            if (checkBoxPauseDisplay.Checked)
            {
                return;
            }

            receiveFlushTimer.Stop();
            receiveFlushTimer.Start();
        });
    }

    private void DataReceiver_BlockReady(object? sender, string block)
    {
        BeginInvoke(() =>
        {
            if (checkBoxPauseDisplay.Checked)
            {
                return;
            }

            AppendReceiveStream(block);
        });
    }

    private void receiveFlushTimer_Tick(object? sender, EventArgs e)
    {
        receiveFlushTimer.Stop();
        _dataReceiver.Flush(GetReceiveEncoding(), checkBoxHexDisplay.Checked, true);
    }

    private void buttonClear_Click(object? sender, EventArgs e)
    {
        textBoxReceive.Clear();
    }

    private void buttonSaveFile_Click(object? sender, EventArgs e)
    {
        using SaveFileDialog dialog = new();
        dialog.Filter = "文本文件|*.txt|所有文件|*.*";
        dialog.FileName = $"串口日志_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            File.WriteAllText(dialog.FileName, textBoxReceive.Text, Encoding.UTF8);
        }
    }

    private void AppendReceiveLine(string content, bool withTimestamp = true)
    {
        string text = content;
        if (withTimestamp && checkBoxTimestamp.Checked)
        {
            text = $"[{DateTime.Now:HH:mm:ss.fff}] {text}";
        }

        if (!text.EndsWith(Environment.NewLine))
        {
            text += Environment.NewLine;
        }

        textBoxReceive.AppendText(text);
    }

    private void AppendReceiveStream(string content)
    {
        if (checkBoxTimestamp.Checked)
        {
            string text = content;
            if (!text.EndsWith(Environment.NewLine))
            {
                text += Environment.NewLine;
            }

            textBoxReceive.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {text}");
            return;
        }

        textBoxReceive.AppendText(content);
    }

    private void checkBoxPauseDisplay_CheckedChanged(object? sender, EventArgs e)
    {
        if (!checkBoxPauseDisplay.Checked)
        {
            _dataReceiver.Flush(GetReceiveEncoding(), checkBoxHexDisplay.Checked, true);
        }
    }

    private static Encoding GetReceiveEncoding()
    {
        return Encoding.ASCII;
    }

    private void radioRecvAscii_CheckedChanged(object? sender, EventArgs e)
    {
        if (radioRecvAscii.Checked)
        {
            checkBoxHexDisplay.Checked = false;
        }
    }

    private void checkBoxHexDisplay_CheckedChanged(object? sender, EventArgs e)
    {
        radioRecvAscii.Checked = !checkBoxHexDisplay.Checked;
    }
}
