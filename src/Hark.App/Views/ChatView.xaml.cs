using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hark.App.ViewModels;

namespace Hark.App.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _vm;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Bind(e.NewValue as ChatViewModel);
        Loaded += (_, _) =>
        {
            Bind(DataContext as ChatViewModel);
            InputBox.Focus();
            Scroller.ScrollToEnd();
        };
        Unloaded += (_, _) => Bind(null);
    }

    private void Bind(ChatViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null)
        {
            _vm.ScrollRequested -= OnScrollRequested;
            _vm.Messages.CollectionChanged -= OnMessagesChanged;
        }
        _vm = vm;
        if (_vm is not null)
        {
            _vm.ScrollRequested += OnScrollRequested;
            _vm.Messages.CollectionChanged += OnMessagesChanged;
        }
        UpdateEmptyState();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState() =>
        EmptyState.Visibility = _vm is null || _vm.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnScrollRequested() => Scroller.ScrollToEnd();

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter adds a new line.
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (_vm?.SendCommand.CanExecute(null) == true) _vm.SendCommand.Execute(null);
    }
}
