using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using SystemComposition = Windows.UI.Composition;

namespace Buoy.Windows.Shell;

/// <summary>
/// The panel's glass, drawn as the backdrop of a <see cref="TransparentWindow"/>:
/// the desktop behind, blurred, under a light or dark tint, cut to a rounded
/// rectangle (<see cref="Shape"/>) that can move and change size inside the
/// window, with the Mac's drop shadow around it.
///
/// Only the system compositor can blur what is behind a window (WinUI's has no
/// host backdrop brush), so the glass is one effect brush there: tint over
/// blur, masked by the shape, over the shape's shadow with the glass cut out of
/// it. One mask feeds both, so the shadow cannot lag the glass. The window's
/// content is clipped to the same shape on WinUI's side (<see cref="ClipContent"/>);
/// the two clocks run the same curve from the same tick.
///
/// Under Reduce Transparency, or with Windows' transparency effects off, the
/// glass is an opaque fill of the same shape.
/// </summary>
internal sealed class GlassBackdrop : SystemBackdrop
{
    /// <summary>Mac: surfaceShadowOpacity, surfaceShadowRadius, surfaceShadowOffsetY.</summary>
    private const float ShadowOpacity = 0.30f, ShadowBlur = 6, ShadowOffsetY = 4;


    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }

    private static IntPtr _queueController;

    private SystemComposition.Compositor? _system;
    private ICompositionSupportsSystemBackdrop? _target;
    private SystemComposition.ShapeVisual? _shapes;
    private SystemComposition.CompositionSpriteShape? _fill;
    private SystemComposition.CompositionColorBrush? _fillBrush;
    private SystemComposition.CompositionRoundedRectangleGeometry? _mask;
    private SystemComposition.CompositionVisualSurface? _surface;
    private SystemComposition.CompositionSurfaceBrush? _maskBrush;
    private CompositionRoundedRectangleGeometry? _clip;
    private double _scale = 1;
    private bool _dark;
    private bool _solid;

    /// <summary>Where the glass is headed, in the window's DIPs.</summary>
    public Rect Shape { get; private set; }

    public double CornerRadius { get; private set; }

    /// <summary>Clips <paramref name="content"/>, which spans the window, to the glass.</summary>
    public void ClipContent(UIElement content)
    {
        var visual = ElementCompositionPreview.GetElementVisual(content);
        _clip = visual.Compositor.CreateRoundedRectangleGeometry();
        visual.Clip = visual.Compositor.CreateGeometricClip(_clip);
        SetShape(Shape, CornerRadius, animate: false);
    }

    /// <summary>The window's size in DIPs and its scale, which the mask is drawn at.</summary>
    public void SetCanvas(Size size, double scale)
    {
        _scale = scale;
        var pixels = new Vector2((float)(size.Width * scale), (float)(size.Height * scale));
        if (_shapes is not null) _shapes.Size = pixels;
        if (_surface is not null) _surface.SourceSize = pixels;
        SetShape(Shape, CornerRadius, animate: false);
    }

    /// <summary>Light or dark tint, and whether the glass is an opaque fill.</summary>
    public void SetAppearance(bool dark, bool solid)
    {
        if (dark == _dark && solid == _solid && _target?.SystemBackdrop is not null) return;
        _dark = dark;
        _solid = solid;
        if (_target is not null) _target.SystemBackdrop = CreateBrush();
    }

    /// <summary>
    /// Scales the glass about its centre to <paramref name="scale"/> (on
    /// <paramref name="spring"/> when given, else the fade's curve) and fades it
    /// to <paramref name="opacity"/>, with its shadow, over <paramref name="duration"/>
    /// from wherever they are; at once for zero. The mask's shape carries both,
    /// since the blur and the shadow are cut from it. Under reduced motion only
    /// the fade runs.
    /// </summary>
    public void Present(float opacity, float scale, TimeSpan duration, (double Response, double Damping)? spring = null)
    {
        if (_fill is not { } fill || _fillBrush is not { } brush) return;
        if (Motion.IsReduced) scale = 1;
        var s = (float)_scale;
        fill.CenterPoint = new Vector2((float)(Shape.X + Shape.Width / 2), (float)(Shape.Y + Shape.Height / 2)) * s;
        var color = Color.FromArgb((byte)Math.Round(opacity * 255), 255, 255, 255);
        if (duration <= TimeSpan.Zero)
        {
            fill.StopAnimation("Scale");
            brush.StopAnimation("Color");
            fill.Scale = new Vector2(scale);
            brush.Color = color;
            return;
        }
        var c = fill.Compositor;
        // Ease-out, the curve Motion.ScaleFade gives the content, so the two move as one.
        var curve = c.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0.58f, 1));
        if (spring is { } s2)
        {
            var bounce = c.CreateSpringVector2Animation();
            bounce.FinalValue = new Vector2(scale);
            bounce.DampingRatio = (float)s2.Damping;
            bounce.Period = Motion.SpringPeriod(s2.Response);
            fill.StartAnimation("Scale", bounce);
        }
        else
        {
            var grow = c.CreateVector2KeyFrameAnimation();
            grow.InsertKeyFrame(1, new Vector2(scale), curve);
            grow.Duration = duration;
            fill.StartAnimation("Scale", grow);
        }
        var fade = c.CreateColorKeyFrameAnimation();
        fade.InsertKeyFrame(1, color, curve);
        fade.Duration = duration;
        brush.StartAnimation("Color", fade);
    }

    /// <summary>
    /// Moves the glass to <paramref name="shape"/> (window DIPs) with corners of
    /// <paramref name="radius"/>, from wherever it is, over the Mac's Harbor sweep
    /// (0.26s ease-in-out), or at once. Under reduced motion it is always at once,
    /// as the Mac sets its window frame outright.
    /// </summary>
    public void SetShape(Rect shape, double radius, bool animate)
    {
        Shape = shape;
        CornerRadius = radius;
        var s = (float)_scale;
        var (left, top) = ((float)shape.X, (float)shape.Y);
        var (right, bottom) = ((float)(shape.X + shape.Width), (float)(shape.Y + shape.Height));
        var corner = new Vector2((float)radius);
        animate &= !Motion.IsReduced;

        // The same shape twice: the content's clip in DIPs, the glass's mask in pixels.
        var offset = new Vector2(left, top);
        var size = new Vector2(right - left, bottom - top);
        if (_clip is { } clip)
        {
            if (animate)
            {
                var c = clip.Compositor;
                clip.StartAnimation("Offset", Sweep(c, offset));
                clip.StartAnimation("Size", Sweep(c, size));
                clip.StartAnimation("CornerRadius", Sweep(c, corner));
            }
            else
            {
                foreach (var name in ShapeProperties) clip.StopAnimation(name);
                (clip.Offset, clip.Size, clip.CornerRadius) = (offset, size, corner);
            }
        }
        if (_mask is { } mask)
        {
            offset *= s;
            size *= s;
            if (animate)
            {
                var c = mask.Compositor;
                mask.StartAnimation("Offset", Sweep(c, offset));
                mask.StartAnimation("Size", Sweep(c, size));
                mask.StartAnimation("CornerRadius", Sweep(c, corner * s));
            }
            else
            {
                foreach (var name in ShapeProperties) mask.StopAnimation(name);
                (mask.Offset, mask.Size, mask.CornerRadius) = (offset, size, corner * s);
            }
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _target = target;

        // The system compositor needs a system dispatcher queue on this thread.
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() is null)
            CreateDispatcherQueueController(new DispatcherQueueOptions { Size = 12, ThreadType = 2, ApartmentType = 2 }, out _queueController);
        var c = _system = new SystemComposition.Compositor();

        _mask = c.CreateRoundedRectangleGeometry();
        _fillBrush = c.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
        _fill = c.CreateSpriteShape(_mask);
        _fill.FillBrush = _fillBrush;
        _shapes = c.CreateShapeVisual();
        _shapes.Shapes.Add(_fill);
        _surface = c.CreateVisualSurface();
        _surface.SourceVisual = _shapes;
        _maskBrush = c.CreateSurfaceBrush(_surface);
        _maskBrush.Stretch = SystemComposition.CompositionStretch.None;
        _maskBrush.HorizontalAlignmentRatio = 0;
        _maskBrush.VerticalAlignmentRatio = 0;

        target.SystemBackdrop = CreateBrush();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        target.SystemBackdrop = null;
        _target = null;
    }

    private SystemComposition.CompositionBrush CreateBrush()
    {
        var c = _system!;
        static SystemComposition.CompositionEffectSourceParameter Source(string name) => new(name);

        // Close to Windows' thin acrylic: enough tint to read text over a busy desktop.
        var tint = _dark ? Color.FromArgb(_solid ? (byte)255 : (byte)110, 32, 32, 32)
                         : Color.FromArgb(_solid ? (byte)255 : (byte)150, 243, 243, 243);
        global::Windows.Graphics.Effects.IGraphicsEffectSource body = _solid
            ? new ColorSourceEffect { Color = tint }
            : new CompositeEffect { Mode = CanvasComposite.SourceOver, Sources = { Source("backdrop"), new ColorSourceEffect { Color = tint } } };

        var glass = new AlphaMaskEffect { Source = body, AlphaMask = Source("mask") };
        var shadow = new CompositeEffect
        {
            Mode = CanvasComposite.DestinationOut,
            Sources =
            {
                new AlphaMaskEffect
                {
                    Source = new ColorSourceEffect { Color = Color.FromArgb((byte)(ShadowOpacity * 255), 0, 0, 0) },
                    AlphaMask = new GaussianBlurEffect
                    {
                        BlurAmount = ShadowBlur,
                        BorderMode = EffectBorderMode.Soft,
                        Source = new Transform2DEffect { TransformMatrix = Matrix3x2.CreateTranslation(0, ShadowOffsetY * (float)_scale), Source = Source("shadowMask") },
                    },
                },
                Source("cutout"),
            },
        };
        var all = new CompositeEffect { Mode = CanvasComposite.SourceOver, Sources = { shadow, glass } };

        var brush = c.CreateEffectFactory(all).CreateBrush();
        if (!_solid) brush.SetSourceParameter("backdrop", c.CreateHostBackdropBrush());
        brush.SetSourceParameter("mask", _maskBrush);
        brush.SetSourceParameter("shadowMask", _maskBrush);
        brush.SetSourceParameter("cutout", _maskBrush);
        return brush;
    }

    private static readonly string[] ShapeProperties = ["Offset", "Size", "CornerRadius"];

    // The same curve on both compositors, so the content's clip and the glass move as one.
    // A key frame only at the end starts from wherever the value is, so a fold
    // interrupted mid-restore turns back without a jump.
    private static Vector2KeyFrameAnimation Sweep(Compositor c, Vector2 to)
    {
        var sweep = c.CreateVector2KeyFrameAnimation();
        sweep.InsertKeyFrame(1, to, Motion.EaseInOut(c));
        sweep.Duration = Motion.HarborSweep;
        return sweep;
    }

    private static SystemComposition.Vector2KeyFrameAnimation Sweep(SystemComposition.Compositor c, Vector2 to)
    {
        var sweep = c.CreateVector2KeyFrameAnimation();
        sweep.InsertKeyFrame(1, to, c.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1)));
        sweep.Duration = Motion.HarborSweep;
        return sweep;
    }
}
