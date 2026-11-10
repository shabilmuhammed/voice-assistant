using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Hark.App.ViewModels;

namespace Hark.App.Voice;

/// <summary>
/// The small panel that appears above other windows when Hark wakes. It never takes keyboard focus,
/// so whatever you were typing in keeps the caret.
/// </summary>
public partial class VoiceOverlay : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x00000080;

    private readonly VoiceViewModel _vm;
    private Storyboard? _breathing;

    public VoiceOverlay(VoiceViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnChanged;
        SizeChanged += (_, _) => Place();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExToolWindow);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VoiceViewModel.State):
                if (_vm.IsVisible)
                {
                    if (!IsVisible) Show();
                    Place();
                }
                else if (IsVisible) Hide();
                Breathe(_vm.IsWorking);
                if (!_vm.IsListening) SetHalo(_vm.IsWorking ? 0.85 : 0.7);
                break;

            case nameof(VoiceViewModel.Level) when _vm.IsListening:
                SetHalo(0.7 + Math.Min(1, _vm.Level * 1.6) * 0.55);
                break;
        }
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
}
