using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AnomalyDonationBridge.App;

/// <summary>Draws centered vector icons using the current foreground and display scale.</summary>
public sealed class VectorIcon : FrameworkElement
{
    private const string Plus = "\uE710";
    private const string List = "\uE8FD";
    private static readonly ConcurrentDictionary<string, Geometry> Outlines = new(StringComparer.Ordinal);
    private static readonly Lazy<GlyphTypeface?> SymbolTypeface = new(() =>
        new Typeface(new FontFamily("Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal)
            .TryGetGlyphTypeface(out var typeface) ? typeface : null);

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(VectorIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender, GlyphChanged));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(VectorIcon), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    internal static readonly DependencyProperty LayoutScaleProperty = DependencyProperty.RegisterAttached(
        "LayoutScale", typeof(double), typeof(VectorIcon),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private static void GlyphChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var straightStrokes = args.NewValue is Plus or List or "+";
        RenderOptions.SetEdgeMode(element, straightStrokes ? EdgeMode.Aliased : EdgeMode.Unspecified);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var glyph = Glyph switch
        {
            "+" => Plus,
            "×" => "\uE711",
            "⋯" => "\uE712",
            "✎" => "\uE70F",
            "↻" => "\uE72C",
            _ => Glyph
        };
        if (string.IsNullOrEmpty(glyph) || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        if (glyph is Plus or List)
        {
            DrawPixelAlignedIcon(drawingContext, glyph);
            return;
        }

        var outline = Outlines.GetOrAdd(glyph, LoadOutline);
        var bounds = outline.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;
        var size = 0.86 * Math.Min(RenderSize.Width / bounds.Width, RenderSize.Height / bounds.Height);
        var offsetX = RenderSize.Width / 2 - (bounds.X + bounds.Width / 2) * size;
        var offsetY = RenderSize.Height / 2 - (bounds.Y + bounds.Height / 2) * size;
        drawingContext.PushTransform(new MatrixTransform(size, 0, 0, size, offsetX, offsetY));
        drawingContext.DrawGeometry(Foreground, null, outline);
        drawingContext.Pop();
    }

    private void DrawPixelAlignedIcon(DrawingContext drawingContext, string glyph)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var layoutScale = (double)GetValue(LayoutScaleProperty);
        var scaleX = dpi.DpiScaleX * layoutScale;
        var scaleY = dpi.DpiScaleY * layoutScale;
        var pixels = Math.Min(RenderSize.Width * scaleX, RenderSize.Height * scaleY);
        if (pixels < 6) return;

        // Give all strokes integral device-pixel dimensions and snap the whole
        // shape together. Independent edges can round in opposite directions.
        var rectangles = new List<Rect>(glyph == Plus ? 2 : 6);
        var centerX = RenderSize.Width * scaleX / 2;
        var centerY = RenderSize.Height * scaleY / 2;
        var stroke = Math.Max(1, Math.Round(pixels * 0.125));
        if (glyph == Plus)
        {
            // Matching parity gives the four arms exactly the same length.
            var span = 2 * Math.Round((pixels * 0.86 - stroke) / 2) + stroke;
            rectangles.Add(new Rect(centerX - span / 2, centerY - stroke / 2, span, stroke));
            rectangles.Add(new Rect(centerX - stroke / 2, centerY - span / 2, stroke, span));
        }
        else
        {
            var gap = Math.Max(1, Math.Round(pixels * 0.13));
            var width = Math.Round(pixels * 0.86);
            var left = centerX - width / 2;
            var top = centerY - (3 * stroke + 2 * gap) / 2;
            for (var row = 0; row < 3; row++)
            {
                var y = top + row * (stroke + gap);
                rectangles.Add(new Rect(left, y, stroke, stroke));
                rectangles.Add(new Rect(left + stroke + gap, y, width - stroke - gap, stroke));
            }
        }

        var shape = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var rect in rectangles)
        {
            shape.Children.Add(new RectangleGeometry(
                new Rect(rect.X / scaleX, rect.Y / scaleY, rect.Width / scaleX, rect.Height / scaleY)));
        }
        var bounds = shape.Bounds;
        drawingContext.PushGuidelineSet(new GuidelineSet([bounds.Left], [bounds.Top]));
        drawingContext.DrawGeometry(Foreground, null, shape);
        drawingContext.Pop();
    }

    private static Geometry LoadOutline(string glyph)
    {
        var font = SymbolTypeface.Value;
        if (font is null || glyph.Length != 1 || !font.CharacterToGlyphMap.TryGetValue(glyph[0], out var index))
            return Geometry.Empty;
        var geometry = font.GetGlyphOutline(index, 16, 16);
        geometry.Freeze();
        return geometry;
    }
}
