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
    private ListView? _entityListView;
    private string _selectedEntity = string.Empty;
    private long _selectedEntityId;
    private readonly SchoologyAuthService _auth = new(false);
    private readonly SchoologyApiClient _api;
    private readonly ObservableCollection<SchoologyEntity> _courses = new();
    private readonly ObservableCollection<SchoologyEntity> _groups = new();
    private SchoologyAuthService.Session? _session;
    private QrCameraScanner? _qrScanner;
    private MediaPlayer? _qrPlayer;
    private static readonly string WindowStatePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Schoology", "window-state.json");
    private bool _restoringWindowState;

    public MainWindow()
    {
        App.WriteStartupLog("MainWindow constructor entered.");
        InitializeComponent();
        App.WriteStartupLog("MainWindow.InitializeComponent completed.");

        _root = Root;
        _api = new SchoologyApiClient(_auth);
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
                App.WriteStartupLog("Startup session restoration sequence completed.");
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
        _entityListView = null;
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
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var nativeTitleBar = AppWindow.TitleBar;
        var titleBar = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(16, 0, Math.Max(12, nativeTitleBar.RightInset + 12), 0),
            MinHeight = 48
        };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = Text("schoology", 17, true);
        brand.Foreground = ThemeBrush("SchoologyBlueBrush");
        titleBar.Children.Add(brand);
        var caption = Text(section == "Recent Activity" ? "Home" : section, 13, true);
        caption.Foreground = ThemeBrush("SchoologyMutedTextBrush");
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(caption, 1);
        titleBar.Children.Add(caption);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var searchButton = Action("Search", (_, _) => ShowSearch());
        AutomationProperties.SetAutomationId(searchButton, "TitleBarSearchButton");
        var createButton = Action("＋ Create", (_, _) => ShowCreateMenu(), true);
        AutomationProperties.SetAutomationId(createButton, "TitleBarCreateButton");
        commands.Children.Add(searchButton);
        commands.Children.Add(createButton);
        Grid.SetColumn(commands, 2);
        titleBar.Children.Add(commands);
        shell.Children.Add(titleBar);
        SetTitleBar(titleBar);
        // AppWindowTitleBar exposes inset values, not a Changed event.
        titleBar.Padding = new Thickness(16, 0, Math.Max(12, nativeTitleBar.RightInset + 12), 0);

        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.LeftMinimal,
            IsPaneOpen = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = true,
            OpenPaneLength = 304,
            CompactPaneLength = 64,
            PaneBackground = Brush("#FF002137"),
            Foreground = Brush("#FFFFFFFF"),
            CompactModeThresholdWidth = 760,
            ExpandedModeThresholdWidth = 1120,
            Background = new SolidColorBrush(Colors.Transparent),
            Content = null
        };
        var animationsEnabled = new UISettings().AnimationsEnabled;
        var initialTransition = new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight };
        var sectionFrame = new Frame
        {
            Background = new SolidColorBrush(Colors.Transparent),
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
            ("School Profile", Symbol.Home)
        };
        foreach (var entry in items)
        {
            if (entry.Name == "Home")
                nav.MenuItems.Add(new NavigationViewItemSeparator());
            var item = new NavigationViewItem { Content = entry.Name == "Upcoming" ? "Calendar" : entry.Name, Tag = entry.Name, Icon = new SymbolIcon(entry.Icon) };
            if (entry.Name is "Courses" or "Groups") item.IsExpanded = false;
            nav.MenuItems.Add(item);
            if (entry.Name == (section == "Recent Activity" ? "Home" : section)) nav.SelectedItem = item;
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
        Grid.SetRow(nav, 1);
        shell.Children.Add(nav);
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
        var animationsEnabled = new UISettings().AnimationsEnabled;
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
        var split = new SplitView
        {
            DisplayMode = SplitViewDisplayMode.Inline, IsPaneOpen = true, OpenPaneLength = 620,
            CompactPaneLength = 0, PanePlacement = SplitViewPanePlacement.Left,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        var left = new Grid { Margin = new Thickness(20, 14, 12, 16), RowSpacing = 12 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Text("Home", 23, true));
        var hide = Action("Hide pane", (_, _) => split.IsPaneOpen = !split.IsPaneOpen);
        hide.Padding = new Thickness(8, 4, 8, 4);
        Grid.SetColumn(hide, 1); header.Children.Add(hide);
        left.Children.Add(header);

        var selector = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        var recent = new SelectorBarItem { Text = "Recent Activity" };
        var dashboard = new SelectorBarItem { Text = "Course Dashboard" };
        selector.Items.Add(recent); selector.Items.Add(dashboard); selector.SelectedItem = recent;
        Grid.SetRow(selector, 1); left.Children.Add(selector);
        var leftFrame = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetRow(leftFrame, 2); left.Children.Add(leftFrame);
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is not SelectorBarItem selected) return;
            NavigateShellFrame(leftFrame, selected == dashboard ? BuildCourseDashboardContent() : BuildRecentActivityContent(),
                SlideNavigationTransitionEffect.FromRight);
        };
        NavigateShellFrame(leftFrame, upcomingFirst ? BuildCourseDashboardContent() : BuildRecentActivityContent(),
            SlideNavigationTransitionEffect.FromRight);
        if (upcomingFirst) selector.SelectedItem = dashboard;
        split.Pane = left;

        var right = new Grid { Margin = new Thickness(14, 14, 20, 16), RowSpacing = 10 };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.Children.Add(Text("Upcoming", 23, true));
        var upcomingFrame = new Frame { Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetRow(upcomingFrame, 1); right.Children.Add(upcomingFrame);
        NavigateShellFrame(upcomingFrame, BuildUpcomingContent(), SlideNavigationTransitionEffect.FromRight);
        split.Content = right;
        split.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 900;
            split.DisplayMode = narrow ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
            if (narrow && split.IsPaneOpen) split.IsPaneOpen = false;
            else if (!narrow && !split.IsPaneOpen) split.IsPaneOpen = true;
        };
        _homeSplitView = split;
        return split;
    }

    private UIElement BuildRecentActivityContent()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(4, 2, 10, 8) };
        var status = Text("Loading recent Schoology activity…", 13);
        panel.Children.Add(status);
        _ = LoadRecentActivityAsync(panel, status);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task LoadRecentActivityAsync(StackPanel panel, TextBlock status)
    {
        try
        {
            if (_session is null) throw new InvalidOperationException("Sign in to view recent activity.");
            using var document = await _auth.GetApiJsonAsync(_session, "recent",
                new Dictionary<string, string> { ["start"] = "0", ["limit"] = "30", ["with_attachments"] = "TRUE", ["richtext"] = "1" });
            var records = SchoologyApiClient.ReadRecords(document.RootElement, "update", "updates", "update_list");
            panel.Children.Remove(status);
            if (records.Count == 0) { panel.Children.Add(Surface(Text("No recent activity was returned by Schoology."))); return; }
            foreach (var record in records)
            {
                var card = new StackPanel { Spacing = 5 };
                card.Children.Add(Text(ProfileValue(record.Data, "display_name", "author_name", "user_name", "name_display") ?? "Schoology", 12, true));
                card.Children.Add(Text(record.Title, 15, true));
                if (!string.IsNullOrWhiteSpace(record.Subtitle)) card.Children.Add(Text(record.Subtitle, 13));
                var created = ProfileValue(record.Data, "created", "created_at", "timestamp");
                if (!string.IsNullOrWhiteSpace(created)) card.Children.Add(Text(created, 11));
                panel.Children.Add(Surface(card));
            }
        }
        catch (Exception ex) { status.Text = $"Unable to load recent activity: {ex.Message}"; }
    }

    private UIElement BuildCourseDashboardContent()
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true,
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
            list.Items.Add(new ListViewItem { Content = row, Tag = course });
        }
        list.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is ListViewItem { Tag: SchoologyEntity course }) ShowThreePane("Courses", course);
        };
        return list;
    }

    private UIElement BuildUpcomingContent()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(2) };
        var status = Text("Loading upcoming assignments and events…", 13);
        panel.Children.Add(status);
        _ = LoadUpcomingAsync(panel, status);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private async Task LoadUpcomingAsync(StackPanel panel, TextBlock status)
    {
        try
        {
            if (_session is null) throw new InvalidOperationException("Sign in to view upcoming items.");
            using var document = await _auth.GetApiJsonAsync(_session, $"users/{_session.UserId}/events",
                new Dictionary<string, string> { ["start_date"] = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ["start"] = "0", ["limit"] = "50" });
            var records = SchoologyApiClient.ReadRecords(document.RootElement, "event", "events");
            panel.Children.Remove(status);
            if (records.Count == 0) { panel.Children.Add(Surface(Text("Nothing upcoming was returned by Schoology."))); return; }
            foreach (var record in records.OrderBy(item => ProfileValue(item.Data, "start", "start_time", "date") ?? ""))
            {
                var item = new StackPanel { Spacing = 4 };
                var when = ProfileValue(record.Data, "start", "start_time", "date", "due");
                if (!string.IsNullOrWhiteSpace(when)) item.Children.Add(Text(when, 11, true));
                item.Children.Add(Text(record.Title, 14, true));
                var course = ProfileValue(record.Data, "section_title", "course_title", "section_name");
                if (!string.IsNullOrWhiteSpace(course)) item.Children.Add(Text(course, 12));
                panel.Children.Add(Surface(item, new Thickness(12)));
            }
        }
        catch (Exception ex) { status.Text = $"Unable to load upcoming items: {ex.Message}"; }
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
        var split = new SplitView
        {
            DisplayMode = SplitViewDisplayMode.Inline,
            IsPaneOpen = true,
            OpenPaneLength = 276,
            CompactPaneLength = 0,
            PanePlacement = SplitViewPanePlacement.Left,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        IEnumerable<SchoologyEntity> entries = kind == "Courses" ? _courses :
            kind == "Groups" ? _groups : GetSchoolProfileEntities();
        var master = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        master.Children.Add(Text(kind == "School Profile" ? "School" : $"My {kind}", 20, true));
        var entitySearch = new TextBox { PlaceholderText = $"Search {kind.ToLowerInvariant()}", Margin = new Thickness(0, 4, 0, 8) };
        master.Children.Add(entitySearch);
        var selectedName = Text(initialEntity?.Name ?? "Select an item", 14, true);
        var selector = new SelectorBar { Margin = new Thickness(0, 0, 0, 12) };
        var tabs = kind == "School Profile" ? new[] { "Updates", "Info", "Upcoming", "Resources", "Discussions" } :
            kind == "Groups" ? new[] { "Updates", "Members", "Upcoming", "Discussions", "Resources" } :
            new[] { "Updates", "Materials", "Grades", "Members", "Discussions" };
        foreach (var tab in tabs) selector.Items.Add(new SelectorBarItem { Text = tab });
        var detail = new Frame
        {
            Background = new SolidColorBrush(Colors.Transparent),
            ContentTransitions = new TransitionCollection
            {
                new NavigationThemeTransition
                {
                    DefaultNavigationTransitionInfo = new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight }
                }
            }
        };
        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is SelectorBarItem selected && _selectedEntityId > 0)
                NavigateShellFrame(detail, BuildEntityTabContent(kind, _selectedEntityId, _selectedEntity, selected.Text), SlideNavigationTransitionEffect.FromRight);
        };
        var center = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        center.Children.Add(selectedName);
        center.Children.Add(selector);
        var entityList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            ItemsSource = new ObservableCollection<SchoologyEntity>(entries),
            DisplayMemberPath = nameof(SchoologyEntity.Name),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(entityList, kind + "EntityList");
        _entityListView = entityList;
        var filteredEntities = (ObservableCollection<SchoologyEntity>)entityList.ItemsSource;
        entitySearch.TextChanged += (_, _) =>
        {
            var query = entitySearch.Text.Trim();
            filteredEntities.Clear();
            foreach (var candidate in entries.Where(candidate => query.Length == 0 ||
                candidate.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                candidate.Subtitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                filteredEntities.Add(candidate);
        };
        if (initialEntity is not null)
        {
            entityList.SelectedItem = initialEntity;
            selector.SelectedItem = selector.Items.FirstOrDefault();
            if (selector.SelectedItem is SelectorBarItem initialTab)
                NavigateShellFrame(detail, BuildEntityTabContent(kind, initialEntity.Id, initialEntity.Name, initialTab.Text), SlideNavigationTransitionEffect.FromRight);
        }
        entityList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is not SchoologyEntity entity) return;
            _selectedEntity = entity.Name;
            _selectedEntityId = entity.Id;
            selectedName.Text = entity.Name;
            selector.SelectedItem = null;
            selector.SelectedItem = selector.Items.FirstOrDefault();
        };
        master.Children.Add(entityList);
        split.Pane = new ScrollViewer { Content = master, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var centerPane = Surface(center, new Thickness(14));
        var rightPane = Surface(detail, new Thickness(14));
        var columns = new Grid { ColumnSpacing = 0 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(centerPane, 0);
        Grid.SetColumn(rightPane, 1);
        columns.Children.Add(centerPane);
        columns.Children.Add(rightPane);
        split.Content = columns;
        split.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 820;
            split.DisplayMode = narrow ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
            if (narrow && split.IsPaneOpen) split.IsPaneOpen = false;
            else if (!narrow && !split.IsPaneOpen) split.IsPaneOpen = true;
        };
        return split;
    }

    private void NavigateShellFrame(Frame frame, UIElement content, SlideNavigationTransitionEffect effect)
    {
        var animationsEnabled = new UISettings().AnimationsEnabled;
        var transition = new SlideNavigationTransitionInfo { Effect = effect };
        frame.ContentTransitions = animationsEnabled
            ? new TransitionCollection { new NavigationThemeTransition { DefaultNavigationTransitionInfo = transition } }
            : null;
        frame.Navigate(typeof(SchoologyShellContentPage), content, animationsEnabled ? transition : null);
    }

    private UIElement BuildEntityTabContent(string kind, long entityId, string entity, string tab)
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(8) };
        body.Children.Add(Text(entity, 23, true));
        body.Children.Add(Text(tab, 17, true));
        var status = Text("Loading from Schoology…", 13);
        status.Foreground = ThemeBrush("SchoologyMutedTextBrush");
        body.Children.Add(status);
        _ = LoadEntityTabAsync(kind, entityId, tab, body, status);
        return body;
    }

    private async Task LoadEntityTabAsync(string kind, long entityId, string tab, StackPanel body, TextBlock status)
    {
        try
        {
            var session = _session;
            if (session is null || entityId <= 0) throw new InvalidOperationException("Select a Schoology item after signing in.");
            using var document = await _api.GetEntityTabAsync(session, kind, entityId, tab);
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
            if (records.Count == 0)
            {
                body.Children.Add(Surface(Text($"No {tab.ToLowerInvariant()} were returned by Schoology.")));
                return;
            }
            if (tab == "Updates")
            {
                foreach (var record in records) body.Children.Add(BuildUpdateCard(kind, entityId, record));
                return;
            }
            var list = new ListView { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
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
        Button back = null!;
        back = Action("‹ Back to course materials", (_, _) =>
        {
            if (history.Count == 0) return;
            var previous = history.Pop();
            Render(previous.Records);
            breadcrumb.Text = previous.Title;
            back.IsEnabled = history.Count > 0;
        });
        back.IsEnabled = false;
        IReadOnlyList<SchoologyApiRecord> current = initialRecords;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(list, "CourseMaterialsList");
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
                list.Items.Add(new ListViewItem { Content = row, Tag = record });
            }
        }
        list.ItemClick += async (_, args) =>
        {
            if (args.ClickedItem is not ListViewItem { Tag: SchoologyApiRecord record }) return;
            if (!string.Equals(ProfileValue(record.Data, "type"), "folder", StringComparison.OrdinalIgnoreCase)) return;
            var folderId = JsonLong(record.Data, "id", "folder_id", "folderId");
            if (folderId <= 0 || _session is null) return;
            try
            {
                using var folder = await _api.GetCourseFolderAsync(_session, sectionId, folderId);
                var records = SchoologyApiClient.ReadRecords(folder.RootElement, "folder-item", "folder_item", "folderItems", "items");
                history.Push((current.ToArray(), breadcrumb.Text));
                breadcrumb.Text = ProfileValue(record.Data, "title", "name") ?? record.Title;
                back.IsEnabled = true;
                Render(records);
                if (records.Count == 0) list.Items.Add(new ListViewItem { Content = Text("This folder is empty.", 13) });
            }
            catch (Exception ex) { await ShowApiErrorAsync("Unable to open folder", ex.Message); }
        };
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
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch };
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

    private UIElement BuildUpdateCard(string kind, long entityId, SchoologyApiRecord update)
    {
        var updateId = JsonLong(update.Data, "id", "update_id");
        var card = new StackPanel { Spacing = 10 };
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(Text(update.Title, 15, true));
        var author = ProfileValue(update.Data, "user_name", "author_name", "name_display", "created_by");
        var created = ProfileValue(update.Data, "created", "created_at", "timestamp");
        var metadata = string.Join(" · ", new[] { author, created }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(metadata))
            heading.Children.Add(Text(metadata, 11));
        card.Children.Add(heading);
        if (!string.IsNullOrWhiteSpace(update.Subtitle))
            card.Children.Add(Text(update.Subtitle, 13));

        var commentsPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var commentStatus = Text("Comments have not been loaded.", 12);
        var commentList = new StackPanel { Spacing = 6 };
        var commentsLoaded = false;
        var commentInput = new TextBox
        {
            PlaceholderText = "Write a comment…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64
        };
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
        composer.Children.Add(commentInput);
        composer.Children.Add(send);
        commentsPanel.Children.Add(commentStatus);
        commentsPanel.Children.Add(commentList);
        commentsPanel.Children.Add(composer);

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
                liked = !liked;
                likeCount = Math.Max(0, likeCount + (liked ? 1 : -1));
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
        actions.Children.Add(likeButton);
        actions.Children.Add(commentsButton);
        card.Children.Add(actions);
        card.Children.Add(commentsPanel);
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
                var row = new StackPanel { Spacing = 3 };
                row.Children.Add(Text(comment.Title, 12, true));
                if (!string.IsNullOrWhiteSpace(comment.Subtitle))
                    row.Children.Add(Text(comment.Subtitle, 12));
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
        var panel = new Grid { Margin = new Thickness(20), RowSpacing = 12 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.Children.Add(Text("Messages", 24, true));
        var folders = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        folders.Items.Add(new SelectorBarItem { Text = "Inbox" });
        folders.Items.Add(new SelectorBarItem { Text = "Sent" });
        folders.SelectedItem = folders.Items[0];
        Grid.SetRow(folders, 1); panel.Children.Add(folders);
        var status = Text("Loading messages…", 13);
        Grid.SetRow(status, 2); panel.Children.Add(status);
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(list, "SchoologyMessagesList");
        Grid.SetRow(list, 3); panel.Children.Add(list);
        async Task LoadFolderAsync(bool sent)
        {
            try
            {
                if (_session is null) throw new InvalidOperationException("Sign in to view messages.");
                status.Text = sent ? "Loading sent messages…" : "Loading inbox…";
                list.Items.Clear();
                using var document = await _api.GetMessagesAsync(_session, sent);
                var records = SchoologyApiClient.ReadRecords(document.RootElement, "message", "messages");
                foreach (var record in records)
                {
                    var idText = ProfileValue(record.Data, sent ? "recipient_ids" : "author_id",
                        sent ? "recipientIds" : "authorId") ?? "";
                    if (idText.Contains(',')) idText = idText.Split(',')[0];
                    long.TryParse(idText, out var userId);
                    string name = sent ? "Recipient" : "Schoology";
                    string? picture = null;
                    if (userId > 0)
                    {
                        try
                        {
                            using var userDoc = await _api.GetUserAsync(_session, userId);
                            var user = userDoc.RootElement.TryGetProperty("user", out var nested) ? nested : userDoc.RootElement;
                            name = ProfileValue(user, "name_display", "display_name", "name") ?? name;
                            picture = ProfileValue(user, "picture_url", "pictureUrl", "picture", "photo_url");
                        }
                        catch { }
                    }
                    var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(8, 9, 8, 9) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var avatar = new Image { Width = 44, Height = 44, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
                    if (!string.IsNullOrWhiteSpace(picture))
                        try { avatar.Source = new BitmapImage(new Uri(NormalizeSchoologyImageUrl(picture))); } catch { }
                    row.Children.Add(new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
                        Background = ThemeBrush("SchoologySubtleBrush"), Child = avatar });
                    var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                    labels.Children.Add(Text(name, 14, true));
                    labels.Children.Add(Text(record.Title, 13));
                    if (!string.IsNullOrWhiteSpace(record.Subtitle)) labels.Children.Add(Text(record.Subtitle, 11));
                    Grid.SetColumn(labels, 1); row.Children.Add(labels);
                    list.Items.Add(new ListViewItem { Content = row, Tag = record });
                }
                status.Text = records.Count == 0 ? (sent ? "No sent messages." : "Your inbox is empty.") : $"{records.Count} messages";
            }
            catch (Exception ex) { status.Text = $"Unable to load messages: {ex.Message}"; }
        }
        folders.SelectionChanged += (_, _) => _ = LoadFolderAsync(folders.SelectedItem == folders.Items[1]);
        list.ItemClick += async (_, args) =>
        {
            if (args.ClickedItem is not ListViewItem { Tag: SchoologyApiRecord message }) return;
            var dialog = new ContentDialog { Title = message.Title, Content = message.Subtitle,
                CloseButtonText = "Close", XamlRoot = _root.XamlRoot };
            await dialog.ShowAsync();
        };
        _ = LoadFolderAsync(false);
        return panel;
    }

    private UIElement BuildGradesPage()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        panel.Children.Add(Text("Grades", 24, true));
        panel.Children.Add(Text("Choose a course to view its current Schoology gradebook.", 13));
        var list = new ListView
        {
            ItemsSource = _courses,
            DisplayMemberPath = nameof(SchoologyEntity.Name),
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true
        };
        AutomationProperties.SetAutomationId(list, "SchoologyGradesCourseList");
        var details = new StackPanel { Spacing = 8 };
        list.ItemClick += async (_, args) =>
        {
            if (args.ClickedItem is not SchoologyEntity course || _session is null) return;
            details.Children.Clear();
            details.Children.Add(Text($"Loading grades for {course.Name}…", 13));
            try
            {
                using var document = await _api.GetEntityTabAsync(_session, "Courses", course.Id, "Grades");
                var records = SchoologyApiClient.ReadRecords(document.RootElement);
                details.Children.Clear();
                if (records.Count == 0) details.Children.Add(Text("Schoology returned no grade rows for this course.", 13));
                foreach (var grade in records)
                {
                    var row = new StackPanel { Spacing = 3 };
                    row.Children.Add(Text(grade.Title, 14, true));
                    if (!string.IsNullOrWhiteSpace(grade.Subtitle)) row.Children.Add(Text(grade.Subtitle, 12));
                    details.Children.Add(Surface(row));
                }
            }
            catch (Exception ex)
            {
                details.Children.Clear();
                details.Children.Add(Text($"Unable to load grades: {ex.Message}", 13));
            }
        };
        panel.Children.Add(list);
        panel.Children.Add(details);
        return panel;
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

