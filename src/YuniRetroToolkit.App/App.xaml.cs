using System.Windows;

namespace YuniRetroToolkit.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        if (e.Args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            window.ApplyTemplate();
            Console.WriteLine("UI_SMOKE_PASS");
            Shutdown(0);
            return;
        }
        MainWindow = window;
        window.Show();
    }
}
