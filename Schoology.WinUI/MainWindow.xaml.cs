using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Windows.Graphics;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.ViewManagement;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Schoology.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly Grid _root;
    private readonly DispatcherTimer _splashTimer;
    private string _page = "login";
    private string _lastRenderedPage = "splash";
    private string _section = "Recent Activity";
    private NavigationView? _appNavigation;
    private Frame? _sectionFrame;
    private string _selectedEntity = string.Empty;
    private long _selectedEntityId;
    private readonly SchoologyAuthService _auth = new(false);
    private readonly SchoologyApiClient _api;
    private readonly ObservableCollection<SchoologyEntity> _courses = new();
    private readonly ObservableCollection<SchoologyEntity> _groups = new();
    private readonly Dictionary<long, JsonElement> _userProfileCache = new();
    private WebView2? _sharedWebView;
    private CoreWebView2Environment? _sharedWebViewEnvironment;
    private bool _sharedWebViewInitialized;
    private DispatcherTimer? _notificationTimer;
    private bool _notificationPollInProgress;
    private bool _allowExit;
    private bool _isBackgroundHidden;
    private bool _releaseNotesPending;
    private bool _trayIconAdded;
    private IntPtr _previousWindowProc;
    private WindowProc? _windowProcDelegate;
    private const uint WmClose = 0x0010;
    private const uint WmEndSession = 0x0016;
    private const uint WmTrayCallback = 0x8000 + 41;
    private const uint WmActivateExisting = 0x8000 + 42;
    private const int GwlWndProc = -4;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private bool _sharedWebViewAllowLti;
    private long _sharedWebViewSessionUserId;
    private TextBlock? _sharedWebViewStatus;
    private SchoologyAuthService.Session? _session;
    private QrCameraScanner? _qrScanner;
    private MediaPlayer? _qrPlayer;
    private static readonly string WindowStatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schoology", "window-state.json");
    private static readonly string ReleaseNotesStatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schoology", "release-notes-version.txt");
    private static readonly string AppSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schoology", "settings.json");
    private static readonly string NotificationSeenStatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schoology", "notification-seen.json");
    private static readonly HttpClient ReleaseNotesClient = new();
    private bool _restoringWindowState;

    public MainWindow()
    {
        App.WriteStartupLog("MainWindow constructor entered.");
        InitializeComponent();
        App.WriteStartupLog("MainWindow.InitializeComponent completed.");

        _root = Root;
        _api = new SchoologyApiClient(_auth);
        _windowProcDelegate = HandleWindowMessage;
        var windowHandle = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        _previousWindowProc = SetWindowLongPtr(windowHandle, GwlWndProc, _windowProcDelegate);
        ConfigureStartupRegistration();
        Closed += (_, _) =>
        {
            DestroyTrayIcon();
            try { AppNotificationManager.Default.Unregister(); } catch (Exception ex) { App.WriteStartupLog("Could not unregister app notifications during shutdown.", ex); }
        };
        _root.RequestedTheme = ReadAppSetting("AppTheme", "System default") switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        _root.ActualThemeChanged += (_, _) => UpdateSharedWebViewTheme();
        Title = "Schoology";
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        try { AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall; } catch { }
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        try { SystemBackdrop = new MicaBackdrop(); } catch { }

        RestoreWindowState();
        AppWindow.Changed += (_, _) => SaveWindowState();
        _ = SetWindowIconAsync();
        App.WriteStartupLog("Previous window bounds and native window icon configured.");

        App.WriteStartupLog("Creating splash screen.");
        ShowSplash();
        App.WriteStartupLog("Splash screen created.");

        _splashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _splashTimer.Tick += async (_, _) =>
        {
            _splashTimer.Stop();
            try
            {
                App.WriteStartupLog("Splash timer tick; switching to login.");
                ShowLogin();
                App.WriteStartupLog("Login screen created; beginning saved-session restoration.");
                await RestoreSavedSessionAsync();
                if (_isBackgroundHidden) _releaseNotesPending = true;
                else await CheckAndShowReleaseNotesAsync();
                App.WriteStartupLog("Startup session restoration and release-notes sequence completed.");
            }
            catch (Exception ex)
            {
                App.WriteStartupLog("Unhandled exception in splash/login/session transition.", ex);
                try
                {
                    if (_page != "login") ShowLogin();
                }
                catch (Exception fallbackException)
                {
                    App.WriteStartupLog("Could not restore the login screen after startup failure.", fallbackException);
                }
            }
        };
        _splashTimer.Start();
        App.WriteStartupLog("Splash timer started.");
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, WindowProc newProc);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr previousProc, IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowNativeWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "LoadIconW", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "CreatePopupMenu")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState; public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    private bool BackgroundModeEnabled => string.Equals(ReadAppSetting("RunInBackgroundAtStartup", "true"), "true", StringComparison.OrdinalIgnoreCase);
    private void ConfigureStartupRegistration()
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (runKey is null) return;
            if (BackgroundModeEnabled)
            {
                var executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executable)) runKey.SetValue("Schoology", $"\"{executable}\" --background");
            }
            else runKey.DeleteValue("Schoology", throwOnMissingValue: false);
        }
        catch (Exception ex) { App.WriteStartupLog("Could not configure the current-user Windows startup entry.", ex); }
    }
    private IntPtr HandleWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmEndSession && wParam != IntPtr.Zero)
        {
            _allowExit = true;
            DestroyTrayIcon();
        }
        if (message == WmClose && BackgroundModeEnabled && !_allowExit)
        {
            EnsureTrayIcon();
            _isBackgroundHidden = true;
            ShowNativeWindow(hwnd, 0);
            return IntPtr.Zero;
        }
        if (message == WmActivateExisting)
        {
            ShowFromBackgroundNotification();
            return IntPtr.Zero;
        }
        if (message == WmTrayCallback)
        {
            var trayMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
            if (trayMessage == WmLButtonDoubleClick) ShowFromBackgroundNotification();
            else if (trayMessage == WmRButtonUp) ShowTrayMenu(hwnd);
            return IntPtr.Zero;
        }
        return _previousWindowProc == IntPtr.Zero ? IntPtr.Zero : CallWindowProc(_previousWindowProc, hwnd, message, wParam, lParam);
    }
    private void EnsureTrayIcon()
    {
        if (_trayIconAdded) return;
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = hwnd, uID = 1,
            uFlags = 1 | 2 | 4, uCallbackMessage = WmTrayCallback,
            hIcon = LoadIcon(IntPtr.Zero, new IntPtr(32512)), szTip = "Schoology", szInfo = string.Empty, szInfoTitle = string.Empty
        };
        _trayIconAdded = ShellNotifyIcon(0, ref data);
    }
    private void DestroyTrayIcon()
    {
        if (!_trayIconAdded) return;
        var data = new NotifyIconData { cbSize = (uint)Marshal.SizeOf<NotifyIconData>(), hWnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id), uID = 1, szTip = string.Empty, szInfo = string.Empty, szInfoTitle = string.Empty };
        ShellNotifyIcon(2, ref data);
        _trayIconAdded = false;
    }
    private void ShowTrayMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, 0, new UIntPtr(1), "Open Schoology");
            AppendMenu(menu, 0, new UIntPtr(2), "Exit");
            GetCursorPos(out var point);
            SetForegroundWindow(hwnd);
            var command = TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, hwnd, IntPtr.Zero);
            if (command == 1) ShowFromBackgroundNotification();
            else if (command == 2)
            {
                _allowExit = true;
                DestroyTrayIcon();
                SendMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally { DestroyMenu(menu); }
    }
    internal void HideToBackground()
    {
        if (!BackgroundModeEnabled) return;
        EnsureTrayIcon();
        _isBackgroundHidden = true;
        ShowNativeWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id), 0);
    }
    internal void ShowFromBackgroundNotification()
    {
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        DestroyTrayIcon();
        _isBackgroundHidden = false;
        ShowNativeWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
        if (_releaseNotesPending)
        {
            _releaseNotesPending = false;
            _ = CheckAndShowReleaseNotesAsync();
        }
    }
    private void SetWindowSize(int widthDip, int heightDip)
    {
        try
        {
            var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            var scale = GetDpiForWindow(hwnd) / 96.0;
            AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
        }
        catch { }
    }


    private sealed record SavedWindowState(int Width, int Height, bool Maximized);
    private SizeInt32 _lastNormalWindowSize;

    private void RestoreWindowState()
    {
        try
        {
            _restoringWindowState = true;
            SavedWindowState? saved = File.Exists(WindowStatePath)
                ? JsonSerializer.Deserialize<SavedWindowState>(File.ReadAllText(WindowStatePath)) : null;
            if (saved is { Width: >= 640, Height: >= 480 })
            {
                _lastNormalWindowSize = new SizeInt32(saved.Width, saved.Height);
                AppWindow.Resize(_lastNormalWindowSize);
                if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
                App.WriteStartupLog($"Restored previous window size {saved.Width}x{saved.Height}px; maximized={saved.Maximized}.");
            }
            else
            {
                SetWindowSize(1260, 820);
                var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
                var scale = GetDpiForWindow(hwnd) / 96.0;
                _lastNormalWindowSize = new SizeInt32((int)(1260 * scale), (int)(820 * scale));
            }
        }
        catch (Exception ex)
        {
            App.WriteStartupLog("Could not restore previous window state.", ex);
            SetWindowSize(1260, 820);
        }
        finally { _restoringWindowState = false; }
    }

    private void SaveWindowState()
    {
        if (_restoringWindowState) return;
        try
        {
            var maximized = AppWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Maximized;
            var size = AppWindow.Size;
            if (!maximized && size.Width >= 640 && size.Height >= 480) _lastNormalWindowSize = size;
            if (_lastNormalWindowSize.Width < 640 || _lastNormalWindowSize.Height < 480) return;
            Directory.CreateDirectory(Path.GetDirectoryName(WindowStatePath)!);
            File.WriteAllText(WindowStatePath, JsonSerializer.Serialize(
                new SavedWindowState(_lastNormalWindowSize.Width, _lastNormalWindowSize.Height, maximized)));
        }
        catch (Exception ex) { App.WriteStartupLog("Could not persist window state.", ex); }
    }

    private static SolidColorBrush Brush(string color) => new(Microsoft.UI.ColorHelper.FromArgb(
        255, Convert.ToByte(color.Substring(1, 2), 16), Convert.ToByte(color.Substring(3, 2), 16), Convert.ToByte(color.Substring(5, 2), 16)));
    private static TextBlock Text(string value, double size = 14, bool bold = false) => new()
    {
        Text = value, FontSize = size, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };
    private UIElement EmptyState(string title, string? description = null)
    {
        var content = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 520,
            Margin = new Thickness(20)
        };
        content.Children.Add(Text(title, 18, true));
        if (!string.IsNullOrWhiteSpace(description))
        {
            var detail = Text(description, 13);
            detail.Foreground = ThemeBrush("SchoologyMutedTextBrush");
            content.Children.Add(detail);
        }
        return Surface(content, new Thickness(20));
    }

    private static Button Action(string label, RoutedEventHandler handler, bool primary = false)
    {
        var b = new Button { Content = label, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Stretch };
        if (primary) { b.Background = Brush("#2E66A3"); b.Foreground = Brush("#FFFFFF"); }
        b.Click += handler; return b;
    }
    private Brush ThemeBrush(string key)
    {
        // These brushes live in ThemeDictionaries, so looking them up directly in
        // Application.Resources throws at runtime. Resolve against the active
        // Window theme and keep a safe fallback so startup cannot fail on a lookup.
        var themeName = _root.ActualTheme switch
        {
            ElementTheme.Dark => "Dark",
            ElementTheme.Light => "Light",
            _ => "Light"
        };

        try
        {
            if (Application.Current.Resources.ThemeDictionaries[themeName] is ResourceDictionary themeDictionary &&
                themeDictionary[key] is Brush themedBrush)
                return themedBrush;
        }
        catch { }

        return key switch
        {
            "SchoologySurfaceBrush" => Brush("#FFFFFFFF"),
            "SchoologySubtleBrush" => Brush("#FFF2F5F7"),
            "SchoologyDividerBrush" => Brush("#FFD9E0E5"),
            "SchoologyTextBrush" => Brush("#FF202124"),
            "SchoologyMutedTextBrush" => Brush("#FF52616D"),
            "SchoologyBlueDarkBrush" => Brush("#FF204D7B"),
            _ => Brush("#FF2E66A3")
        };
    }

    private Border Surface(UIElement child, Thickness? padding = null)
    {
        // WinUI 3 AcrylicBrush is an in-app material. Unlike UWP's brush,
        // it has no BackgroundSource/AcrylicBackgroundSource API.
        // MicaBackdrop is applied to the Window; this brush provides a
        // theme-tinted translucent surface with a safe opaque fallback.
        var dark = _root.ActualTheme == ElementTheme.Dark;
        Brush material = new AcrylicBrush
        {
            TintColor = dark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 30, 35, 40)
                : Microsoft.UI.ColorHelper.FromArgb(255, 248, 250, 252),
            TintOpacity = dark ? 0.82 : 0.76,
            FallbackColor = dark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 36, 40)
                : Microsoft.UI.ColorHelper.FromArgb(255, 255, 255, 255)
        };
        return new Border { Background = material, BorderBrush = ThemeBrush("SchoologyDividerBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = padding ?? new Thickness(16), Child = child, Margin = new Thickness(0, 0, 0, 12) };
    }
    private void Reset(UIElement content)
    {
        var previousPage = _lastRenderedPage;
        var destinationPage = _page;
        _root.Children.Clear();

        // Use native WinUI entrance transitions with direction derived from the
        // navigation origin. Returning to the login root moves in from the left;
        // opening a child screen moves in from the right; sibling/home navigation
        // uses a subtle forward motion. Respect the system reduced-motion setting.
        if (new UISettings().AnimationsEnabled)
        {
            var returningToRoot = destinationPage == "login" &&
                previousPage is not "splash" and not "login";
            var movingBackToHome = destinationPage == "home" &&
                previousPage is not "login" and not "splash" &&
                previousPage != "home";
            var horizontalOffset = returningToRoot || movingBackToHome ? -36 : 36;
            content.Transitions = new TransitionCollection
            {
                new EntranceThemeTransition
                {
                    FromHorizontalOffset = horizontalOffset,
                    FromVerticalOffset = 8
                }
            };
        }
        else
        {
            content.Transitions = null;
        }

        _root.Children.Add(content);
        _lastRenderedPage = destinationPage;
    }

    private async Task SetWindowIconAsync()
    {
        try
        {
            var assembly = typeof(MainWindow).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("Schoology.WinUI.Assets.Schoology.ico", StringComparison.Ordinal));
            if (resourceName is null)
            {
                App.WriteStartupLog("Embedded Schoology window icon resource was not found.");
                return;
            }

            await using var source = assembly.GetManifestResourceStream(resourceName);
            if (source is null) return;
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            var iconDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Schoology", "Assets");
            Directory.CreateDirectory(iconDirectory);
            var iconPath = Path.Combine(iconDirectory, "Schoology.ico");
            await File.WriteAllBytesAsync(iconPath, buffer.ToArray());
            AppWindow.SetIcon(iconPath);
            App.WriteStartupLog("Native Schoology window icon applied.");
        }
        catch (Exception ex)
        {
            App.WriteStartupLog("Could not apply the native Schoology window icon.", ex);
        }
    }

    private void ShowSplash()
    {
        _page = "splash";
        // Unpackaged WinUI 3 apps do not receive the MSIX-managed splash surface.
        // Use a compact native WinUI launch surface over the Window's Mica backdrop.
        var grid = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var stack = new StackPanel
        {
            Width = 360,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 18
        };
        var logo = new Image
        {
            Width = 270,
            Height = 74,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(logo);
        var ring = new ProgressRing
        {
            IsActive = new UISettings().AnimationsEnabled,
            Width = 28,
            Height = 28,
            Foreground = ThemeBrush("SchoologyBlueBrush"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(ring);
        var caption = Text("Getting Schoology ready…", 13);
        caption.Foreground = ThemeBrush("SchoologyMutedTextBrush");
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(caption);
        grid.Children.Add(stack);
        Reset(grid);
        _ = SetEmbeddedImageAsync(logo, "Schoology.WinUI.Assets.logo_schoology.png");
    }

    private void ShowLogin()
    {
        _notificationTimer?.Stop();
        _page = "login";
        _sectionFrame = null;
        _ = StopQrScannerAsync();
        var dark = _root.ActualTheme == ElementTheme.Dark;
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        var stack = new StackPanel
        {
            Width = 440,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 14,
            Margin = new Thickness(16, 12, 16, 12)
        };
        var logo = new Image
        {
            Width = 265,
            Height = 72,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20)
        };
        stack.Children.Add(logo);
        stack.Children.Add(LoginActionButton("Sign in through my school", "ic_school.svg", ShowSchoolSignInPage));
        stack.Children.Add(LoginActionButton("Sign in with my account", "ic_sgy_logo.svg", ShowAccountSignInPage));
        stack.Children.Add(LoginActionButton("Sign in with a QR code", "ic_qr_code.svg", ShowQrSignInPage));
        var help = new Button
        {
            Content = "I need help signing in",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = ThemeBrush("SchoologyTextBrush")
        };
        help.Click += (_, _) => ShowSignInHelpPage();
        stack.Children.Add(help);
        // The window remains transparent so Mica is visible. Acrylic is only
        // used on this compact, vertically centered card.
        scroll.Content = stack;
        scroll.MaxHeight = 720;
        var card = new Border
        {
            Width = 520,
            MaxWidth = 520,
            MaxHeight = 760,
            Background = new AcrylicBrush
            {
                TintColor = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 36, 40)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 248, 250, 252),
                TintOpacity = 0.22,
                FallbackColor = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 36, 40)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 248, 250, 252)
            },
            BorderBrush = ThemeBrush("SchoologyDividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0),
            Child = scroll,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var page = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        page.Children.Add(card);
        page.SizeChanged += (_, args) =>
        {
            card.Width = Math.Min(520, Math.Max(220, args.NewSize.Width - 48));
            scroll.MaxHeight = Math.Max(240, args.NewSize.Height - 48);
            // Keep horizontal insets fixed; do not increase them with the window
            // size. The previous wide-window margin calculation squeezed the
            // buttons inside their card and clipped their outer edges.
            stack.Margin = new Thickness(16, args.NewSize.Height < 560 ? 8 : 16, 16, args.NewSize.Height < 560 ? 8 : 16);
            stack.Width = Math.Max(180, card.Width - stack.Margin.Left - stack.Margin.Right);
            logo.Width = Math.Min(265, Math.Max(140, stack.Width - 40));
        };
        Reset(page);
        _ = SetEmbeddedImageAsync(logo, "Schoology.WinUI.Assets.logo_schoology.png");
    }

    private Button LoginActionButton(string label, string iconResource, Action navigate)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var image = new Image
        {
            Width = 26,
            Height = 26,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        AutomationProperties.SetName(image, label + " icon");
        row.Children.Add(image);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        var button = new Button
        {
            Content = row,
            Padding = new Thickness(18, 14, 18, 14),
            MinHeight = 58,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, label);
        button.Click += (_, _) => navigate();
        _ = SetEmbeddedImageAsync(image, $"Schoology.WinUI.Assets.{iconResource}");
        return button;
    }

    private Grid CreateLoginSubpage(string title, string subtitle, out StackPanel content)
    {
        _page = title;
        var dark = _root.ActualTheme == ElementTheme.Dark;
        var grid = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Margin = new Thickness(20, 14, 20, 14), ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var back = new Button { Content = "← Back", Padding = new Thickness(12, 8, 12, 8), VerticalAlignment = VerticalAlignment.Center };
        back.Click += (_, _) => ShowLogin();
        header.Children.Add(back);
        var heading = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(Text(title, 21, true));
        heading.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Brush(dark ? "#FFC5CBD1" : "#FF52616D"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        content = new StackPanel
        {
            Spacing = 14,
            Width = 620,
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0)
        };
        // Only the form card uses Acrylic; the surrounding page shows Mica.
        scroll.Content = new Border
        {
            Background = new AcrylicBrush
            {
                TintColor = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 36, 40)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 248, 250, 252),
                TintOpacity = 0.24,
                FallbackColor = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 36, 40)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 248, 250, 252)
            },
            BorderBrush = ThemeBrush("SchoologyDividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            Child = content,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        // Copy the out parameter to a local before capturing it in the event handler.
        // C# does not allow ref/out parameters to be captured by lambdas.
        var responsiveContent = content;
        grid.SizeChanged += (_, args) => responsiveContent.Width = Math.Min(620, Math.Max(180, args.NewSize.Width - 144));
        Reset(grid);
        return grid;
    }

    private async Task SetEmbeddedImageAsync(Image image, string resourceName)
    {
        var assembly = typeof(MainWindow).Assembly;
        var resolvedName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.Equals(resourceName, StringComparison.Ordinal) ||
                                    name.EndsWith(resourceName, StringComparison.Ordinal));

        if (resolvedName is null)
        {
            App.WriteStartupLog($"Embedded image resource was not found: {resourceName}");
            return;
        }

        await using var source = assembly.GetManifestResourceStream(resolvedName);
        if (source is null)
        {
            App.WriteStartupLog($"Embedded image resource could not be opened: {resolvedName}");
            return;
        }

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer);
        var imageBytes = buffer.ToArray();

        if (resourceName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            // The supplied Schoology school/QR glyphs are white artwork intended
            // for the old blue login background. Recolor their white paths for
            // contrast on native light-mode buttons; retain white in dark mode.
            if (_root.ActualTheme != ElementTheme.Dark)
            {
                var markup = Encoding.UTF8.GetString(imageBytes)
                    .Replace("#ffffff", "#204D7B", StringComparison.OrdinalIgnoreCase);
                imageBytes = Encoding.UTF8.GetBytes(markup);
            }
        }

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var output = randomAccessStream.GetOutputStreamAt(0))
        using (var writer = new DataWriter(output))
        {
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        randomAccessStream.Seek(0);
        if (resourceName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var vector = new SvgImageSource();
            await vector.SetSourceAsync(randomAccessStream);
            image.Source = vector;
        }
        else
        {
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(randomAccessStream);
            image.Source = bitmap;
        }
    }

    private void ShowSchoolSignInPage()
    {
        CreateLoginSubpage("Sign in through my school", "Find your school, then continue through its secure sign-in page.", out var content);
        var search = new AutoSuggestBox
        {
            Header = "School or organization",
            PlaceholderText = "Search by school name or domain",
            UpdateTextOnSelect = false
        };
        var status = Text("Select your school to continue.", 13);
        var continueButton = new Button { Content = "Continue to school sign-in", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = false };
        var browser = new WebView2 { MinHeight = 420, Visibility = Visibility.Collapsed };
        SchoologyAuthService.School? selectedSchool = null;
        search.TextChanged += async (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            selectedSchool = null;
            continueButton.IsEnabled = false;
            if (search.Text.Trim().Length < 2) return;
            try
            {
                var matches = await _auth.SearchSchoolsAsync(search.Text);
                search.ItemsSource = matches;
                search.IsSuggestionListOpen = matches.Count > 0;
                status.Text = matches.Count == 0 ? "No matching schools found. Try another name or domain." : "Choose your school from the suggestions.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        search.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is SchoologyAuthService.School school)
            {
                selectedSchool = school;
                search.Text = school.Name;
                continueButton.IsEnabled = true;
                status.Text = $"Selected {school.Name}. Continue to open its secure sign-in page.";
            }
        };
        continueButton.Click += async (_, _) =>
        {
            if (selectedSchool is null) return;
            continueButton.IsEnabled = false;
            status.Text = "Preparing secure school sign-in…";
            try
            {
                search.Visibility = Visibility.Collapsed;
                continueButton.Visibility = Visibility.Collapsed;
                await OpenRemoteSchoolLoginAsync(selectedSchool, browser, status, search);
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                continueButton.IsEnabled = true;
            }
        };
        content.Children.Add(search);
        content.Children.Add(status);
        content.Children.Add(continueButton);
        content.Children.Add(browser);
    }

    private void ShowAccountSignInPage()
    {
        CreateLoginSubpage("Sign in with my account", "Use your Schoology username or email and password.", out var content);
        var username = new TextBox { Header = "Username or email", PlaceholderText = "Enter your username or email" };
        var password = new PasswordBox { Header = "Password", PlaceholderText = "Enter your password" };
        var status = Text("", 13);
        var browser = new WebView2 { MinHeight = 420, Visibility = Visibility.Collapsed };
        var credentialForm = new StackPanel { Spacing = 14 };
        var signIn = new Button { Content = "Sign in", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        signIn.Click += async (_, _) =>
        {
            var enteredUsername = username.Text.Trim();
            var at = enteredUsername.LastIndexOf('@');
            var emailDomain = at > 0 && at < enteredUsername.Length - 1
                ? enteredUsername[(at + 1)..].Trim().ToLowerInvariant()
                : string.Empty;
            var isLausdDomain = emailDomain == "lausd.net" ||
                emailDomain.EndsWith(".lausd.net", StringComparison.Ordinal) ||
                emailDomain == "lauds.net" ||
                emailDomain.EndsWith(".lauds.net", StringComparison.Ordinal);

            if (isLausdDomain)
            {
                var suggestedUrl = "https://lms.lausd.net";
                var dialog = new ContentDialog
                {
                    Title = "Your school uses SSO/Remote Authentication",
                    Content = "Your school is currently using SSO/Remote Authentication. Did you mean to log in from " + suggestedUrl + "?",
                    PrimaryButtonText = "Continue",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = _root.XamlRoot
                };
                var choice = await dialog.ShowAsync();
                if (choice == ContentDialogResult.Primary)
                {
                    var remoteSchool = new SchoologyAuthService.School(
                        "Los Angeles Unified School District", string.Empty,
                        "remote_auth", suggestedUrl, "lausd.net");
                    await OpenRemoteSchoolLoginAsync(remoteSchool, browser, status, credentialForm);
                }
                return;
            }

            signIn.IsEnabled = false;
            status.Text = "Signing in and verifying your Schoology profile…";
            try
            {
                _session = await _auth.SignInAsync(username.Text, password.Password, null);
                password.Password = string.Empty;
                ShowHome();
                _ = LoadAccountCollectionsAsync();
            }
            catch (Exception ex) { status.Text = ex.Message; signIn.IsEnabled = true; }
        };
        credentialForm.Children.Add(username);
        credentialForm.Children.Add(password);
        credentialForm.Children.Add(signIn);
        content.Children.Add(credentialForm);
        content.Children.Add(status);
        content.Children.Add(browser);
    }

    private async Task OpenRemoteSchoolLoginAsync(
        SchoologyAuthService.School school,
        WebView2 browser,
        TextBlock status,
        UIElement credentialForm)
    {
        status.Text = "Preparing secure school sign-in…";
        try
        {
            var request = await _auth.BeginSchoolBrowserLoginAsync(school);
            await browser.EnsureCoreWebView2Async();
            browser.CoreWebView2.NavigationStarting += async (_, navigation) =>
            {
                if (!Uri.TryCreate(navigation.Uri, UriKind.Absolute, out var callback) ||
                    callback.Scheme != "schoology" ||
                    !callback.Host.Equals("external_login_callback", StringComparison.OrdinalIgnoreCase))
                    return;

                navigation.Cancel = true;
                var requestToken = HttpUtility.ParseQueryString(callback.Query).Get("request_token");
                if (string.IsNullOrWhiteSpace(requestToken))
                {
                    status.Text = "Schoology returned an invalid sign-in callback. Please try again.";
                    return;
                }

                try
                {
                    status.Text = "Verifying your Schoology account…";
                    _session = await _auth.CompleteSchoolBrowserSignInAsync(requestToken);
                    ShowHome();
                    await LoadAccountCollectionsAsync();
                }
                catch (Exception ex)
                {
                    status.Text = ex.Message;
                    credentialForm.Visibility = Visibility.Visible;
                    browser.Visibility = Visibility.Collapsed;
                }
            };

            credentialForm.Visibility = Visibility.Collapsed;
            browser.Visibility = Visibility.Visible;
            status.Text = "Complete sign-in with your school. Your password is entered only on the school sign-in page.";
            browser.Source = new Uri(request.AuthorizationUrl);
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
            credentialForm.Visibility = Visibility.Visible;
            browser.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowQrSignInPage()
    {
        CreateLoginSubpage("Sign in with a QR code", "Allow camera access and hold your Schoology QR code in front of the camera.", out var content);
        var preview = new MediaPlayerElement
        {
            MinHeight = 320,
            AreTransportControlsEnabled = false,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill
        };
        var status = Text("Camera has not started.", 13);
        var start = new Button { Content = "Start camera", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        start.Click += async (_, _) =>
        {
            start.IsEnabled = false;
            status.Text = "Requesting camera access…";
            try
            {
                await StopQrScannerAsync();
                _qrScanner = new QrCameraScanner();
                _qrScanner.QrCodeDetected += (_, qr) =>
                {
                    DispatcherQueue.TryEnqueue(() => _ = CompleteQrSignInAsync(qr, status, start));
                };
                _qrPlayer = await _qrScanner.StartAsync();
                preview.SetMediaPlayer(_qrPlayer);
                _qrPlayer.Play();
                status.Text = "Camera is active. Hold the QR code steady in the frame.";
            }
            catch (Exception ex)
            {
                status.Text = $"Could not start the camera: {ex.Message}";
                start.IsEnabled = true;
            }
        };
        content.Children.Add(preview);
        content.Children.Add(status);
        content.Children.Add(start);
    }

    private async Task CompleteQrSignInAsync(string qr, TextBlock status, Button start)
    {
        start.IsEnabled = false;
        status.Text = "QR code detected. Verifying your Schoology sign-in…";
        try
        {
            await StopQrScannerAsync();
            _session = await _auth.SignInWithQrAsync(qr);
            ShowHome();
            _ = LoadAccountCollectionsAsync();
            _ = LoadAccountCollectionsAsync();
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
            start.IsEnabled = true;
        }
    }

    private async Task StopQrScannerAsync()
    {
        if (_qrPlayer is not null)
        {
            try { _qrPlayer.Pause(); _qrPlayer.Dispose(); } catch { }
            _qrPlayer = null;
        }
        if (_qrScanner is not null)
        {
            try { await _qrScanner.DisposeAsync(); } catch { }
            _qrScanner = null;
        }
    }

    private async void ShowSignInHelpPage()
    {
        // Match classic port.28: this action opens the official Schoology
        // student mobile sign-in help article in the system browser.
        var helpUri = new Uri("https://docs.powerschool.com/SGYH/en/students/schoology-mobile-apps/logging-into-the-schoology-mobile-app-students");
        try
        {
            var launched = await Launcher.LaunchUriAsync(helpUri);
            if (!launched)
            {
                var dialog = new ContentDialog
                {
                    Title = "Unable to open sign-in help",
                    Content = "Windows could not open the Schoology sign-in help page.",
                    CloseButtonText = "OK",
                    XamlRoot = _root.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Unable to open sign-in help",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = _root.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private async Task RestoreSavedSessionAsync()
    {
        App.WriteStartupLog("RestoreSavedSessionAsync entered.");
        try
        {
            App.WriteStartupLog("Requesting saved Schoology session from Windows Credential Locker.");
            var restored = await _auth.RestoreSessionAsync();
            App.WriteStartupLog(restored is null
                ? "No usable saved Schoology session was restored; remaining on login."
                : $"Saved Schoology session restored for user ID {restored.UserId}.");

            if (restored is null || _page != "login")
            {
                App.WriteStartupLog($"Skipping homepage transition; restored={restored is not null}, current page={_page}.");
                return;
            }

            _session = restored;
            App.WriteStartupLog("Building logged-in homepage from restored session.");
            ShowHome();
            App.WriteStartupLog("Logged-in homepage visual tree created.");
            await LoadAccountCollectionsAsync();
            App.WriteStartupLog("Initial account collections request finished.");
        }
        catch (Exception ex)
        {
            App.WriteStartupLog("Saved Schoology session restoration or homepage initialization failed.", ex);
            _session = null;
            try
            {
                ShowLogin();
                App.WriteStartupLog("Returned to login after saved-session startup failure.");
            }
            catch (Exception fallbackException)
            {
                App.WriteStartupLog("Failed to return to login after homepage initialization failure.", fallbackException);
            }
        }
    }

    private async Task CheckAndShowReleaseNotesAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/tling0001/schoology-winui/releases/latest");
            request.Headers.TryAddWithoutValidation("User-Agent", "Schoology-WinUI");
            using var response = await ReleaseNotesClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;

            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = release.RootElement;
            var tag = ProfileValue(root, "tag_name");
            var body = ProfileValue(root, "body");
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(body)) return;

            var previouslyShown = File.Exists(ReleaseNotesStatePath)
                ? await File.ReadAllTextAsync(ReleaseNotesStatePath) : string.Empty;
            if (string.Equals(previouslyShown.Trim(), tag, StringComparison.Ordinal)) return;

            var notes = new StackPanel { Spacing = 8 };
            foreach (var line in body.Replace("\r", string.Empty).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("## [", StringComparison.Ordinal)) continue;
                TextBlock paragraph;
                if (line.StartsWith("### ", StringComparison.Ordinal))
                    paragraph = Text(line[4..].TrimEnd(':'), 16, true);
                else if (line.StartsWith("- ", StringComparison.Ordinal))
                    paragraph = Text("• " + line[2..], 13);
                else
                    paragraph = Text(line, 13);
                paragraph.Foreground = ThemeBrush("SchoologyTextBrush");
                notes.Children.Add(paragraph);
            }

            var dialog = new ContentDialog
            {
                Title = $"What’s new in Schoology {tag}",
                Content = new ScrollViewer
                {
                    Content = notes,
                    MaxHeight = 520,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                },
                CloseButtonText = "Got it",
                XamlRoot = _root.XamlRoot
            };
            await dialog.ShowAsync();

            Directory.CreateDirectory(Path.GetDirectoryName(ReleaseNotesStatePath)!);
            await File.WriteAllTextAsync(ReleaseNotesStatePath, tag);
        }
        catch (Exception ex)
        {
            // Release notes are best-effort: offline startup must remain unaffected.
            App.WriteStartupLog("Could not retrieve or display the latest Schoology release notes.", ex);
        }
    }

    private async void OpenDistrictSignInAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "District single sign-on",
            Content = "District SSO requires a secure embedded authorization window and Schoology session-cookie exchange. That flow is not enabled in this phase; no district password is collected or saved.",
            CloseButtonText = "OK",
            XamlRoot = _root.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private UIElement BuildApplicationShell(string section, UIElement content)
    {
        var shell = new Grid { Background = ThemeBrush("SchoologySubtleBrush") };
        // NavigationView fills the window so its acrylic pane continues behind the custom title bar.
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var nativeTitleBar = AppWindow.TitleBar;
        // Keep the custom title bar at the same fixed height as the native
        // Tall caption-button area. Without an explicit Height, this Grid
        // stretches over the entire shell and vertically centers its controls
        // in the window instead of in the title bar.
        var titleBar = new Grid
        {
            Height = 48,
            MinHeight = 48,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(16, 0, Math.Max(12, nativeTitleBar.RightInset + 12), 0)
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Drawer toggle
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Schoology brand
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Drag region / page title
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Commands
        var brand = new Image { Width = 112, Height = 30, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(brand, 1);
        titleBar.Children.Add(brand);
        _ = SetEmbeddedImageAsync(brand, "Schoology.WinUI.Assets.logo_schoology.png");
        var caption = Text(section == "Recent Activity" ? "Home" : section, 13, true);
        caption.Foreground = ThemeBrush("SchoologyMutedTextBrush");
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(caption, 2);
        titleBar.Children.Add(caption);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var searchButton = Action("Search", (_, _) => ShowSearch());
        AutomationProperties.SetAutomationId(searchButton, "TitleBarSearchButton");
        var createButton = Action("＋ Create", (_, _) => ShowCreateMenu(), true);
        AutomationProperties.SetAutomationId(createButton, "TitleBarCreateButton");
        commands.Children.Add(searchButton);
        commands.Children.Add(createButton);
        Grid.SetColumn(commands, 3);
        titleBar.Children.Add(commands);
        // AppWindowTitleBar exposes inset values, not a Changed event.
        titleBar.Padding = new Thickness(16, 0, Math.Max(12, nativeTitleBar.RightInset + 12), 0);

        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.LeftMinimal,
            IsPaneOpen = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false,
            OpenPaneLength = 304,
            CompactPaneLength = 64,
            Foreground = ThemeBrush("SchoologyTextBrush"),
            CompactModeThresholdWidth = 760,
            ExpandedModeThresholdWidth = 1120,
            Background = new SolidColorBrush(Colors.Transparent),
            Content = null
        };
        // Give the pane a legible in-app acrylic surface. Transparent pane brushes
        // expose the page beneath and make navigation labels difficult to read.
        var darkPane = _root.ActualTheme == ElementTheme.Dark;
        var paneMaterial = new AcrylicBrush
        {
            TintColor = darkPane
                ? Microsoft.UI.ColorHelper.FromArgb(255, 28, 34, 40)
                : Microsoft.UI.ColorHelper.FromArgb(255, 238, 244, 249),
            TintOpacity = darkPane ? 0.86 : 0.82,
            FallbackColor = darkPane
                ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 38, 44)
                : Microsoft.UI.ColorHelper.FromArgb(255, 245, 248, 251)
        };
        nav.PaneHeader = new Border
        {
            Height = 48,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        nav.Resources["NavigationViewDefaultPaneBackground"] = paneMaterial;
        nav.Resources["NavigationViewExpandedPaneBackground"] = paneMaterial;
        nav.Resources["NavigationViewCompactPaneBackground"] = paneMaterial;
        nav.Resources["NavigationViewItemForeground"] = ThemeBrush("SchoologyTextBrush");
        nav.Resources["NavigationViewItemForegroundPointerOver"] = ThemeBrush("SchoologyTextBrush");
        nav.Resources["NavigationViewItemForegroundSelected"] = ThemeBrush("SchoologyTextBrush");
        var drawerToggle = new Button
        {
            Content = new FontIcon { Glyph = "\uE700", FontSize = 17 },
            Width = 40, Height = 40, Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = ThemeBrush("SchoologyTextBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(drawerToggle, "Toggle navigation pane");
        AutomationProperties.SetAutomationId(drawerToggle, "TitleBarNavigationToggle");
        drawerToggle.Click += (_, _) => nav.IsPaneOpen = !nav.IsPaneOpen;
        Grid.SetColumn(drawerToggle, 0);
        titleBar.Children.Insert(0, drawerToggle);

        var animationsEnabled = AnimationsEnabled;
        var initialTransition = new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight };
        var sectionFrame = new Frame
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Margin = new Thickness(0, 48, 0, 0),
            ContentTransitions = animationsEnabled
                ? new TransitionCollection { new NavigationThemeTransition { DefaultNavigationTransitionInfo = initialTransition } }
                : null
        };
        App.WriteStartupLog($"Creating compiled shell page for initial section '{section}'.");
        sectionFrame.Navigate(typeof(SchoologyShellContentPage), content,
            animationsEnabled ? initialTransition : null);
        App.WriteStartupLog($"Initial Frame.Navigate completed for section '{section}'.");
        _sectionFrame = sectionFrame;
        nav.Content = sectionFrame;
        var profileRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 12, 8, 12) };
        profileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        profileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var profileAvatar = new Image { Width = 40, Height = 40, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
        var profileName = _session is null ? "Profile" :
            ProfileValue(_session.User, "name_display", "display_name", "name") ?? "My Profile";
        var profilePicture = _session is null ? null :
            ProfileValue(_session.User, "picture_url", "pictureUrl", "picture", "photo_url");
        if (!string.IsNullOrWhiteSpace(profilePicture))
            try { profileAvatar.Source = new BitmapImage(new Uri(NormalizeSchoologyImageUrl(profilePicture))); } catch { }
        profileRow.Children.Add(new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
            Background = Brush("#FF526F8B"), Child = profileAvatar });
        var profileLabels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        profileLabels.Children.Add(Text(profileName, 14, true));
        profileLabels.Children.Add(Text("Schoology account", 11));
        Grid.SetColumn(profileLabels, 1); profileRow.Children.Add(profileLabels);
        nav.MenuItems.Add(new NavigationViewItem { Content = profileRow, Tag = "Profile" });
        nav.MenuItems.Add(new NavigationViewItemSeparator());

        var items = new (string Name, Symbol Icon)[]
        {
            ("Messages", Symbol.Mail), ("Notifications", Symbol.Mail), ("Requests", Symbol.People),
            ("Home", Symbol.Home), ("Courses", Symbol.Library), ("Groups", Symbol.People),
            ("Resources", Symbol.Folder), ("Grades", Symbol.Page), ("Upcoming", Symbol.Calendar),
            ("School Profile", Symbol.Home), ("Settings", Symbol.Setting)
        };
        foreach (var entry in items)
        {
            if (entry.Name == "Home")
                nav.MenuItems.Add(new NavigationViewItemSeparator());
            var item = new NavigationViewItem { Content = entry.Name == "Upcoming" ? "Calendar" : entry.Name,
                Tag = entry.Name == "Upcoming" ? "Calendar" : entry.Name,
                Icon = entry.Name == "Notifications" ? new FontIcon { Glyph = "\uE7F4", FontSize = 18 } : new SymbolIcon(entry.Icon) };
            // Parent rows only expand/collapse. Selecting a specific child performs navigation.
            item.SelectsOnInvoked = entry.Name is not ("Courses" or "Groups");
            if (entry.Name is "Courses" or "Groups") item.IsExpanded = false;
            nav.MenuItems.Add(item);
            if (entry.Name == (section == "Recent Activity" ? "Home" : section) ||
                (entry.Name == "Upcoming" && section == "Calendar")) nav.SelectedItem = item;
        }
        nav.FooterMenuItems.Add(new NavigationViewItem
        {
            Content = _session is null ? "Profile and settings" : "Sign out",
            Tag = "Sign out",
            Icon = new SymbolIcon(Symbol.Contact)
        });
        nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is not NavigationViewItem item) return;
            if (item.Tag is string target && target.Contains(':'))
            {
                var parts = target.Split(':', 2);
                if (long.TryParse(parts[1], out var id))
                {
                    var kind = parts[0] == "group" ? "Groups" : "Courses";
                    var entity = (kind == "Groups" ? _groups : _courses).FirstOrDefault(candidate => candidate.Id == id);
                    if (entity is not null) ShowThreePane(kind, entity);
                }
                return;
            }
            if (item.Tag is not string rootTarget) return;
            if (rootTarget == "Sign out") { _auth.SignOut(); _session = null; _sharedWebViewInitialized = false; _sharedWebViewSessionUserId = 0; _sectionFrame = null; ShowLogin(); }
            else ShowHome(rootTarget == "Home" ? "Recent Activity" : rootTarget);
        };
        _appNavigation = nav;
        Grid.SetRow(nav, 0);
        shell.Children.Add(nav);
        // Draw the custom title bar above NavigationView while retaining a 48px
        // header reservation in both the pane and content area.
        shell.Children.Add(titleBar);
        SetTitleBar(titleBar);
        return shell;
    }

    private void ShowHome(string section = "Recent Activity")
    {
        _ = StopQrScannerAsync();
        _page = "home";
        _section = section;
        if (_session is not null) EnsureBackgroundNotificationPolling();
        if (section is "Courses" or "Groups" or "School Profile") { ShowThreePane(section); return; }
        UIElement page;
        if (section is "Upcoming" or "Recent Activity" or "Home")
            page = BuildHomeSplitView(section == "Upcoming");
        else if (section == "Calendar")
            page = BuildCalendarPage();
        else if (section == "Settings")
            page = BuildSettingsPage();
        else if (section == "Messages")
            page = BuildMessagesPage();
        else if (section == "Grades")
            page = BuildGradesPage();
        else if (section == "Profile")
            page = BuildProfilePage();
        else if (section == "Notifications")
            page = BuildNotificationsPage();
        else if (section == "Requests")
            page = BuildRequestsPage();
        else if (section == "Resources")
            page = BuildResourcesPage();
        else
        {
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
            panel.Children.Add(Text(section, 24, true));
            panel.Children.Add(EmptyState($"No {section.ToLowerInvariant()} available."));
            page = panel;
        }
        if (_sectionFrame is not null)
            NavigateMainSection(page, section);
        else
            Reset(BuildApplicationShell(section, page));
    }

    private void NavigateMainSection(UIElement page, string section)
    {
        if (_sectionFrame is null) return;
        var effect = section is "Home" or "Recent Activity" ? SlideNavigationTransitionEffect.FromLeft : SlideNavigationTransitionEffect.FromRight;
        var info = new SlideNavigationTransitionInfo { Effect = effect };
        var animationsEnabled = AnimationsEnabled;
        _sectionFrame.ContentTransitions = animationsEnabled
            ? new TransitionCollection { new NavigationThemeTransition { DefaultNavigationTransitionInfo = info } }
            : null;
        _sectionFrame.Navigate(typeof(SchoologyShellContentPage), page, animationsEnabled ? info : null);
    }

    private async Task LoadAccountCollectionsAsync()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var results = await Task.WhenAll(_api.GetCoursesAsync(session), _api.GetGroupsAsync(session));
            _courses.Clear();
            foreach (var item in results[0]) _courses.Add(item);
            _groups.Clear();
            foreach (var item in results[1]) _groups.Add(item);
            PopulateNavigationHierarchy();
            App.WriteStartupLog($"Loaded Schoology account collections: {_courses.Count} courses, {_groups.Count} groups.");
        }
        catch (Exception ex)
        {
            App.WriteStartupLog("Could not load Schoology account collections.", ex);
        }
    }


    private void PopulateNavigationHierarchy()
    {
        if (_appNavigation is null) return;
        foreach (var parent in _appNavigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (parent.Tag is not string tag || tag is not ("Courses" or "Groups")) continue;
            parent.MenuItems.Clear();
            parent.SelectsOnInvoked = false;
            var entities = tag == "Groups" ? _groups : _courses;
            foreach (var entity in entities)
            {
                parent.MenuItems.Add(new NavigationViewItem
                {
                    Content = entity.Name,
                    Tag = (tag == "Groups" ? "group:" : "course:") + entity.Id,
                    Icon = new SymbolIcon(tag == "Groups" ? Symbol.People : Symbol.Page),
                    SelectsOnInvoked = true
                });
            }
        }
    }

    private UIElement BuildHomeSplitView(bool upcomingFirst)
    {
        // Landscape mirrors the classic port's two-pane home proportions.
        // Portrait changes the task model to a single content surface with
        // Upcoming promoted into the SelectorBar instead of an overlay pane.
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var wide = new Grid { ColumnSpacing = 0 };
        wide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        wide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var wideLeft = new Grid { Margin = new Thickness(20, 14, 12, 16), RowSpacing = 12 };
        wideLeft.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        wideLeft.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        wideLeft.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        wideLeft.Children.Add(Text("Home", 23, true));
        var wideSelector = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        var wideRecent = new SelectorBarItem { Text = "Recent Activity" };
        var wideDashboard = new SelectorBarItem { Text = "Course Dashboard" };
        wideSelector.Items.Add(wideRecent);
        wideSelector.Items.Add(wideDashboard);
        Grid.SetRow(wideSelector, 1); wideLeft.Children.Add(wideSelector);
        var wideFrame = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetRow(wideFrame, 2); wideLeft.Children.Add(wideFrame);
        wideSelector.SelectionChanged += (_, _) =>
        {
            if (wideSelector.SelectedItem is not SelectorBarItem item) return;
            NavigateShellFrame(wideFrame,
                item == wideDashboard ? BuildCourseDashboardContent() : BuildRecentActivityContent(),
                SlideNavigationTransitionEffect.FromRight);
        };
        wideSelector.SelectedItem = upcomingFirst ? wideDashboard : wideRecent;
        NavigateShellFrame(wideFrame, upcomingFirst ? BuildCourseDashboardContent() : BuildRecentActivityContent(),
            SlideNavigationTransitionEffect.FromRight);
        Grid.SetColumn(wideLeft, 0); wide.Children.Add(wideLeft);

        var wideUpcoming = new Grid { Margin = new Thickness(14, 14, 20, 16), RowSpacing = 10 };
        wideUpcoming.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        wideUpcoming.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        wideUpcoming.Children.Add(Text("Upcoming", 23, true));
        var wideUpcomingFrame = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetRow(wideUpcomingFrame, 1); wideUpcoming.Children.Add(wideUpcomingFrame);
        NavigateShellFrame(wideUpcomingFrame, BuildUpcomingContent(), SlideNavigationTransitionEffect.FromRight);
        Grid.SetColumn(wideUpcoming, 1); wide.Children.Add(wideUpcoming);

        var narrow = new Grid { Margin = new Thickness(16, 12, 16, 16), RowSpacing = 10, Visibility = Visibility.Collapsed };
        narrow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        narrow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        narrow.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        narrow.Children.Add(Text("Home", 23, true));
        var narrowSelector = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Stretch };
        var narrowRecent = new SelectorBarItem { Text = "Recent Activity" };
        var narrowDashboard = new SelectorBarItem { Text = "Course Dashboard" };
        var narrowUpcoming = new SelectorBarItem { Text = "Upcoming" };
        narrowSelector.Items.Add(narrowRecent);
        narrowSelector.Items.Add(narrowDashboard);
        narrowSelector.Items.Add(narrowUpcoming);
        Grid.SetRow(narrowSelector, 1); narrow.Children.Add(narrowSelector);
        var narrowFrame = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetRow(narrowFrame, 2); narrow.Children.Add(narrowFrame);
        void ShowNarrowTab(SelectorBarItem item)
        {
            var content = item == narrowDashboard ? BuildCourseDashboardContent() :
                item == narrowUpcoming ? BuildUpcomingContent() : BuildRecentActivityContent();
            NavigateShellFrame(narrowFrame, content, SlideNavigationTransitionEffect.FromRight);
        }
        narrowSelector.SelectionChanged += (_, _) =>
        {
            if (narrowSelector.SelectedItem is SelectorBarItem item) ShowNarrowTab(item);
        };
        narrowSelector.SelectedItem = upcomingFirst ? narrowUpcoming : narrowRecent;
        ShowNarrowTab(upcomingFirst ? narrowUpcoming : narrowRecent);

        root.Children.Add(wide);
        root.Children.Add(narrow);
        root.SizeChanged += (_, args) =>
        {
            var useNarrow = args.NewSize.Width < 800;
            wide.Visibility = useNarrow ? Visibility.Collapsed : Visibility.Visible;
            narrow.Visibility = useNarrow ? Visibility.Visible : Visibility.Collapsed;
        };
        return root;
    }

    private UIElement BuildRecentActivityContent()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(4, 2, 10, 8) };
        var status = Text("Loading recent Schoology activity…", 13);
        panel.Children.Add(status);
        _ = LoadRecentActivityAsync(panel, status);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task ResolvePostAuthorsAsync(IReadOnlyList<SchoologyApiRecord> records, SchoologyAuthService.Session session)
    {
        var ids = records.Select(record => JsonLong(record.Data, "user_id", "uid", "author_id", "authorId", "created_by_id"))
            .Where(id => id > 0).Distinct().Where(id => !_userProfileCache.ContainsKey(id)).ToArray();
        await Task.WhenAll(ids.Select(async id =>
        {
            try
            {
                using var profile = await _api.GetUserAsync(session, id);
                var user = profile.RootElement;
                if (user.ValueKind == JsonValueKind.Object && user.TryGetProperty("user", out var nested)) user = nested;
                _userProfileCache[id] = user.Clone();
            }
            catch (Exception ex) { App.WriteStartupLog($"Could not resolve Schoology post author {id}.", ex); }
        }));
    }

    private async Task LoadRecentActivityAsync(StackPanel panel, TextBlock status)
    {
        try
        {
            var session = _session ?? throw new InvalidOperationException("Sign in to view recent activity.");
            using var document = await _auth.GetApiJsonAsync(session, "recent",
                new Dictionary<string, string> { ["start"] = "0", ["limit"] = "30", ["with_attachments"] = "TRUE", ["richtext"] = "1" });
            var records = SchoologyApiClient.ReadRecords(document.RootElement, "update", "updates", "update_list");
            await ResolvePostAuthorsAsync(records, session);
            panel.Children.Remove(status);
            if (records.Count == 0) { panel.Children.Add(EmptyState("No recent activity", "Your recent Schoology activity will appear here.")); return; }
            foreach (var record in records)
            {
                var id = JsonLong(record.Data, "user_id", "uid", "author_id", "authorId", "created_by_id");
                JsonElement? profile = id > 0 && _userProfileCache.TryGetValue(id, out var cached) ? cached : null;
                panel.Children.Add(BuildUpdateCard("Recent", 0, record, profile));
            }
        }
        catch (Exception ex)
        {
            status.Text = $"Unable to load recent activity: {ex.Message}";
            panel.Children.Add(Action("Retry", async (_, _) => await LoadRecentActivityAsync(panel, status)));
        }
    }

    private UIElement BuildCourseDashboardContent()
    {
        // Tauri uses responsive course cards with a wide image, title and period.
        // GridView keeps the same wrapping layout and virtualizes large course lists.
        var grid = new GridView
        {
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12),
            Background = new SolidColorBrush(Colors.Transparent)
        };
        AutomationProperties.SetAutomationId(grid, "HomeCourseDashboardGrid");
        if (_courses.Count == 0)
        {
            grid.Items.Add(new GridViewItem { Content = EmptyState("No courses were returned for this account.") });
            return grid;
        }
        foreach (var course in _courses)
        {
            var cardContent = new StackPanel { Spacing = 0 };
            var imageSurface = new Border { Height = 130, Background = ThemeBrush("SchoologyBlueDarkBrush") };
            imageSurface.Child = new TextBlock
            {
                Text = course.Name.Length > 0 ? course.Name[..1].ToUpperInvariant() : "C",
                FontSize = 42, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush("#FFFFFFFF"), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (!string.IsNullOrWhiteSpace(course.ImageUrl))
            {
                try { imageSurface.Child = new Image { Source = new BitmapImage(new Uri(NormalizeSchoologyImageUrl(course.ImageUrl))),
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill, Height = 130 }; } catch { }
            }
            cardContent.Children.Add(imageSurface);
            var labels = new StackPanel { Spacing = 4, Padding = new Thickness(12, 10, 12, 12),
                Background = ThemeBrush("SchoologySurfaceBrush") };
            labels.Children.Add(Text(course.Name, 15, true));
            if (!string.IsNullOrWhiteSpace(course.Subtitle))
            {
                var subtitle = Text(course.Subtitle, 12);
                subtitle.Foreground = ThemeBrush("SchoologyMutedTextBrush");
                labels.Children.Add(subtitle);
            }
            cardContent.Children.Add(labels);
            var card = new Border { Width = 250, Height = 205, CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1), BorderBrush = ThemeBrush("SchoologyDividerBrush"),
                Background = ThemeBrush("SchoologySurfaceBrush"), Child = cardContent };
            var item = new GridViewItem { Content = card, Width = 250, Height = 205,
                Padding = new Thickness(0), Margin = new Thickness(8), Tag = course };
            AutomationProperties.SetName(item, course.Name);
            item.Tapped += (_, _) => ShowThreePane("Courses", course);
            grid.Items.Add(item);
        }
        return grid;
    }

    private void EnsureBackgroundNotificationPolling()
    {
        if (_session is null || !string.Equals(ReadAppSetting("DesktopNotifications", "true"), "true", StringComparison.OrdinalIgnoreCase)) return;
        if (_notificationTimer is null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _notificationTimer = timer;
            timer.Tick += async (_, _) =>
            {
                if (_notificationPollInProgress || _session is null) return;
                timer.Stop();
                try { await PollSchoologyNotificationsAsync(); }
                finally
                {
                    timer.Interval = TimeSpan.FromMinutes(5);
                    if (string.Equals(ReadAppSetting("DesktopNotifications", "true"), "true", StringComparison.OrdinalIgnoreCase))
                        timer.Start();
                }
            };
        }
        if (!_notificationTimer.IsEnabled) _notificationTimer.Start();
    }

    private async Task PollSchoologyNotificationsAsync()
    {
        if (_session is null || _notificationPollInProgress ||
            !string.Equals(ReadAppSetting("DesktopNotifications", "true"), "true", StringComparison.OrdinalIgnoreCase)) return;
        _notificationPollInProgress = true;
        try
        {
            using var document = await _api.GetNotificationsAsync(_session);
            var root = document.RootElement;
            JsonElement raw = default;
            if (root.TryGetProperty("notification", out var notification)) raw = notification;
            else if (root.TryGetProperty("notifications", out var notifications)) raw = notifications;
            else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("notification", out notification)) raw = notification;
                else if (data.TryGetProperty("notifications", out notifications)) raw = notifications;
            }
            if (raw.ValueKind == JsonValueKind.Object)
            {
                if (raw.TryGetProperty("notification", out var nested)) raw = nested;
                else if (raw.TryGetProperty("notifications", out nested)) raw = nested;
                else if (raw.TryGetProperty("items", out nested)) raw = nested;
            }
            var items = raw.ValueKind == JsonValueKind.Array
                ? raw.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => item.Clone()).ToArray()
                : Array.Empty<JsonElement>();
            var userId = _session.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var keys = items.Select(NotificationKey).ToArray();
            string previousUser = string.Empty;
            var previousIds = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(NotificationSeenStatePath))
                {
                    using var previous = JsonDocument.Parse(File.ReadAllText(NotificationSeenStatePath));
                    previousUser = ProfileValue(previous.RootElement, "userId") ?? string.Empty;
                    if (previous.RootElement.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                        foreach (var id in ids.EnumerateArray()) if (id.ValueKind == JsonValueKind.String) previousIds.Add(id.GetString() ?? string.Empty);
                }
            }
            catch { }
            if (previousUser == userId)
                foreach (var item in items.Where(item => !previousIds.Contains(NotificationKey(item))).Take(3).Reverse())
                    ShowSchoologyAppNotification(item);
            Directory.CreateDirectory(Path.GetDirectoryName(NotificationSeenStatePath)!);
            File.WriteAllText(NotificationSeenStatePath, JsonSerializer.Serialize(new { userId, ids = keys.Take(200).ToArray() }));
        }
        catch (Exception ex)
        {
            if (!ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase) &&
                !ex.Message.Contains("Not signed in", StringComparison.OrdinalIgnoreCase))
                App.WriteStartupLog("Background Schoology notification polling failed.", ex);
        }
        finally { _notificationPollInProgress = false; }
    }

    private static string NotificationKey(JsonElement item)
    {
        var id = ProfileValue(item, "id", "notification_id", "notificationId");
        if (!string.IsNullOrWhiteSpace(id)) return id;
        return string.Join(":", ProfileValue(item, "type") ?? string.Empty,
            ProfileValue(item, "created", "timestamp") ?? string.Empty,
            ProfileValue(item, "body", "message", "title") ?? string.Empty);
    }

    private static string NotificationBody(JsonElement item)
    {
        var body = ProfileValue(item, "body", "message", "title") ?? "You have a new Schoology notification";
        JsonElement args = default;
        if (item.TryGetProperty("body_args", out args) || item.TryGetProperty("bodyArgs", out args) || item.TryGetProperty("body_arg", out args))
        {
            if (args.ValueKind == JsonValueKind.Array)
                foreach (var argument in args.EnumerateArray())
                {
                    var replacement = ProfileValue(argument, "title", "name") ?? string.Empty;
                    var index = body.IndexOf("%s", StringComparison.Ordinal);
                    if (index >= 0) body = body[..index] + replacement + body[(index + 2)..];
                }
        }
        var clean = string.Join(" ", Regex.Replace(body, "<[^>]*>", " ")
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > 220 ? clean[..220] : clean;
    }

    private static void ShowSchoologyAppNotification(JsonElement item)
    {
        try
        {
            var body = NotificationBody(item);
            var builder = new AppNotificationBuilder().AddText("Schoology").AddText(body);
            if (!string.Equals(ReadAppSetting("NotificationSound", "false"), "true", StringComparison.OrdinalIgnoreCase))
                builder.MuteAudio();
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex) { App.WriteStartupLog("Could not display a Windows app notification.", ex); }
    }

    private UIElement BuildUpcomingContent()
    {
        var root = new Grid { RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var status = Text("Loading upcoming assignments and events…", 13);
        status.Foreground = ThemeBrush("SchoologyMutedTextBrush");
        root.Children.Add(status);
        var list = new ListView { SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent) };
        AutomationProperties.SetAutomationId(list, "UpcomingEventsList");
        Grid.SetRow(list, 1); root.Children.Add(list);
        _ = LoadUpcomingAsync(list, status);
        return root;
    }

    private async Task<IReadOnlyList<SchoologyApiRecord>> FetchUpcomingRecordsAsync()
    {
        if (_session is null) throw new InvalidOperationException("Sign in to view upcoming items.");
        using var document = await _auth.GetApiJsonAsync(_session, $"users/{_session.UserId}/events",
            new Dictionary<string, string> { ["start_date"] = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["start"] = "0", ["limit"] = "100" });
        return SchoologyApiClient.ReadRecords(document.RootElement, "event", "events");
    }

    private static DateTime? ParseSchoologyDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (long.TryParse(value, out var epoch))
        {
            try { return epoch > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).LocalDateTime :
                DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime; } catch { }
        }
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
    }

    private void RenderGroupedUpcoming(ListView list, IReadOnlyList<SchoologyApiRecord> records, DateTime? selectedDate = null)
    {
        list.Items.Clear();
        var ordered = records.Select(record => new { Record = record,
            Date = ParseSchoologyDate(ProfileValue(record.Data, "start", "start_time", "date", "due", "due_date")) })
            .Where(x => selectedDate is null || x.Date?.Date == selectedDate.Value.Date)
            .OrderBy(x => x.Date ?? DateTime.MaxValue).ToArray();
        foreach (var group in ordered.GroupBy(x => x.Date?.Date ?? DateTime.MinValue))
        {
            var title = group.Key == DateTime.MinValue ? "Date not specified" :
                group.Key.ToString("dddd, MMMM d", System.Globalization.CultureInfo.CurrentCulture);
            list.Items.Add(new ListViewItem { Content = Text(title, 13, true),
                IsHitTestVisible = false, IsTabStop = false, Padding = new Thickness(12, 10, 12, 6) });
            foreach (var entry in group)
            {
                var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 9, 12, 9) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new FontIcon { Glyph = "\\uE787", FontSize = 19,
                    Foreground = ThemeBrush("SchoologyBlueBrush"), VerticalAlignment = VerticalAlignment.Center });
                var details = new StackPanel { Spacing = 3 };
                details.Children.Add(Text(entry.Record.Title, 14, true));
                var course = ProfileValue(entry.Record.Data, "section_title", "course_title", "section_name");
                if (!string.IsNullOrWhiteSpace(course)) details.Children.Add(Text(course, 12));
                var time = entry.Date?.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
                if (!string.IsNullOrWhiteSpace(time)) details.Children.Add(Text(time, 11));
                Grid.SetColumn(details, 1); row.Children.Add(details);
                var item = new ListViewItem { Content = row, Tag = entry.Record,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                item.Tapped += async (_, _) => await ShowUpcomingItemAsync(entry.Record);
                list.Items.Add(item);
            }
        }
        if (ordered.Length == 0)
            list.Items.Add(new ListViewItem { Content = Surface(Text("Nothing is scheduled for this date.")) });
    }

    private async Task ShowUpcomingItemAsync(SchoologyApiRecord record)
    {
        var type = (ProfileValue(record.Data, "type", "event_type", "eventType") ?? "").ToLowerInvariant();
        var itemId = JsonLong(record.Data, "assignment_id", "assignmentId", "id");
        var sectionId = JsonLong(record.Data, "section_id", "sectionId");
        if (type == "assignment" && itemId > 0 && sectionId > 0 && _session is not null)
        {
            using var assignmentDocument = await _api.GetAssignmentAsync(_session, sectionId, itemId);
            var assignment = assignmentDocument.RootElement.TryGetProperty("assignment", out var nested)
                ? nested : assignmentDocument.RootElement;
            var assignmentType = (ProfileValue(assignment, "assignment_type", "assignmentType", "type") ?? "").ToLowerInvariant();
            var assignmentUrl = ProfileValue(assignment, "web_url", "webUrl", "launch_url", "launchUrl", "url", "href", "location");
            if (assignmentType == "lti_submission" && !string.IsNullOrWhiteSpace(assignmentUrl))
                await OpenSharedWebViewAsync(assignmentUrl, record.Title, allowLtiExternal: true,
                    onBack: () => ShowHome("Upcoming"));
            else
                await OpenSharedWebViewAsync($"https://app.schoology.com/assignment/{itemId}", record.Title,
                    allowLtiExternal: true, onBack: () => ShowHome("Upcoming"));
            return;
        }
        if (type is "assessment" or "assessment_v2" or "managed_assessment" or "quiz" or "test")
        {
            if (itemId > 0)
            {
                await OpenSharedWebViewAsync($"https://app.schoology.com/assignment/{itemId}", record.Title,
                    allowLtiExternal: true, onBack: () => ShowHome("Upcoming"));
                return;
            }
        }
        if ((type is "external_tool" or "lti" or "scorm") && sectionId > 0 && itemId > 0)
        {
            var route = type == "external_tool" ? "external-tool" : type;
            await OpenSharedWebViewAsync($"https://app.schoology.com/section/{sectionId}/{route}/{itemId}",
                record.Title, allowLtiExternal: true, onBack: () => ShowHome("Upcoming"));
            return;
        }
        if (type == "discussion" && sectionId > 0 && itemId > 0)
        {
            await OpenSharedWebViewAsync($"https://app.schoology.com/section/{sectionId}/discussion/view/{itemId}",
                record.Title, onBack: () => ShowHome("Upcoming"));
            return;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Text(record.Title, 20, true));
        var course = ProfileValue(record.Data, "section_title", "course_title", "section_name");
        if (!string.IsNullOrWhiteSpace(course)) content.Children.Add(Text(course, 13));
        var date = ProfileValue(record.Data, "start", "start_time", "date", "due", "due_date");
        if (!string.IsNullOrWhiteSpace(date)) content.Children.Add(Text(date, 12));
        var description = ProfileValue(record.Data, "description", "body", "description_richtext");
        if (!string.IsNullOrWhiteSpace(description)) content.Children.Add(Text(StripMarkup(description), 13));
        await new ContentDialog { Title = "Upcoming item", Content = content, CloseButtonText = "Close", XamlRoot = _root.XamlRoot }.ShowAsync();
    }

    private async Task LoadUpcomingAsync(ListView list, TextBlock status)
    {
        try
        {
            var records = await FetchUpcomingRecordsAsync();
            RenderGroupedUpcoming(list, records);
            status.Text = records.Count == 0 ? "No upcoming events or assignments." : $"{records.Count} upcoming items";
        }
        catch (Exception ex)
        {
            status.Text = $"Unable to load upcoming items: {ex.Message}";
            if (list.Items.Count == 0) list.Items.Add(new ListViewItem { Content = Action("Retry", async (_, _) => await LoadUpcomingAsync(list, status)) });
        }
    }

    private UIElement BuildCalendarPage()
    {
        var layout = new Grid { Margin = new Thickness(18), ColumnSpacing = 16, RowSpacing = 12 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(Text("Calendar", 24, true));
        var calendar = new CalendarView { SelectionMode = CalendarViewSelectionMode.Single, IsTodayHighlighted = true };
        Grid.SetRow(calendar, 1); layout.Children.Add(calendar);
        var right = new Grid { RowSpacing = 8 };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = Text(DateTime.Today.ToString("dddd, MMMM d"), 18, true);
        right.Children.Add(heading);
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(list, "CalendarEventsList");
        Grid.SetRow(list, 1); right.Children.Add(list);
        Grid.SetColumn(right, 1); Grid.SetRow(right, 1); layout.Children.Add(right);
        IReadOnlyList<SchoologyApiRecord> events = [];
        calendar.SelectedDatesChanged += (_, _) =>
        {
            var date = calendar.SelectedDates.FirstOrDefault().Date;
            heading.Text = date.ToString("dddd, MMMM d");
            RenderGroupedUpcoming(list, events, date);
        };
        _ = FetchCalendarIntoAsync(list, heading, () => events, value => events = value);
        return layout;
    }

    private async Task FetchCalendarIntoAsync(ListView list, TextBlock heading,
        Func<IReadOnlyList<SchoologyApiRecord>> getEvents, Action<IReadOnlyList<SchoologyApiRecord>> setEvents)
    {
        try
        {
            var events = await FetchUpcomingRecordsAsync();
            setEvents(events);
            _root.DispatcherQueue.TryEnqueue(() => RenderGroupedUpcoming(list, events, DateTime.Today));
        }
        catch (Exception ex)
        {
            _root.DispatcherQueue.TryEnqueue(() =>
            {
                list.Items.Clear();
                var error = new StackPanel { Spacing = 8, Margin = new Thickness(12) };
                error.Children.Add(Text($"Unable to load calendar: {ex.Message}", 13));
                error.Children.Add(Action("Retry", async (_, _) =>
                    await FetchCalendarIntoAsync(list, heading, getEvents, setEvents)));
                list.Items.Add(new ListViewItem { Content = error });
            });
        }
    }

    private sealed record SchoologyRequestItem(string Type, long Id, string Title, string Subtitle, JsonElement Data);
    private sealed record ResourceTreeEntry(string Title, string Kind, long Id, long CollectionId, JsonElement Data)
    {
        public override string ToString() => Title;
    }

    private UIElement BuildNotificationsPage()
    {
        var root = new Grid { Margin = new Thickness(24), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Text("Notifications", 24, true));
        var list = new ListView { SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(list, "SchoologyNotificationsList");
        Grid.SetRow(list, 1); root.Children.Add(list);
        var status = Text("Loading notifications…", 13);
        list.Items.Add(new ListViewItem { Content = status });
        _ = LoadNotificationsAsync(list);
        return root;
    }

    private async Task LoadNotificationsAsync(ListView list)
    {
        try
        {
            var session = _session ?? throw new InvalidOperationException("Sign in to view notifications.");
            using var document = await _api.GetNotificationsAsync(session);
            var root = document.RootElement;
            var notificationData = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataNode) &&
                dataNode.ValueKind == JsonValueKind.Object && dataNode.TryGetProperty("notifications", out var dataNotifications))
                notificationData = dataNotifications;
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("notifications", out var nestedNotifications))
                notificationData = nestedNotifications;
            var unviewed = Math.Max(JsonLong(root, "unviewed_count", "unviewedCount"),
                JsonLong(notificationData, "unviewed_count", "unviewedCount"));
            if (unviewed > 0)
            {
                try { using var ignored = await _api.MarkNotificationsReadAsync(session); }
                catch (Exception ex) { App.WriteStartupLog("Could not mark notifications as read.", ex); }
            }
            var records = SchoologyApiClient.ReadRecords(root, "notification", "notifications");
            if (records.Count == 0)
                records = SchoologyApiClient.ReadRecords(notificationData, "notification", "notifications", "items");
            list.Items.Clear();
            if (records.Count == 0)
            {
                list.Items.Add(new ListViewItem { Content = EmptyState("You have no notifications") });
                return;
            }
            foreach (var record in records)
            {
                var body = ProfileValue(record.Data, "body", "message", "title", "text") ?? record.Title;
                var args = record.Data.ValueKind == JsonValueKind.Object &&
                    record.Data.TryGetProperty("body_args", out var bodyArgs) ? bodyArgs :
                    record.Data.ValueKind == JsonValueKind.Object &&
                    record.Data.TryGetProperty("bodyArgs", out var bodyArgsCamel) ? bodyArgsCamel : default;
                if (args.ValueKind == JsonValueKind.Array)
                    foreach (var arg in args.EnumerateArray())
                        body = body.Replace("%s", ProfileValue(arg, "title", "name") ?? "", StringComparison.Ordinal);
                var type = (ProfileValue(record.Data, "type", "notification_type", "notificationType") ?? "").ToLowerInvariant();
                var iconGlyph = type.Contains("grade") ? "\uE8A5" :
                    type.Contains("assignment") || type.Contains("assessment") ? "\uE8F1" :
                    type.Contains("comment") ? "\uE90A" :
                    type.Contains("event") ? "\uE787" : "\uE713";
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 12, 10, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new FontIcon { Glyph = iconGlyph, FontSize = 22,
                    Foreground = ThemeBrush("SchoologyBlueBrush"), VerticalAlignment = VerticalAlignment.Top });
                var labels = new StackPanel { Spacing = 4 };
                labels.Children.Add(Text(StripMarkup(body), 14, true));
                var dateRaw = ProfileValue(record.Data, "created", "timestamp", "created_at");
                if (!string.IsNullOrWhiteSpace(dateRaw))
                {
                    var date = Text(ParseSchoologyDate(dateRaw)?.ToString("g",
                        System.Globalization.CultureInfo.CurrentCulture) ?? dateRaw, 11);
                    date.Foreground = ThemeBrush("SchoologyMutedTextBrush");
                    labels.Children.Add(date);
                }
                Grid.SetColumn(labels, 1); row.Children.Add(labels);
                var disclosure = new FontIcon { Glyph = "\uE76C", FontSize = 14,
                    Foreground = ThemeBrush("SchoologyMutedTextBrush"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(disclosure, 2); row.Children.Add(disclosure);
                var item = new ListViewItem { Content = row, Tag = record,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(item, StripMarkup(body));
                item.Tapped += async (_, _) => await OpenNotificationAsync(record, body);
                item.KeyDown += async (_, args) =>
                {
                    if (args.Key == Windows.System.VirtualKey.Enter)
                        await OpenNotificationAsync(record, body);
                };
                list.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            list.Items.Clear();
            list.Items.Add(new ListViewItem { Content = EmptyState("Unable to load notifications", ex.Message) });
            list.Items.Add(new ListViewItem { Content = Action("Retry", async (_, _) => await LoadNotificationsAsync(list)) });
        }
    }

    private static string? FindNotificationLink(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "url", "href", "link", "target_url", "targetUrl", "web_url", "webUrl" })
                if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
            foreach (var property in element.EnumerateObject())
            {
                var nested = FindNotificationLink(property.Value);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindNotificationLink(item);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private async Task OpenNotificationAsync(SchoologyApiRecord record, string body)
    {
        var url = FindNotificationLink(record.Data);
        if (string.IsNullOrWhiteSpace(url))
        {
            var dialog = new ContentDialog
            {
                Title = "Notification",
                Content = Text(StripMarkup(body), 14),
                CloseButtonText = "Close",
                XamlRoot = _root.XamlRoot
            };
            await dialog.ShowAsync();
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            uri = new Uri("https://app.schoology.com/" + url.TrimStart('/'));
        await OpenSharedWebViewAsync(uri.AbsoluteUri, "Notification", onBack: () => ShowHome("Notifications"));
    }

    private UIElement BuildRequestsPage()
    {
        var root = new Grid { Margin = new Thickness(24), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Text("Requests", 24, true));
        var list = new ListView { SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(list, "SchoologyRequestsList");
        Grid.SetRow(list, 1); root.Children.Add(list);
        var status = Text("Loading requests…", 13);
        list.Items.Add(new ListViewItem { Content = status });
        _ = LoadRequestsAsync(list);
        return root;
    }

    private async Task LoadRequestsAsync(ListView list)
    {
        try
        {
            var session = _session ?? throw new InvalidOperationException("Sign in to view requests.");
            var results = await Task.WhenAll(
                _api.GetFriendRequestsAsync(session),
                _api.GetGroupInvitationsAsync(session),
                _api.GetSectionInvitationsAsync(session));
            using var friendRequests = results[0];
            using var groupInvitations = results[1];
            using var sectionInvitations = results[2];
            var requests = new List<SchoologyRequestItem>();
            AddRequests(friendRequests.RootElement, "friend", "request", "requests");
            AddRequests(groupInvitations.RootElement, "group", "invite", "invites");
            AddRequests(sectionInvitations.RootElement, "section", "invite", "invites");
            void AddRequests(JsonElement root, string type, params string[] keys)
            {
                foreach (var record in SchoologyApiClient.ReadRecords(root, keys))
                {
                    var id = JsonLong(record.Data, "id", "request_id", "requestId", "invite_id", "inviteId");
                    if (id <= 0) continue;
                    var title = type == "friend"
                        ? ProfileValue(record.Data, "requester_name", "requesterName", "name", "display_name") ?? "Friend request"
                        : ProfileValue(record.Data, "name", "title", type == "group" ? "group_name" : "section_title") ?? "Invitation";
                    var subtitle = ProfileValue(record.Data, "school_name", "schoolName", "description", "message") ?? "";
                    requests.Add(new SchoologyRequestItem(type, id, title, subtitle, record.Data.Clone()));
                }
            }

            list.Items.Clear();
            if (requests.Count == 0)
            {
                list.Items.Add(new ListViewItem { Content = EmptyState("You have no requests") });
                return;
            }
            foreach (var request in requests)
            {
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 12, 10, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var details = new StackPanel { Spacing = 4 };
                var title = Action(request.Title, async (_, _) => await OpenRequestAsync(request));
                title.HorizontalAlignment = HorizontalAlignment.Left;
                title.HorizontalContentAlignment = HorizontalAlignment.Left;
                title.Padding = new Thickness(0, 2, 0, 2);
                title.Background = new SolidColorBrush(Colors.Transparent);
                title.BorderThickness = new Thickness(0);
                details.Children.Add(title);
                if (!string.IsNullOrWhiteSpace(request.Subtitle))
                {
                    var subtitle = Text(request.Subtitle, 12);
                    subtitle.Foreground = ThemeBrush("SchoologyMutedTextBrush");
                    details.Children.Add(subtitle);
                }
                Grid.SetColumn(details, 0); row.Children.Add(details);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center };
                var accept = Action("Accept", async (_, _) => await RespondToRequestAsync(request, true), true);
                accept.Width = 88;
                var decline = Action("Decline", async (_, _) => await RespondToRequestAsync(request, false));
                decline.Width = 88;
                actions.Children.Add(accept); actions.Children.Add(decline);
                Grid.SetColumn(actions, 1); row.Children.Add(actions);
                var item = new ListViewItem { Content = row, Tag = request,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                list.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            list.Items.Clear();
            list.Items.Add(new ListViewItem { Content = EmptyState("Unable to load requests", ex.Message) });
            list.Items.Add(new ListViewItem { Content = Action("Retry", async (_, _) => await LoadRequestsAsync(list)) });
        }
    }

    private async Task OpenRequestAsync(SchoologyRequestItem request)
    {
        var id = request.Type == "friend"
            ? JsonLong(request.Data, "requester_id", "requesterId", "user_id", "uid")
            : JsonLong(request.Data, "realm_id", "realmId", "group_id", "groupId", "section_id", "sectionId");
        if (id <= 0) return;
        var url = request.Type == "friend" ? $"https://app.schoology.com/user/{id}" :
            request.Type == "group" ? $"https://app.schoology.com/group/{id}" : $"https://app.schoology.com/course/{id}";
        await OpenSharedWebViewAsync(url, request.Title, onBack: () => ShowHome("Requests"));
    }

    private async Task RespondToRequestAsync(SchoologyRequestItem request, bool accept)
    {
        try
        {
            if (_session is null) throw new InvalidOperationException("Sign in to manage requests.");
            using var response = await _api.RespondToRequestAsync(_session, request.Type, request.Id, accept);
            if (_sectionFrame is not null) await LoadRequestsAsync((ListView)((Grid)((SchoologyShellContentPage)_sectionFrame.Content!).Content!).Children.OfType<ListView>().First());
            else ShowHome("Requests");
        }
        catch (Exception ex) { await ShowApiErrorAsync($"Unable to {(accept ? "accept" : "decline")} request", ex.Message); }
    }

    private UIElement BuildResourcesPage()
    {
        var root = new Grid { Margin = new Thickness(24), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Text("Resources", 24, true));
        var tree = new TreeView { HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetAutomationId(tree, "SchoologyResourcesTree");
        Grid.SetRow(tree, 1); root.Children.Add(tree);
        var status = Text("Loading resources…", 13);
        tree.RootNodes.Add(new TreeViewNode { Content = status });
        tree.Expanding += async (_, args) =>
        {
            if (args.Node.Content is ResourceTreeEntry entry &&
                (entry.Kind is "collection" or "folder") && args.Node.HasUnrealizedChildren)
                await LoadResourceTreeChildrenAsync(args.Node, entry);
        };
        tree.ItemInvoked += async (_, args) =>
        {
            if (args.InvokedItem is ResourceTreeEntry entry && (entry.Kind is "resource" or "app"))
                await OpenResourceTreeEntryAsync(entry);
        };
        _ = LoadResourcesAsync(tree);
        return root;
    }

    private async Task LoadResourcesAsync(TreeView tree)
    {
        try
        {
            var session = _session ?? throw new InvalidOperationException("Sign in to view resources.");
            using var collectionsDocument = await _api.GetResourceCollectionsAsync(session);
            var apps = new List<SchoologyApiRecord>();
            try
            {
                using var appsDocument = await _api.GetResourceAppsAsync(session);
                apps.AddRange(SchoologyApiClient.ReadRecords(appsDocument.RootElement,
                    "resource_apps", "resourceApps", "collection", "collections"));
            }
            catch (Exception ex) { App.WriteStartupLog("Schoology resource apps could not be loaded; keeping resource collections available.", ex); }
            var all = SchoologyApiClient.ReadRecords(collectionsDocument.RootElement, "collection", "collections", "collection_list");
            var uid = session.UserId;
            var mine = new List<SchoologyApiRecord>();
            var shared = new List<SchoologyApiRecord>();
            var groups = new List<SchoologyApiRecord>();
            foreach (var item in all)
            {
                var owner = JsonLong(item.Data, "collection_owner_id", "collectionOwnerId", "uid", "owner_id");
                var realm = (ProfileValue(item.Data, "collection_realm", "collectionRealm", "realm") ?? "").ToLowerInvariant();
                var sharedCount = JsonLong(item.Data, "shared_users_count", "sharedUsersCount", "shared_users");
                if (string.IsNullOrWhiteSpace(realm) && owner == uid) mine.Add(item);
                else if (sharedCount > 0) shared.Add(item);
                else if (realm == "group") groups.Add(item);
            }
            tree.RootNodes.Clear();
            if (mine.Count + shared.Count + groups.Count + apps.Count == 0)
            {
                tree.RootNodes.Add(new TreeViewNode { Content = EmptyState("No resources") });
                return;
            }
            AddCategory("My Resources", mine);
            AddCategory("Shared", shared);
            AddCategory("Groups", groups);
            AddCategory("Resource Apps", apps);

            void AddCategory(string title, IReadOnlyList<SchoologyApiRecord> items)
            {
                var category = new TreeViewNode { Content = title, IsExpanded = false };
                if (items.Count == 0)
                    category.Children.Add(new TreeViewNode { Content = "No resources" });
                foreach (var item in items)
                {
                    var id = JsonLong(item.Data, "collection_id", "collectionID", "id", "app_id", "appId");
                    var entryTitle = ProfileValue(item.Data, "collection_title", "app_title", "appTitle", "title", "name") ?? item.Title;
                    var entry = new ResourceTreeEntry(entryTitle, title == "Resource Apps" ? "app" : "collection", id, 0, item.Data.Clone());
                    var node = new TreeViewNode { Content = entry, HasUnrealizedChildren = entry.Kind == "collection" };
                                        category.Children.Add(node);
                }
                tree.RootNodes.Add(category);
            }
        }
        catch (Exception ex)
        {
            tree.RootNodes.Clear();
            var error = new TreeViewNode { Content = $"Unable to load resources: {ex.Message}" };
            tree.RootNodes.Add(error);
            tree.RootNodes.Add(new TreeViewNode { Content = Action("Retry", async (_, _) => await LoadResourcesAsync(tree)) });
        }
    }

    private async Task LoadResourceTreeChildrenAsync(TreeViewNode node, ResourceTreeEntry entry)
    {
        node.HasUnrealizedChildren = false;
        node.Children.Clear();
        node.Children.Add(new TreeViewNode { Content = "Loading resources…" });
        try
        {
            if (_session is null)
                throw new InvalidOperationException("Sign in to view this resource collection.");
            var collectionId = entry.Kind == "collection" ? entry.Id : entry.CollectionId;
            if (collectionId <= 0)
                throw new InvalidOperationException("This resource collection is unavailable.");
            var folderId = entry.Kind == "folder" ? entry.Id :
                JsonLong(entry.Data, "folder_id", "folderId", "collection_folder_id", "collectionFolderId");
            using var document = await _api.GetCollectionResourcesAsync(_session, collectionId, folderId);
            var records = SchoologyApiClient.ReadRecords(document.RootElement, "resource", "resources", "resource_list");
            node.Children.Clear();
            if (records.Count == 0)
            {
                node.Children.Add(new TreeViewNode { Content = "No resources" });
                return;
            }
            foreach (var record in records)
            {
                var type = (ProfileValue(record.Data, "type", "resource_type", "document_type", "documentType") ?? "").ToLowerInvariant();
                var isFolder = type.Contains("folder");
                var id = JsonLong(record.Data, "id", "resource_id", "resourceId", "folder_id", "folderId");
                var childTitle = ProfileValue(record.Data, "resource_title", "resourceTitle", "title", "name",
                    "filename", "file_name") ?? record.Title;
                var child = new ResourceTreeEntry(childTitle, isFolder ? "folder" : "resource", id,
                    collectionId, record.Data.Clone());
                var childNode = new TreeViewNode { Content = child, HasUnrealizedChildren = isFolder };
                node.Children.Add(childNode);
            }
        }
        catch (Exception ex)
        {
            node.Children.Clear();
            node.Children.Add(new TreeViewNode { Content = $"Unable to load resources: {ex.Message}" });
            node.HasUnrealizedChildren = true;
        }
    }

    private async Task OpenResourceTreeEntryAsync(ResourceTreeEntry entry)
    {
        try
        {
            if (_session is null) return;
            if (entry.Kind == "app")
            {
                if (entry.Id <= 0) throw new InvalidOperationException("This resource app is missing its ID.");
                using var launch = await _api.LaunchResourceAppAsync(_session, entry.Id);
                var launchUrl = ProfileValue(launch.RootElement, "url", "launch_token_url", "launchTokenUrl");
                if (string.IsNullOrWhiteSpace(launchUrl) &&
                    launch.RootElement.TryGetProperty("data", out var data))
                    launchUrl = ProfileValue(data, "url", "launchTokenUrl", "launch_token_url");
                if (string.IsNullOrWhiteSpace(launchUrl))
                    throw new InvalidOperationException("Schoology did not return a resource-app launch URL.");
                await OpenSharedWebViewAsync(launchUrl, entry.Title, allowLtiExternal: true,
                    onBack: () => ShowHome("Resources"));
                return;
            }

            var url = ProfileValue(entry.Data, "web_url", "webUrl", "launch_url", "launchUrl",
                "url", "link", "download_url", "downloadUrl", "file_download_url", "fileDownloadURL", "download_path", "downloadPath", "file_url", "fileUrl");
            var type = (ProfileValue(entry.Data, "type", "resource_type", "document_type", "documentType") ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(url))
            {
                var details = new StackPanel { Spacing = 8 };
                details.Children.Add(Text(entry.Title, 18, true));
                var description = ProfileValue(entry.Data, "description", "body", "text");
                if (!string.IsNullOrWhiteSpace(description)) details.Children.Add(Text(StripMarkup(description), 13));
                details.Children.Add(Text("Schoology did not provide a web link for this resource.", 12));
                await new ContentDialog { Title = "Resource details", Content = details,
                    CloseButtonText = "Close", XamlRoot = _root.XamlRoot }.ShowAsync();
                return;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var target))
                target = new Uri("https://app.schoology.com/" + url.TrimStart('/'));
            var embeddedExternal = type.Contains("lti") || type.Contains("external_tool") ||
                type.Contains("assessment") || type.Contains("quiz") || type.Contains("test") ||
                        type.Contains("embed") || type.Contains("scorm");
            await OpenSharedWebViewAsync(target.AbsoluteUri, entry.Title, embeddedExternal,
                () => ShowHome("Resources"));
        }
        catch (Exception ex) { await ShowApiErrorAsync("Unable to open resource", ex.Message); }
    }

    private UIElement BuildSettingsPage()
    {
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(24), MaxWidth = 760 };
        panel.Children.Add(Text("Settings", 24, true));
        var appearance = new StackPanel { Spacing = 8 };
        appearance.Children.Add(Text("Appearance", 18, true));
        appearance.Children.Add(Text("App theme", 13, true));
        var theme = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var value in new[] { "System default", "Light", "Dark" }) theme.Items.Add(value);
        var saved = ReadAppSetting("AppTheme", "System default");
        theme.SelectedItem = saved;
        theme.SelectionChanged += (_, _) =>
        {
            var value = theme.SelectedItem?.ToString() ?? "System default";
            WriteAppSetting("AppTheme", value);
            _root.RequestedTheme = value switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        };
        appearance.Children.Add(theme);
        panel.Children.Add(Surface(appearance, new Thickness(14)));
        var behavior = new StackPanel { Spacing = 8 };
        behavior.Children.Add(Text("Behavior", 18, true));
        var desktopNotifications = new CheckBox { Content = "Show Schoology desktop notifications",
            IsChecked = !string.Equals(ReadAppSetting("DesktopNotifications", "true"), "false", StringComparison.OrdinalIgnoreCase) };
        desktopNotifications.Checked += (_, _) => { WriteAppSetting("DesktopNotifications", "true"); EnsureBackgroundNotificationPolling(); };
        desktopNotifications.Unchecked += (_, _) => { WriteAppSetting("DesktopNotifications", "false"); _notificationTimer?.Stop(); };
        behavior.Children.Add(desktopNotifications);
        var notificationSound = new CheckBox { Content = "Play notification sounds",
            IsChecked = string.Equals(ReadAppSetting("NotificationSound", "false"), "true", StringComparison.OrdinalIgnoreCase) };
        notificationSound.Checked += (_, _) => WriteAppSetting("NotificationSound", "true");
        notificationSound.Unchecked += (_, _) => WriteAppSetting("NotificationSound", "false");
        behavior.Children.Add(notificationSound);
        var startupBackground = new CheckBox { Content = "Run Schoology in the background at Windows startup", IsChecked = BackgroundModeEnabled };
        startupBackground.Checked += (_, _) => { WriteAppSetting("RunInBackgroundAtStartup", "true"); ConfigureStartupRegistration(); };
        startupBackground.Unchecked += (_, _) => { WriteAppSetting("RunInBackgroundAtStartup", "false"); ConfigureStartupRegistration(); };
        behavior.Children.Add(startupBackground);
        var reduceMotion = new CheckBox { Content = "Reduce motion",
            IsChecked = string.Equals(ReadAppSetting("ReduceMotion", "false"), "true", StringComparison.OrdinalIgnoreCase) };
        reduceMotion.Checked += (_, _) => WriteAppSetting("ReduceMotion", "true");
        reduceMotion.Unchecked += (_, _) => WriteAppSetting("ReduceMotion", "false");
        behavior.Children.Add(reduceMotion);
        panel.Children.Add(Surface(behavior, new Thickness(14)));
        panel.Children.Add(Text("About", 18, true));
        panel.Children.Add(Surface(Text($"Schoology · {GetType().Assembly.GetName().Version}", 13)));
        panel.Children.Add(Action("Open Schoology website", (_, _) =>
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.schoology.com"))));
        panel.Children.Add(Action("Account Info", async (_, _) =>
            await OpenSharedWebViewAsync("https://app.schoology.com/settings/account", "Account Info",
                onBack: () => ShowHome("Settings"))));
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static UIElement ActivityItem(string course, string title, string meta)
    {
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(Text(course.ToUpperInvariant(), 11, true));
        stack.Children.Add(Text(title, 16, true));
        stack.Children.Add(Text(meta, 12));
        return stack;
    }

    private static UIElement CourseRow(string name)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(7), Background = Brush("#2E66A3"),
            Child = new TextBlock { Text = name[..1], FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = Brush("#FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        grid.Children.Add(tile);
        var labels = new StackPanel { Spacing = 3 };
        labels.Children.Add(Text(name, 14, true));
        labels.Children.Add(Text("Course", 12));
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        return grid;
    }

    private void ShowUpcoming() => ShowHome("Upcoming");

    private void ShowThreePane(string kind, SchoologyEntity? initialEntity = null)
    {
        _page = kind;
        _section = kind;
        if (initialEntity is not null) { _selectedEntity = initialEntity.Name; _selectedEntityId = initialEntity.Id; }
        else { _selectedEntity = string.Empty; _selectedEntityId = 0; }
        var page = BuildThreePane(kind, initialEntity);
        if (_sectionFrame is not null)
            NavigateMainSection(page, kind);
        else
            Reset(BuildApplicationShell(kind, page));
    }

    private IEnumerable<SchoologyEntity> GetSchoolProfileEntities()
    {
        if (_session is null) return Array.Empty<SchoologyEntity>();
        var schoolId = ProfileValue(_session.User, "school_id", "schoolId");
        if (!long.TryParse(schoolId, out var id) || id <= 0) return Array.Empty<SchoologyEntity>();
        var schoolName = ProfileValue(_session.User, "school_title", "school_name", "school") ?? "My School";
        return new[] { new SchoologyEntity(id, schoolName, string.Empty, null, _session.User.Clone()) };
    }

    private UIElement BuildThreePane(string kind, SchoologyEntity? initialEntity = null)
    {
        IEnumerable<SchoologyEntity> entries = kind == "Courses" ? _courses :
            kind == "Groups" ? _groups : GetSchoolProfileEntities();
        var selected = initialEntity ?? entries.FirstOrDefault();
        var entityId = selected?.Id ?? 0;
        var entityName = selected?.Name ?? (kind == "School Profile" ? "School" : kind);
        _selectedEntity = entityName;
        _selectedEntityId = entityId;
        var tabs = kind == "School Profile" ? new[] { "Updates", "Info", "Resources", "Discussions" } :
            kind == "Groups" ? new[] { "Updates", "Members", "Discussions", "Resources" } :
            new[] { "Materials", "Updates", "Grades", "Members", "Discussions" };

        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var wide = new Grid { ColumnSpacing = 0 };
        wide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        wide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        // The left structural pane holds the course/group/school infobox and its app navigation.
        var sidebar = new Grid { Background = ThemeBrush("SchoologySurfaceBrush") };
        sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var identity = new StackPanel { Spacing = 8, Margin = new Thickness(14) };
        var hero = new Border { Height = 124, CornerRadius = new CornerRadius(8),
            Background = ThemeBrush("SchoologyBlueDarkBrush") };
        if (!string.IsNullOrWhiteSpace(selected?.ImageUrl))
        {
            try { hero.Child = new Image { Source = new BitmapImage(new Uri(NormalizeSchoologyImageUrl(selected.ImageUrl))),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill }; } catch { }
        }
        // Landscape keeps the left rail image-only. Course/group/school details
        // belong in the infobox above the selected tab's content.
        identity.Children.Add(hero);
        sidebar.Children.Add(identity);

        var detail = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
        var compactFrame = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
        var navigation = new ListView { SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent) };
        AutomationProperties.SetAutomationId(navigation, kind + "ProfileNavigation");
        foreach (var tab in tabs)
        {
            var tabRow = new Grid { ColumnSpacing = 12 };
            tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            tabRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tabIcon = new SymbolIcon(tab switch
            {
                "Materials" => Symbol.Folder,
                "Updates" => Symbol.Message,
                "Grades" => Symbol.Calculator,
                "Members" => Symbol.People,
                "Discussions" => Symbol.Comment,
                "Resources" => Symbol.Folder,
                "Info" => Symbol.Help,
                _ => Symbol.Page
            })
            {
                Width = 22, Height = 22,
                Foreground = ThemeBrush("SchoologyBlueBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            tabRow.Children.Add(tabIcon);
            var tabLabel = Text(tab, 14, true);
            tabLabel.Foreground = ThemeBrush("SchoologyTextBrush");
            Grid.SetColumn(tabLabel, 1);
            tabRow.Children.Add(tabLabel);
            var tabItem = new ListViewItem
            {
                Content = tabRow, Tag = tab,
                Padding = new Thickness(14, 10, 14, 10)
            };
            tabItem.Tapped += (_, _) => ShowTab(tab);
            navigation.Items.Add(tabItem);
        }
        Grid.SetRow(navigation, 1);
        sidebar.Children.Add(navigation);

        var upcoming = new Grid { RowSpacing = 8, Padding = new Thickness(14),
            Background = ThemeBrush("SchoologySurfaceBrush") };
        upcoming.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        upcoming.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        upcoming.Children.Add(Text("Upcoming", 19, true));
        var upcomingFrame = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetRow(upcomingFrame, 1);
        upcoming.Children.Add(upcomingFrame);
        Grid.SetColumn(sidebar, 0); Grid.SetColumn(detail, 1); Grid.SetColumn(upcoming, 2);
        wide.Children.Add(sidebar); wide.Children.Add(detail); wide.Children.Add(upcoming);

        // In narrow/portrait windows, the infobox remains visible and destinations become SelectorBar tabs.
        var narrow = new Grid { RowSpacing = 8, Padding = new Thickness(12), Visibility = Visibility.Collapsed };
        narrow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        narrow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        narrow.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var compactIdentity = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 4) };
        compactIdentity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        compactIdentity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var compactImage = new Border { Width = 68, Height = 68, CornerRadius = new CornerRadius(6),
            Background = ThemeBrush("SchoologyBlueDarkBrush") };
        if (!string.IsNullOrWhiteSpace(selected?.ImageUrl))
        {
            try { compactImage.Child = new Image { Source = new BitmapImage(new Uri(NormalizeSchoologyImageUrl(selected.ImageUrl))),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill }; } catch { }
        }
        compactIdentity.Children.Add(compactImage);
        var compactLabels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        compactLabels.Children.Add(Text(entityName, 18, true));
        compactLabels.Children.Add(Text(kind == "Courses" ? "Course" :
            kind == "Groups" ? "Group" : "School profile", 12));
        if (kind == "Courses" && !string.IsNullOrWhiteSpace(selected?.Subtitle))
            compactLabels.Children.Add(Text(selected.Subtitle, 12));
        if (kind == "Courses" && selected is not null)
        {
            var schoolName = ProfileValue(selected.Data, "school_title", "school_name", "school") ??
                (_session is null ? null : ProfileValue(_session.User, "school_title", "school_name", "school"));
            if (!string.IsNullOrWhiteSpace(schoolName) && GetSchoolProfileEntities().Any())
                compactLabels.Children.Add(Action($"School: {schoolName}", (_, _) => ShowThreePane("School Profile")));
        }
        Grid.SetColumn(compactLabels, 1); compactIdentity.Children.Add(compactLabels);
        var compactSelector = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var tab in tabs.Concat(["Upcoming"]))
            compactSelector.Items.Add(new SelectorBarItem { Text = tab });
        // In portrait the tab selector precedes the entity infobox.
        Grid.SetRow(compactSelector, 0); narrow.Children.Add(compactSelector);
        Grid.SetRow(compactIdentity, 1); narrow.Children.Add(compactIdentity);
        Grid.SetRow(compactFrame, 2); narrow.Children.Add(compactFrame);

        void ShowTab(string tab)
        {
            if (entityId <= 0) return;
            NavigateShellFrame(detail, tab == "Upcoming" ? BuildUpcomingContent() :
                BuildEntityTabContent(kind, entityId, entityName, tab, selected, showInfobox: true), SlideNavigationTransitionEffect.FromRight);
            NavigateShellFrame(compactFrame, tab == "Upcoming" ? BuildUpcomingContent() :
                BuildEntityTabContent(kind, entityId, entityName, tab, selected, showInfobox: false), SlideNavigationTransitionEffect.FromRight);
        }

        compactSelector.SelectionChanged += (_, _) =>
        {
            if (compactSelector.SelectedItem is SelectorBarItem item) ShowTab(item.Text);
        };
        NavigateShellFrame(upcomingFrame, BuildUpcomingContent(), SlideNavigationTransitionEffect.FromRight);
        if (entityId > 0)
        {
            navigation.SelectedIndex = 0;
            ShowTab(tabs[0]);
            compactSelector.SelectedItem = compactSelector.Items.FirstOrDefault();
        }
        root.Children.Add(wide);
        root.Children.Add(narrow);
        root.SizeChanged += (_, args) =>
        {
            var useNarrow = args.NewSize.Width < 900;
            wide.Visibility = useNarrow ? Visibility.Collapsed : Visibility.Visible;
            narrow.Visibility = useNarrow ? Visibility.Visible : Visibility.Collapsed;
        };
        return root;
    }

    private bool AnimationsEnabled =>
        new UISettings().AnimationsEnabled &&
        !string.Equals(ReadAppSetting("ReduceMotion", "false"), "true", StringComparison.OrdinalIgnoreCase);

    private static string ReadAppSetting(string key, string fallback)
    {
        try
        {
            if (!File.Exists(AppSettingsPath)) return fallback;
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AppSettingsPath));
            return values is not null && values.TryGetValue(key, out var value) ? value : fallback;
        }
        catch { return fallback; }
    }

    private static void WriteAppSetting(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppSettingsPath)!);
            var values = File.Exists(AppSettingsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AppSettingsPath)) ?? new Dictionary<string, string>()
                : new Dictionary<string, string>();
            values[key] = value;
            File.WriteAllText(AppSettingsPath, JsonSerializer.Serialize(values));
        }
        catch (Exception ex) { App.WriteStartupLog($"Could not save setting '{key}'.", ex); }
    }

    private void NavigateShellFrame(Frame frame, UIElement content, SlideNavigationTransitionEffect effect)
    {
        var animationsEnabled = AnimationsEnabled;
        var transition = new SlideNavigationTransitionInfo { Effect = effect };
        frame.ContentTransitions = animationsEnabled
            ? new TransitionCollection { new NavigationThemeTransition { DefaultNavigationTransitionInfo = transition } }
            : null;
        frame.Navigate(typeof(SchoologyShellContentPage), content, animationsEnabled ? transition : null);
    }

    private UIElement BuildEntityTabContent(string kind, long entityId, string entity, string tab,
        SchoologyEntity? entityInfo = null, bool showInfobox = true)
    {
        var layout = new Grid { RowSpacing = 8, Margin = new Thickness(8) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Spacing = 8 };
        var infobox = new StackPanel { Spacing = 4 };
        infobox.Children.Add(Text(entity, 20, true));
        if (!string.IsNullOrWhiteSpace(entityInfo?.Subtitle))
            infobox.Children.Add(Text(entityInfo.Subtitle, 12));
        if (kind == "Courses" && entityInfo is not null)
        {
            var schoolName = ProfileValue(entityInfo.Data, "school_title", "school_name", "school") ??
                (_session is null ? null : ProfileValue(_session.User, "school_title", "school_name", "school"));
            if (!string.IsNullOrWhiteSpace(schoolName))
            {
                if (GetSchoolProfileEntities().Any())
                    infobox.Children.Add(Action($"School: {schoolName}", (_, _) => ShowThreePane("School Profile")));
                else
                {
                    var school = Text(schoolName, 12);
                    school.Foreground = ThemeBrush("SchoologyBlueBrush");
                    infobox.Children.Add(school);
                }
            }
        }
        if (showInfobox) heading.Children.Add(Surface(infobox, new Thickness(12)));
        heading.Children.Add(Text(tab, 17, true));
        Grid.SetRow(heading, 0); layout.Children.Add(heading);
        var body = new StackPanel { Spacing = 14 };
        FrameworkElement bodyContent = tab is "Materials" or "Members" or "Discussions" or "Grades" or "Resources"
            ? body
            : new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body, Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetRow(bodyContent, 1); layout.Children.Add(bodyContent);
        var status = Text("Loading from Schoology…", 13);
        status.Foreground = ThemeBrush("SchoologyMutedTextBrush"); body.Children.Add(status);
        _ = LoadEntityTabAsync(kind, entityId, tab, body, status);
        return layout;
    }

    private async Task LoadEntityTabAsync(string kind, long entityId, string tab, StackPanel body, TextBlock status)
    {
        try
        {
            var session = _session;
            if (session is null || entityId <= 0) throw new InvalidOperationException("Select a Schoology item after signing in.");
            using var document = tab == "Materials" && kind == "Courses"
                ? await _api.GetCourseFolderAsync(session, entityId, 0)
                : tab == "Grades"
                    ? await _api.GetUserGradesAsync(session)
                    : await _api.GetEntityTabAsync(session, kind, entityId, tab);
            if (kind == "School Profile" && tab == "Info")
            {
                body.Children.Remove(status);
                var school = document.RootElement;
                AddProfileField(body, "Address", string.Join(" ", new[]
                {
                    ProfileValue(school, "address1"), ProfileValue(school, "address2"),
                    ProfileValue(school, "city"), ProfileValue(school, "state"),
                    ProfileValue(school, "postal_code"), ProfileValue(school, "country")
                }.Where(value => !string.IsNullOrWhiteSpace(value))));
                AddProfileField(body, "Phone", ProfileValue(school, "phone"));
                if (body.Children.Count <= 2) body.Children.Add(Surface(Text("No additional school information was returned.")));
                return;
            }
            var records = SchoologyApiClient.ReadRecords(document.RootElement,
                tab == "Members" ? ["enrollment", "enrollments", "member", "members", "user", "users"] :
                tab == "Materials" ? ["folder-item", "folder_item", "folderItems", "items"] : []);
            body.Children.Remove(status);
            if (tab == "Members") { body.Children.Add(BuildMembersList(records, kind)); return; }
            if (tab == "Materials") { body.Children.Add(BuildMaterialsList(entityId, records)); return; }
            if (tab == "Grades")
            {
                var sections = SchoologyApiClient.ReadRecords(document.RootElement, "section", "sections");
                var selectedSection = sections.FirstOrDefault(record =>
                    JsonLong(record.Data, "section_id", "sectionId", "id") == entityId);
                if (selectedSection is null)
                    body.Children.Add(EmptyState("No grades"));
                else body.Children.Add(BuildGradeDetails(selectedSection.Data, expandPeriods: true));
                return;
            }
            if (records.Count == 0)
            {
                body.Children.Add(tab switch
                {
                    "Updates" => EmptyState("No updates", kind == "Courses" ? "There are no updates in this course." : null),
                    "Discussions" => EmptyState("No discussions."),
                    "Resources" => EmptyState("No resources."),
                    "Members" => EmptyState("No people found."),
                    _ => EmptyState($"No {tab.ToLowerInvariant()}.")
                });
                return;
            }
            if (tab == "Updates")
            {
                await ResolvePostAuthorsAsync(records, session);
                foreach (var record in records)
                {
                    var authorId = JsonLong(record.Data, "user_id", "uid", "author_id", "authorId", "created_by_id");
                    JsonElement? profile = authorId > 0 && _userProfileCache.TryGetValue(authorId, out var cached) ? cached : null;
                    body.Children.Add(BuildUpdateCard(kind, entityId, record, profile));
                }
                return;
            }
            var list = new ListView { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxHeight = 680 };
            AutomationProperties.SetAutomationId(list, $"{kind}{tab}List");
            foreach (var record in records)
            {
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 9, 8, 9) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (tab == "Materials" && IsFolderRecord(record.Data))
                {
                    var folderIcon = new Image { Width = 32, Height = 32, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
                    row.Children.Add(folderIcon);
                    _ = ApplyFolderIconAsync(folderIcon, FolderColorName(record.Data));
                }
                else row.Children.Add(new FontIcon { Glyph = tab == "Materials" ? MaterialGlyph(record.Data) : "\uE8A5",
                    FontSize = 22, Foreground = ThemeBrush("SchoologyBlueBrush"), Width = 32 });
                var labels = new StackPanel { Spacing = 3 };
                labels.Children.Add(Text(record.Title, 14, true));
                if (!string.IsNullOrWhiteSpace(record.Subtitle)) labels.Children.Add(Text(record.Subtitle, 12));
                Grid.SetColumn(labels, 1); row.Children.Add(labels);
                list.Items.Add(new ListViewItem { Content = row });
            }
            body.Children.Add(list);
        }
        catch (Exception ex)
        {
            status.Text = $"Unable to load {tab.ToLowerInvariant()}: {ex.Message}";
        }
    }



    private UIElement BuildMaterialsList(long sectionId, IReadOnlyList<SchoologyApiRecord> initialRecords)
    {
        var panel = new StackPanel { Spacing = 8 };
        var breadcrumb = Text("Materials", 13, true);
        var history = new Stack<(SchoologyApiRecord[] Records, string Title)>();
        IReadOnlyList<SchoologyApiRecord> current = initialRecords;
        // Create the list before the Back handler captures Render/list.
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxHeight = 680 };
        AutomationProperties.SetAutomationId(list, "CourseMaterialsList");
        Button back = null!;
        back = Action("‹ Back to course materials", (_, _) =>
        {
            if (history.Count == 0) return;
            var previous = history.Pop();
            Render(previous.Records);
            breadcrumb.Text = previous.Title;
            back.IsEnabled = history.Count > 0;
            back.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        back.IsEnabled = false;
        back.Visibility = Visibility.Collapsed;
        void Render(IReadOnlyList<SchoologyApiRecord> records)
        {
            current = records;
            list.Items.Clear();
            foreach (var record in records)
            {
                var type = ProfileValue(record.Data, "type", "document_type", "documentType") ?? "";
                var title = ProfileValue(record.Data, "title", "name") ?? record.Title;
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 9, 8, 9) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (IsFolderRecord(record.Data))
                {
                    var folderIcon = new Image { Width = 25, Height = 25, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                        VerticalAlignment = VerticalAlignment.Center };
                    row.Children.Add(folderIcon);
                    _ = ApplyFolderIconAsync(folderIcon, FolderColorName(record.Data));
                }
                else row.Children.Add(new FontIcon { Glyph = MaterialGlyph(record.Data), FontSize = 23,
                    Foreground = ThemeBrush("SchoologyBlueBrush"), VerticalAlignment = VerticalAlignment.Center });
                var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                labels.Children.Add(Text(title, 14, true));
                var subtitle = ProfileValue(record.Data, "description", "body", "due", "due_date");
                if (!string.IsNullOrWhiteSpace(subtitle)) labels.Children.Add(Text(subtitle, 12));
                Grid.SetColumn(labels, 1); row.Children.Add(labels);
                var item = new ListViewItem { Content = row, Tag = record,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                item.Tapped += async (_, _) => await OpenFolderAsync(record);
                list.Items.Add(item);
            }
        }
        async Task OpenMaterialAsync(SchoologyApiRecord record)
        {
            var title = ProfileValue(record.Data, "title", "name", "filename", "file_name") ?? record.Title;
            var materialType = (ProfileValue(record.Data, "type", "resource_type", "document_type", "documentType") ?? "").ToLowerInvariant();
            var materialId = JsonLong(record.Data, "id", "assignment_id", "assignmentId", "document_id", "documentId");
            if ((materialType is "assignment" or "assignment_v2") && materialId > 0 && _session is not null)
            {
                using var assignmentDocument = await _api.GetAssignmentAsync(_session, sectionId, materialId);
                var assignment = assignmentDocument.RootElement.TryGetProperty("assignment", out var nestedAssignment)
                    ? nestedAssignment : assignmentDocument.RootElement;
                var assignmentType = (ProfileValue(assignment, "assignment_type", "assignmentType", "type") ?? "").ToLowerInvariant();
                var assignmentUrl = ProfileValue(assignment, "web_url", "webUrl", "launch_url", "launchUrl");
                if (assignmentType == "lti_submission" && !string.IsNullOrWhiteSpace(assignmentUrl))
                {
                    await OpenSharedWebViewAsync(assignmentUrl, title, allowLtiExternal: true,
                        () => ShowThreePane("Courses", _courses.FirstOrDefault(course => course.Id == sectionId)));
                    return;
                }
                await OpenSharedWebViewAsync($"https://app.schoology.com/assignment/{materialId}", title,
                    allowLtiExternal: true,
                    onBack: () => ShowThreePane("Courses", _courses.FirstOrDefault(course => course.Id == sectionId)));
                return;
            }
            if (materialType is "assessment" or "assessment_v2" or "managed_assessment" or "quiz" or "test")
            {
                if (materialId > 0)
                {
                    await OpenSharedWebViewAsync($"https://app.schoology.com/assignment/{materialId}", title,
                        allowLtiExternal: true,
                        onBack: () => ShowThreePane("Courses", _courses.FirstOrDefault(course => course.Id == sectionId)));
                    return;
                }
            }
            if ((materialType is "lti" or "external_tool" or "scorm") && materialId > 0)
            {
                var route = materialType == "external_tool" ? "external-tool" : materialType;
                await OpenSharedWebViewAsync($"https://app.schoology.com/section/{sectionId}/{route}/{materialId}",
                    title, allowLtiExternal: true,
                    onBack: () => ShowThreePane("Courses", _courses.FirstOrDefault(course => course.Id == sectionId)));
                return;
            }
            var rawUrl = ProfileValue(record.Data, "web_url", "webUrl", "launch_url", "launchUrl",
                "external_url", "externalUrl", "url", "href", "link", "location", "download_url", "downloadUrl",
                "file_download_url", "fileDownloadURL", "download_path", "downloadPath", "file_url", "fileUrl");
            if ((materialType is "link" or "video") && materialId > 0 && _session is not null)
            {
                using var documentDetails = await _api.GetDocumentAsync(_session, sectionId, materialId);
                var documentData = documentDetails.RootElement.TryGetProperty("document", out var nestedDocument)
                    ? nestedDocument : documentDetails.RootElement;
                if (string.IsNullOrWhiteSpace(rawUrl))
                    rawUrl = ProfileValue(documentData, "web_url", "webUrl", "url", "href", "launch_url", "launchUrl", "location");
            }
            if (!string.IsNullOrWhiteSpace(rawUrl))
            {
                if (!rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    rawUrl = "https://app.schoology.com/" + rawUrl.TrimStart('/');
                if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    var type = (ProfileValue(record.Data, "type", "resource_type", "document_type", "documentType") ?? "").ToLowerInvariant();
                    var isLtiOrAssessment = type.Contains("lti") || type.Contains("external_tool") ||
                        type.Contains("assessment") || type.Contains("quiz") || type.Contains("test") ||
                type.Contains("embed") || type.Contains("scorm");
                    await OpenSharedWebViewAsync(uri.AbsoluteUri, title, isLtiOrAssessment,
                        () => ShowThreePane("Courses", _courses.FirstOrDefault(course => course.Id == sectionId)));
                    return;
                }
            }
            var details = new StackPanel { Spacing = 8 };
            details.Children.Add(Text(title, 18, true));
            var description = ProfileValue(record.Data, "description", "body", "due", "due_date", "type");
            if (!string.IsNullOrWhiteSpace(description)) details.Children.Add(Text(StripMarkup(description), 13));
            details.Children.Add(Text("Schoology did not provide an embeddable HTTPS link for this material.", 12));
            await new ContentDialog { Title = "Material details", Content = details,
                CloseButtonText = "Close", XamlRoot = _root.XamlRoot }.ShowAsync();
        }

        async Task OpenFolderAsync(SchoologyApiRecord record)
        {
            if (!string.Equals(ProfileValue(record.Data, "type", "document_type", "documentType"), "folder", StringComparison.OrdinalIgnoreCase))
            {
                await OpenMaterialAsync(record);
                return;
            }
            var folderId = JsonLong(record.Data, "id", "folder_id", "folderId");
            if (folderId <= 0 || _session is null) return;
            try
            {
                using var folder = await _api.GetCourseFolderAsync(_session, sectionId, folderId);
                var records = SchoologyApiClient.ReadRecords(folder.RootElement, "folder-item", "folder_item", "folderItems", "items");
                history.Push((current.ToArray(), breadcrumb.Text));
                breadcrumb.Text = ProfileValue(record.Data, "title", "name") ?? record.Title;
                back.IsEnabled = true;
                back.Visibility = Visibility.Visible;
                Render(records);
                if (records.Count == 0) list.Items.Add(new ListViewItem { Content = Text("This folder is empty.", 13) });
            }
            catch (Exception ex) { await ShowApiErrorAsync("Unable to open folder", ex.Message); }
        }
        panel.Children.Add(back);
        panel.Children.Add(breadcrumb);
        panel.Children.Add(list);
        Render(initialRecords);
        if (initialRecords.Count == 0) list.Items.Add(new ListViewItem { Content = EmptyState("This folder is empty") });
        return panel;
    }

    private UIElement BuildMembersList(IReadOnlyList<SchoologyApiRecord> records, string kind)
    {
        var people = records.Select(record =>
        {
            var data = record.Data;
            var user = data.ValueKind == System.Text.Json.JsonValueKind.Object && data.TryGetProperty("user", out var nested) ? nested : data;
            var id = JsonLong(data, "uid", "user_id", "userId", "id");
            if (id <= 0) id = JsonLong(user, "id", "uid", "user_id");
            var first = ProfileValue(user, "name_first", "first_name") ?? ProfileValue(data, "name_first", "first_name") ?? "";
            var last = ProfileValue(user, "name_last", "last_name") ?? ProfileValue(data, "name_last", "last_name") ?? "";
            var name = ProfileValue(user, "name_display", "display_name", "name") ?? ProfileValue(data, "name_display", "display_name", "name")
                ?? string.Join(" ", new[] { first, last }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var picture = ProfileValue(user, "picture_url", "pictureUrl", "picture") ?? ProfileValue(data, "picture_url", "pictureUrl", "picture");
            var role = ProfileValue(data, "role_name", "roleName", "role", "position") ?? ProfileValue(user, "role_name", "roleName", "role");
            var admin = JsonBoolean(data, "admin", "is_admin", "isAdmin", "owner", "is_owner") ||
                JsonBoolean(user, "admin", "is_admin", "isAdmin", "owner", "is_owner") ||
                (!string.IsNullOrWhiteSpace(role) && System.Text.RegularExpressions.Regex.IsMatch(role, @"\b(admin|administrator|owner)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            return new { Id = id, First = first, Last = last, Name = string.IsNullOrWhiteSpace(name) ? "Schoology member" : name, Picture = picture, Role = role, Admin = admin };
        }).Where(person => person.Id > 0).ToList();
        var root = new StackPanel { Spacing = 8 };
        var filters = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        var all = new SelectorBarItem { Text = "All" };
        var members = new SelectorBarItem { Text = "Members" };
        var admins = new SelectorBarItem { Text = "Admins" };
        filters.Items.Add(all); filters.Items.Add(members); filters.Items.Add(admins); filters.SelectedItem = all;
        var search = new TextBox { PlaceholderText = $"Search {kind.ToLowerInvariant()} members" };
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxHeight = 680 };
        AutomationProperties.SetAutomationId(list, kind + "MembersList");
        void Render()
        {
            list.Items.Clear();
            var query = search.Text.Trim();
            var visible = people.Where(person =>
                (filters.SelectedItem == all || (filters.SelectedItem == admins ? person.Admin : !person.Admin)) &&
                (query.Length == 0 || person.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                 person.Last.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                .OrderByDescending(person => person.Admin).ThenBy(person => person.Last, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(person => person.First, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (visible.Count == 0)
            {
                list.Items.Add(new ListViewItem { Content = Text(filters.SelectedItem == admins ? "No admins found." :
                    filters.SelectedItem == members ? "No members found." : "No members were returned by Schoology.", 13) });
                return;
            }
            var lastHeader = "";
            foreach (var person in visible)
            {
                var header = filters.SelectedItem == all && person.Admin ? "Admins" :
                    (string.IsNullOrWhiteSpace(person.Last) ? "#" : person.Last[..1].ToUpperInvariant());
                if (header != lastHeader)
                {
                    list.Items.Add(new ListViewItem { IsEnabled = false, Content = Text(header, 12, true),
                        Background = ThemeBrush("SchoologySubtleBrush"), Padding = new Thickness(10, 6, 10, 6) });
                    lastHeader = header;
                }
                var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 7, 8, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var avatar = new Image { Width = 40, Height = 40, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
                if (!string.IsNullOrWhiteSpace(person.Picture))
                    try { avatar.Source = new BitmapImage(new Uri(NormalizeSchoologyImageUrl(person.Picture))); } catch { }
                else _ = LoadMemberAvatarAsync(person.Id, avatar);
                row.Children.Add(new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
                    Background = ThemeBrush("SchoologySubtleBrush"), Child = avatar });
                var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                labels.Children.Add(Text(person.Name, 14, true));
                if (!string.IsNullOrWhiteSpace(person.Role)) labels.Children.Add(Text(person.Role, 11));
                Grid.SetColumn(labels, 1); row.Children.Add(labels);
                var memberItem = new ListViewItem { Content = row, Tag = person.Id,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                memberItem.Tapped += async (_, _) => await OpenMemberProfileAsync(person.Id);
                list.Items.Add(memberItem);
            }
        }
        filters.SelectionChanged += (_, _) => Render();
        search.TextChanged += (_, _) => Render();
        Render();
        root.Children.Add(filters); root.Children.Add(search); root.Children.Add(list);
        if (people.Count == 0) root.Children.Add(Text("The enrollment response contained no recognizable user IDs.", 12));
        return root;
    }



    private async Task OpenMemberProfileAsync(long userId)
    {
        if (_session is null || userId <= 0) return;
        try
        {
            using var document = await _api.GetUserAsync(_session, userId);
            var user = document.RootElement;
            if (user.ValueKind == JsonValueKind.Object && user.TryGetProperty("user", out var nested)) user = nested;
            var details = new StackPanel { Spacing = 8 };
            var name = ProfileValue(user, "name_display", "display_name", "name") ?? "Schoology member";
            details.Children.Add(Text(name, 19, true));
            AddProfileField(details, "Username", ProfileValue(user, "username"));
            AddProfileField(details, "Email", ProfileValue(user, "primary_email", "email"));
            AddProfileField(details, "School", ProfileValue(user, "school_title", "school_name"));
            await new ContentDialog { Title = "Member profile", Content = details,
                CloseButtonText = "Close", XamlRoot = _root.XamlRoot }.ShowAsync();
        }
        catch (Exception ex) { await ShowApiErrorAsync("Unable to open member profile", ex.Message); }
    }

    private static string NormalizeSchoologyImageUrl(string value)
    {
        var url = value.Trim();
        if (url.StartsWith("//")) return "https:" + url;
        if (url.StartsWith("/")) return "https://app.schoology.com" + url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "https://" + url[7..];
        return url;
    }

    private async Task LoadMemberAvatarAsync(long userId, Image image)
    {
        try
        {
            if (_session is null || userId <= 0) return;
            using var user = await _api.GetUserAsync(_session, userId);
            var data = user.RootElement.TryGetProperty("user", out var nested) ? nested : user.RootElement;
            var picture = ProfileValue(data, "picture_url", "pictureUrl", "picture", "photo_url");
            if (!string.IsNullOrWhiteSpace(picture))
                image.Source = new BitmapImage(new Uri(NormalizeSchoologyImageUrl(picture)));
        }
        catch { }
    }

    private static bool IsFolderRecord(JsonElement data)
    {
        var type = ProfileValue(data, "type", "templateDocumentType") ?? string.Empty;
        if (type.Equals("folder", StringComparison.OrdinalIgnoreCase)) return true;
        return data.TryGetProperty("template_fields", out var fields) &&
            (ProfileValue(fields, "document_type", "documentType") ?? string.Empty).Equals("folder", StringComparison.OrdinalIgnoreCase);
    }
    private static string FolderColorName(JsonElement data)
    {
        var color = (ProfileValue(data, "color", "folder_color", "folderColor") ?? "gray").Trim().ToLowerInvariant();
        return color is "black" or "blue" or "gray" or "green" or "orange" or "pink" or "purple" or "red" or "yellow"
            ? color : "gray";
    }
    private static async Task ApplyFolderIconAsync(Image image, string color)
    {
        try { image.Source = await FolderIcons.LoadAsync(color); }
        catch (Exception ex) { App.WriteStartupLog("Could not load an embedded Schoology folder icon.", ex); }
    }
    private static string MaterialGlyph(System.Text.Json.JsonElement data)
    {
        if (IsFolderRecord(data)) return "\uE8B7";
        var type = ProfileValue(data, "type", "document_type", "mime_type", "mimeType", "extension")?.ToLowerInvariant() ?? "";
        if (type.Contains("pdf")) return "\uEA90";
        if (type.Contains("image") || type is "png" or "jpg" or "jpeg") return "\uEB9F";
        if (type.Contains("video")) return "\uE714";
        if (type.Contains("audio")) return "\uE8D6";
        if (type.Contains("link") || type.Contains("url")) return "\uE71B";
        if (type.Contains("spreadsheet") || type.Contains("xls")) return "\uE9F9";
        return "\uE8A5";
    }

    private UIElement BuildUpdateCard(string kind, long entityId, SchoologyApiRecord update, JsonElement? authorProfile = null)
    {
        var updateId = JsonLong(update.Data, "id", "update_id");
        var card = new StackPanel { Spacing = 10 };
        var profile = authorProfile ?? update.Data;
        var author = ProfileValue(profile, "name_display", "display_name", "name", "user_name") ??
            ProfileValue(update.Data, "user_name", "author_name", "name_display", "created_by") ?? "Schoology";
        var createdRaw = ProfileValue(update.Data, "created", "created_at", "timestamp");
        var created = ParseSchoologyDate(createdRaw)?.ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? createdRaw;
        var picture = ProfileValue(profile, "picture_url", "pictureUrl", "picture", "photo_url", "profile_url") ??
            ProfileValue(update.Data, "picture_url", "author_picture", "user_picture", "avatar_url");
        var heading = new Grid { ColumnSpacing = 10 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var avatar = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
            Background = ThemeBrush("SchoologySubtleBrush") };
        if (!string.IsNullOrWhiteSpace(picture))
        {
            try { avatar.Child = new Image { Source = new BitmapImage(new Uri(NormalizeSchoologyImageUrl(picture))),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill }; } catch { }
        }
        else avatar.Child = new FontIcon { Glyph = "\uE77B", FontSize = 19,
            Foreground = ThemeBrush("SchoologyMutedTextBrush"), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(avatar);
        var authorInfo = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        authorInfo.Children.Add(Text(author, 14, true));
        if (!string.IsNullOrWhiteSpace(created))
        {
            var date = Text(created, 11);
            date.Foreground = ThemeBrush("SchoologyMutedTextBrush");
            authorInfo.Children.Add(date);
        }
        Grid.SetColumn(authorInfo, 1); heading.Children.Add(authorInfo);
        card.Children.Add(heading);
        var title = ProfileValue(update.Data, "title", "subject");
        if (!string.IsNullOrWhiteSpace(title)) card.Children.Add(Text(title, 16, true));
        var postBody = ProfileValue(update.Data, "body", "message", "description", "update_body", "updateBody",
            "richtext_body", "richtextBody", "text") ?? update.Subtitle;
        if (!string.IsNullOrWhiteSpace(postBody))
        {
            var body = Text(StripMarkup(postBody), 14);
            body.TextWrapping = TextWrapping.WrapWholeWords;
            card.Children.Add(body);
        }
        if (update.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
            update.Data.TryGetProperty("attachments", out var attachments) &&
            attachments.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var attachmentPanel = new StackPanel { Spacing = 5 };
            foreach (var attachment in attachments.EnumerateArray())
            {
                var name = ProfileValue(attachment, "title", "name", "filename", "file_name") ?? "Attachment";
                var type = ProfileValue(attachment, "type", "mime_type", "extension");
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                line.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 17,
                    Foreground = ThemeBrush("SchoologyBlueBrush") });
                line.Children.Add(Text(string.IsNullOrWhiteSpace(type) ? name : $"{name} · {type}", 12, true));
                attachmentPanel.Children.Add(line);
            }
            if (attachmentPanel.Children.Count > 0) card.Children.Add(Surface(attachmentPanel, new Thickness(10)));
        }

        if (update.Data.ValueKind == JsonValueKind.Object)
        {
            JsonElement updateAttachments = default;
            foreach (var key in new[] { "attachments", "attachment", "files" })
                if (update.Data.TryGetProperty(key, out updateAttachments)) break;
            var values = updateAttachments.ValueKind == JsonValueKind.Array ? updateAttachments.EnumerateArray().ToArray() :
                updateAttachments.ValueKind == JsonValueKind.Object ? new[] { updateAttachments } : Array.Empty<JsonElement>();
            foreach (var attachment in values)
            {
                var url = ProfileValue(attachment, "url", "download_url", "downloadUrl", "file_url", "fileUrl", "thumbnail_url");
                var mime = ProfileValue(attachment, "mime_type", "mimeType", "type", "extension") ?? "";
                var name = ProfileValue(attachment, "title", "name", "filename", "file_name") ?? "Attachment";
                if (!string.IsNullOrWhiteSpace(url) && (mime.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                    new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" }.Any(ext => url.Contains(ext, StringComparison.OrdinalIgnoreCase))))
                {
                    try { card.Children.Add(new Image { Source = new BitmapImage(new Uri(NormalizeSchoologyImageUrl(url))),
                        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxHeight = 360, HorizontalAlignment = HorizontalAlignment.Left }); }
                    catch { card.Children.Add(Text(name, 12)); }
                }
                else card.Children.Add(Text(name, 12));
            }
            JsonElement poll = default;
            foreach (var key in new[] { "pollUpdateModel", "poll_update_model", "poll", "poll_update", "pollUpdate" })
                if (update.Data.TryGetProperty(key, out poll)) break;
            if (poll.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                var pollPanel = new StackPanel { Spacing = 6 };
                pollPanel.Children.Add(Text("Poll", 13, true));
                JsonElement options = poll;
                if (poll.ValueKind == JsonValueKind.Object)
                    foreach (var key in new[] { "options", "pollOptionsList", "poll_options", "pollOptions" })
                        if (poll.TryGetProperty(key, out var nested)) { options = nested; break; }
                if (options.ValueKind == JsonValueKind.Array)
                    foreach (var option in options.EnumerateArray())
                    {
                        var optionTitle = ProfileValue(option, "title", "name", "text") ?? "Poll option";
                        var votes = ProfileValue(option, "count", "votes", "vote_count", "voteCount", "total_votes") ?? "0";
                        var optionRow = new StackPanel { Spacing = 2 };
                        optionRow.Children.Add(Text(optionTitle, 13, true));
                        optionRow.Children.Add(Text($"{votes} votes", 11));
                        pollPanel.Children.Add(Surface(optionRow, new Thickness(8)));
                    }
                else pollPanel.Children.Add(Text("Poll options are not available.", 12));
                card.Children.Add(Surface(pollPanel, new Thickness(10)));
            }
        }

        var commentRealm = kind;
        var commentRealmId = entityId;
        if (kind == "Recent")
        {
            var realm = (ProfileValue(update.Data, "realm", "realm_name", "realmName") ?? "").ToLowerInvariant();
            commentRealm = realm switch
            {
                "group" or "groups" => "Groups",
                "school" or "schools" => "School Profile",
                "user" or "users" => "Profile",
                _ => "Courses"
            };
            commentRealmId = JsonLong(update.Data, "realm_id", "realmId", "section_id", "sectionId",
                "group_id", "groupId", "school_id", "schoolId", "user_id", "uid");
        }

        var commentCount = JsonLong(update.Data, "num_comments", "comment_count", "comments_count", "commentCount");
        var likeCount = JsonLong(update.Data, "likes", "like_count", "likes_count", "likeCount");
        var liked = JsonBoolean(update.Data, "user_like_action", "user_liked", "userLiked", "liked", "is_liked");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        Button commentCountButton = null!;
        Button likeCountButton = null!;
        StackPanel previewPanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
        Button viewMoreButton = null!;

        var commentButton = Action("Comment", async (_, _) =>
        {
            if (updateId <= 0 || commentRealmId <= 0 || _session is null) return;
            await ShowUpdateCommentComposerAsync(commentRealm, commentRealmId, updateId, () =>
            {
                commentCount++;
                commentCountButton.Content = commentCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
                commentCountButton.Visibility = Visibility.Visible;
                if (commentCountButton.Tag is Border divider) divider.Visibility = Visibility.Visible;
                _ = LoadRecentPostCommentsAsync(commentRealm, commentRealmId, updateId, previewPanel, viewMoreButton);
            });
        });
        AutomationProperties.SetAutomationId(commentButton, $"UpdateComment_{updateId}");

        commentCountButton = Action(commentCount.ToString(System.Globalization.CultureInfo.CurrentCulture),
            async (_, _) => await ShowUpdateCommentsDialogAsync(commentRealm, commentRealmId, updateId, () =>
            {
                commentCount++;
                commentCountButton.Content = commentCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
                commentCountButton.Visibility = Visibility.Visible;
                AutomationProperties.SetName(commentCountButton, $"{commentCount} comments");
                if (commentCountButton.Tag is Border divider) divider.Visibility = Visibility.Visible;
                _ = LoadRecentPostCommentsAsync(commentRealm, commentRealmId, updateId, previewPanel, viewMoreButton);
            }));
        commentCountButton.Visibility = commentCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(commentCountButton, $"{commentCount} comments");
        AutomationProperties.SetAutomationId(commentCountButton, $"UpdateCommentCount_{updateId}");
        if (commentRealmId <= 0 || updateId <= 0)
        {
            commentButton.IsEnabled = false;
            commentCountButton.IsEnabled = false;
        }

        Button likeButton = null!;
        likeButton = Action(liked ? "Unlike" : "Like", async (_, _) =>
        {
            if (updateId <= 0 || _session is null) return;
            likeButton.IsEnabled = false;
            try
            {
                using var result = await _api.SetUpdateLikeAsync(_session, updateId, !liked);
                liked = !liked;
                likeCount = Math.Max(0, likeCount + (liked ? 1 : -1));
                likeButton.Content = liked ? "Unlike" : "Like";
                likeCountButton.Content = likeCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
                likeCountButton.Visibility = likeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (likeCountButton.Tag is Border divider) divider.Visibility = likeCountButton.Visibility;
                AutomationProperties.SetName(likeCountButton, $"{likeCount} likes");
            }
            catch (Exception ex) { await ShowApiErrorAsync("Unable to update like", ex.Message); }
            finally { likeButton.IsEnabled = true; }
        });
        AutomationProperties.SetAutomationId(likeButton, $"UpdateLike_{updateId}");

        likeCountButton = Action(likeCount.ToString(System.Globalization.CultureInfo.CurrentCulture),
            async (_, _) => await ShowUpdateLikesDialogAsync(updateId));
        likeCountButton.Visibility = likeCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(likeCountButton, $"{likeCount} likes");
        AutomationProperties.SetAutomationId(likeCountButton, $"UpdateLikeCount_{updateId}");

        actions.Children.Add(ConnectedButtons(commentButton, commentCountButton));
        actions.Children.Add(ConnectedButtons(likeButton, likeCountButton));
        card.Children.Add(actions);

        viewMoreButton = Action("View more comments", async (_, _) =>
            await ShowUpdateCommentsDialogAsync(commentRealm, commentRealmId, updateId, () =>
            {
                commentCount++;
                commentCountButton.Content = commentCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
                commentCountButton.Visibility = Visibility.Visible;
                AutomationProperties.SetName(commentCountButton, $"{commentCount} comments");
                if (commentCountButton.Tag is Border divider) divider.Visibility = Visibility.Visible;
                _ = LoadRecentPostCommentsAsync(commentRealm, commentRealmId, updateId, previewPanel, viewMoreButton);
            }));
        viewMoreButton.HorizontalAlignment = HorizontalAlignment.Left;
        viewMoreButton.Width = double.NaN;
        card.Children.Add(previewPanel);
        if (commentCount > 0 && commentRealmId > 0 && updateId > 0)
            _ = LoadRecentPostCommentsAsync(commentRealm, commentRealmId, updateId, previewPanel, viewMoreButton);

        return Surface(card);
    }

    private async Task LoadUpdateCommentsAsync(
        string kind, long entityId, long updateId, StackPanel panel,
        TextBlock status, StackPanel list)
    {
        try
        {
            if (_session is null || updateId <= 0)
                throw new InvalidOperationException("This update does not have a valid Schoology ID.");
            status.Text = "Loading comments…";
            list.Children.Clear();
            using var document = await _api.GetUpdateCommentsAsync(_session, kind, entityId, updateId);
            var comments = SchoologyApiClient.ReadRecords(document.RootElement, "comment", "comments");
            foreach (var comment in comments)
            {
                var row = new StackPanel { Spacing = 4 };
                var author = ProfileValue(comment.Data, "user_name", "author_name", "name_display", "created_by") ?? comment.Title;
                row.Children.Add(Text(author, 12, true));
                var created = ProfileValue(comment.Data, "created", "created_at", "timestamp");
                if (!string.IsNullOrWhiteSpace(created)) { var date = Text(created, 10); date.Foreground = ThemeBrush("SchoologyMutedTextBrush"); row.Children.Add(date); }
                var commentText = StripMarkup(ProfileValue(comment.Data, "comment", "body", "text", "message") ?? comment.Subtitle);
                if (!string.IsNullOrWhiteSpace(commentText)) row.Children.Add(Text(commentText, 12));
                list.Children.Add(Surface(row));
            }
            status.Text = comments.Count == 0 ? "No comments yet. Be the first to comment." : $"{comments.Count} comments";
        }
        catch (Exception ex)
        {
            status.Text = $"Unable to load comments: {ex.Message}";
            var retry = Action("Retry", async (_, _) =>
                await LoadUpdateCommentsAsync(kind, entityId, updateId, panel, status, list));
            panel.Children.Insert(Math.Max(0, panel.Children.IndexOf(status) + 1), retry);
        }
    }

    private Border ConnectedButtons(Button primary, Button count)
    {
        primary.Background = new SolidColorBrush(Colors.Transparent);
        primary.BorderThickness = new Thickness(0);
        primary.Padding = new Thickness(12, 8, 12, 8);
        count.Background = new SolidColorBrush(Colors.Transparent);
        count.BorderThickness = new Thickness(0);
        count.Padding = new Thickness(10, 8, 12, 8);

        var group = new Grid { ColumnSpacing = 0 };
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        group.Children.Add(primary);
        var divider = new Border { Width = 1, Background = ThemeBrush("SchoologyDividerBrush"),
            VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 6),
            Visibility = count.Visibility };
        Grid.SetColumn(divider, 1);
        group.Children.Add(divider);
        count.Tag = divider;
        Grid.SetColumn(count, 1);
        group.Children.Add(count);
        return new Border
        {
            BorderBrush = ThemeBrush("SchoologyDividerBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrush("SchoologySubtleBrush"),
            Child = group
        };
    }

    private async Task LoadRecentPostCommentsAsync(
        string kind, long entityId, long updateId, StackPanel preview, Button viewMore)
    {
        try
        {
            if (_session is null || entityId <= 0 || updateId <= 0)
            {
                preview.Visibility = Visibility.Collapsed;
                return;
            }

            using var document = await _api.GetUpdateCommentsAsync(_session, kind, entityId, updateId);
            var comments = SchoologyApiClient.ReadRecords(document.RootElement, "comment", "comments");
            preview.Children.Clear();
            if (comments.Count == 0)
            {
                preview.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var comment in comments.TakeLast(2))
            {
                var row = new StackPanel { Spacing = 3, Margin = new Thickness(10, 7, 10, 7) };
                var author = ProfileValue(comment.Data, "user_name", "author_name", "name_display", "display_name") ??
                    comment.Title;
                row.Children.Add(Text(author, 12, true));
                var body = StripMarkup(ProfileValue(comment.Data, "comment", "body", "text", "message") ?? comment.Subtitle);
                if (!string.IsNullOrWhiteSpace(body)) row.Children.Add(Text(body, 12));
                preview.Children.Add(Surface(row, new Thickness(0)));
            }

            if (comments.Count > 2)
            {
                viewMore.Content = $"View more comments ({comments.Count})";
                preview.Children.Add(viewMore);
            }
            preview.Visibility = Visibility.Visible;
        }
        catch
        {
            // Keep the post usable if preview loading fails; the full comments
            // dialog still provides a retryable, explicit interaction.
            preview.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ShowUpdateCommentComposerAsync(
        string kind, long entityId, long updateId, Action? onPosted = null)
    {
        if (_session is null || entityId <= 0 || updateId <= 0) return;
        var input = new TextBox
        {
            PlaceholderText = "Write a comment…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100
        };
        AutomationProperties.SetAutomationId(input, $"UpdateCommentComposer_{updateId}");
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(Text("Add a comment to this post.", 13));
        content.Children.Add(input);
        var dialog = new ContentDialog
        {
            Title = "Add comment",
            Content = content,
            CloseButtonText = "Cancel",
            XamlRoot = _root.XamlRoot
        };
        Button post = null!;
        post = Action("Post comment", async (_, _) =>
        {
            var value = input.Text.Trim();
            if (value.Length == 0) return;
            post.IsEnabled = false;
            try
            {
                using var result = await _api.AddUpdateCommentAsync(_session, kind, entityId, updateId, value);
                dialog.Hide();
                onPosted?.Invoke();
            }
            catch (Exception ex) { await ShowApiErrorAsync("Unable to post comment", ex.Message); }
            finally { post.IsEnabled = true; }
        }, primary: true);
        content.Children.Add(post);
        await dialog.ShowAsync();
    }

    private async Task ShowUpdateCommentsDialogAsync(
        string kind, long entityId, long updateId, Action? onPosted = null)
    {
        if (_session is null || entityId <= 0 || updateId <= 0) return;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(Text("Loading comments…", 12));
        var dialog = new ContentDialog
        {
            Title = "Comments",
            Content = new ScrollViewer { Content = content, MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Add comment",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _root.XamlRoot
        };
        try
        {
            using var document = await _api.GetUpdateCommentsAsync(_session, kind, entityId, updateId);
            var comments = SchoologyApiClient.ReadRecords(document.RootElement, "comment", "comments");
            content.Children.Clear();
            if (comments.Count == 0) content.Children.Add(Text("No comments yet.", 13));
            foreach (var comment in comments)
            {
                var row = new StackPanel { Spacing = 4, Margin = new Thickness(10, 8, 10, 8) };
                var authorId = JsonLong(comment.Data, "uid", "user_id", "userId", "author_id", "authorId");
                var author = ProfileValue(comment.Data, "user_name", "author_name", "name_display", "display_name");
                if (authorId > 0)
                {
                    try
                    {
                        using var userDocument = await _api.GetUserAsync(_session, authorId);
                        var user = userDocument.RootElement.TryGetProperty("user", out var nestedUser)
                            ? nestedUser : userDocument.RootElement;
                        author ??= ProfileValue(user, "name_display", "display_name", "name", "user_name");
                    }
                    catch { }
                }
                row.Children.Add(Text(author ?? comment.Title, 12, true));
                var created = ProfileValue(comment.Data, "created", "created_at", "timestamp");
                if (!string.IsNullOrWhiteSpace(created))
                {
                    var date = Text(ParseSchoologyDate(created)?.ToString("g",
                        System.Globalization.CultureInfo.CurrentCulture) ?? created, 10);
                    date.Foreground = ThemeBrush("SchoologyMutedTextBrush");
                    row.Children.Add(date);
                }
                var body = StripMarkup(ProfileValue(comment.Data, "comment", "body", "text", "message") ?? comment.Subtitle);
                if (!string.IsNullOrWhiteSpace(body)) row.Children.Add(Text(body, 13));
                content.Children.Add(Surface(row));
            }
            dialog.Title = comments.Count == 0 ? "Comments" : $"Comments ({comments.Count})";
        }
        catch (Exception ex)
        {
            content.Children.Clear();
            content.Children.Add(Text($"Unable to load comments: {ex.Message}", 12));
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await ShowUpdateCommentComposerAsync(kind, entityId, updateId, onPosted);
    }

    private async Task ShowUpdateLikesDialogAsync(long updateId)
    {
        if (_session is null || updateId <= 0) return;
        try
        {
            using var document = await _api.GetUpdateLikesAsync(_session, updateId);
            var users = SchoologyApiClient.ReadRecords(document.RootElement, "user", "users", "like", "likes");
            var content = new StackPanel { Spacing = 8 };
            if (users.Count == 0) content.Children.Add(Text("No likes were returned.", 13));
            foreach (var person in users)
            {
                var name = ProfileValue(person.Data, "name_display", "display_name", "name", "user_name") ??
                    string.Join(" ", new[] { ProfileValue(person.Data, "name_first"), ProfileValue(person.Data, "name_last") }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                content.Children.Add(Text(string.IsNullOrWhiteSpace(name) ? person.Title : name, 13));
            }
            await new ContentDialog
            {
                Title = users.Count == 1 ? "1 like" : $"{users.Count} likes",
                Content = new ScrollViewer { Content = content, MaxHeight = 480,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                CloseButtonText = "Close",
                XamlRoot = _root.XamlRoot
            }.ShowAsync();
        }
        catch (Exception ex) { await ShowApiErrorAsync("Unable to load likes", ex.Message); }
    }

    private async Task ShowApiErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = _root.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private static long JsonLong(System.Text.Json.JsonElement data, params string[] keys)
    {
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object) return 0;
        foreach (var key in keys)
            if (data.TryGetProperty(key, out var value) && long.TryParse(value.ToString(), out var result))
                return result;
        return 0;
    }

    private static bool JsonBoolean(System.Text.Json.JsonElement data, params string[] keys)
    {
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        foreach (var key in keys)
        {
            if (!data.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (value.ValueKind == System.Text.Json.JsonValueKind.False) return false;
            if (bool.TryParse(value.ToString(), out var parsed)) return parsed;
            if (value.ToString() == "1") return true;
        }
        return false;
    }

    private UIElement BuildMessagesPage()
    {
        var root = new Grid { Margin = new Thickness(18), ColumnSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // One master list: the three mailbox modes share the same selection and
        // detail surface rather than navigating between separate inbox/sent pages.
        var left = new Grid { RowSpacing = 10, Background = ThemeBrush("SchoologySurfaceBrush"),
            Margin = new Thickness(8) };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.Children.Add(Text("Messages", 23, true));

        var modes = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var label in new[] { "Inbox", "Unread", "Sent" })
            modes.Items.Add(new SelectorBarItem { Text = label });
        modes.SelectedItem = modes.Items[0];
        Grid.SetRow(modes, 1); left.Children.Add(modes);

        var listHost = new Grid();
        var mailboxFrame = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetRow(mailboxFrame, 2); left.Children.Add(mailboxFrame);
        var status = Text("Loading messages…", 12);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent) };
        AutomationProperties.SetAutomationId(list, "SchoologyMessagesList");
        AutomationProperties.SetName(list, "Messages");
        listHost.Children.Add(list);
        var empty = Text("No messages in this folder.", 13);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.VerticalAlignment = VerticalAlignment.Center;
        empty.Visibility = Visibility.Collapsed;
        listHost.Children.Add(empty);
        var statusOverlay = new Border { Background = ThemeBrush("SchoologySurfaceBrush"),
            Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Top, Child = status };
        listHost.Children.Add(statusOverlay);
        NavigateShellFrame(mailboxFrame, listHost, SlideNavigationTransitionEffect.FromRight);

        var detail = new Grid { Background = ThemeBrush("SchoologySurfaceBrush") };
        var detailFrame = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
        detail.Children.Add(detailFrame);
        // The detail Frame exclusively owns its content; never parent a placeholder in both places.
        Grid.SetColumn(left, 0); Grid.SetColumn(detail, 1);
        root.Children.Add(left); root.Children.Add(detail);

        IReadOnlyList<SchoologyApiRecord> inboxRecords = [];
        IReadOnlyList<SchoologyApiRecord> sentRecords = [];
        var currentMode = "Inbox";
        bool loading = false;
        bool messageSelected = false;

        bool IsUnread(SchoologyApiRecord record)
        {
            var data = record.Data;
            var unread = JsonBoolean(data, "unread", "is_unread", "isUnread");
            if (unread) return true;
            var read = JsonBoolean(data, "read", "is_read", "isRead");
            if (data.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var key in new[] { "message_status", "status", "read_status" })
                    if (data.TryGetProperty(key, out var value))
                    {
                        var state = value.ToString();
                        if (state.Equals("unread", StringComparison.OrdinalIgnoreCase) ||
                            state.Equals("new", StringComparison.OrdinalIgnoreCase)) return true;
                        if (state.Equals("read", StringComparison.OrdinalIgnoreCase)) return false;
                    }
            }
            return !read && JsonBoolean(data, "is_new", "new_message");
        }

        void ShowDetailPlaceholder(string title, string description)
        {
            messageSelected = false;
            var placeholder = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420 };
            placeholder.Children.Add(new FontIcon { Glyph = "\uE715", FontSize = 36,
                Foreground = ThemeBrush("SchoologyMutedTextBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            placeholder.Children.Add(Text(title, 19, true));
            var message = Text(description, 13);
            message.Foreground = ThemeBrush("SchoologyMutedTextBrush");
            placeholder.Children.Add(message);
            NavigateShellFrame(detailFrame, placeholder, SlideNavigationTransitionEffect.FromLeft);
        }

        void RenderMessages()
        {
            list.Items.Clear();
            var records = currentMode switch
            {
                "Sent" => sentRecords,
                "Unread" => inboxRecords.Where(IsUnread).ToArray(),
                _ => inboxRecords
            };
            foreach (var record in records)
            {
                var data = record.Data;
                var id = JsonLong(data, "id", "message_id", "messageId");
                var isUnread = currentMode != "Sent" && IsUnread(record);
                var personName = currentMode == "Sent"
                    ? ProfileValue(data, "recipient_name", "recipient_display_name", "to_name") ?? "To recipient"
                    : ProfileValue(data, "author_name", "user_name", "name_display", "sender_name") ?? "Schoology user";
                var subject = ProfileValue(data, "subject", "message_subject", "title") ?? record.Title;
                var snippet = ProfileValue(data, "body", "message", "text", "message_body") ?? record.Subtitle;
                var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 10, 8, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var marker = new Border { Width = 3, CornerRadius = new CornerRadius(2),
                    Background = isUnread ? ThemeBrush("SchoologyBlueBrush") : new SolidColorBrush(Colors.Transparent) };
                row.Children.Add(marker);
                var textStack = new StackPanel { Spacing = 3 };
                var sender = Text(personName, 13, isUnread);
                var subjectText = Text(subject, 13, isUnread);
                subjectText.MaxLines = 1;
                var preview = Text(StripMarkup(snippet), 11);
                preview.MaxLines = 2;
                preview.Foreground = ThemeBrush("SchoologyMutedTextBrush");
                textStack.Children.Add(sender); textStack.Children.Add(subjectText); textStack.Children.Add(preview);
                Grid.SetColumn(textStack, 1); row.Children.Add(textStack);
                var item = new ListViewItem { Content = row, Tag = record,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(item, $"{personName}: {subject}");
                AttachMessageActivation(item, record);
                list.Items.Add(item);
            }
            empty.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            statusOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            statusOverlay.Child = status;
        }

        async Task LoadMailboxAsync(string mode)
        {
            if (_session is null)
            {
                loading = false;
                status.Text = "Sign in to view messages.";
                RenderMessages();
                ShowDetailPlaceholder("Messages unavailable", "Sign in to load your Schoology messages.");
                return;
            }
            currentMode = mode;
            loading = true;
            status.Text = $"Loading {mode.ToLowerInvariant()}…";
            RenderMessages();
            try
            {
                if (mode == "Sent")
                {
                    using var sent = await _api.GetMessagesAsync(_session, true);
                    sentRecords = SchoologyApiClient.ReadRecords(sent.RootElement, "message", "messages");
                }
                else
                {
                    using var inbox = await _api.GetMessagesAsync(_session, false);
                    inboxRecords = SchoologyApiClient.ReadRecords(inbox.RootElement, "message", "messages");
                }
                loading = false;
                RenderMessages();
                status.Text = recordsCountText(mode);
                if (mode == "Unread" && !inboxRecords.Any(IsUnread))
                    ShowDetailPlaceholder("You're all caught up", "There are no unread messages.");
                else if (mode == "Sent" && sentRecords.Count == 0)
                    ShowDetailPlaceholder("No sent messages", "Messages you send will appear here.");
                else if (mode == "Inbox" && inboxRecords.Count == 0)
                    ShowDetailPlaceholder("Your inbox is empty", "New messages will appear here.");
            }
            catch (Exception ex)
            {
                loading = false;
                status.Text = $"Unable to load {mode.ToLowerInvariant()}: {ex.Message}";
                RenderMessages();
            }
        }

        string recordsCountText(string mode)
        {
            var count = mode == "Sent" ? sentRecords.Count :
                mode == "Unread" ? inboxRecords.Count(IsUnread) : inboxRecords.Count;
            return count == 1 ? "1 message" : $"{count} messages";
        }

        async Task OpenMessageAsync(SchoologyApiRecord record)
        {
            if (_session is null) return;
            var id = JsonLong(record.Data, "id", "message_id", "messageId");
            if (id <= 0)
            {
                await ShowApiErrorAsync("Unable to open message", "Schoology did not return a valid message ID.");
                return;
            }
            messageSelected = true;
            var loadingPanel = new StackPanel { Spacing = 10, Margin = new Thickness(24) };
            loadingPanel.Children.Add(new ProgressRing { IsActive = true, Width = 28, Height = 28 });
            loadingPanel.Children.Add(Text("Loading message…", 13));
            NavigateShellFrame(detailFrame, loadingPanel, SlideNavigationTransitionEffect.FromRight);
            try
            {
                using var document = await _api.GetMessageAsync(_session, id);
                var rootMessage = document.RootElement;
                if (rootMessage.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    rootMessage.TryGetProperty("message", out var nested)) rootMessage = nested;
                var subject = ProfileValue(rootMessage, "subject", "message_subject", "title") ?? record.Title;
                var sender = ProfileValue(rootMessage, "author_name", "user_name", "sender_name", "name_display") ??
                    ProfileValue(record.Data, "author_name", "user_name", "sender_name") ?? "Schoology user";
                var bodyText = ProfileValue(rootMessage, "body", "message", "text", "message_body", "content") ??
                    ProfileValue(record.Data, "body", "message", "text", "message_body", "content") ?? record.Subtitle;
                var detailContent = new Grid { RowSpacing = 0 };
                detailContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                detailContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var header = new StackPanel { Spacing = 6, Margin = new Thickness(22, 18, 22, 14) };
                var backButton = Action("‹ Messages", (_, _) => { left.Visibility = Visibility.Visible; if (root.ActualWidth < 760) detail.Visibility = Visibility.Collapsed; ShowDetailPlaceholder("Select a message", "Choose a message from the list to read it."); });
                AutomationProperties.SetAutomationId(backButton, "MessageDetailBack");
                backButton.Visibility = root.ActualWidth < 760 ? Visibility.Visible : Visibility.Collapsed;
                header.Children.Add(backButton);
                header.Children.Add(Text(subject, 22, true));
                header.Children.Add(Text(sender, 12));
                var created = ProfileValue(rootMessage, "created", "created_at", "timestamp");
                if (!string.IsNullOrWhiteSpace(created)) header.Children.Add(Text(created, 11));
                Grid.SetRow(header, 0); detailContent.Children.Add(header);
                var body = new StackPanel { Spacing = 12 };
                var messageBody = Text(StripMarkup(bodyText), 14);
                messageBody.TextWrapping = TextWrapping.WrapWholeWords;
                body.Children.Add(messageBody);
                var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(22, 14, 22, 22) };
                Grid.SetRow(scroll, 1); detailContent.Children.Add(scroll);
                NavigateShellFrame(detailFrame, detailContent, SlideNavigationTransitionEffect.FromRight);
            }
            catch (Exception ex)
            {
                var error = new StackPanel { Spacing = 10, Padding = new Thickness(24) };
                error.Children.Add(Text($"Unable to load message: {ex.Message}", 13));
                error.Children.Add(Action("Retry", async (_, _) => await OpenMessageAsync(record)));
                NavigateShellFrame(detailFrame, error, SlideNavigationTransitionEffect.FromRight);
            }
        }

        // Activate each concrete row directly; this is reliable for manually
        // supplied ListViewItem containers and preserves the two-screen portrait flow.
        void AttachMessageActivation(ListViewItem item, SchoologyApiRecord record)
        {
            item.Tapped += async (_, _) =>
            {
                await OpenMessageAsync(record);
                if (root.ActualWidth < 760) { left.Visibility = Visibility.Collapsed; detail.Visibility = Visibility.Visible; }
            };
        }
        modes.SelectionChanged += async (_, _) =>
        {
            if (modes.SelectedItem is not SelectorBarItem item) return;
            var mode = item.Text;
            if (mode == currentMode) return;
            ShowDetailPlaceholder("Select a message", "Choose a message from the list to read it.");
            await LoadMailboxAsync(mode);
        };

        // Responsive master/detail: on narrow windows, opening a message replaces
        // the list surface and Back returns to the same selected mailbox.
        root.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 760;
            left.Visibility = narrow && messageSelected ? Visibility.Collapsed : Visibility.Visible;
            detail.Visibility = narrow && !messageSelected ? Visibility.Collapsed : Visibility.Visible;
        };
        ShowDetailPlaceholder("Select a message", "Choose a message from the list to read it.");
        _ = LoadMailboxAsync("Inbox");
        return root;
    }

    private static string StripMarkup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = System.Net.WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " "));
        return System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
    }

    private TreeView BuildGradeDetails(JsonElement section, bool expandPeriods)
    {
        var tree = new TreeView { HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(tree, "SchoologyCourseGradesTree");
        PopulateGradeTreeNodes(tree.RootNodes, section, expandPeriods);
        if (tree.RootNodes.Count == 0) tree.RootNodes.Add(new TreeViewNode { Content = "No grades" });
        return tree;
    }

    private void PopulateGradeTreeNodes(IList<TreeViewNode> nodes, JsonElement section, bool expandPeriods)
    {
        if (section.ValueKind != JsonValueKind.Object) return;
        if (section.TryGetProperty("final_grade", out var finalGrades) && finalGrades.ValueKind == JsonValueKind.Array)
        {
            foreach (var final in finalGrades.EnumerateArray())
            {
                var grade = ProfileValue(final, "override_str", "override_numeric", "grade") ?? "Not posted";
                var finalNode = new TreeViewNode { Content = "Final grade: " + grade };
                var comment = ProfileValue(final, "comment");
                if (!string.IsNullOrWhiteSpace(comment))
                    finalNode.Children.Add(new TreeViewNode { Content = StripMarkup(comment) });
                nodes.Add(finalNode);
            }
        }
        if (section.TryGetProperty("period", out var periods) && periods.ValueKind == JsonValueKind.Array)
        {
            foreach (var period in periods.EnumerateArray())
            {
                var name = ProfileValue(period, "period_title", "title", "name") ?? "Grading period";
                var grade = ProfileValue(period, "override_str", "override_numeric", "grade");
                var periodNode = new TreeViewNode
                {
                    Content = string.IsNullOrWhiteSpace(grade) ? name : $"{name} · {grade}",
                    IsExpanded = expandPeriods
                };
                if (period.TryGetProperty("assignment", out var assignments) && assignments.ValueKind == JsonValueKind.Array)
                {
                    foreach (var assignment in assignments.EnumerateArray())
                    {
                        var title = ProfileValue(assignment, "title", "assignment_title", "name") ??
                            ("Assignment " + (ProfileValue(assignment, "assignment_id", "id") ?? ""));
                        var assignmentGrade = ProfileValue(assignment, "grade", "calculated_grade");
                        var max = ProfileValue(assignment, "max_points");
                        var label = string.IsNullOrWhiteSpace(assignmentGrade) ? title :
                            $"{title} · {(string.IsNullOrWhiteSpace(max) ? assignmentGrade : assignmentGrade + " / " + max)}";
                        var assignmentNode = new TreeViewNode { Content = label };
                        var comment = ProfileValue(assignment, "comment");
                        if (!string.IsNullOrWhiteSpace(comment))
                            assignmentNode.Children.Add(new TreeViewNode { Content = StripMarkup(comment) });
                        periodNode.Children.Add(assignmentNode);
                    }
                }
                if (periodNode.Children.Count == 0)
                    periodNode.Children.Add(new TreeViewNode { Content = "No assignments" });
                nodes.Add(periodNode);
            }
        }
        if (section.TryGetProperty("grading_category", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            var categoryNode = new TreeViewNode { Content = "Grading categories" };
            foreach (var category in categories.EnumerateArray())
            {
                var name = ProfileValue(category, "title", "name") ?? "Category";
                var grade = ProfileValue(category, "grade");
                categoryNode.Children.Add(new TreeViewNode
                {
                    Content = string.IsNullOrWhiteSpace(grade) ? name : $"{name}: {grade}"
                });
            }
            nodes.Add(categoryNode);
        }
    }

    private UIElement BuildGradesPage()
    {
        var root = new Grid { Margin = new Thickness(24), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Text("Grades", 24, true));
        var tree = new TreeView { HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch };
        AutomationProperties.SetAutomationId(tree, "SchoologyGradesTree");
        Grid.SetRow(tree, 1); root.Children.Add(tree);
        var courses = new Dictionary<TreeViewNode, SchoologyEntity>();
        foreach (var course in _courses)
        {
            var node = new TreeViewNode { Content = course.Name, HasUnrealizedChildren = true };
            node.Children.Add(new TreeViewNode { Content = "Expand to load grades" });
            courses[node] = course;
            tree.RootNodes.Add(node);
        }
        if (_courses.Count == 0) tree.RootNodes.Add(new TreeViewNode { Content = "No grades" });
        tree.Expanding += async (_, args) =>
        {
            if (!courses.TryGetValue(args.Node, out var course) || !args.Node.HasUnrealizedChildren) return;
            args.Node.HasUnrealizedChildren = false;
            args.Node.Children.Clear();
            args.Node.Children.Add(new TreeViewNode { Content = "Loading grades…" });
            try
            {
                if (_session is null) throw new InvalidOperationException("Sign in to view grades.");
                using var document = await _api.GetUserGradesAsync(_session);
                var sections = SchoologyApiClient.ReadRecords(document.RootElement, "section", "sections");
                var section = sections.FirstOrDefault(record =>
                    JsonLong(record.Data, "section_id", "sectionId", "id") == course.Id);
                args.Node.Children.Clear();
                if (section is null) args.Node.Children.Add(new TreeViewNode { Content = "No grades" });
                else PopulateGradeTreeNodes(args.Node.Children, section.Data, false);
                if (args.Node.Children.Count == 0) args.Node.Children.Add(new TreeViewNode { Content = "No grades" });
            }
            catch (Exception ex)
            {
                args.Node.Children.Clear();
                args.Node.Children.Add(new TreeViewNode { Content = $"Unable to load grades: {ex.Message}" });
                args.Node.IsExpanded = false;
                args.Node.HasUnrealizedChildren = true;
            }
        };
        return root;
    }

    private static string NormalizeSchoologyWebUrl(string value)
    {
        var raw = value.Trim();
        if (raw.StartsWith("//", StringComparison.Ordinal)) raw = "https:" + raw;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme == Uri.UriSchemeHttp && IsSchoologyWebHost(absolute.Host))
                return new UriBuilder(absolute) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.AbsoluteUri;
            return absolute.AbsoluteUri;
        }
        return new Uri(new Uri("https://app.schoology.com/"), raw.TrimStart('/')).AbsoluteUri;
    }

    private void UpdateSharedWebViewTheme()
    {
        if (_sharedWebView is null) return;
        try
        {
            var core = _sharedWebView.CoreWebView2;
            if (core is null) return;
            core.Profile.PreferredColorScheme =
                _root.ActualTheme == ElementTheme.Dark
                    ? CoreWebView2PreferredColorScheme.Dark
                    : CoreWebView2PreferredColorScheme.Light;
        }
        catch (InvalidOperationException) { }
    }

    private static bool IsSchoologyWebHost(string host)
    {
        host = host.ToLowerInvariant();
        return host == "schoology.com" || host.EndsWith(".schoology.com") ||
            host == "schoologyca.com" || host.EndsWith(".schoologyca.com") || host == "lms.lausd.net";
    }

    private async Task OpenSharedWebViewAsync(string rawUrl, string title, bool allowLtiExternal = false,
        System.Action? onBack = null)
    {
        try
        {

        if (_session is null) throw new InvalidOperationException("Sign in to open Schoology web content.");
        if (!Uri.TryCreate(NormalizeSchoologyWebUrl(rawUrl), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Only secure HTTPS links can be opened in the shared Schoology view.");
        if (!IsSchoologyWebHost(uri.Host) && !allowLtiExternal)
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
            return;
        }

        _sharedWebView ??= new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinWidth = 400,
            MinHeight = 320
        };
        var webViewDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Schoology", "WebView2");
        Directory.CreateDirectory(webViewDataFolder);
        _sharedWebViewEnvironment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null, webViewDataFolder, new CoreWebView2EnvironmentOptions());
        await _sharedWebView.EnsureCoreWebView2Async(_sharedWebViewEnvironment);
        UpdateSharedWebViewTheme();
        _sharedWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _sharedWebView.CoreWebView2.Settings.IsWebMessageEnabled = false;
        _sharedWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        if (!_sharedWebViewInitialized || _sharedWebViewSessionUserId != _session.UserId)
        {
            _sharedWebView.CoreWebView2.CookieManager.DeleteAllCookies();
            _sharedWebViewInitialized = false;
            _sharedWebViewSessionUserId = _session.UserId;
        }
        // Refresh the OAuth-backed browser session before each embedded launch so
        // the persistent WebView never silently reuses an expired SESS cookie.
        var cookies = await _auth.GetWebSessionCookiesAsync(_session);
        foreach (var item in cookies)
        {
            var cookie = _sharedWebView.CoreWebView2.CookieManager.CreateCookie(
                item.Name, item.Value, item.Domain, item.Path);
            cookie.IsHttpOnly = item.HttpOnly;
            cookie.IsSecure = item.Secure;
            _sharedWebView.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
        }
        var installedCookies = await _sharedWebView.CoreWebView2.CookieManager.GetCookiesAsync(_auth.WebSessionUrl);
        if (!installedCookies.Any(cookie => cookie.Name.Equals("SESS", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cookie.Value)))
            throw new InvalidOperationException("The shared Schoology WebView did not receive a usable SESS cookie. Sign in again and retry.");
        _sharedWebViewInitialized = true;
        _sharedWebViewAllowLti = allowLtiExternal;
        if (!_sharedWebViewInitialized) throw new InvalidOperationException("The Schoology browser session could not be initialized.");

        if (_sharedWebViewStatus is null)
        {
            _sharedWebView.CoreWebView2.NavigationStarting += async (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var destination)) return;
                if (destination.Scheme != Uri.UriSchemeHttps)
                {
                    args.Cancel = true;
                    return;
                }
                if (!_sharedWebViewAllowLti && !IsSchoologyWebHost(destination.Host))
                {
                    args.Cancel = true;
                    await Windows.System.Launcher.LaunchUriAsync(destination);
                    return;
                }
                if (_sharedWebViewStatus is not null) _sharedWebViewStatus.Text = "Loading…";
            };
            _sharedWebView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (_sharedWebViewStatus is not null)
                    _sharedWebViewStatus.Text = args.IsSuccess ? " " : "Unable to load this page. Use Reload to try again.";
            };
            _sharedWebView.CoreWebView2.NewWindowRequested += async (_, args) =>
            {
                args.Handled = true;
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var popup) ||
                    popup.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(popup.UserInfo)) return;
                if (_sharedWebViewAllowLti || IsSchoologyWebHost(popup.Host))
                    _sharedWebView.CoreWebView2.Navigate(popup.AbsoluteUri);
                else
                    await Windows.System.Launcher.LaunchUriAsync(popup);
            };
        }

        var host = new Grid { RowSpacing = 8, Margin = new Thickness(12) };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var toolbar = new Grid { ColumnSpacing = 10 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = Action("‹ Back", (_, _) => (onBack ?? (() => ShowHome(_section)))());
        back.HorizontalAlignment = HorizontalAlignment.Left;
        toolbar.Children.Add(back);
        var heading = Text(title, 16, true);
        Grid.SetColumn(heading, 1); toolbar.Children.Add(heading);
        _sharedWebViewStatus = Text("Loading…", 11);
        _sharedWebViewStatus.Foreground = ThemeBrush("SchoologyMutedTextBrush");
        Grid.SetColumn(_sharedWebViewStatus, 2); toolbar.Children.Add(_sharedWebViewStatus);
        var reload = Action("Reload", (_, _) => _sharedWebView?.CoreWebView2?.Reload());
        reload.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(reload, 3); toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(reload);
        host.Children.Add(toolbar);

        if (_sharedWebView.Parent is Panel oldParent) oldParent.Children.Remove(_sharedWebView);
        Grid.SetRow(_sharedWebView, 1);
        host.Children.Add(_sharedWebView);
        Grid.SetRow(host, 0);
        if (_sectionFrame is not null) NavigateMainSection(host, title);
        else Reset(BuildApplicationShell(title, host));
        _sharedWebView.CoreWebView2.Navigate(uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            await ShowApiErrorAsync("Unable to open Schoology web content", ex.Message);
        }
    }
    private UIElement BuildProfilePage()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        panel.Children.Add(Text("My Profile", 24, true));
        var status = Text("Loading your Schoology profile…", 13);
        panel.Children.Add(status);
        _ = LoadProfileAsync(panel, status);
        return panel;
    }

    private async Task LoadProfileAsync(StackPanel panel, TextBlock status)
    {
        try
        {
            if (_session is null) throw new InvalidOperationException("Sign in to view your profile.");
            using var document = await _api.GetProfileAsync(_session);
            var user = document.RootElement;
            panel.Children.Remove(status);
            var name = ProfileValue(user, "name_display", "nameDisplay", "display_name", "name") ?? "Schoology user";
            panel.Children.Add(Surface(Text(name, 20, true)));
            AddProfileField(panel, "Username", ProfileValue(user, "username"));
            AddProfileField(panel, "Email", ProfileValue(user, "primary_email", "email"));
            AddProfileField(panel, "School", ProfileValue(user, "school_title", "school_name"));
            AddProfileField(panel, "User ID", ProfileValue(user, "id"));
        }
        catch (Exception ex) { status.Text = $"Unable to load profile: {ex.Message}"; }
    }

    private void AddProfileField(StackPanel panel, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var row = new StackPanel { Spacing = 3 };
        row.Children.Add(Text(label, 12, true));
        row.Children.Add(Text(value, 14));
        panel.Children.Add(Surface(row));
    }

    private static string? ProfileValue(System.Text.Json.JsonElement user, params string[] keys)
    {
        if (user.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        foreach (var key in keys)
            if (user.TryGetProperty(key, out var value) &&
                value.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number)
                return value.ToString();
        return null;
    }

    private void ShowSearch()
    {
        _page = "Search";
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        panel.Children.Add(Text("Search Schoology", 22, true));
        var searchBox = new AutoSuggestBox { PlaceholderText = "Search courses, groups, people, and updates" };
        panel.Children.Add(searchBox);
        panel.Children.Add(Action("Back", (_, _) => ShowHome()));
        if (_sectionFrame is not null) NavigateMainSection(panel, "Search");
        else Reset(BuildApplicationShell("Recent Activity", panel));
    }
    private async void ShowCreateMenu()
    {
        var dialog = new ContentDialog { Title = "Create", Content = "Choose what you want to create. Actions will be connected to Schoology permissions and APIs in a later phase.", PrimaryButtonText = "Post an update", SecondaryButtonText = "Create event", CloseButtonText = "Cancel", XamlRoot = _root.XamlRoot };
        var result = await dialog.ShowAsync(); if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary) { var info = new ContentDialog { Title = result == ContentDialogResult.Primary ? "Post an update" : "Create event", Content = "This action is not connected until Schoology authentication and permissions are implemented.", CloseButtonText = "OK", XamlRoot = _root.XamlRoot }; await info.ShowAsync(); }
    }
}

