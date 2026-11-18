using System.Windows;
using System.Windows.Threading;
using Hark.App.ViewModels;
using Hark.App.Views;
using Hark.Core;

namespace Hark.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private System.Windows.Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _main;
    private QuickPromptWindow? _quickPrompt;
    private GlobalHotkey? _hotkeys;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, "Hark.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("MarTechChat is already running. Look for its icon in the system tray.", "MarTechChat");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };

        Log.Info($"Hark starting, {Environment.OSVersion}, .NET {Environment.Version}");

        ApplyPalette();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == Microsoft.Win32.UserPreferenceCategory.General) Dispatcher.BeginInvoke(ApplyPalette);
        };

        _main = new MainViewModel();
        _window = new MainWindow { DataContext = _main };
        _window.Closing += (_, args) =>
        {
            if (_main.Settings.CloseToTray && _tray is not null)
            {
                args.Cancel = true;
                _window.Hide();
            }
            else
            {
                Quit();
            }
        };

        // "--demo" fills the screens with sample data and talks to nothing; used for automated screenshots.
        var demo = e.Args.Contains("--demo");
        _demoMode = demo;
        if (demo)
        {
            // The build machine's screen is 1024 x 768; keep the whole window on it.
            _window.Width = 1000;
            _window.Height = 700;
        }
        _main.CaptureScreen = () => ScreenCapture.CaptureAsync(_window);
        _quickPrompt = new QuickPromptWindow(_main.QuickPrompt) { StayOpen = demo };
        if (!demo) CreateTray();
        _window.Show();
        if (demo)
        {
            _main.LoadDemo(PageArg(e.Args), approvals: e.Args.Contains("--demo-approvals"), skillEditor: e.Args.Contains("--demo-skill-editor"));
            if (e.Args.Contains("--demo-slash")) _main.Chat.Input = "/re";
            if (e.Args.Contains("--demo-azure")) _main.Preferences.ShowAzureDemo();
            if (e.Args.Contains("--demo-empty"))
            {
                _main.Chat.Messages.Clear();
                _main.Chat.Title = "New chat";
            }
            if (e.Args.Contains("--demo-capture")) _ = DemoCaptureAsync();
            if (e.Args.Contains("--selftest")) _ = SelfTestAsync();
            var quickDemo = Array.IndexOf(e.Args, "--demo-quick");
            if (quickDemo >= 0 && quickDemo + 1 < e.Args.Length) _main.QuickPrompt.LoadDemo(e.Args[quickDemo + 1]);
        }
        else
        {
            ScreenCapture.CleanUp();
            try
            {
                _hotkeys = new GlobalHotkey();
                _hotkeys.QuickPrompt += () => _main.QuickPrompt.Toggle();
                _main.QuickPromptShortcutAvailable = _hotkeys.QuickPromptAvailable;
            }
            catch (Exception ex)
            {
                Log.Error("Keyboard shortcuts could not be set up", ex);
            }
            _ = _main.InitializeAsync();
        }
    }

    /// <summary>
    /// Build-time check: visits every page and presses every button (mouse down only, so nothing is actually run),
    /// which exercises hover, press and focus animations. Any error lands in the log, which the build then reads.
    /// </summary>
    private async Task SelfTestAsync()
    {
        var pressed = 0;
        try
        {
            if (_window is null || _main is null) return;
            await Task.Delay(1500);
            for (var page = 0; page <= MainViewModel.SettingsPage; page++)
            {
                _main.SelectedNavIndex = page;
                await Task.Delay(700);
                foreach (var button in Descendants<System.Windows.Controls.Primitives.ButtonBase>(_window))
                {
                    if (!button.IsVisible || !button.IsEnabled) continue;
                    button.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
                    button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                        { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                    await Task.Delay(30);
                    button.ReleaseMouseCapture();
                    button.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
                    pressed++;
                }
                foreach (var box in Descendants<System.Windows.Controls.CheckBox>(_window))
                {
                    if (!box.IsVisible) continue;
                    box.IsChecked = box.IsChecked != true;
                    await Task.Delay(30);
                    box.IsChecked = box.IsChecked != true;
                }
            }
            // The chat with approvals, and an empty chat, have controls the first pass did not show.
            _main.SelectedNavIndex = 0;
            _main.Chat.Messages.Clear();
            await Task.Delay(700);
            foreach (var mode in new[] { "typing", "working", "answer" })
            {
                _main.QuickPrompt.LoadDemo(mode);
                await Task.Delay(500);
            }
            _main.QuickPrompt.DismissCommand.Execute(null);
            await Task.Delay(500);
            Log.Info($"SELFTEST DONE: pressed {pressed} buttons.");
        }
        catch (Exception ex)
        {
            Log.Error("SELFTEST FAILED", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private async Task DemoCaptureAsync()
    {
        var shot = await ScreenCapture.CaptureAsync(_window);
        if (shot is not null) _main?.Chat.AddDemoImage(shot.Path);
    }

    private bool? _darkApplied;
    private bool _demoMode;

    /// <summary>Loads the light or dark colour palette to match the Windows app theme.</summary>
    private void ApplyPalette()
    {
        var dark = false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex)
        {
            Log.Error("Reading the Windows theme failed; using the light palette.", ex);
        }
        if (_darkApplied == dark) return;
        _darkApplied = dark;
        try
        {
            // Find our palette by name. Its position is not fixed: the Windows theme adds its own dictionaries too.
            var palette = new ResourceDictionary
            {
                Source = new Uri(dark ? "pack://application:,,,/Theme/Dark.xaml" : "pack://application:,,,/Theme/Light.xaml"),
            };
            var merged = Resources.MergedDictionaries;
            var index = -1;
            for (var i = 0; i < merged.Count; i++)
            {
                var source = merged[i].Source?.OriginalString ?? "";
                if (source.EndsWith("Theme/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                    source.EndsWith("Theme/Dark.xaml", StringComparison.OrdinalIgnoreCase)) index = i;
            }
            if (index >= 0) merged[index] = palette;
            else merged.Add(palette);
            Log.Info($"Palette: {(dark ? "dark" : "light")}.");
        }
        catch (Exception ex)
        {
            Log.Error("Applying the colour palette failed", ex);
        }
    }

    private static int PageArg(string[] args)
    {
        var i = Array.IndexOf(args, "--page");
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var page) ? page : 0;
    }

    private void CreateTray()
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open MarTechChat", null, (_, _) => ShowWindow());
            menu.Items.Add("New chat", null, (_, _) => { ShowWindow(); _main?.Chat.NewChatCommand.Execute(null); });
            var quick = new System.Windows.Forms.ToolStripMenuItem("Quick prompt") { ShortcutKeyDisplayString = "Ctrl+Alt+Space" };
            // Opened once the menu has closed, so the menu closing does not take the focus back from the box.
            quick.Click += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _main?.QuickPrompt.Open());
            menu.Items.Add(quick);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => Quit());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = TrayIcon(),
                Text = "MarTechChat",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.MouseClick += (_, args) =>
            {
                if (args.Button == System.Windows.Forms.MouseButtons.Left) ShowWindow();
            };
        }
        catch (Exception ex)
        {
            Log.Error("Could not create the tray icon; closing the window will quit instead.", ex);
            _tray = null;
        }
    }

    private static System.Drawing.Icon TrayIcon()
    {
        try
        {
            var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is not null)
                using (resource.Stream)
                    return new System.Drawing.Icon(resource.Stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Log.Error("Loading the tray icon failed; using the default one.", ex);
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void Quit()
    {
        try
        {
            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            _hotkeys?.Dispose();
            _quickPrompt?.Close();
            if (_main is not null) await _main.ShutdownAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown failed", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        if (_demoMode)
        {
            e.Handled = true;
            return;
        }
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to:\n{AppPaths.LogFile}",
            "MarTechChat", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
