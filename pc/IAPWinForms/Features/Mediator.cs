// 轻量中介器，负责模块间的松耦合消息转发。
namespace IAPWinForms.Features;

// 用主题订阅的方式转发消息，避免模块直接互相依赖。
internal sealed class Mediator
{
    private readonly Dictionary<string, List<WeakReference<Action<object?>>>> _handlers = new();

    // 订阅指定主题的消息。
    public void Subscribe(string topic, Action<object?> handler)
    {
        if (!_handlers.ContainsKey(topic)) _handlers[topic] = [];
        _handlers[topic].Add(new WeakReference<Action<object?>>(handler));
    }

    // 向指定主题发布消息，并顺带清理失效订阅。
    public void Publish(string topic, object? payload)
    {
        if (!_handlers.TryGetValue(topic, out List<WeakReference<Action<object?>>>? refs)) return;
        for (int i = refs.Count - 1; i >= 0; i--)
        {
            if (refs[i].TryGetTarget(out Action<object?>? action))
            {
                action(payload);
            }
            else
            {
                refs.RemoveAt(i);
            }
        }
    }
}
