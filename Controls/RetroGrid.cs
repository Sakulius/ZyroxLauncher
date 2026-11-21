using System.Windows;
using System.Windows.Media;

namespace Zyrox.Controls;

// Perspective floor and ceiling grid that scrolls toward the viewer.
public class RetroGrid : FrameworkElement
{
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(RetroGrid),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    private const int Columns = 14;
    private const int Rows = 14;
    private const double SecondsPerRow = 1.6;

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private bool _animate;
    private bool _hooked;

    public RetroGrid()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => UpdateHook();
        Unloaded += (_, _) => UpdateHook();
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public bool Animate
    {
        get => _animate;
        set
        {
            _animate = value;
            UpdateHook();
        }
    }

    private void UpdateHook()
    {
        var hook = _animate && IsLoaded;
        if (hook == _hooked)
            return;
        _hooked = hook;
        if (hook)
            CompositionTarget.Rendering += OnFrame;
        else
            CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        var pen = new Pen(LineBrush, 1);
        pen.Freeze();
        var phase = _clock.Elapsed.TotalSeconds / SecondsPerRow % 1;
        DrawPlane(dc, pen, w, h * 0.58, h, phase);
        DrawPlane(dc, pen, w, h * 0.42, 0, phase);
    }

    // Draws one plane from the horizon line toward the edge (floor: edge below, ceiling: edge above).
    private static void DrawPlane(DrawingContext dc, Pen pen, double w, double horizon, double edge, double phase)
    {
        var cx = w / 2;
        var depth = edge - horizon;
        var spread = w * 1.6 / Columns;

        for (var i = -Columns; i <= Columns; i++)
            dc.DrawLine(pen, new Point(cx + i * spread * 0.08, horizon), new Point(cx + i * spread, edge));

        for (var k = 0; k < Rows; k++)
        {
            // z runs 1..Rows+1; rows move from far (large z) to near (small z) as phase grows
            var z = k + 1 - phase;
            if (z <= 0.05)
                continue;
            var y = horizon + depth / z * 0.9;
            if ((depth > 0 && y > edge) || (depth < 0 && y < edge))
                continue;
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
        }
    }
}
