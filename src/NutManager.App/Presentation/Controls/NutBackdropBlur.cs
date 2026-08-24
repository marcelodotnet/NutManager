using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace NutManager.App.Presentation.Controls;

/// <summary>
/// A pane that blurs whatever the application has already drawn behind it.
///
/// Avalonia has no backdrop filter, and the three things that look like one are not: a blur effect
/// blurs the element together with its own children, the acrylic material reaches the window's
/// backdrop rather than the application's content, and a <c>VisualBrush</c> pointed at a visual that
/// is already in the tree does not paint it at all.
///
/// What does work is the one thing Skia exposes directly. By the time a control renders, the surface
/// already holds everything drawn before it, so a snapshot of that surface is the backdrop — the real
/// pixels of the page underneath, not a second rendering of it. Blurring the snapshot and painting it
/// back over the same rectangle is a backdrop filter in the only sense that matters here.
///
/// The approach is Nikita Tsukanov's, by way of the control in rocksdanister/weather. It is a custom
/// draw operation rather than anything Avalonia supports as a feature, which is worth knowing before
/// relying on it: it reads the frame buffer every time it renders, so it belongs on a small, fixed
/// band and not on a large or frequently invalidated surface.
/// </summary>
/// <summary>Which way the frost thins out, if it does.</summary>
public enum NutBlurFade
{
    /// <summary>Uniform across the band.</summary>
    None,

    /// <summary>Solid at the top edge, gone at the bottom.</summary>
    Bottom,

    /// <summary>Solid at the bottom edge, gone at the top.</summary>
    Top
}

public sealed class NutBackdropBlur : Control
{
    /// <summary>How far the blur reaches, in device pixels.</summary>
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<NutBackdropBlur, double>(nameof(Radius), 12d);

