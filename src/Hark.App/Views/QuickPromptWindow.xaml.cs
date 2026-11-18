using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hark.App.ViewModels;

namespace Hark.App.Views;

/// <summary>
/// The small panel that appears above other windows on Ctrl + Alt + Space. It takes the keyboard while you type,
/// then hands it back to the app you were in, and stays on top to show the answer.
/// </summary>
public partial class QuickPromptWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    private readonly QuickPromptViewModel _vm;
    private Storyboard? _breathing;
    private QuickPromptState _shown = QuickPromptState.Hidden;
    private IntPtr _returnTo;

    /// <summary>Screenshots: keep the box open when another window takes the focus.</summary>
    public bool StayOpen { get; set; }

    public QuickPromptWindow(QuickPromptViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnChanged;
        SizeChanged += (_, _) => Place();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            _vm.Dismiss();
        };
        // Clicking into another app while typing closes the box; what was typed is kept for next time.
        Deactivated += (_, _) =>
        {
            if (_vm.IsTyping && !StayOpen) _vm.Dismiss();
        };
        Surface.MouseEnter += (_, _) => _vm.HoldOpen(true);
        Surface.MouseLeave += (_, _) => _vm.HoldOpen(false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Kept out of Alt + Tab and the taskbar.
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExToolWindow);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickPromptViewModel.State)) return;
        var before = _shown;
        _shown = _vm.State;

        if (_vm.IsVisible)
        {
            if (!IsVisible) Show();
            Place();
        }

        if (_vm.IsTyping)
        {
            if (before != QuickPromptState.Typing) _returnTo = GetForegroundWindow();
            Activate();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                Input.Focus();
                Keyboard.Focus(Input);
                Input.CaretIndex = Input.Text.Length;
            });
        }
        else if (before == QuickPromptState.Typing && IsActive)
        {
            ReturnFocus();
        }

        if (!_vm.IsVisible && IsVisible) Hide();
        Breathe(_vm.IsWorking);
        SetHalo(_vm.IsWorking ? 0.85 : 0.7);
    }

    /// <summary>Back to the app that was in front before the box opened.</summary>
    private void ReturnFocus()
    {
        var target = _returnTo;
        _returnTo = IntPtr.Zero;
        if (target != IntPtr.Zero && target != new WindowInteropHelper(this).Handle && IsWindow(target)) SetForegroundWindow(target);
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        if (_vm.SubmitCommand.CanExecute(null)) _vm.SubmitCommand.Execute(null);
    }

    /// <summary>The halo swells a little with each key.</summary>
    private void Input_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_vm.IsTyping) return;
        var swell = new DoubleAnimation(0.86, TimeSpan.FromMilliseconds(90)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var settle = new DoubleAnimation(0.7, TimeSpan.FromMilliseconds(380)) { BeginTime = TimeSpan.FromMilliseconds(90), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var storyboard = new Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
            foreach (var step in new[] { swell.Clone(), settle.Clone() })
            {
                Storyboard.SetTargetName(step, nameof(HaloScale));
                Storyboard.SetTargetProperty(step, new PropertyPath(property));
                storyboard.Children.Add(step);
            }
        if (SystemParameters.ClientAreaAnimation) storyboard.Begin(this);
    }

    /// <summary>Bottom centre of the main screen, just above the taskbar.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 12;
    }

    private void SetHalo(double scale)
    {
        var animation = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(110)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        HaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        HaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    /// <summary>A slow pulse of the core while Copilot is working. Skipped when Windows animations are turned off.</summary>
    private void Breathe(bool on)
    {
        _breathing?.Stop(this);
        _breathing = null;
        CoreScale.ScaleX = CoreScale.ScaleY = 1;
        if (!on || !SystemParameters.ClientAreaAnimation) return;

        var pulse = new DoubleAnimation(0.86, 1.08, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        var storyboard = new Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var clone = pulse.Clone();
            Storyboard.SetTargetName(clone, nameof(CoreScale));
            Storyboard.SetTargetProperty(clone, new PropertyPath(property));
            storyboard.Children.Add(clone);
        }
        _breathing = storyboard;
        storyboard.Begin(this, isControllable: true);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);
}
