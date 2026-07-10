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
        win.Show();

        // "open with cut" / file association hands us the path as an argument
        if (e.Args.FirstOrDefault(File.Exists) is { } file)
            win.OpenFile(file);
    }
}
