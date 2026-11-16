using System.ComponentModel;
using System.Windows;
using Hark.App.Controls;
using Hark.App.ViewModels;

namespace Hark.App;

public partial class MainWindow : Window
{
    private const double NavItemHeight = 38; // 36 high plus a 2 px gap
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null)
            {
                _vm.AttentionRequested -= BringToFront;
                _vm.PropertyChanged -= OnVmChanged;
            }
            _vm = e.NewValue as MainViewModel;
            if (_vm is not null)
            {
                _vm.AttentionRequested += BringToFront;
                _vm.PropertyChanged += OnVmChanged;
                NavPillMove.Y = _vm.SelectedNavIndex * NavItemHeight;
                UpdateDots();
            }
        };
        StateChanged += (_, _) => OnStateChanged();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedNavIndex):
                Motion.SlideY(NavPillMove, _vm.SelectedNavIndex * NavItemHeight);
                break;
            case nameof(MainViewModel.CurrentPage):
                // The new page fades up while the pill slides.
                Dispatcher.BeginInvoke(() => Motion.Enter(PageHost, rise: 10, milliseconds: 320));
                break;
            case nameof(MainViewModel.IsConnected):
                UpdateDots();
                break;
        }
    }

    private void UpdateDots()
    {
        if (_vm is null) return;
        CopilotDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, _vm.IsConnected ? "H.Ok" : "H.Ink3");
    }

    // A maximised window with custom chrome hangs over the screen edge by the resize border; pad it back in.
    private void OnStateChanged()
    {
        var maximised = WindowState == WindowState.Maximized;
        Root.Padding = maximised ? new Thickness(7) : new Thickness(0);
        MaximizeGlyph.Text = maximised ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
