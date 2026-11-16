using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Hark.App.Controls;

/// <summary>
/// The one expressive element in the app: three soft discs of light drifting inside a circle.
/// Shown on a new chat and in the voice panel. Stays still when Windows animations are off.
/// </summary>
public sealed class Orb : Grid
{
    public static readonly DependencyProperty LivelyProperty = DependencyProperty.Register(
        nameof(Lively), typeof(bool), typeof(Orb), new PropertyMetadata(false, (d, _) => ((Orb)d).Restart()));

    private readonly Canvas _canvas = new();
    private readonly List<(Ellipse Disc, double X, double Y, double Dx, double Dy, double Scale, double Seconds)> _discs = new();
    private bool _built;

    public Orb()
    {
        Width = Height = 64;
        ClipToBounds = true;
        Loaded += (_, _) => { Build(); Restart(); };
        SizeChanged += (_, _) => { _built = false; Build(); Restart(); };
        IsVisibleChanged += (_, _) => Restart();
    }

    /// <summary>Faster drift, used while Hark is listening.</summary>
    public bool Lively
    {
        get => (bool)GetValue(LivelyProperty);
        set => SetValue(LivelyProperty, value);
    }

    private void Build()
    {
        if (_built || ActualWidth <= 0) return;
        _built = true;
        var size = ActualWidth;
        Children.Clear();
        _canvas.Children.Clear();
        _discs.Clear();

        Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
        Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x07, 0x5B, 0x7D)) });
        Children.Add(_canvas);

        // colour, size, start x, start y, drift x, drift y, scale at the far end, seconds per pass
        Add(Color.FromRgb(0x7F, 0xE0, 0xFF), 0.70, -0.10, -0.05, 0.26, 0.22, 1.15, 6);
        Add(Color.FromRgb(0x1A, 0xA0, 0xD8), 0.65, 0.47, 0.20, -0.30, -0.12, 0.90, 7);
        Add(Color.FromRgb(0xD7, 0xF6, 0xFF), 0.60, 0.15, 0.58, 0.08, -0.34, 1.20, 8);

        void Add(Color colour, double relativeSize, double x, double y, double dx, double dy, double scale, double seconds)
        {
            var disc = new Ellipse
            {
                Width = size * relativeSize,
                Height = size * relativeSize,
                Fill = new SolidColorBrush(colour),
                Opacity = 0.9,
                Effect = new BlurEffect { Radius = size * 0.16, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform(0, 0) } },
            };
            Canvas.SetLeft(disc, size * x);
            Canvas.SetTop(disc, size * y);
            _canvas.Children.Add(disc);
            _discs.Add((disc, x, y, dx * size, dy * size, scale, seconds));
        }
    }

    private void Restart()
    {
        if (!_built) return;
        foreach (var (disc, _, _, dx, dy, scale, seconds) in _discs)
        {
            var group = (TransformGroup)disc.RenderTransform;
            var grow = (ScaleTransform)group.Children[0];
            var move = (TranslateTransform)group.Children[1];
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.BeginAnimation(TranslateTransform.YProperty, null);
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (!Motion.Enabled || !IsVisible) continue;

            var duration = TimeSpan.FromSeconds((Lively ? 0.28 : 1) * seconds / 2);
            DoubleAnimation Loop(double to, double from = 0) => new(from, to, duration)
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            move.BeginAnimation(TranslateTransform.XProperty, Loop(dx));
            move.BeginAnimation(TranslateTransform.YProperty, Loop(dy));
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(scale, 1));
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(scale, 1));
        }
    }
}
