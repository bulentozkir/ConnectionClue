using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConnectionClue.App.Controls;

/// <summary>
/// Newspaper columns: children keep their reading order (down the first column, then the second), and the split point
/// balances the two column heights so long lists fit without scrolling. A child marked KeepWithNext (a heading) never
/// ends a column. Below WideThreshold everything stacks in one column. Mirrors automatically in RTL.
/// </summary>
public sealed class FlowColumns : Panel
{
    public static readonly DependencyProperty KeepWithNextProperty = DependencyProperty.RegisterAttached(
        "KeepWithNext", typeof(bool), typeof(FlowColumns), new FrameworkPropertyMetadata(false));

    public static bool GetKeepWithNext(DependencyObject element) => (bool)element.GetValue(KeepWithNextProperty);
    public static void SetKeepWithNext(DependencyObject element, bool value) => element.SetValue(KeepWithNextProperty, value);

    public double WideThreshold { get; set; } = 760;
    public double ColumnGap { get; set; } = 12;

    /// <summary>Fewer children than this stay in one column: a short list reads better than two half-empty columns.</summary>
    public int MinItemsForColumns { get; set; } = 1;

    /// <summary>Width limit for the single column, so lines stay readable on wide windows.</summary>
    public double SingleColumnMaxWidth { get; set; } = double.PositiveInfinity;

    private int _split; // first child of the second column; 0 = one column

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Visible();
        _split = 0;
        if (children.Count > 1 && children.Count >= MinItemsForColumns && !double.IsInfinity(availableSize.Width) && availableSize.Width >= WideThreshold)
        {
            double column = (availableSize.Width - ColumnGap) / 2;
            var heights = children.Select(c => { c.Measure(new Size(column, double.PositiveInfinity)); return c.DesiredSize.Height; }).ToArray();
            double total = heights.Sum(), first = 0, best = double.MaxValue;
            for (int k = 1; k < children.Count; k++)
            {
                first += heights[k - 1];
                if (KeepsWithNext(children[k - 1])) continue;
                double tallest = Math.Max(first, total - first);
                if (tallest < best) (best, _split) = (tallest, k);
            }
            if (_split > 0) return new Size(availableSize.Width, best);
        }

        double height = 0, width = 0, single = Math.Min(availableSize.Width, SingleColumnMaxWidth);
        foreach (var c in children)
        {
            c.Measure(new Size(single, double.PositiveInfinity));
            height += c.DesiredSize.Height;
            width = Math.Max(width, c.DesiredSize.Width);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = Visible();
        double column = _split > 0 ? (finalSize.Width - ColumnGap) / 2 : Math.Min(finalSize.Width, SingleColumnMaxWidth), x = 0, y = 0;
        for (int i = 0; i < children.Count; i++)
        {
            if (i == _split && _split > 0) (x, y) = (column + ColumnGap, 0);
            children[i].Arrange(new Rect(x, y, column, children[i].DesiredSize.Height));
            y += children[i].DesiredSize.Height;
        }
        return finalSize;
    }

    // Items controls wrap each item in a ContentPresenter; the marker sits on the template root.
    private static bool KeepsWithNext(UIElement child) => GetKeepWithNext(child)
        || child is ContentPresenter p && VisualTreeHelper.GetChildrenCount(p) > 0 && GetKeepWithNext(VisualTreeHelper.GetChild(p, 0));

    private List<UIElement> Visible() => [.. InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed)];
}
