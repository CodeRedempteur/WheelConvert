using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using WheelConvert.Core;

namespace WheelConvert.UI;

/// <summary>Roue radiale à N secteurs (utilisée par la fenêtre flottante et l'aperçu du designer).</summary>
public sealed class WheelControl : Canvas
{
    public const double Diameter = 300, C = 150, RInner = 52, ROuter = 140;

    private static readonly Brush Normal = Frozen(0xEE, 0x1C, 0x1C, 0x22);
    private static readonly Brush HubBrush = Frozen(0xF8, 0x12, 0x12, 0x16);
    private static readonly Brush Stroke = Frozen(0x40, 0xFF, 0xFF, 0xFF);
    private static readonly Brush SubBrush = Frozen(0xA8, 0xFF, 0xFF, 0xFF);
    private static readonly Brush DefaultAccent = Frozen(0xFF, 0x2D, 0x7D, 0xFF);
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji, Segoe UI Symbol, Segoe UI");

    private readonly List<Path> _sectors = new();
    private readonly List<Brush> _accents = new();
    private readonly StackPanel _hubText = new() { IsHitTestVisible = false };
    private readonly TextBlock _title = new()
    {
        Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, FontFamily = EmojiFont,
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = RInner * 2 - 16
    };
    private readonly TextBlock _hint = new()
    {
        Foreground = SubBrush, FontSize = 9.5,
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = RInner * 2 - 14
    };

    public int HoverIndex { get; private set; } = -1;
    public int Count => _sectors.Count;

    public WheelControl()
    {
        Width = Diameter;
        Height = Diameter;
        Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black };
        _hubText.Children.Add(_title);
        _hubText.Children.Add(_hint);
        SetItems(Array.Empty<WheelEntry>(), "", "");
    }

    public void SetItems(IReadOnlyList<WheelEntry> items, string title, string hint)
    {
        Children.Clear();
        _sectors.Clear();
        _accents.Clear();
        HoverIndex = -1;

        int n = items.Count;
        if (n == 0)
        {
            Children.Add(new Path { Data = Ring(), Fill = Normal, Stroke = Stroke, StrokeThickness = 1 });
        }
        else
        {
            double span = 360.0 / n;
            double gap = n == 1 ? 0 : 1.0;
            for (int i = 0; i < n; i++)
            {
                var item = items[i];
                double mid = i * span;
                var path = new Path
                {
                    Data = n == 1 ? Ring() : Sector(mid - span / 2 + gap, mid + span / 2 - gap),
                    Fill = Normal, Stroke = Stroke, StrokeThickness = 1
                };
                _sectors.Add(path);
                _accents.Add(ParseBrush(item.Color));
                Children.Add(path);

                var content = new StackPanel { IsHitTestVisible = false };
                if (!string.IsNullOrEmpty(item.Glyph))
                    content.Children.Add(new TextBlock
                    {
                        Text = item.Glyph, FontFamily = EmojiFont, FontSize = n <= 6 ? 22 : 18,
                        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center
                    });
                content.Children.Add(new TextBlock
                {
                    Text = item.Label, FontSize = n <= 6 ? 14 : 12, FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = n <= 4 ? 96 : 72
                });
                if (!string.IsNullOrEmpty(item.Sub))
                    content.Children.Add(new TextBlock
                    {
                        Text = item.Sub, FontSize = 10, Foreground = SubBrush,
                        HorizontalAlignment = HorizontalAlignment.Center
                    });

                Place(content, Polar(mid, (RInner + ROuter) / 2));
                Children.Add(content);
            }
        }

        double hubR = RInner - 5;
        var hub = new Ellipse { Width = hubR * 2, Height = hubR * 2, Fill = HubBrush, Stroke = Stroke, StrokeThickness = 1 };
        SetLeft(hub, C - hubR);
        SetTop(hub, C - hubR);
        Children.Add(hub);

        Children.Add(_hubText);
        SetTitle(title, hint);
    }

    public void SetTitle(string title, string hint)
    {
        _title.Text = title;
        _hint.Text = hint;
        _hint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        Place(_hubText, new Point(C, C));
    }

    public void SetHover(int index)
    {
        if (index < 0 || index >= _sectors.Count) index = -1;
        if (index == HoverIndex) return;
        if (HoverIndex >= 0 && HoverIndex < _sectors.Count) _sectors[HoverIndex].Fill = Normal;
        if (index >= 0) _sectors[index].Fill = _accents[index];
        HoverIndex = index;
    }

    /// Index du secteur sous le point (coordonnées locales), -1 si centre ou dehors.
    public int HitTest(Point p)
    {
        int n = Count;
        if (n == 0) return -1;
        double dx = p.X - C, dy = p.Y - C, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < RInner || d > ROuter + 14) return -1;
        if (n == 1) return 0;
        double angle = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360; // 0 = haut, sens horaire
        double span = 360.0 / n;
        return (int)(((angle + span / 2) % 360) / span) % n;
    }

    // ---------- Géométrie ----------

    private static Point Polar(double deg, double r)
    {
        double rad = deg * Math.PI / 180;
        return new Point(C + r * Math.Sin(rad), C - r * Math.Cos(rad));
    }

    private static Geometry Sector(double a0, double a1)
    {
        bool large = a1 - a0 > 180;
        var fig = new PathFigure { StartPoint = Polar(a0, ROuter), IsClosed = true };
        fig.Segments.Add(new ArcSegment(Polar(a1, ROuter), new Size(ROuter, ROuter), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(Polar(a1, RInner), true));
        fig.Segments.Add(new ArcSegment(Polar(a0, RInner), new Size(RInner, RInner), 0, large, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }

    private static Geometry Ring()
    {
        var g = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(C, C), ROuter, ROuter),
            new EllipseGeometry(new Point(C, C), RInner, RInner));
        g.Freeze();
        return g;
    }

    private static void Place(FrameworkElement el, Point center)
    {
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(el, center.X - el.DesiredSize.Width / 2);
        SetTop(el, center.Y - el.DesiredSize.Height / 2);
    }

    private static Brush ParseBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return DefaultAccent;
        try
        {
            var b = (Brush?)new BrushConverter().ConvertFromString(hex);
            if (b is null) return DefaultAccent;
            b.Freeze();
            return b;
        }
        catch { return DefaultAccent; }
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
