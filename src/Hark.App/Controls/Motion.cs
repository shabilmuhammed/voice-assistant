using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hark.App.Controls;

/// <summary>
/// The app's small set of movements. Everything checks the Windows "animation effects" setting first,
/// so with that off the interface simply switches state.
/// </summary>
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    private static T Frozen<T>(T easing) where T : EasingFunctionBase
    {
        easing.Freeze();
        return easing;
    }

    /// <summary>Fade in while rising a few pixels: new messages, pages, suggestion cards.</summary>
    public static void Enter(FrameworkElement element, double rise = 8, int milliseconds = 360, int delay = 0)
    {
        if (!Enabled) return;
        var move = new TranslateTransform(0, rise);
        element.RenderTransform = move;
        element.Opacity = 0;
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var begin = TimeSpan.FromMilliseconds(delay);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, duration) { EasingFunction = Ease, BeginTime = begin });
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = Ease, BeginTime = begin });
    }

    /// <summary>Slide a transform to a new offset: the sidebar's selection pill.</summary>
    public static void SlideY(TranslateTransform transform, double to, int milliseconds = 320)
    {
        if (!Enabled)
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = to;
            return;
        }
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Ease });
    }

    public static void Fade(UIElement element, double to, int milliseconds = 200)
    {
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = to;
            return;
        }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Ease });
    }

    // Attached property so XAML can say Motion.EnterOnLoad="True".
    public static readonly DependencyProperty EnterOnLoadProperty = DependencyProperty.RegisterAttached(
        "EnterOnLoad", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEnterOnLoadChanged));

    public static void SetEnterOnLoad(DependencyObject element, bool value) => element.SetValue(EnterOnLoadProperty, value);
    public static bool GetEnterOnLoad(DependencyObject element) => (bool)element.GetValue(EnterOnLoadProperty);

    public static readonly DependencyProperty EnterDelayProperty = DependencyProperty.RegisterAttached(
        "EnterDelay", typeof(int), typeof(Motion), new PropertyMetadata(0));

    public static void SetEnterDelay(DependencyObject element, int value) => element.SetValue(EnterDelayProperty, value);
    public static int GetEnterDelay(DependencyObject element) => (int)element.GetValue(EnterDelayProperty);

    private static void OnEnterOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true) return;
        element.Loaded += (_, _) => Enter(element, delay: GetEnterDelay(element));
    }
}
