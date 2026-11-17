using System.ComponentModel;
using System.Windows.Controls;
using Hark.App.ViewModels;

namespace Hark.App.Views;

public partial class SkillsView : UserControl
{
    private SkillsViewModel? _vm;

    public SkillsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnChanged;
            _vm = e.NewValue as SkillsViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnChanged;
        };
        Loaded += (_, _) => FocusName();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SkillsViewModel.IsEditing)) FocusName();
    }

    // A new skill starts with the cursor in the name box.
    private void FocusName()
    {
        if (_vm is { IsEditing: true, IsNew: true }) Dispatcher.BeginInvoke(() => NameBox.Focus());
    }
}
