using System.Numerics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;

namespace NutManager.App.Presentation.Controls;

/// <summary>How the indicator should read. Mirrors the connection states the shell already exposes.</summary>
public enum NutLedState
{
    Unavailable,
    Healthy,
    Pending,
    Critical
}

/// <summary>
/// The shell's status light. The breathing halo is the only continuous animation in the
/// application and it is confined to this control: it runs on the render thread through the
/// Composition API, so there is no timer and no UI-thread work per frame. Healthy, pending, and
/// critical states keep a semantic glow; healthy and pending share the same breathing wave while
/// critical keeps a stronger static blur. The core remains stable throughout, so disabling motion
/// never removes the state cue.
/// </summary>
public partial class NutStatusLed : UserControl
{
    private const string ScaleTarget = "Scale";
    private const string OpacityTarget = "Opacity";

    public static readonly StyledProperty<NutLedState> StateProperty =
        AvaloniaProperty.Register<NutStatusLed, NutLedState>(nameof(State));

    private bool _pulseRunning;

    public NutStatusLed()
    {
        InitializeComponent();
    }

    public NutLedState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }

    /// <summary>
    /// Colour for every layer. Redundant with the state text beside the control, never the only cue.
    /// </summary>
    public IBrush? StateBrush => this.FindResource(State switch
    {
        // Healthy uses the LED's own green rather than the shared healthy token: a small lit ball
        // needs more saturation to read as lit than badge text does.
        NutLedState.Healthy => "NutLedHealthyBrush",
        NutLedState.Pending => "NutWarningBrush",
        NutLedState.Critical => "NutCriticalBrush",
        _ => "NutUnavailableBrush"
    }) as IBrush;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopPulse();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != StateProperty) return;
        RaisePropertyChanged(StateBrushProperty, default, default);
        ApplyState();
    }

    private void ApplyState()
    {
        var period = PulsePeriodFor(State);

        // The shadow colour lives in a style, so the state is handed to both halo layers as a class.
        ApplyStateClasses(AmbientHalo);
        ApplyStateClasses(Halo);
        // Fully opaque, letting the shadow's own alpha decide how bright the resting glow is. Reach is
        // set by the blur radius and the wave's travel; this layer only controls intensity, so turning
        // it up brightens without growing anything.
        AmbientHalo.Opacity = State switch
        {
            NutLedState.Healthy => 1.0,
            NutLedState.Pending => 1.0,
            NutLedState.Critical => 0.96,
            _ => 0
        };
        if (period == TimeSpan.Zero)
        {
            StopPulse();
            return;
        }

        StartPulse(period);
    }

    /// <summary>
    /// How long one breath takes, per state.
    ///
    /// A failed connection breathes too, and slower. Standing still is what an indicator does when it
    /// has nothing to say, so a static red dot reads as an old value nobody refreshed rather than as a
    /// live fault — the one reading it must not have. The slower period keeps it from competing with
    /// the healthy state for attention while still being unmistakably alive.
    ///
    /// Only the halo moves. The core never fades, so the state stays legible at every point in the
    /// cycle, and the text beside it never animates at all.
    /// </summary>
    public static TimeSpan PulsePeriodFor(NutLedState state) => state switch
    {
        NutLedState.Healthy or NutLedState.Pending => TimeSpan.FromSeconds(2.0),
        NutLedState.Critical => TimeSpan.FromSeconds(3.0),
        _ => TimeSpan.Zero
    };

    private void ApplyStateClasses(Border halo)
    {
        halo.Classes.Set("healthy", State == NutLedState.Healthy);
        halo.Classes.Set("pending", State == NutLedState.Pending);
        halo.Classes.Set("critical", State == NutLedState.Critical);
    }

    private void StartPulse(TimeSpan period)
    {
        if (ElementComposition.GetElementVisual(Halo) is not { } halo) return;

        // Restarting an already-running animation would visibly jump the halo, so a state that
        // keeps the same period leaves the running one alone.
        if (_pulseRunning && Halo.Tag is TimeSpan current && current == period) return;
        Halo.Tag = period;

        // Scaling is centred on the layer, otherwise the halo would grow towards the bottom right.
        halo.CenterPoint = new Vector3D(Halo.Width / 2, Halo.Height / 2, 0);

        var easing = new SineEaseInOut();
        var scale = halo.Compositor.CreateVector3DKeyFrameAnimation();
        scale.Target = ScaleTarget;
        scale.Duration = period;
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        // The ring leaves the core and travels out through the ambient glow. It starts at roughly the
        // core's own diameter, so it reads as something emitted by the ball rather than as a second
        // shape that was always there, and ends outside the glow it crossed.
        scale.InsertKeyFrame(0f, new Vector3D(0.72, 0.72, 1), easing);
        scale.InsertKeyFrame(0.72f, new Vector3D(2.0, 2.0, 1), easing);
        scale.InsertKeyFrame(1f, new Vector3D(2.6, 2.6, 1), easing);

        var opacity = halo.Compositor.CreateScalarKeyFrameAnimation();
        opacity.Target = OpacityTarget;
        opacity.Duration = period;
        opacity.IterationBehavior = AnimationIterationBehavior.Forever;
        // Bright, because the wave has to be seen, and safe to be bright because the shadow carries no
        // spread. The visible ring this control had came from that spread — a solid band drawn before
        // the blur begins, which turns into a travelling edge once the wave expands. Opacity only made
        // that edge easier to find; it was never the cause, and dropping it merely dimmed the glow
        // while leaving the geometry that produced the ring.
        // Fades with distance rather than at the end of the cycle. Holding the ring bright most of the
        // way and then dropping it reads as the animation stopping; decaying as it travels reads as the
        // wave losing energy, which is the thing being imitated.
        //
        // Gone by two thirds of the cycle, with the remainder held at zero.
        //
        // The tail used to run all the way to the loop point, and a faint ring lingering out at the edge
        // reads as the glow being slow to clear rather than as a wave that has passed. Ending it early
        // also gives the cycle a rest: emit, travel, gone, pause. Without that pause each wave leaves
        // just as the next arrives and the light never settles.
        //
        // The last stretch of expansion happens invisibly, which costs nothing — the ring is already at
        // zero and the scale animation simply carries it the rest of the way with nothing to show.
        opacity.InsertKeyFrame(0f, 0.95f, easing);
        opacity.InsertKeyFrame(0.22f, 0.52f, easing);
        opacity.InsertKeyFrame(0.45f, 0.16f, easing);
        opacity.InsertKeyFrame(0.66f, 0f, easing);
        opacity.InsertKeyFrame(1f, 0f, easing);

        halo.StartAnimation(ScaleTarget, scale);
        halo.StartAnimation(OpacityTarget, opacity);

        _pulseRunning = true;
    }

    private void StopPulse()
    {
        Halo.Tag = null;
        _pulseRunning = false;
        if (ElementComposition.GetElementVisual(Halo) is { } halo)
        {
            halo.StopAnimation(ScaleTarget);
            halo.StopAnimation(OpacityTarget);
            halo.Scale = new Vector3D(1, 1, 1);
            halo.Opacity = 0f;
        }

        // The core is intentionally never animated. It remains the stable, non-motion state cue.
    }

    private static readonly DirectProperty<NutStatusLed, IBrush?> StateBrushProperty =
        AvaloniaProperty.RegisterDirect<NutStatusLed, IBrush?>(nameof(StateBrush), owner => owner.StateBrush);
}
