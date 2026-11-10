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
        DataObject.AddPastingHandler(InputBox, OnPaste);
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

    // ---- attachments by drag and drop, and by paste ---------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _vm?.AddAttachments(files);
        e.Handled = true;
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                _vm?.AddAttachments(Clipboard.GetFileDropList().Cast<string>().ToList());
                e.CancelCommand();
            }
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                var path = System.IO.Path.Combine(ScreenCapture.Folder, $"screen-pasted-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                using (var stream = File.Create(path)) encoder.Save(stream);
                _vm?.AddAttachments([path]);
                e.CancelCommand();
            }
        }
        catch (Exception ex)
        {
            Hark.Core.Log.Error("Pasting an attachment failed", ex);
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter adds a new line.
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (_vm?.SendCommand.CanExecute(null) == true) _vm.SendCommand.Execute(null);
    }
}
