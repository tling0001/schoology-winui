using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;

namespace Schoology.WinUI;

public partial class App : Application
{
    private Window? _window;
    private DispatcherQueue? _dispatcherQueue;
    private static Mutex? _instanceMutex;
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    internal static bool IsBackgroundLaunch { get; private set; }
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Schoology", "Logs", "startup.log");

    public App()
    {
        // Register before InitializeComponent so XAML startup failures are captured.
        UnhandledException += (_, args) =>
        {
            WriteStartupLog($"Microsoft.UI.Xaml.Application.UnhandledException (Handled={args.Handled})", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteStartupLog($"AppDomain.UnhandledException (IsTerminating={args.IsTerminating})", args.ExceptionObject as Exception);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            WriteStartupLog("AppDomain.ProcessExit reached.");

        TaskScheduler.UnobservedTaskException += (_, args) =>
            WriteStartupLog($"TaskScheduler.UnobservedTaskException (Observed={args.Observed})", args.Exception);

        WriteStartupLog("App constructor entered.");
        InitializeComponent();
        WriteStartupLog("App.InitializeComponent completed.");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        WriteStartupLog("OnLaunched entered.");
        if ((args.Arguments ?? string.Empty).Contains("--unregister-notifications", StringComparison.OrdinalIgnoreCase))
        {
            try { AppNotificationManager.Default.Unregister(); WriteStartupLog("Windows App Notifications unregistered for uninstall."); }
            catch (Exception ex) { WriteStartupLog("Could not unregister Windows App Notifications during uninstall.", ex); }
            Environment.Exit(0);
            return;
        }
        _instanceMutex = new Mutex(initiallyOwned: true, name: @"Local\Schoology.WinUI.Singleton", createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            var existingWindow = FindWindow(null, "Schoology");
            if (existingWindow != IntPtr.Zero) PostMessage(existingWindow, 0x8000 + 42, IntPtr.Zero, IntPtr.Zero);
            WriteStartupLog("Forwarded launch request to the existing background instance.");
            GC.KeepAlive(_instanceMutex);
            return;
        }
        IsBackgroundLaunch = (args.Arguments ?? string.Empty).Contains("--background", StringComparison.OrdinalIgnoreCase);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        try
        {
            var notificationManager = AppNotificationManager.Default;
            notificationManager.NotificationInvoked += OnNotificationInvoked;
            notificationManager.Register();
            WriteStartupLog("Windows App Notifications registered.");
        }
        catch (Exception notificationException)
        {
            WriteStartupLog("Windows App Notifications could not be registered; continuing without toast delivery.", notificationException);
        }
        try
        {
            WriteStartupLog("Creating MainWindow.");
            _window = new MainWindow();
            WriteStartupLog("MainWindow constructor completed.");

            _window.Activate();
            WriteStartupLog("MainWindow.Activate completed.");
            if (IsBackgroundLaunch && _window is MainWindow mainWindow) mainWindow.HideToBackground();
        }
        catch (Exception ex)
        {
            WriteStartupLog("Exception caught in OnLaunched.", ex);
            throw;
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_window is MainWindow mainWindow) mainWindow.ShowFromBackgroundNotification();
        });
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
                        .Append(current.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture))
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
