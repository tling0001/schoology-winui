using Microsoft.UI.Xaml;
using System.Text;

namespace Schoology.WinUI;

public partial class App : Application
{
    private Window? _window;
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Schoology", "Logs", "startup.log");

    public App()
    {
        // Register before InitializeComponent so XAML startup failures are captured.
        UnhandledException += (_, args) =>
            WriteStartupLog("Microsoft.UI.Xaml.Application.UnhandledException", args.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteStartupLog("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
            WriteStartupLog("TaskScheduler.UnobservedTaskException", args.Exception);

        WriteStartupLog("App constructor entered.");
        InitializeComponent();
        WriteStartupLog("App.InitializeComponent completed.");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        WriteStartupLog("OnLaunched entered.");
        try
        {
            WriteStartupLog("Creating MainWindow.");
            _window = new MainWindow();
            WriteStartupLog("MainWindow constructor completed.");

            _window.Activate();
            WriteStartupLog("MainWindow.Activate completed.");
        }
        catch (Exception ex)
        {
            WriteStartupLog("Exception caught in OnLaunched.", ex);
            throw;
        }
    }

    internal static void WriteStartupLog(string message, Exception? exception = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O"))
                .Append(" [")
                .Append(Environment.ProcessId)
                .Append("] ")
                .AppendLine(message);

            if (exception is not null)
            {
                for (Exception? current = exception; current is not null; current = current.InnerException)
                {
                    entry.Append("Exception detail: ")
                        .Append(current.GetType().FullName)
                        .Append(" | HRESULT=0x")
                        .Append(current.HResult.ToString("X8"))
                        .Append(" | ")
                        .AppendLine(current.Message);
                    entry.AppendLine(current.StackTrace);
                }
                if (exception.Data.Count > 0)
                {
                    entry.AppendLine("Exception data:");
                    foreach (System.Collections.DictionaryEntry item in exception.Data)
                        entry.Append(item.Key).Append(" = ").AppendLine(item.Value?.ToString());
                }
                entry.AppendLine(exception.ToString());
            }

            File.AppendAllText(LogPath, entry.ToString());
        }
        catch
        {
            // Diagnostics must never introduce or worsen a startup failure.
        }
    }
}
