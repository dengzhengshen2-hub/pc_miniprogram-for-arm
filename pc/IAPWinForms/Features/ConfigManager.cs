// 配置管理器，负责配置读写和文件变化监听。
using System.Text.Json;
using System.Xml.Serialization;

namespace IAPWinForms.Features;

// 定义配置的加载、保存和热监听能力。
public interface IConfigManager
{
    // 配置文件变化时触发。
    event EventHandler ConfigChanged;
    // 读取当前配置。
    Dictionary<string, string> Load();
    // 保存配置到磁盘。
    void Save(Dictionary<string, string> config);
    // 开启配置文件监听。
    void StartWatch();
    // 关闭配置文件监听。
    void StopWatch();
}

// 默认配置管理实现，同时兼容 JSON 和 XML。
internal sealed class ConfigManager : IConfigManager
{
    private readonly string _jsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
    private readonly string _xmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.xml");
    private readonly FileSystemWatcher _watcher;
    public event EventHandler? ConfigChanged;

    // 初始化监听器，关注配置文件的新增和修改。
    public ConfigManager()
    {
        _watcher = new FileSystemWatcher(AppDomain.CurrentDomain.BaseDirectory, "config.*")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
        };
        _watcher.Changed += (_, _) => ConfigChanged?.Invoke(this, EventArgs.Empty);
        _watcher.Created += (_, _) => ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    // 优先读取 JSON，缺失时回退到 XML，再没有则返回默认值。
    public Dictionary<string, string> Load()
    {
        if (File.Exists(_jsonPath))
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_jsonPath)) ?? CreateDefault();
        }
        if (File.Exists(_xmlPath))
        {
            XmlSerializer serializer = new(typeof(List<ConfigEntry>));
            using FileStream fs = File.OpenRead(_xmlPath);
            List<ConfigEntry> entries = serializer.Deserialize(fs) as List<ConfigEntry> ?? [];
            return entries.ToDictionary(x => x.Key, x => x.Value);
        }
        return CreateDefault();
    }

    // 同时输出 JSON 和 XML，方便兼容不同读取方式。
    public void Save(Dictionary<string, string> config)
    {
        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_jsonPath, json);
        XmlSerializer serializer = new(typeof(List<ConfigEntry>));
        List<ConfigEntry> entries = config.Select(x => new ConfigEntry { Key = x.Key, Value = x.Value }).ToList();
        using FileStream fs = File.Create(_xmlPath);
        serializer.Serialize(fs, entries);
    }

    // 开始监听配置文件变化。
    public void StartWatch() => _watcher.EnableRaisingEvents = true;
    // 停止监听配置文件变化。
    public void StopWatch() => _watcher.EnableRaisingEvents = false;

    // 生成默认配置。
    private static Dictionary<string, string> CreateDefault() => new()
    {
        ["DefaultPort"] = string.Empty,
        ["BaudRate"] = "115200",
        ["EncodingName"] = "UTF-8"
    };
}

// XML 序列化用的键值项结构。
public sealed class ConfigEntry
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
