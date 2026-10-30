using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using System.Web;
using System.Text;
using System.Text.Json;
using System.Collections.ObjectModel;
using Windows.Media.Playback;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;
using Windows.System;
using Windows.Storage;

namespace Schoology.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly Grid _root;
    private readonly DispatcherTimer _splashTimer;
    private string _page = "login";
    private string _lastRenderedPage = "splash";
    private string _section = "Recent Activity";
    private NavigationView? _appNavigation;
    private SplitView? _homeSplitView;
    private Frame? _sectionFrame;
    private string _selectedEntity = string.Empty;
    private long _selectedEntityId;
    private readonly SchoologyAuthService _auth = new(false);
    private readonly SchoologyApiClient _api;
    private readonly ObservableCollection<SchoologyEntity> _courses = new();
    private readonly ObservableCollection<SchoologyEntity> _groups = new();
    private readonly Dictionary<long, JsonElement> _userProfileCache = new();
    private SchoologyAuthService.Session? _session;
    private QrCameraScanner? _qrScanner;
    private MediaPlayer? _qrPlayer;
    private static readonly string WindowStatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schoology", "window-state.json");
    private static readonly string ReleaseNotesStatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schoology", "release-notes-version.txt");
    private static readonly string AppSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schoology", "settings.json");
    private static readonly HttpClient ReleaseNotesClient = new();
    private bool _restoringWindowState;

    public MainWindow()
    {
        App.WriteStartupLog("MainWindow constructor entered.");
        InitializeComponent();
        App.WriteStartupLog("MainWindow.InitializeComponent completed.");

        _root = Root;
        _api = new SchoologyApiClient(_auth);
        _root.RequestedTheme = ReadAppSetting("AppTheme", "System default") switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
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
                await CheckAndShowReleaseNotesAsync();
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

        var animationsEnabled = AnimationsEnabled
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
                Tag = entry.Name == "Upcoming" ? "Calendar" : entry.Name, Icon = new SymbolIcon(entry.Icon) };
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
            if (rootTarget == "Sign out") { _auth.SignOut(); _session = null; _sectionFrame = null; ShowLogin(); }
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
        else
        {
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
            panel.Children.Add(Text(section, 26, true));
            panel.Children.Add(Surface(Text($"The {section.ToLowerInvariant()} view is ready for its Schoology data provider. No sample records are presented as live account data.")));
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
        var animationsEnabled = AnimationsEnabled
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
        _homeSplitView = null;
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
            if (records.Count == 0) { panel.Children.Add(Surface(Text("No recent activity was returned by Schoology."))); return; }
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
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(list, "HomeCourseDashboardList");
        if (_courses.Count == 0) list.Items.Add(new ListViewItem { Content = Text("No courses were returned for this account.", 13) });
        foreach (var course in _courses)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 10, 8, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var image = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(4),
                Background = ThemeBrush("SchoologyBlueDarkBrush"),
                Child = new TextBlock { Text = course.Name.Length > 0 ? course.Name[..1].ToUpperInvariant() : "C",
                    FontSize = 22, Foreground = Brush("#FFFFFFFF"), HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center } };
            if (!string.IsNullOrWhiteSpace(course.ImageUrl))
            {
                try { image.Child = new Image { Source = new BitmapImage(new Uri(NormalizeSchoologyImageUrl(course.ImageUrl))),
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill }; } catch { }
            }
            row.Children.Add(image);
            var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(Text(course.Name, 14, true));
            if (!string.IsNullOrWhiteSpace(course.Subtitle)) labels.Children.Add(Text(course.Subtitle, 12));
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var item = new ListViewItem { Content = row, Tag = course,
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            item.Tapped += (_, _) => ShowThreePane("Courses", course);
            list.Items.Add(item);
        }
        return list;
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
                list.Items.Add(new ListViewItem { Content = Surface(Text($"Unable to load calendar: {ex.Message}")) });
            });
        }
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
            var schoolName = ProfileValue(selected.Data, "school_title", "school_name", "school");
            var schoolId = ProfileValue(selected.Data, "school_id", "schoolId");
            if (!string.IsNullOrWhiteSpace(schoolName) && long.TryParse(schoolId, out var schoolNumber) && schoolNumber > 0)
                compactLabels.Children.Add(Action($"School: {schoolName}", (_, _) => ShowThreePane("School Profile")));
        }
        Grid.SetColumn(compactLabels, 1); compactIdentity.Children.Add(compactLabels);
        var compactSelector = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var tab in tabs.Concat(["Upcoming"]))
            compactSelector.Items.Add(new SelectorBarItem { Text = tab });
        // In portrait the tab selector precedes the entity infobox.
        Grid.SetRow(compactSelector, 0); narrow.Children.Add(compactSelector);
        Grid.SetRow(compactIdentity, 1); narrow.Children.Add(compactIdentity);
        var compactFrame = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
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
            var schoolName = ProfileValue(entityInfo.Data, "school_title", "school_name", "school");
            if (!string.IsNullOrWhiteSpace(schoolName))
            {
                var school = Text(schoolName, 12);
                school.Foreground = ThemeBrush("SchoologyBlueBrush");
                infobox.Children.Add(school);
            }
        }
        if (showInfobox) heading.Children.Add(Surface(infobox, new Thickness(12)));
        heading.Children.Add(Text(tab, 17, true));
        Grid.SetRow(heading, 0); layout.Children.Add(heading);
        var body = new StackPanel { Spacing = 14 };
        UIElement bodyContent = tab is "Materials" or "Members" or "Discussions" or "Grades" or "Resources"
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
                    body.Children.Add(Surface(Text("No grades were returned for this course.")));
                else body.Children.Add(BuildGradeDetails(selectedSection.Data, expandPeriods: true));
                return;
            }
            if (records.Count == 0)
            {
                body.Children.Add(Surface(Text($"No {tab.ToLowerInvariant()} were returned by Schoology.")));
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
                row.Children.Add(new FontIcon { Glyph = tab == "Materials" ? MaterialGlyph(record.Data) : "\uE8A5",
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
                row.Children.Add(new FontIcon { Glyph = MaterialGlyph(record.Data), FontSize = 23,
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
            var rawUrl = ProfileValue(record.Data, "web_url", "webUrl", "url", "link", "download_url", "downloadUrl", "file_url", "fileUrl");
            if (!string.IsNullOrWhiteSpace(rawUrl))
            {
                if (!rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    rawUrl = "https://app.schoology.com/" + rawUrl.TrimStart('/');
                if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                {
                    var browser = new WebView2 { MinWidth = 680, MinHeight = 520 };
                    try
                    {
                        await browser.EnsureCoreWebView2Async();
                        browser.CoreWebView2.Navigate(uri.AbsoluteUri);
                        var dialog = new ContentDialog { Title = title, Content = browser,
                            CloseButtonText = "Close", XamlRoot = _root.XamlRoot };
                        await dialog.ShowAsync();
                        return;
                    }
                    catch (Exception ex) { App.WriteStartupLog("Embedded material navigation failed.", ex); }
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
        if (initialRecords.Count == 0) list.Items.Add(new ListViewItem { Content = Text("This course has no materials.", 13) });
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
                list.Items.Add(new ListViewItem { Content = row, Tag = person.Id });
            }
        }
        filters.SelectionChanged += (_, _) => Render();
        search.TextChanged += (_, _) => Render();
        Render();
        root.Children.Add(filters); root.Children.Add(search); root.Children.Add(list);
        if (people.Count == 0) root.Children.Add(Text("The enrollment response contained no recognizable user IDs.", 12));
        return root;
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

    private static string MaterialGlyph(System.Text.Json.JsonElement data)
    {
        var type = ProfileValue(data, "type", "document_type", "mime_type", "mimeType", "extension")?.ToLowerInvariant() ?? "";
        if (type.Contains("folder")) return "\uE8B7";
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
        var created = ProfileValue(update.Data, "created", "created_at", "timestamp");
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
            JsonElement attachments = default;
            foreach (var key in new[] { "attachments", "attachment", "files" })
                if (update.Data.TryGetProperty(key, out attachments)) break;
            var values = attachments.ValueKind == JsonValueKind.Array ? attachments.EnumerateArray().ToArray() :
                attachments.ValueKind == JsonValueKind.Object ? new[] { attachments } : Array.Empty<JsonElement>();
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
            commentRealm = realm switch { "group" or "groups" => "Groups", "school" or "schools" => "School Profile", _ => "Courses" };
            commentRealmId = JsonLong(update.Data, "realm_id", "realmId", "section_id", "sectionId", "group_id", "groupId");
        }
        var commentsPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var commentStatus = Text("Comments have not been loaded.", 12);
        var commentList = new StackPanel { Spacing = 6 };
        var commentsLoaded = false;
        var commentInput = new TextBox { PlaceholderText = "Write a comment…", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
        AutomationProperties.SetAutomationId(commentInput, $"UpdateCommentInput_{updateId}");
        var composer = new StackPanel { Spacing = 6 };
        Button send = null!;
        send = Action("Post comment", async (_, _) =>
        {
            var value = commentInput.Text.Trim();
            if (updateId <= 0 || _session is null || value.Length == 0) return;
            send.IsEnabled = false;
            try
            {
                using var posted = await _api.AddUpdateCommentAsync(_session, kind, entityId, updateId, value);
                commentInput.Text = string.Empty;
                commentsLoaded = false;
                await LoadUpdateCommentsAsync(kind, entityId, updateId, commentsPanel, commentStatus, commentList);
                commentsLoaded = true;
            }
            catch (Exception ex) { await ShowApiErrorAsync("Unable to post comment", ex.Message); }
            finally { send.IsEnabled = true; }
        });
        AutomationProperties.SetAutomationId(send, $"UpdateCommentSend_{updateId}");
        composer.Children.Add(commentInput); composer.Children.Add(send);
        commentsPanel.Children.Add(commentStatus); commentsPanel.Children.Add(commentList); commentsPanel.Children.Add(composer);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var liked = JsonBoolean(update.Data, "user_liked", "liked", "user_like_action");
        var likeCount = JsonLong(update.Data, "like_count", "likes_count", "likes");
        Button likeButton = null!;
        likeButton = Action(liked ? $"Unlike ({likeCount})" : $"Like ({likeCount})", async (_, _) =>
        {
            if (updateId <= 0 || _session is null) return;
            likeButton.IsEnabled = false;
            try
            {
                using var result = await _api.SetUpdateLikeAsync(_session, updateId, !liked);
                liked = !liked; likeCount = Math.Max(0, likeCount + (liked ? 1 : -1));
                likeButton.Content = liked ? $"Unlike ({likeCount})" : $"Like ({likeCount})";
            }
            catch (Exception ex) { await ShowApiErrorAsync("Unable to update like", ex.Message); }
            finally { likeButton.IsEnabled = true; }
        });
        AutomationProperties.SetAutomationId(likeButton, $"UpdateLike_{updateId}");
        var commentsButton = Action("Comments", async (_, _) =>
        {
            commentsPanel.Visibility = commentsPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
            if (commentsPanel.Visibility == Visibility.Visible && !commentsLoaded)
            {
                await LoadUpdateCommentsAsync(kind, entityId, updateId, commentsPanel, commentStatus, commentList);
                commentsLoaded = true;
            }
        });
        AutomationProperties.SetAutomationId(commentsButton, $"UpdateComments_{updateId}");
        if (commentRealmId <= 0) commentsButton.IsEnabled = false;
        actions.Children.Add(likeButton); actions.Children.Add(commentsButton);
        card.Children.Add(actions); card.Children.Add(commentsPanel);
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

    private UIElement BuildGradeDetails(JsonElement section, bool expandPeriods)
    {
        var content = new StackPanel { Spacing = 8 };
        if (section.ValueKind != JsonValueKind.Object) return content;
        if (section.TryGetProperty("period", out var periods) && periods.ValueKind == JsonValueKind.Array)
        {
            foreach (var period in periods.EnumerateArray())
            {
                var periodName = ProfileValue(period, "period_title", "title", "name") ?? "Grading period";
                var periodGrade = ProfileValue(period, "override_str", "override_numeric", "grade");
                var periodContent = new StackPanel { Spacing = 6, Margin = new Thickness(10) };
                if (!string.IsNullOrWhiteSpace(periodGrade))
                    periodContent.Children.Add(Text("Period grade: " + periodGrade, 13, true));
                if (period.TryGetProperty("assignment", out var assignments) && assignments.ValueKind == JsonValueKind.Array)
                {
                    foreach (var assignment in assignments.EnumerateArray())
                    {
                        var assignmentId = ProfileValue(assignment, "assignment_id", "id") ?? "Assignment";
                        var grade = ProfileValue(assignment, "grade", "calculated_grade");
                        var max = ProfileValue(assignment, "max_points");
                        var line = new StackPanel { Spacing = 2 };
                        line.Children.Add(Text("Assignment " + assignmentId, 13, true));
                        if (!string.IsNullOrWhiteSpace(grade))
                            line.Children.Add(Text(string.IsNullOrWhiteSpace(max) ? grade : grade + " / " + max, 12));
                        var comment = ProfileValue(assignment, "comment");
                        if (!string.IsNullOrWhiteSpace(comment)) line.Children.Add(Text(StripMarkup(comment), 12));
                        periodContent.Children.Add(Surface(line));
                    }
                }
                content.Children.Add(new Expander { Header = string.IsNullOrWhiteSpace(periodGrade) ? periodName : periodName + " · " + periodGrade,
                    IsExpanded = expandPeriods, Content = periodContent });
            }
        }
        if (section.TryGetProperty("final_grade", out var finalGrades) && finalGrades.ValueKind == JsonValueKind.Array)
        {
            foreach (var final in finalGrades.EnumerateArray())
            {
                var grade = ProfileValue(final, "override_str", "override_numeric", "grade");
                var comment = ProfileValue(final, "comment");
                var finalPanel = new StackPanel { Spacing = 3 };
                finalPanel.Children.Add(Text("Final grade: " + (grade ?? "Not posted"), 13, true));
                if (!string.IsNullOrWhiteSpace(comment)) finalPanel.Children.Add(Text(StripMarkup(comment), 12));
                content.Children.Add(Surface(finalPanel));
            }
        }
        if (section.TryGetProperty("grading_category", out var categories) && categories.ValueKind == JsonValueKind.Array)
        {
            foreach (var category in categories.EnumerateArray())
            {
                var name = ProfileValue(category, "title", "name") ?? "Category";
                var grade = ProfileValue(category, "grade");
                content.Children.Add(Text(string.IsNullOrWhiteSpace(grade) ? name : name + ": " + grade, 12));
            }
        }
        if (content.Children.Count == 0) content.Children.Add(Text("No grade details were returned for this course.", 13));
        return content;
    }

    private UIElement BuildGradesPage()
    {
        var root = new Grid { Margin = new Thickness(24), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Text("Grades", 24, true));
        var list = new ListView { SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(list, "SchoologyGradesCourseList");
        Grid.SetRow(list, 1); root.Children.Add(list);
        foreach (var course in _courses)
        {
            var content = new StackPanel { Spacing = 8, Margin = new Thickness(12, 8, 12, 12) };
            var expander = new Expander { Header = course.Name, IsExpanded = false, Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            bool loaded = false;
            async Task LoadGradesAsync()
            {
                if (loaded || _session is null) return;
                loaded = true;
                content.Children.Clear();
                content.Children.Add(Text("Loading grades…", 13));
                try
                {
                    using var document = await _api.GetUserGradesAsync(_session);
                    var sections = SchoologyApiClient.ReadRecords(document.RootElement, "section", "sections");
                    var section = sections.FirstOrDefault(record =>
                        JsonLong(record.Data, "section_id", "sectionId", "id") == course.Id);
                    content.Children.Clear();
                    if (section is null) content.Children.Add(Text("No grades were returned for this course.", 13));
                    else content.Children.Add(BuildGradeDetails(section.Data, expandPeriods: false));
                }
                catch (Exception ex)
                {
                    loaded = false;
                    content.Children.Clear();
                    content.Children.Add(Text($"Unable to load grades: {ex.Message}", 13));
                    content.Children.Add(Action("Retry", async (_, _) => await LoadGradesAsync()));
                }
            }
            expander.Expanding += async (_, _) => await LoadGradesAsync();
            list.Items.Add(new ListViewItem { Content = expander, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (_courses.Count == 0) list.Items.Add(new ListViewItem { Content = Surface(Text("No courses are available for grades.")) });
        return root;
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

