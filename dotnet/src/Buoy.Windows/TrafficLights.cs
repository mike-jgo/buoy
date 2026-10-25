using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Buoy.Windows;

/// <summary>
/// The Mac panel's traffic lights (<c>TrafficLightsView</c>): red hides, yellow
/// folds into Harbor Mode, green toggles the tall size. Coloured while the
/// panel is active and grey while it isn't; hovering the group shows each
/// light's glyph, as macOS does. A light with no handler is drawn disabled.
/// </summary>
internal sealed class TrafficLights : UserControl
{
    private const double Diameter = 12;
    private const double Pitch = 20; // macOS's distance between button origins

    private static readonly Color Red = Color.FromArgb(0xFF, 0xFF, 0x5F, 0x57);
    private static readonly Color Yellow = Color.FromArgb(0xFF, 0xFE, 0xBC, 0x2E);
    private static readonly Color Green = Color.FromArgb(0xFF, 0x28, 0xC8, 0x40);
    private static readonly Color Glyph = Color.FromArgb(0x99, 0x00, 0x00, 0x00);

    private readonly List<Light> _lights = [];
    private bool _isActive = true;
    private bool _isHovering;

    public TrafficLights(Action close, Action? minimize, Action expand)
    {
        var row = new Canvas { Width = Pitch * 2 + Diameter, Height = Diameter };
        AddLight(row, 0, "Hide", "", 6, Red, close);
        AddLight(row, 1, "Harbor Mode", "", 7, Yellow, minimize);
        AddLight(row, 2, "Expand", "", 7, Green, expand);
        Content = row;

        row.PointerEntered += (_, _) => { _isHovering = true; Refresh(); };
        row.PointerExited += (_, _) => { _isHovering = false; Refresh(); };
        ActualThemeChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>Grey while the panel isn't the active window, as macOS draws inactive lights.</summary>
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; Refresh(); }
    }

    private void AddLight(Canvas row, int index, string name, string glyph, double glyphSize, Color color, Action? action)
    {
        var dot = new Ellipse { Width = Diameter, Height = Diameter };
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = glyphSize,
            Foreground = new SolidColorBrush(Glyph),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // A slightly larger hit target than the dot, like AppKit's widget.
        var target = new Grid { Width = Diameter + 4, Height = Diameter + 4, Background = new SolidColorBrush(Colors.Transparent), Children = { dot, icon } };
        Canvas.SetLeft(target, index * Pitch - 2);
        Canvas.SetTop(target, -2);
        AutomationProperties.SetName(target, name);
        row.Children.Add(target);

        var light = new Light(dot, icon, color, action);
        _lights.Add(light);
        if (action is null) return;

        ToolTipService.SetToolTip(target, name);
        target.PointerPressed += (_, e) => { light.Pressed = true; Refresh(); e.Handled = true; };
        target.PointerReleased += (_, e) =>
        {
            var wasPressed = light.Pressed;
            light.Pressed = false;
            Refresh();
            if (wasPressed) action();
            e.Handled = true;
        };
        target.PointerExited += (_, _) => { light.Pressed = false; Refresh(); };
        target.PointerCanceled += (_, _) => { light.Pressed = false; Refresh(); };
    }

    private void Refresh()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        var idle = dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0xD6, 0xD6, 0xD6);
        foreach (var light in _lights)
        {
            var enabled = light.Action is not null;
            var lit = enabled && (_isActive || _isHovering);
            var color = lit ? light.Color : idle;
            if (light.Pressed) color = Darken(color);
            light.Dot.Fill = new SolidColorBrush(color);
            light.Dot.Stroke = new SolidColorBrush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
            light.Dot.StrokeThickness = 0.5;
            light.Icon.Opacity = enabled && _isHovering ? 1 : 0;
        }
    }

    private static Color Darken(Color color) =>
        Color.FromArgb(color.A, (byte)(color.R * 0.8), (byte)(color.G * 0.8), (byte)(color.B * 0.8));

    private sealed class Light(Ellipse dot, FontIcon icon, Color color, Action? action)
    {
        public Ellipse Dot { get; } = dot;
        public FontIcon Icon { get; } = icon;
        public Color Color { get; } = color;
        public Action? Action { get; } = action;
        public bool Pressed { get; set; }
    }
}
