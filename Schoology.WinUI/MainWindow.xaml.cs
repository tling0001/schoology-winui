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
    private string _selectedEntity = string.Empty;
    private readonly SchoologyAuthService _auth = new(false);
    private SchoologyAuthService.Session? _session;
    private QrCameraScanner? _qrScanner;
    private MediaPlayer? _qrPlayer;
    private readonly List<string> _courses = ["English Language Arts", "Algebra II", "Biology", "World History", "Computer Science"];
    private readonly List<string> _groups = ["Robotics Club", "Student Council", "Yearbook"];

    public MainWindow()
    {
        App.WriteStartupLog("MainWindow constructor entered.");
        InitializeComponent();
        App.WriteStartupLog("MainWindow.InitializeComponent completed.");

        _root = Root;
        Title = "Schoology";
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        try { SystemBackdrop = new MicaBackdrop(); } catch { }

        App.WriteStartupLog("Setting initial window size.");
        SetWindowSize(520, 360);
        _ = SetWindowIconAsync();
        App.WriteStartupLog("Initial splash window size and native window icon configured.");

        App.WriteStartupLog("Creating splash screen.");
        ShowSplash();
        App.WriteStartupLog("Splash screen created.");

        _splashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _splashTimer.Tick += (_, _) =>
        {
            try
            {
                App.WriteStartupLog("Splash timer tick; switching to login.");
                _splashTimer.Stop();
                SetWindowSize(1260, 820);
                ShowLogin();
                App.WriteStartupLog("Login screen created.");
                _ = RestoreSavedSessionAsync();
            }
            catch (Exception ex)
            {
                App.WriteStartupLog("Exception while switching from splash to login.", ex);
                throw;
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
        try
        {
            var restored = await _auth.RestoreSessionAsync();
            if (restored is null || _page != "login") return;
            _session = restored;
            ShowHome();
        }
        catch (Exception ex) { App.WriteStartupLog("Saved Schoology session restoration failed.", ex); }
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

        var titleBar = new Grid { Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(16, 0, 12, 0) };
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
        commands.Children.Add(Action("Search", (_, _) => ShowSearch()));
        commands.Children.Add(Action("＋ Create", (_, _) => ShowCreateMenu(), true));
        Grid.SetColumn(commands, 2);
        titleBar.Children.Add(commands);
        shell.Children.Add(titleBar);
        SetTitleBar(titleBar);

        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsPaneOpen = true,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = true,
            OpenPaneLength = 252,
            CompactPaneLength = 64,
            CompactModeThresholdWidth = 760,
            ExpandedModeThresholdWidth = 1120,
            Background = new SolidColorBrush(Colors.Transparent),
            Content = content
        };
        var items = new (string Name, Symbol Icon)[]
        {
            ("Home", Symbol.Home), ("Upcoming", Symbol.Calendar), ("Courses", Symbol.Library),
            ("Groups", Symbol.People), ("School Profile", Symbol.Home), ("Messages", Symbol.Mail),
            ("Grades", Symbol.Page), ("Resources", Symbol.Folder)
        };
        foreach (var entry in items)
        {
            var item = new NavigationViewItem { Content = entry.Name, Tag = entry.Name, Icon = new SymbolIcon(entry.Icon) };
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
            if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string target) return;
            if (target == "Sign out") { _auth.SignOut(); _session = null; ShowLogin(); }
            else if (target == "Home") ShowHome();
            else if (target == "Upcoming") ShowUpcoming();
            else if (target is "Courses" or "Groups" or "School Profile") ShowThreePane(target);
            else ShowHome(target);
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
        else
        {
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
            panel.Children.Add(Text(section, 26, true));
            panel.Children.Add(Surface(Text($"The {section.ToLowerInvariant()} view is ready for its Schoology data provider. No sample records are presented as live account data.")));
            page = panel;
        }
        Reset(BuildApplicationShell(section, page));
    }

    private UIElement BuildHomeSplitView(bool upcomingFirst)
    {
        var split = new SplitView
        {
            DisplayMode = SplitViewDisplayMode.Inline,
            IsPaneOpen = true,
            OpenPaneLength = 420,
            CompactPaneLength = 0,
            PanePlacement = SplitViewPanePlacement.Left,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        var primary = new StackPanel { Spacing = 14, Margin = new Thickness(22, 18, 14, 18) };
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(Text(upcomingFirst ? "Upcoming" : "Recent Activity", 22, true));
        var toggle = Action("Hide pane", (_, _) => split.IsPaneOpen = !split.IsPaneOpen);
        toggle.Padding = new Thickness(8, 4, 8, 4);
        Grid.SetColumn(toggle, 1);
        heading.Children.Add(toggle);
        primary.Children.Add(heading);
        var secondary = new StackPanel { Spacing = 14, Margin = new Thickness(20, 18, 24, 18) };
        secondary.Children.Add(Text(upcomingFirst ? "Recent Activity" : "Upcoming", 22, true));
        foreach (var item in upcomingFirst ? BuildUpcomingItems() : BuildRecentActivityItems()) primary.Children.Add(item);
        foreach (var item in upcomingFirst ? BuildRecentActivityItems() : BuildUpcomingItems()) secondary.Children.Add(item);
        split.Pane = new ScrollViewer { Content = primary, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        split.Content = new ScrollViewer { Content = secondary, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        split.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 760;
            split.DisplayMode = narrow ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
            if (narrow && split.IsPaneOpen) split.IsPaneOpen = false;
            else if (!narrow && !split.IsPaneOpen) split.IsPaneOpen = true;
        };
        _homeSplitView = split;
        return split;
    }

    private IEnumerable<UIElement> BuildRecentActivityItems()
    {
        yield return Text("Recent activity from courses and groups will appear here when the Schoology data provider is connected.", 14);
        yield return Surface(ActivityItem("Recent Activity", "Activity feed", "Waiting for Schoology account data"));
        yield return Surface(ActivityItem("Courses", "Course updates", "Waiting for Schoology account data"));
    }

    private IEnumerable<UIElement> BuildUpcomingItems()
    {
        yield return Text("Assignments and events will appear here when the Schoology data provider is connected.", 14);
        yield return Surface(ActivityItem("Upcoming", "Assignments and events", "Waiting for Schoology account data"));
        yield return Surface(ActivityItem("Calendar", "Due dates", "Waiting for Schoology account data"));
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

    private void ShowThreePane(string kind)
    {
        _page = kind;
        _section = kind;
        _selectedEntity = string.Empty;
        Reset(BuildApplicationShell(kind, BuildThreePane(kind)));
    }

    private UIElement BuildThreePane(string kind)
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
        var entries = kind == "Courses" ? _courses : kind == "Groups" ? _groups :
            new List<string> { "School updates", "Events", "Staff directory" };
        var master = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        master.Children.Add(Text(kind == "School Profile" ? "School" : $"My {kind}", 20, true));
        master.Children.Add(new TextBox { PlaceholderText = $"Search {kind.ToLowerInvariant()}", Margin = new Thickness(0, 4, 0, 8) });
        var selectedName = Text("Select an item", 14, true);
        var selector = new SelectorBar { Margin = new Thickness(0, 0, 0, 12) };
        var tabs = kind == "School Profile" ? new[] { "Updates", "Events", "Staff" } :
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
            if (selector.SelectedItem is SelectorBarItem selected && !string.IsNullOrEmpty(_selectedEntity))
                NavigateShellFrame(detail, BuildEntityTabContent(kind, _selectedEntity, selected.Text), SlideNavigationTransitionEffect.FromRight);
        };
        var center = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
        center.Children.Add(selectedName);
        center.Children.Add(selector);
        var entityList = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true,
            ItemsSource = entries, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        entityList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is not string entity) return;
            _selectedEntity = entity;
            selectedName.Text = entity;
            selector.SelectedItem = selector.Items.FirstOrDefault();
            if (selector.SelectedItem is SelectorBarItem first)
                NavigateShellFrame(detail, BuildEntityTabContent(kind, entity, first.Text), SlideNavigationTransitionEffect.FromRight);
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
        frame.Navigate(typeof(SchoologyShellContentPage), content, new SlideNavigationTransitionInfo { Effect = effect });
    }

    private UIElement BuildEntityTabContent(string kind, string entity, string tab)
    {
        var body = new StackPanel { Spacing = 14, Margin = new Thickness(8) };
        body.Children.Add(Text(entity, 23, true));
        body.Children.Add(Text(tab, 17, true));
        body.Children.Add(Surface(Text($"The {tab.ToLowerInvariant()} feed for this {kind.ToLowerInvariant().TrimEnd('s')} will be populated by the native Schoology data layer. This shell does not substitute sample records for account data.")));
        return body;
    }

    private void ShowSearch()
    {
        _page = "Search";
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        panel.Children.Add(Text("Search Schoology", 22, true));
        panel.Children.Add(new AutoSuggestBox { PlaceholderText = "Search courses, groups, people, and updates" });
        panel.Children.Add(Action("Back", (_, _) => ShowHome()));
        Reset(BuildApplicationShell("Recent Activity", panel));
    }
    private async void ShowCreateMenu()
    {
        var dialog = new ContentDialog { Title = "Create", Content = "Choose what you want to create. Actions will be connected to Schoology permissions and APIs in a later phase.", PrimaryButtonText = "Post an update", SecondaryButtonText = "Create event", CloseButtonText = "Cancel", XamlRoot = _root.XamlRoot };
        var result = await dialog.ShowAsync(); if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary) { var info = new ContentDialog { Title = result == ContentDialogResult.Primary ? "Post an update" : "Create event", Content = "This action is not connected until Schoology authentication and permissions are implemented.", CloseButtonText = "OK", XamlRoot = _root.XamlRoot }; await info.ShowAsync(); }
    }
}

internal sealed class SchoologyShellContentPage : Page
{
    public SchoologyShellContentPage() => NavigationCacheMode = NavigationCacheMode.Disabled;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is UIElement content) Content = content;
    }
}