    /// <summary>The glass tint laid over the blur. Alpha included; transparent means blur alone.</summary>
    public static readonly StyledProperty<Color> TintProperty =
        AvaloniaProperty.Register<NutBackdropBlur, Color>(nameof(Tint), Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));

    /// <summary>Which edge the frost is anchored to, and which way it thins out.</summary>
    public static readonly StyledProperty<NutBlurFade> FadeTowardsProperty =
        AvaloniaProperty.Register<NutBackdropBlur, NutBlurFade>(nameof(FadeTowards), NutBlurFade.None);

    public NutBlurFade FadeTowards
    {
        get => GetValue(FadeTowardsProperty);
        set => SetValue(FadeTowardsProperty, value);
    }

    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public Color Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    static NutBackdropBlur() =>
        AffectsRender<NutBackdropBlur>(RadiusProperty, TintProperty, FadeTowardsProperty);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(default, Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        context.Custom(new BlurBehind(bounds, Radius, Tint, FadeTowards));
    }

    /// <summary>
    /// Snapshot, blur, paint back.
    ///
    /// The canvas transform has to be inverted before the snapshot is used as a shader: the snapshot
    /// is in surface coordinates and the drawing happens in this control's, so without the inverse
    /// the backdrop would be sampled from the wrong place on screen.
    /// </summary>
    private sealed class BlurBehind : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly double _radius;
        private readonly Color _tint;
        private readonly NutBlurFade _fade;

        internal BlurBehind(Rect bounds, double radius, Color tint, NutBlurFade fade)
        {
            _bounds = bounds;
            _radius = radius;
            _tint = tint;
            _fade = fade;
        }

        /// <summary>Inflated so the blur's own spill is included when the region is invalidated.</summary>
        public Rect Bounds => _bounds.Inflate(4);

        /// <summary>Transparent to the pointer: the page underneath keeps its clicks.</summary>
        public bool HitTest(Point point) => false;

        public void Dispose()
        {
        }

        public bool Equals(ICustomDrawOperation? other) =>
            other is BlurBehind operation &&
            operation._bounds == _bounds &&
            operation._radius.Equals(_radius) &&
            operation._tint == _tint &&
            operation._fade == _fade;

        public void Render(ImmediateDrawingContext context)
        {
            // Absent on any backend that is not Skia. Drawing nothing is the right answer there:
            // the band simply stops frosting rather than the window failing to render.
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } leaseFeature) return;

            using var lease = leaseFeature.Lease();
            // The surface is absent while the lease is serving a non-surface target, and there is
            // nothing to snapshot then.
            if (lease.SkSurface is not { } surface) return;
            if (!lease.SkCanvas.TotalMatrix.TryInvert(out var inverted)) return;

            var width = (int)Math.Ceiling(_bounds.Width);
            var height = (int)Math.Ceiling(_bounds.Height);
            if (width <= 0 || height <= 0) return;

            using var backdrop = surface.Snapshot();
            using var backdropShader = SKShader.CreateImage(
                backdrop, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, inverted);
            using var blurred = SKSurface.Create(
                lease.GrContext,
                false,
                new SKImageInfo(width, height, SKImageInfo.PlatformColorType, SKAlphaType.Premul));
            if (blurred is null) return;

            var radius = (float)Math.Max(0.1d, _radius);
            using (var filter = SKImageFilter.CreateBlur(radius, radius, SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { Shader = backdropShader, ImageFilter = filter })
            {
                blurred.Canvas.DrawRect(0, 0, width, height, paint);
            }

            // Frost and tint go into one layer so the falloff below can thin them together. Without
            // that they would fade at different rates and the tint would outlive the blur.
            var fading = _fade != NutBlurFade.None;
            if (fading) lease.SkCanvas.SaveLayer();

            using (var snapshot = blurred.Snapshot())
            using (var shader = SKShader.CreateImage(snapshot))
            using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
            {
                lease.SkCanvas.DrawRect(0, 0, width, height, paint);
            }

            if (_tint.A != 0)
            {
                using var tintPaint = new SKPaint
                {
                    Color = new SKColor(_tint.R, _tint.G, _tint.B, _tint.A),
                    IsAntialias = true
                };
                lease.SkCanvas.DrawRect(0, 0, width, height, tintPaint);
            }

            if (!fading) return;

            // The falloff, and the reason it is applied here rather than through an OpacityMask on
            // the control: a mask puts the control on its own render layer, and the layer the
            // snapshot then reads is empty, so the blur silently disappears while everything still
            // renders. Inside the draw operation it cannot break that way.
            //
            // The ramp spans the whole band rather than being anchored to fixed pixels, because the
            // band already stops short of the page title and has no content further down to protect.
            var solidAtTop = _fade == NutBlurFade.Bottom;
            // Weighted heavily rather than evenly, because a mask at half alpha shows half the frost
            // laid over half the original, and the original's sharp edges are what the eye reads. The
            // radius cannot fix that — past about forty it saturates against a band this shallow, and
            // the ghost stays. Holding the mask near opaque for most of the band is what actually
            // makes content dissolve instead of doubling.
            var alphas = new byte[] { 255, 235, 170, 0 };
            var offsets = new[] { 0f, 0.35f, 0.7f, 1f };

            // Written anchored to the top, then turned over for the band at the other end. Both the
            // weights and the positions have to be mirrored: reversing one without the other leaves
            // a ramp that runs the right way with the wrong shape.
            if (!solidAtTop)
            {
                Array.Reverse(alphas);
                for (var i = 0; i < offsets.Length; i++) offsets[i] = 1f - offsets[i];
                Array.Reverse(offsets);
            }

            var ramp = new SKColor[alphas.Length];
            for (var i = 0; i < alphas.Length; i++) ramp[i] = SKColors.Black.WithAlpha(alphas[i]);

            using (var falloff = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, height),
                ramp,
                offsets,
                SKShaderTileMode.Clamp))
            using (var mask = new SKPaint { Shader = falloff, BlendMode = SKBlendMode.DstIn })
            {
                lease.SkCanvas.DrawRect(0, 0, width, height, mask);
            }

            lease.SkCanvas.Restore();
        }
    }
}
