using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class AiAssistantPage : UserControl
{
    private const double DefaultDrawerWidth = 374d;
    private const double SplitterWidth = 14d;
    private const double PreferredLeftMinWidth = 500d;
    private const double PreferredRightMinWidth = 320d;
    private const double HardRightMaxWidth = 920d;
    private const double MaxRightRatio = 0.55d;

    private double _preferredDrawerWidth = DefaultDrawerWidth;

    public AiAssistantPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ApplyResponsiveDrawerBounds(RootLayoutGrid.ActualWidth);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AiAssistantViewModel oldVm)
        {
            oldVm.PendingApiKeyAccessor = null;
            oldVm.ClearPendingApiKeyBoxAction = null;
        }

        if (e.NewValue is AiAssistantViewModel newVm)
        {
            newVm.PendingApiKeyAccessor = () => ApiKeyPasswordBox.Password;
            newVm.ClearPendingApiKeyBoxAction = () => ApiKeyPasswordBox.Clear();
        }
    }

    private void RootLayoutGrid_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            ApplyResponsiveDrawerBounds(e.NewSize.Width);
        }
    }

    private void DrawerGridSplitter_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        double currentWidth = RightDrawerColumn.ActualWidth > 0
            ? RightDrawerColumn.ActualWidth
            : RightDrawerColumn.Width.Value;
        DrawerResizeBounds bounds = CalculateCurrentBounds(RootLayoutGrid.ActualWidth);
        double clamped = DrawerResizeRangeHelper.ClampWidth(currentWidth, bounds);

        _preferredDrawerWidth = clamped;
        RightDrawerColumn.Width = new GridLength(clamped, GridUnitType.Pixel);
    }

    private void DrawerGridSplitter_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _preferredDrawerWidth = DefaultDrawerWidth;
        ApplyResponsiveDrawerBounds(RootLayoutGrid.ActualWidth);
        e.Handled = true;
    }

    private void ApplyResponsiveDrawerBounds(double containerWidth)
    {
        if (containerWidth <= 0)
        {
            return;
        }

        DrawerResizeBounds bounds = CalculateCurrentBounds(containerWidth);
        LeftMainColumn.MinWidth = bounds.LeftMinWidth;
        RightDrawerColumn.MinWidth = bounds.RightMinWidth;
        RightDrawerColumn.MaxWidth = bounds.RightMaxWidth;

        // 窗口缩小时临时夹紧防止裁切，再次最大化时平滑恢复用户上次拖动的偏好宽度
        double targetWidth = DrawerResizeRangeHelper.ClampWidth(_preferredDrawerWidth, bounds);
        RightDrawerColumn.Width = new GridLength(targetWidth, GridUnitType.Pixel);
    }

    private static DrawerResizeBounds CalculateCurrentBounds(double containerWidth) =>
        DrawerResizeRangeHelper.CalculateBounds(
            containerWidth,
            SplitterWidth,
            PreferredLeftMinWidth,
            PreferredRightMinWidth,
            HardRightMaxWidth,
            MaxRightRatio);
}
