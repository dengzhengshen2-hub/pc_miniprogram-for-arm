namespace IAPWinForms;

public partial class Form1
{
    private void ApplyLightTheme()
    {
        splitContainerMain.Panel1.BackColor = Color.White;
        splitContainerMain.Panel2.BackColor = Color.White;
        panelLeft.BackColor = Color.White;
        panelRight.BackColor = Color.White;
        BackColor = Color.White;
        foreach (Control control in GetAllControls(this))
        {
            switch (control)
            {
                case GroupBox gb:
                    gb.BackColor = Color.White;
                    gb.ForeColor = Color.Black;
                    break;
                case Label lb:
                    lb.ForeColor = Color.Black;
                    break;
                case TextBox tb:
                    tb.BackColor = Color.White;
                    tb.ForeColor = Color.Black;
                    break;
                case Button btn:
                    btn.BackColor = Color.FromArgb(245, 245, 245);
                    btn.ForeColor = Color.Black;
                    break;
                case ComboBox cb:
                    cb.BackColor = Color.White;
                    cb.ForeColor = Color.Black;
                    break;
                case CheckBox ck:
                    ck.ForeColor = Color.Black;
                    break;
                case RadioButton rb:
                    rb.ForeColor = Color.Black;
                    break;
                case ProgressBar pb:
                    pb.ForeColor = Color.Green;
                    break;
            }
        }
    }

    private IEnumerable<Control> GetAllControls(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (Control child in GetAllControls(control))
            {
                yield return child;
            }
        }
    }

    private void ApplySendModeUI()
    {
        string selectedSendMode = comboBoxSendMode.SelectedItem?.ToString() ?? SendModeSingle;
        bool isMulti = string.Equals(selectedSendMode, SendModeMulti, StringComparison.Ordinal);
        string selectedOneClickCommandKey = GetSelectedOneClickDebugCommandKey();
        bool isOneClickDebug = !string.IsNullOrEmpty(selectedOneClickCommandKey);
        bool isFirmwareEncryption = string.Equals(selectedSendMode, SendModeFirmwareEncryption, StringComparison.Ordinal);
        bool isIapPackage = string.Equals(selectedSendMode, SendModeIapPackage, StringComparison.Ordinal);
        bool isYModemRecv = comboBoxProtocolRecv != null &&
            string.Equals(comboBoxProtocolRecv.SelectedItem?.ToString(), "ymodem", StringComparison.Ordinal);
        bool isYModemSend = comboBoxProtocolSend != null &&
            string.Equals(comboBoxProtocolSend.SelectedItem?.ToString(), "ymodem", StringComparison.Ordinal);
        bool isYModem = !isOneClickDebug && (isYModemRecv || isYModemSend);

        _isYModemModeSelected = isYModem;
        UpdateRawReceiveDisplaySuppression();

        if (isMulti || isYModem || isFirmwareEncryption || isIapPackage || isOneClickDebug)
        {
            fileSendTimer.Stop();
        }

        bool showSingle = !isMulti && !isYModem && !isFirmwareEncryption && !isIapPackage && !isOneClickDebug;
        textBoxMainBuffer.Visible = showSingle;
        textBoxSend.Visible = showSingle;
        buttonSend.Visible = showSingle;
        buttonClearSend.Visible = showSingle;
        buttonLoadFile.Visible = showSingle;
        buttonSendFile.Visible = showSingle;
        buttonStopSend.Visible = showSingle;
        if (progressBarFileSend != null) progressBarFileSend.Visible = showSingle;
        if (labelFileSendPercent != null) labelFileSendPercent.Visible = showSingle;

        if (panelMultiSendMode != null) panelMultiSendMode.Visible = isMulti;
        if (panelFirmwareEncryption != null) panelFirmwareEncryption.Visible = isFirmwareEncryption && !isYModem && !isOneClickDebug;
        if (panelIapPackage != null) panelIapPackage.Visible = isIapPackage && !isYModem && !isOneClickDebug;
        if (panelOneClickDebug != null) panelOneClickDebug.Visible = isOneClickDebug;
        if (panelYModem != null) panelYModem.Visible = isYModem;

        if (isOneClickDebug)
        {
            SelectOneClickDebugCommand(selectedOneClickCommandKey);
        }

        if (isYModem && buttonYCancel != null)
        {
            buttonYCancel.Text = isYModemSend ? "\u53d6\u6d88\u53d1\u9001" : "\u53d6\u6d88\u63a5\u6536";
        }
    }

    private void UpdateRawReceiveDisplaySuppression()
    {
        bool shouldSuppress = _isYModemBusy;
        bool suppressionChanged = _suppressRawReceiveDisplay != shouldSuppress;
        _suppressRawReceiveDisplay = shouldSuppress;

        if (suppressionChanged && shouldSuppress)
        {
            receiveFlushTimer.Stop();
            _dataReceiver.Reset();
        }
    }
}
