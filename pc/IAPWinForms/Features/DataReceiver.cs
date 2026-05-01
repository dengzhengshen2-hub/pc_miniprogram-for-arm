using System.Text;

namespace IAPWinForms.Features;

public interface IDataReceiver
{
    event EventHandler<string> BlockReady;

    void Push(byte[] data, Encoding encoding, bool asHex);

    void Flush(Encoding encoding, bool asHex, bool flushPartial);

    void Reset();
}

internal sealed class DataReceiver : IDataReceiver
{
    private readonly List<byte> _byteBuffer = new();
    private readonly StringBuilder _textBuffer = new();
    private readonly object _sync = new();

    public event EventHandler<string>? BlockReady;

    public void Push(byte[] data, Encoding encoding, bool asHex)
    {
        if (data.Length == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (asHex)
            {
                string hex = BitConverter.ToString(data).Replace("-", " ");
                BlockReady?.Invoke(this, hex + Environment.NewLine);
                return;
            }

            _byteBuffer.AddRange(data);
        }
    }

    public void Flush(Encoding encoding, bool asHex, bool flushPartial)
    {
        if (asHex)
        {
            return;
        }

        byte[] chunk;
        lock (_sync)
        {
            if (_byteBuffer.Count == 0)
            {
                if (!flushPartial)
                {
                    return;
                }

                chunk = Array.Empty<byte>();
            }
            else
            {
                chunk = _byteBuffer.ToArray();
                _byteBuffer.Clear();
            }
        }

        if (chunk.Length > 0)
        {
            _textBuffer.Append(encoding.GetString(chunk));
        }

        string current = _textBuffer.ToString();
        if (string.IsNullOrEmpty(current))
        {
            return;
        }

        int idx = current.LastIndexOf('\n');
        if (idx < 0 && !flushPartial)
        {
            return;
        }

        int cut = idx >= 0 ? idx + 1 : current.Length;
        string ready = current[..cut]
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("\n", Environment.NewLine);

        _textBuffer.Clear();
        _textBuffer.Append(current[cut..]);
        BlockReady?.Invoke(this, ready);
    }

    public void Reset()
    {
        lock (_sync)
        {
            _byteBuffer.Clear();
            _textBuffer.Clear();
        }
    }
}
