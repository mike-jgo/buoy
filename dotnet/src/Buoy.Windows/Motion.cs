using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Buoy.Windows;

/// <summary>
/// Buoy's animations, after the Mac's <c>BuoyMotion</c>. The gate is Windows'
/// "Animation effects" setting (Settings ▸ Accessibility ▸ Visual effects), the
/// counterpart of macOS Reduce Motion: with it off, anything that moves or
/// scales becomes a short crossfade or an instant change. Fades are left alone;
/// it isn't a switch for every animation.
///
/// Everything here animates the element's composition visual, so layout never
/// runs per frame. Movement uses <c>Translation</c>, not <c>Offset</c>, which
/// XAML layout owns.
/// </summary>
internal static class Motion
{
    /// <summary>The crossfade that stands in for a movement under reduced motion.</summary>
    public static readonly TimeSpan Crossfade = TimeSpan.FromSeconds(0.12);

    /// <summary>Mac: minimizedTransitionDuration, the Harbor fold and restore, glass and content alike.</summary>
    public static readonly TimeSpan HarborSweep = TimeSpan.FromSeconds(0.26);

    private static readonly UISettings Settings = new();

    public static bool IsReduced => !Settings.AnimationsEnabled;

    public static Visual VisualOf(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    /// <summary>
    /// SwiftUI's spring(response:dampingFraction:) as a composition spring. Composition's
    /// Period is "the time for the spring to complete a single oscillation", which is
    /// what SwiftUI's response means, so it passes straight through. (Dividing by 2π,
    /// as an earlier version did, made every spring about 6x too fast.)
    /// </summary>
    public static TimeSpan SpringPeriod(double response) => TimeSpan.FromSeconds(response);

    /// <summary>Fades to <paramref name="opacity"/>, then runs <paramref name="completed"/> on the UI thread.</summary>
    public static void Fade(UIElement element, float opacity, TimeSpan duration, Action? completed = null)
    {
        var visual = VisualOf(element);
        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, opacity, EaseOut(compositor));
        fade.Duration = duration;
        visual.StartAnimation("Opacity", fade);
        batch.End();
        Completed(batch, element, completed);
    }

