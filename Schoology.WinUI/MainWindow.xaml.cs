using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Schoology.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly Grid _root;
    private readonly DispatcherTimer _splashTimer;
    private string _page = "login";
    private string _section = "Recent Activity";
    private readonly List<string> _courses = ["English Language Arts", "Algebra II", "Biology", "World History", "Computer Science"];
    private readonly List<string> _groups = ["Robotics Club", "Student Council", "Yearbook"];

    public MainWindow()
    {
        App.WriteStartupLog("MainWindow constructor entered.");
        InitializeComponent();
        App.WriteStartupLog("MainWindow.InitializeComponent completed.");

        _root = Root;
        Title = "Schoology";
        ExtendsContentIntoTitleBar = false;
        try { SystemBackdrop = new MicaBackdrop(); } catch { }

        App.WriteStartupLog("Setting initial window size.");
        SetWindowSize(1260, 820);
        App.WriteStartupLog("Initial window size step completed.");

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
                ShowLogin();
                App.WriteStartupLog("Login screen created.");
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
            "SchoologyBlueDarkBrush" => Brush("#FF204D7B"),
            _ => Brush("#FF2E66A3")
        };
    }

    private Border Surface(UIElement child, Thickness? padding = null)
    {
        var fallback = ThemeBrush("SchoologySurfaceBrush");
        Brush material = fallback;
        if (_root.ActualTheme != ElementTheme.HighContrast)
        {
            material = new AcrylicBrush
            {
                BackgroundSource = AcrylicBackgroundSource.HostBackdrop,
                TintColor = _root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.ColorHelper.FromArgb(255, 30, 35, 40) : Microsoft.UI.ColorHelper.FromArgb(255, 248, 250, 252),
                TintOpacity = _root.ActualTheme == ElementTheme.Dark ? 0.82 : 0.76,
                FallbackColor = _root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 36, 40) : Microsoft.UI.ColorHelper.FromArgb(255, 255, 255, 255)
            };
        }
        return new Border { Background = material, BorderBrush = ThemeBrush("SchoologyDividerBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = padding ?? new Thickness(16), Child = child, Margin = new Thickness(0, 0, 0, 12) };
    }
    private void Reset(UIElement content) { _root.Children.Clear(); _root.Children.Add(content); }

    private void ShowSplash()
    {
        _page = "splash";
        var grid = new Grid { Background = Brush("#2E66A3") };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 14 };
        stack.Children.Add(new TextBlock { Text = "schoology", FontSize = 48, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush("#FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center });
        var ring = new ProgressRing { IsActive = new UISettings().AnimationsEnabled, Width = 30, Height = 30, Foreground = Brush("#FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(ring); stack.Children.Add(new TextBlock { Text = "Learning starts here", FontSize = 14, Foreground = Brush("#EAF3FB"), HorizontalAlignment = HorizontalAlignment.Center });
        grid.Children.Add(stack); Reset(grid);
    }

    private void ShowLogin()
    {
        _page = "login";
        var outer = new Grid { Margin = new Thickness(0), Background = ThemeBrush("SchoologySubtleBrush") };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(440) });
        var intro = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32, 0, 48, 0), Spacing = 16 };
        intro.Children.Add(new TextBlock { Text = "schoology", FontSize = 46, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Brush("#2E66A3") });
        intro.Children.Add(Text("Your learning community,\nall in one place.", 30, true));
        intro.Children.Add(Text("A native Windows experience for courses, groups, school updates, and upcoming work.", 17));
        intro.Children.Add(Text("Sign in securely with your school or district account.", 14));
        Grid.SetColumn(intro, 0); outer.Children.Add(intro);
        var form = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 12 };
        form.Children.Add(Text("Welcome back", 25, true)); form.Children.Add(Text("Sign in to Schoology", 15));
        var school = new TextBox { Header = "School or organization", PlaceholderText = "Search for your school", Margin = new Thickness(0, 8, 0, 0) };
        var email = new TextBox { Header = "Email or username", PlaceholderText = "Enter your username" };
        var password = new PasswordBox { Header = "Password", PlaceholderText = "Enter your password" };
        form.Children.Add(school); form.Children.Add(email); form.Children.Add(password);
        var status = Text("This starter connects to the official Schoology sign-in page; credentials are not stored by this app.", 12);
        status.Opacity = 0.8; form.Children.Add(status);
        form.Children.Add(Action("Continue to Schoology sign in", (_, _) => OpenOfficialLogin(), true));
        form.Children.Add(Action("Preview app layout", (_, _) => ShowHome()));
        form.Children.Add(Action("Use district sign-in", (_, _) => OpenOfficialLogin()));
        var card = Surface(form, new Thickness(28)); card.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(card, 1); outer.Children.Add(card);
        Reset(outer);
    }

    private async void OpenOfficialLogin()
    {
        var dialog = new ContentDialog
        {
            Title = "Continue to Schoology",
            Content = "For account security, sign in through the official Schoology website. This initial native app shell does not yet import a web session. Open the sign-in page in your browser?",
            PrimaryButtonText = "Open sign-in page", CloseButtonText = "Cancel", XamlRoot = _root.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://app.schoology.com/login"));
    }

    private void ShowHome(string section = "Recent Activity")
    {
        _page = "home"; _section = section;
        var layout = new Grid { Background = ThemeBrush("SchoologySubtleBrush") };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var nav = new Grid { Background = Brush("#204D7B") };
        nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); nav.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); nav.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brand = new StackPanel { Margin = new Thickness(22, 24, 16, 22), Spacing = 3 };
        brand.Children.Add(new TextBlock { Text = "schoology", FontSize = 27, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Brush("#FFFFFF") });
        brand.Children.Add(new TextBlock { Text = "LEARNING MANAGEMENT", FontSize = 10, Foreground = Brush("#C9DFF2") });
        Grid.SetRow(brand, 0); nav.Children.Add(brand);
        var navItems = new StackPanel { Spacing = 5, Margin = new Thickness(12, 8, 12, 12) };
        foreach (var item in new[] { "Home", "Upcoming", "Courses", "Groups", "School Profile", "Messages", "Grades", "Resources" })
        {
            var b = new Button { Content = item, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 11, 12, 11), Background = item == section || (section == "Recent Activity" && item == "Home") ? Brush("#3A6E9E") : new SolidColorBrush(Colors.Transparent), Foreground = Brush("#FFFFFF"), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6) };
            b.Click += (_, _) => { if (item == "Home") ShowHome("Recent Activity"); else if (item == "Upcoming") ShowUpcoming(); else if (item == "Courses" || item == "Groups" || item == "School Profile") ShowThreePane(item); else ShowHome(item); };
            navItems.Children.Add(b);
        }
        var navScroll = new ScrollViewer { Content = navItems, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(navScroll, 1); nav.Children.Add(navScroll);
        var profile = new Button { Content = "◉   Profile and settings", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(18, 18, 18, 18), Background = new SolidColorBrush(Colors.Transparent), Foreground = Brush("#FFFFFF"), BorderThickness = new Thickness(0) };
        profile.Click += (_, _) => ShowLogin(); Grid.SetRow(profile, 2); nav.Children.Add(profile);
        Grid.SetColumn(nav, 0); layout.Children.Add(nav);

        var main = new Grid { Margin = new Thickness(26, 22, 26, 22) };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Text(section == "Recent Activity" ? "Home" : section, 28, true));
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(Action("Search", (_, _) => ShowSearch())); tools.Children.Add(Action("＋ Create", (_, _) => ShowCreateMenu())); Grid.SetColumn(tools, 1); header.Children.Add(tools);
        Grid.SetRow(header, 0); main.Children.Add(header);
        var content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Content = BuildHomeContent(section); Grid.SetRow(content, 1); main.Children.Add(content);
        Grid.SetColumn(main, 1); layout.Children.Add(main); Reset(layout);
    }

    private UIElement BuildHomeContent(string section)
    {
        var stack = new StackPanel { Spacing = 0 };
        if (section == "Recent Activity" || section == "Home")
        {
            var top = new Grid { ColumnSpacing = 16 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var recent = new StackPanel { Spacing = 12 }; recent.Children.Add(Text("Recent Activity", 20, true));
            recent.Children.Add(Surface(ActivityItem("Biology", "Cell structure assignment posted", "Ms. Anderson · Today")));
            recent.Children.Add(Surface(ActivityItem("English Language Arts", "Feedback added to your essay", "Mr. Rivera · Yesterday")));
            recent.Children.Add(Surface(ActivityItem("World History", "Discussion updated", "Ms. Chen · Yesterday")));
            var courses = new StackPanel { Spacing = 12 }; courses.Children.Add(Text("My Courses", 20, true));
            foreach (var c in _courses.Take(4)) courses.Children.Add(Surface(CourseRow(c)));
            Grid.SetColumn(courses, 1); top.Children.Add(recent); top.Children.Add(courses); stack.Children.Add(top);
            stack.Children.Add(Text("Your Schoology dashboard", 13));
        }
        else if (section == "Courses" || section == "Groups" || section == "School Profile") return BuildThreePane(section);
        else if (section == "Upcoming") return BuildUpcoming();
        else
        {
            stack.Children.Add(Text(section, 22, true)); stack.Children.Add(Surface(Text($"Your {section.ToLowerInvariant()} will appear here when connected to your Schoology account.")));
            stack.Children.Add(Action("Back to recent activity", (_, _) => ShowHome()));
        }
        return stack;
    }

    private static UIElement ActivityItem(string course, string title, string meta)
    {
        var s = new StackPanel { Spacing = 5 }; s.Children.Add(Text(course.ToUpperInvariant(), 11, true)); s.Children.Add(Text(title, 16, true)); s.Children.Add(Text(meta, 12)); return s;
    }
    private static UIElement CourseRow(string name)
    {
        var g = new Grid { ColumnSpacing = 12 }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(7), Background = Brush("#2E66A3"), Child = new TextBlock { Text = name[..1], FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Brush("#FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        g.Children.Add(tile); var labels = new StackPanel { Spacing = 3 }; labels.Children.Add(Text(name, 14, true)); labels.Children.Add(Text("Course dashboard", 12)); Grid.SetColumn(labels, 1); g.Children.Add(labels); return g;
    }

    private void ShowUpcoming() => ShowHome("Upcoming");
    private UIElement BuildUpcoming()
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var upcoming = new StackPanel { Spacing = 12 }; upcoming.Children.Add(Text("Upcoming", 20, true));
        upcoming.Children.Add(Text("TODAY · THURSDAY, OCTOBER 29", 12, true));
        upcoming.Children.Add(Surface(ActivityItem("Biology", "Read chapter 6 and complete the worksheet", "Due today · 11:59 PM")));
        upcoming.Children.Add(Text("TOMORROW · FRIDAY, OCTOBER 30", 12, true));
        upcoming.Children.Add(Surface(ActivityItem("Algebra II", "Practice set 4.2", "Due tomorrow · 8:00 AM")));
        upcoming.Children.Add(Text("MONDAY · NOVEMBER 2", 12, true));
        upcoming.Children.Add(Surface(ActivityItem("World History", "Primary source response", "Due Monday · 3:00 PM")));
        var calendar = new StackPanel { Spacing = 12 }; calendar.Children.Add(Text("Recent Activity", 20, true));
        calendar.Children.Add(Surface(ActivityItem("English Language Arts", "Teacher feedback posted", "Today · 9:18 AM")));
        calendar.Children.Add(Surface(ActivityItem("Computer Science", "New assignment posted", "Yesterday · 2:42 PM")));
        calendar.Children.Add(Surface(ActivityItem("Robotics Club", "Group update", "Yesterday · 12:15 PM")));
        grid.Children.Add(upcoming); Grid.SetColumn(calendar, 1); grid.Children.Add(calendar); return grid;
    }

    private void ShowThreePane(string kind) { _page = kind; var shell = new Grid { Background = ThemeBrush("SchoologySubtleBrush") }; shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new Grid { Margin = new Thickness(20, 12, 20, 12), Background = ThemeBrush("SchoologySurfaceBrush") }; top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); top.Children.Add(Text(kind, 23, true)); var back = Action("← Home", (_, _) => ShowHome()); Grid.SetColumn(back, 1); top.Children.Add(back); shell.Children.Add(top); Grid.SetRow(top, 0);
        var panes = BuildThreePane(kind); Grid.SetRow(panes, 1); shell.Children.Add(panes); Reset(shell); }
    private Grid BuildThreePane(string kind)
    {
        var grid = new Grid { Margin = new Thickness(16), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = new StackPanel { Spacing = 8 }; list.Children.Add(Text(kind == "School Profile" ? "School" : $"My {kind}", 18, true));
        var entries = kind == "Courses" ? _courses : kind == "Groups" ? _groups : new List<string> { "School updates", "Events", "Staff directory" };
        var detail = new TextBlock(); var body = new StackPanel { Spacing = 12 };
        foreach (var entry in entries)
        {
            var btn = new Button { Content = entry, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch };
            btn.Click += (_, _) => { detail.Text = entry; body.Children.Clear(); body.Children.Add(Text(entry, 23, true)); body.Children.Add(Text($"{entry} overview", 15)); body.Children.Add(Surface(Text("Updates, resources, and discussions for this space will be loaded here after Schoology account integration is connected.")));
                body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Action("Updates", (_, _) => SetPaneTab(body, entry, "Updates")), Action("Materials", (_, _) => SetPaneTab(body, entry, "Materials")), Action("Discussions", (_, _) => SetPaneTab(body, entry, "Discussions")) } }); };
            list.Children.Add(btn);
        }
        var listScroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var middle = new StackPanel { Spacing = 12 }; middle.Children.Add(Text("Browse", 18, true)); middle.Children.Add(new TextBox { PlaceholderText = $"Search {kind.ToLowerInvariant()}" }); middle.Children.Add(Surface(Text("Choose an item on the left to see its sections.")));
        var right = new StackPanel { Spacing = 12 }; right.Children.Add(Text("Details", 18, true)); right.Children.Add(Text("Select a course, group, or school section to view its details."));
        var leftCard = Surface(listScroll); var middleCard = Surface(middle); var rightCard = Surface(right);
        Grid.SetColumn(leftCard, 0); grid.Children.Add(leftCard); Grid.SetColumn(middleCard, 1); grid.Children.Add(middleCard); Grid.SetColumn(rightCard, 2); grid.Children.Add(rightCard);
        return grid;
    }
    private void SetPaneTab(StackPanel body, string entry, string tab)
    {
        body.Children.Clear(); body.Children.Add(Text(entry, 23, true)); body.Children.Add(Text(tab, 17, true)); body.Children.Add(Surface(Text($"{tab} for {entry} will appear here when the official Schoology session and API layer are connected.")));
    }

    private void ShowSearch()
    {
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(Text("Search Schoology", 22, true)); var box = new AutoSuggestBox { PlaceholderText = "Search courses, groups, people, and updates" }; panel.Children.Add(box); panel.Children.Add(Action("Back", (_, _) => ShowHome())); Reset(Surface(panel, new Thickness(28)));
    }
    private async void ShowCreateMenu()
    {
        var dialog = new ContentDialog { Title = "Create", Content = "Choose what you want to create. Actions will be connected to Schoology permissions and APIs in a later phase.", PrimaryButtonText = "Post an update", SecondaryButtonText = "Create event", CloseButtonText = "Cancel", XamlRoot = _root.XamlRoot };
        var result = await dialog.ShowAsync(); if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary) { var info = new ContentDialog { Title = result == ContentDialogResult.Primary ? "Post an update" : "Create event", Content = "This action is not connected until Schoology authentication and permissions are implemented.", CloseButtonText = "OK", XamlRoot = _root.XamlRoot }; await info.ShowAsync(); }
    }
}
