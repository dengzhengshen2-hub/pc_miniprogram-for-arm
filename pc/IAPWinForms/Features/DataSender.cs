// 发送调度器，负责排队发送和简单重试。
namespace IAPWinForms.Features;

// 定义发送队列的入队、处理和清空能力。
public interface IDataSender
{
    // 每条数据真正发出后触发。
    event EventHandler<byte[]> Sending;
    // 把数据加入待发送队列。
    void Enqueue(byte[] payload, int retry = 0);
    // 按顺序消费发送队列。
    void ProcessQueue(Func<byte[], bool> sendAction);
    // 清空待发送队列。
    void Clear();
}

// 默认发送队列实现。
internal sealed class DataSender : IDataSender
{
    private readonly Queue<(byte[] payload, int retry)> _queue = new();
    public event EventHandler<byte[]>? Sending;

    // 入队一条待发送数据。
    public void Enqueue(byte[] payload, int retry = 0)
    {
        if (payload.Length == 0) return;
        _queue.Enqueue((payload, Math.Max(0, retry)));
    }

    // 顺序处理发送队列，失败时按重试次数回队。
    public void ProcessQueue(Func<byte[], bool> sendAction)
    {
        while (_queue.Count > 0)
        {
            (byte[] payload, int retry) item = _queue.Dequeue();
            bool ok = sendAction(item.payload);
            if (!ok && item.retry > 0)
            {
                _queue.Enqueue((item.payload, item.retry - 1));
                continue;
            }
            Sending?.Invoke(this, item.payload);
        }
    }

    // 清空所有待发送数据。
    public void Clear() => _queue.Clear();
}