    /// <summary>Sets opacity at once, stopping any fade in progress.</summary>
    public static void SetOpacity(UIElement element, float opacity)
    {
        var visual = VisualOf(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = opacity;
    }

    /// <summary>
    /// Springs the element's scale from <paramref name="from"/> to 1 about
    /// <paramref name="anchor"/> (fractions of its size), fading it in. Under
    /// reduced motion, only the fade.
    /// </summary>
    public static void PopIn(UIElement element, float from, Vector2 anchor, double response, double damping)
    {
        var visual = VisualOf(element);
        var compositor = visual.Compositor;
        if (IsReduced)
        {
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
            Fade(element, 1, Crossfade);
            return;
        }

        var size = element.ActualSize;
        visual.CenterPoint = new Vector3(size.X * anchor.X, size.Y * anchor.Y, 0);
        visual.Scale = new Vector3(from, from, 1);
        var spring = compositor.CreateSpringVector3Animation();
        spring.FinalValue = Vector3.One;
        spring.DampingRatio = (float)damping;
        spring.Period = SpringPeriod(response);
        visual.StartAnimation("Scale", spring);
        Fade(element, 1, TimeSpan.FromSeconds(0.15));
    }

    /// <summary>
    /// Springs the element's scale down to <paramref name="to"/> about
    /// <paramref name="anchor"/> while it fades out: PopIn's way back, on the same
    /// spring (Mac: pillPop's removal under pillAnimation). Fade only under reduced motion.
    /// </summary>
    public static void PopOut(UIElement element, float to, Vector2 anchor, double response, double damping, Action? completed = null)
    {
        var visual = VisualOf(element);
        var compositor = visual.Compositor;
        if (IsReduced)
        {
            Fade(element, 0, Crossfade, completed);
            return;
        }
        var size = element.ActualSize;
        visual.CenterPoint = new Vector3(size.X * anchor.X, size.Y * anchor.Y, 0);
        var spring = compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3(to, to, 1);
        spring.DampingRatio = (float)damping;
        spring.Period = SpringPeriod(response);
        visual.StartAnimation("Scale", spring);
        Fade(element, 0, TimeSpan.FromSeconds(0.15), completed);
    }

    /// <summary>
    /// Springs the element's scale to <paramref name="toScale"/> about
    /// <paramref name="anchor"/> (from <paramref name="fromScale"/> when given)
    /// while it fades to <paramref name="toOpacity"/> over <paramref name="fade"/>.
    /// Under reduced motion only the fade runs, as a crossfade of at most 0.12s.
    /// </summary>
    public static void SpringScaleFade(
        UIElement element, float? fromScale, float toScale, float toOpacity, Vector2 anchor,
        double response, double damping, TimeSpan fade, Action? completed = null)
    {
        var visual = VisualOf(element);
        if (IsReduced)
        {
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
            Fade(element, toOpacity, fade > Crossfade ? Crossfade : fade, completed);
            return;
        }
        var size = element.ActualSize;
        visual.CenterPoint = new Vector3(size.X * anchor.X, size.Y * anchor.Y, 0);
        if (fromScale is { } from)
        {
            visual.StopAnimation("Scale");
            visual.Scale = new Vector3(from, from, 1);
        }
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3(toScale, toScale, 1);
        spring.DampingRatio = (float)damping;
        spring.Period = SpringPeriod(response);
        visual.StartAnimation("Scale", spring);
        Fade(element, toOpacity, fade, completed);
    }

    /// <summary>
    /// A SwiftUI transition on a fixed curve: scale (about <paramref name="anchor"/>,
    /// as fractions of the element's size) and opacity together, from where they
    /// are, or from <paramref name="fromScale"/> when the element is entering
    /// afresh. Under reduced motion only the fade runs, as a crossfade of at most
    /// 0.12s (Mac: BuoyMotion.transition).
    /// </summary>
    public static void ScaleFade(
        UIElement element, float? fromScale, float toScale, float toOpacity, Vector2 anchor,
        TimeSpan duration, Func<Compositor, CompositionEasingFunction> curve, Action? completed = null)
    {
        var visual = VisualOf(element);
        var compositor = visual.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        if (IsReduced)
        {
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
            if (duration > Crossfade) duration = Crossfade;
        }
        else
        {
            var size = element.ActualSize;
            visual.CenterPoint = new Vector3(size.X * anchor.X, size.Y * anchor.Y, 0);
            if (fromScale is { } from)
            {
                visual.StopAnimation("Scale");
                visual.Scale = new Vector3(from, from, 1);
            }
            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(1, new Vector3(toScale, toScale, 1), curve(compositor));
            scale.Duration = duration;
            visual.StartAnimation("Scale", scale);
        }
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, toOpacity, curve(compositor));
        fade.Duration = duration;
        visual.StartAnimation("Opacity", fade);
        batch.End();
        Completed(batch, element, completed);
    }

    /// <summary>Springs the element's translation from <paramref name="from"/> to zero.</summary>
    public static void SlideIn(UIElement element, Vector3 from, double response, double damping)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = VisualOf(element);
        visual.Properties.InsertVector3("Translation", from);
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = Vector3.Zero;
        spring.DampingRatio = (float)damping;
        spring.Period = SpringPeriod(response);
        visual.StartAnimation("Translation", spring);
    }

    /// <summary>Springs the element's translation to <paramref name="to"/>, then runs <paramref name="completed"/>.</summary>
    public static void SlideOut(UIElement element, Vector3 to, double response, double damping, Action? completed = null)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = VisualOf(element);
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = to;
        spring.DampingRatio = (float)damping;
        spring.Period = SpringPeriod(response);
        visual.StartAnimation("Translation", spring);
        batch.End();
        Completed(batch, element, completed);
    }

    public static CompositionEasingFunction EaseOut(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0.58f, 1));

    public static CompositionEasingFunction EaseInOut(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));

    /// <summary>Overshoots a little before settling (SwiftUI's easeOutBack, as the Mac's to-do uses).</summary>
    public static CompositionEasingFunction EaseOutBack(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.34f, 1.56f), new Vector2(0.64f, 1));

    private static void Completed(CompositionScopedBatch batch, UIElement element, Action? completed)
    {
        if (completed is null) return;
        var queue = element.DispatcherQueue;
        batch.Completed += (_, _) => queue.TryEnqueue(() => completed());
    }
}
