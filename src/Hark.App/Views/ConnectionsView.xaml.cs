using System.Windows;
using System.Windows.Controls;
using Hark.App.ViewModels;

namespace Hark.App.Views;

public partial class ConnectionsView : UserControl
{
    private ConnectionsViewModel? _vm;

    public ConnectionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PatSaved -= ClearPat;
            _vm = e.NewValue as ConnectionsViewModel;
            if (_vm is not null) _vm.PatSaved += ClearPat;
        };
    }

    private void PatBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.PendingPat = PatBox.Password;
    }

    private void ClearPat() => PatBox.Clear();
}
