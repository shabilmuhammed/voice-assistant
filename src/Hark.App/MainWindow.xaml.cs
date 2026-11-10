using System.Windows;
using Hark.App.ViewModels;

namespace Hark.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.AttentionRequested -= BringToFront;
            if (e.NewValue is MainViewModel vm) vm.AttentionRequested += BringToFront;
        };
    }

    private void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
