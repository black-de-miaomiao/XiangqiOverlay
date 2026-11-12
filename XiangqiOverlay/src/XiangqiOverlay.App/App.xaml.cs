using System.IO;
using System.Windows;
using XiangqiOverlay.Core;
using XiangqiOverlay.Platform;

namespace XiangqiOverlay.App;

/// <summary>
/// App.xaml 的交互逻辑
/// </summary>
public partial class App : System.Windows.Application
{
    private MainController? _controller;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now}] [DispatcherUnhandledException] {args.Exception}\n");
            }
            catch {}
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now}] [AppDomainUnhandledException] {args.ExceptionObject}\n");
            }
            catch {}
        };

        try
        {
            // 加载配置
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
            var settings = AppSettings.Load(settingsPath);

            // 创建主控制器
            _controller = new MainController(settings);

            // 将控制器传递给主窗口
            var mainWindow = new MainWindow(_controller, settings, settingsPath);
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"),
                    $"[{DateTime.Now}] [StartupException] {ex}\n");
            }
            catch {}
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        base.OnExit(e);
    }
}
