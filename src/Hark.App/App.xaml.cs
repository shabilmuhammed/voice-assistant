using System.Windows;
using System.Windows.Threading;
using Hark.App.ViewModels;
using Hark.Core;

namespace Hark.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private System.Windows.Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, "Hark.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Hark is already running. Look for its icon in the system tray.", "Hark");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };

        Log.Info($"Hark starting, {Environment.OSVersion}, .NET {Environment.Version}");

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
        if (!demo) CreateTray();
        _window.Show();
        if (demo) _main.LoadDemo(PageArg(e.Args));
        else _ = _main.InitializeAsync();
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
            menu.Items.Add("Open Hark", null, (_, _) => ShowWindow());
            menu.Items.Add("New chat", null, (_, _) => { ShowWindow(); _main?.Chat.NewChatCommand.Execute(null); });
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => Quit());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "Hark",
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
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to:\n{AppPaths.LogFile}",
            "Hark", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
