using System.Windows;
using Cut.Core;
using Cut.UI;

namespace Cut;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        Logger.Init();

        if (e.Args.Contains("--selftest"))
        {
            // headless CI smoke test, no window
            var code = SelfTest.Run();
            Shutdown(code);
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Logger.Error("unhandled", ex.Exception);
            if (Demo.Active)
            {
                Demo.Fail(ex.Exception);
                ex.Handled = true;
                return;
            }
            MessageBox.Show($"something went wrong:\n{ex.Exception.Message}\n\nlog: {Logger.LogFile}",
                "cut", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        if (ToolPaths.FirstMissing() is { } missing)
        {
            MessageBox.Show($"{missing} is missing next to cut.exe.\nreinstall cut to fix this.",
                "cut", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var win = new MainWindow();
        MainWindow = win;

        // --demo <outdir>: scripted walkthrough for the guide recordings (and an end-to-end ui test)
        var demoAt = Array.IndexOf(e.Args, "--demo");
        if (demoAt >= 0 && demoAt + 1 < e.Args.Length)
        {
            var demo = new Demo(win, e.Args[demoAt + 1]);
            win.DemoHook = demo.RunAsync;
        }

        win.Show();

        // "open with cut" / file association hands us the path as an argument
        if (demoAt < 0 && e.Args.FirstOrDefault(File.Exists) is { } file)
            win.OpenFile(file);
    }
}
