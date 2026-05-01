// 程序入口，负责初始化 WinForms 环境并启动主窗体。
namespace IAPWinForms;

// 应用启动类。
static class Program
{
    // 启动应用并进入主界面消息循环。
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }    
}
