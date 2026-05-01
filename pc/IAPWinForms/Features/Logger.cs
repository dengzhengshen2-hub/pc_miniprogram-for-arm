// 日志服务，负责写文件并把日志回推给界面。
using System.Text;

namespace IAPWinForms.Features;

// 定义信息日志和错误日志的写入能力。
public interface ILogger
{
    // 每条日志写入后触发。
    event EventHandler<string> Logged;
    // 记录普通信息日志。
    void Info(string message);
    // 记录错误日志。
    void Error(string message);
}

// 默认文件日志实现。
internal sealed class Logger : ILogger
{
    private readonly string _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", $"iap_{DateTime.Now:yyyyMMdd}.log");
    public event EventHandler<string>? Logged;

    // 初始化日志目录。
    public Logger()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_logFilePath)!);
    }

    // 记录信息级日志。
    public void Info(string message) => Write("INFO", message);
    // 记录错误级日志。
    public void Error(string message) => Write("ERROR", message);

    // 统一拼接日志格式并写入文件。
    private void Write(string level, string message)
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
        Logged?.Invoke(this, line);
    }
}
