using System.Windows.Controls;
using Hark.App.ViewModels;

namespace Hark.App.Views;

public partial class HistoryView : UserControl
{
    private HistoryViewModel? _vm;

    public HistoryView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.SearchFocusRequested -= FocusSearch;
            _vm = e.NewValue as HistoryViewModel;
            if (_vm is not null) _vm.SearchFocusRequested += FocusSearch;
        };
        Unloaded += (_, _) => { if (_vm is not null) _vm.SearchFocusRequested -= FocusSearch; _vm = null; };
        Loaded += (_, _) =>
        {
            if (_vm is null && DataContext is HistoryViewModel vm) { _vm = vm; _vm.SearchFocusRequested += FocusSearch; }
        };
    }

    private void FocusSearch() => Dispatcher.BeginInvoke(() => SearchBox.Focus());
}
