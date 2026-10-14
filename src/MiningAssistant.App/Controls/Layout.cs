using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace MiningAssistant.App.Controls;

/// <summary>
/// CSS "flex-wrap + flex: 1 1 auto": children keep their natural width, wrap to new rows when they
/// do not fit, and each row's spare width is shared equally so the row is filled edge to edge.
/// </summary>
public sealed class FlexWrapPanel : Panel
{
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(FlexWrapPanel),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    private List<List<UIElement>> Rows(double width)
    {
        var rows = new List<List<UIElement>>();
        var row = new List<UIElement>();
        double used = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var w = child.DesiredSize.Width;
            if (row.Count > 0 && used + Gap + w > width)
            {
                rows.Add(row);
                row = new List<UIElement>();
                used = 0;
            }
            used += (row.Count > 0 ? Gap : 0) + w;
            row.Add(child);
        }
        if (row.Count > 0) rows.Add(row);
        return rows;
    }

    protected override Size MeasureOverride(Size available)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new Size(available.Width, double.PositiveInfinity));
        var width = double.IsInfinity(available.Width) ? InternalChildren.Cast<UIElement>().Sum(c => c.DesiredSize.Width + Gap) : available.Width;
        var rows = Rows(width);
        var height = rows.Sum(r => r.Max(c => c.DesiredSize.Height)) + Gap * Math.Max(0, rows.Count - 1);
        return new Size(double.IsInfinity(available.Width) ? width : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        foreach (var row in Rows(final.Width))
        {
            var natural = row.Sum(c => c.DesiredSize.Width) + Gap * (row.Count - 1);
            var extra = Math.Max(0, final.Width - natural) / row.Count;
            var h = row.Max(c => c.DesiredSize.Height);
            double x = 0;
            foreach (var c in row)
            {
                var w = c.DesiredSize.Width + extra;
                c.Arrange(new Rect(x, y, w, h));
                x += w + Gap;
            }
            y += h + Gap;
        }
        return final;
    }
}

/// <summary>
/// CSS "grid-template-columns: repeat(auto-fit, minmax(500px, 1fr)); align-items: stretch" for two
/// children: side by side with equal width and height when there is room, stacked otherwise.
/// </summary>
public sealed class TwoColumnPanel : Panel
{
    public static readonly DependencyProperty GapProperty =
        DependencyProperty.Register(nameof(Gap), typeof(double), typeof(TwoColumnPanel),
            new FrameworkPropertyMetadata(20.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinColumnWidthProperty =
        DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(TwoColumnPanel),
            new FrameworkPropertyMetadata(480.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }

    private bool SideBySide(double width) => InternalChildren.Count >= 2 && width >= 2 * MinColumnWidth + Gap;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 2 * MinColumnWidth + Gap : available.Width;
        if (SideBySide(width))
        {
            var col = (width - Gap) / 2;
            double h = 0;
            foreach (UIElement c in InternalChildren)
            {
                c.Measure(new Size(col, double.PositiveInfinity));
                h = Math.Max(h, c.DesiredSize.Height);
            }
            return new Size(width, h);
        }
        double total = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(width, double.PositiveInfinity));
            total += c.DesiredSize.Height;
        }
        return new Size(width, total + Gap * Math.Max(0, InternalChildren.Count - 1));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (SideBySide(final.Width))
        {
            var col = (final.Width - Gap) / 2;
            for (var i = 0; i < InternalChildren.Count; i++)
                InternalChildren[i].Arrange(new Rect(i * (col + Gap), 0, col, final.Height));
            return final;
        }
        double y = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Arrange(new Rect(0, y, final.Width, c.DesiredSize.Height));
            y += c.DesiredSize.Height + Gap;
        }
        return final;
    }
}

/// <summary>Logo watermark width: about three quarters of the window, at most 720 px (as in the design).</summary>
public sealed class WatermarkWidth : IValueConverter
{
    public static readonly WatermarkWidth Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double w && w > 0 ? Math.Min(720, w * (w < 640 ? 0.68 : 0.78)) : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Hooks the caption-bar buttons of the "VorxenWindow" style (pure WPF, no native calls).</summary>
public static class VorxenChrome
{
    public static void Attach(Window w)
    {
        w.CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(w)));
        w.CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(w)));
        w.CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) =>
        {
            if (w.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(w);
            else SystemCommands.MaximizeWindow(w);
        }));
    }
}
