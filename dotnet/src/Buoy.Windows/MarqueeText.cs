using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Buoy.Windows;

/// <summary>
/// A one-line title that scrolls when it doesn't fit its lane (the Mac's
/// <c>MarqueeText</c>): it rests 1.2s, then slides left at 34pt/s with a second
/// copy 32pt behind so the loop is seamless. Under reduced motion it stays
/// still and truncates. The scroll is a composition animation, so it costs no
/// layout per frame.
/// </summary>
internal sealed class MarqueeText : UserControl
{
    private const double Gap = 32;
    private const double PointsPerSecond = 34;
    private const double Pause = 1.2;

    private readonly TextBlock _still = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _first = new();
    private readonly TextBlock _second = new();
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Spacing = Gap };
    private readonly Canvas _lane = new();
    private string _text = "";

    public MarqueeText()
    {
        _strip.Children.Add(_first);
        _strip.Children.Add(_second);
        _lane.Children.Add(_strip);
        // On from the start: "Translation.X" can't even be stopped until it exists.
        ElementCompositionPreview.SetIsTranslationEnabled(_strip, true);
        Content = new Grid { Children = { _still, _lane } };
        SizeChanged += (_, _) => Refresh();
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            _still.Text = _first.Text = _second.Text = value;
            Refresh();
        }
    }

    public void SetStyle(FontFamily family, double size, global::Windows.UI.Text.FontWeight weight, Brush foreground)
    {
        foreach (var block in new[] { _still, _first, _second })
        {
            block.FontFamily = family;
            block.FontSize = size;
            block.FontWeight = weight;
            block.Foreground = foreground;
        }
        Refresh();
    }

    private void Refresh()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        _first.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = _first.DesiredSize.Width;
        var visual = Motion.VisualOf(_strip);
        visual.StopAnimation("Translation.X");

        if (width <= 0 || textWidth <= width + 0.5 || Motion.IsReduced)
        {
            _still.Visibility = Visibility.Visible;
            _lane.Visibility = Visibility.Collapsed;
            return;
        }

        _still.Visibility = Visibility.Collapsed;
        _lane.Visibility = Visibility.Visible;
        _lane.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        Canvas.SetTop(_strip, (height - _first.DesiredSize.Height) / 2);

        var distance = (float)(textWidth + Gap);
        var total = Pause + distance / PointsPerSecond;
        var compositor = visual.Compositor;
        var scroll = compositor.CreateScalarKeyFrameAnimation();
        scroll.InsertKeyFrame(0, 0);
        scroll.InsertKeyFrame((float)(Pause / total), 0);
        scroll.InsertKeyFrame(1, -distance, compositor.CreateLinearEasingFunction());
        scroll.Duration = TimeSpan.FromSeconds(total);
        scroll.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Translation.X", scroll);
    }
}
