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
            _vm.FocusRequested -= FocusInput;
        }
        _vm = vm;
        if (_vm is not null)
        {
            _vm.ScrollRequested += OnScrollRequested;
            _vm.FocusRequested += FocusInput;
        }
    }

    private void OnScrollRequested() => Scroller.ScrollToEnd();

    private void FocusInput()
    {
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) =>
        Placeholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The model picker: a menu of the models the Copilot plan offers.</summary>
    private void ModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.Models.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = ModelButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var model in _vm.Models)
        {
            var item = new MenuItem { Header = model.Name, IsCheckable = true, IsChecked = ReferenceEquals(model, _vm.SelectedModel) };
            item.Click += (_, _) => _vm.SelectedModel = model;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

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

    // Clicking a skill in the "/" menu picks it.
    private void SlashList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Hark.Core.SkillInfo skill })
        {
            _vm?.PickSkillCommand.Execute(skill);
            e.Handled = true;
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // While the "/" menu is open, the arrow keys move through it and Enter or Tab picks.
        if (_vm is { IsSlashOpen: true })
        {
            switch (e.Key)
            {
                case Key.Down: _vm.MoveSlash(1); e.Handled = true; ScrollSlash(); return;
                case Key.Up: _vm.MoveSlash(-1); e.Handled = true; ScrollSlash(); return;
                case Key.Enter or Key.Tab when !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift): _vm.AcceptSlash(); e.Handled = true; return;
                case Key.Escape: _vm.DismissSlash(); e.Handled = true; return;
            }
        }

        // Enter sends; Shift+Enter adds a new line.
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (_vm is { IsBusy: false } && _vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
    }

    private void ScrollSlash()
    {
        if (_vm is { SlashListIndex: >= 0 } vm && vm.SlashListIndex < SlashList.Items.Count)
            SlashList.ScrollIntoView(SlashList.Items[vm.SlashListIndex]);
    }
}
