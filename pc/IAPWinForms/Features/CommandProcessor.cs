// 发送命令构造器，负责把输入文本转换成串口负载。
using System.Text;

namespace IAPWinForms.Features;

// 统一处理普通文本和 HEX 输入的编码规则。
public interface ICommandProcessor
{
    // 根据发送模式构造最终字节流。
    byte[] BuildPayload(string raw, bool hexMode, bool appendNewLine, Encoding encoding);
    // 生成适合界面显示的发送内容。
    string BuildDisplay(string raw, byte[] payload, bool hexMode);
}

// 命令构造的默认实现。
internal sealed class CommandProcessor : ICommandProcessor
{
    // 把输入文本转换成实际要发送的字节数组。
    public byte[] BuildPayload(string raw, bool hexMode, bool appendNewLine, Encoding encoding)
    {
        if (hexMode)
        {
            // HEX 模式按空白和逗号拆分，再逐个转字节。
            string[] parts = raw.Split([' ', '\r', '\n', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            List<byte> bytes = new(parts.Length);
            foreach (string part in parts)
            {
                bytes.Add(Convert.ToByte(part, 16));
            }
            return bytes.ToArray();
        }
        string text = appendNewLine ? raw + Environment.NewLine : raw;
        return encoding.GetBytes(text);
    }

    // 生成发送区可读文本，HEX 模式显示为十六进制串。
    public string BuildDisplay(string raw, byte[] payload, bool hexMode)
    {
        return hexMode ? BitConverter.ToString(payload).Replace("-", " ") : raw;
    }
}
