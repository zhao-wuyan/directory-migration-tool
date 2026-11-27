using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class DiskScanPage : UserControl
{
    private const double SplitterWidth = 12d;
    private const double PreferredLeftMinWidth = 480d;
    private const double PreferredRightMinWidth = 310d;
    private const double HardRightMaxWidth = 920d;
    private const double MaxRightRatio = 0.55d;
    private readonly TreeTableColumnLayoutState _treeColumnLayout;

    public DiskScanPage()
    {
        InitializeComponent();
        _treeColumnLayout = (TreeTableColumnLayoutState)Resources["TreeColumnLayout"];
        DataContext = new DiskScanViewModel();
        Loaded += (_, _) =>
        {
            ApplyResponsiveSplitBounds(TreeMainSplitGrid.ActualWidth);
            _treeColumnLayout.UpdateForTableWidth(TreeTableCard.ActualWidth);
        };
    }

    private void TreeMainSplitGrid_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            ApplyResponsiveSplitBounds(e.NewSize.Width);
        }
    }

    private void TreeTableCard_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            _treeColumnLayout.UpdateForTableWidth(e.NewSize.Width);
        }
    }

    private void TreeDrawerGridSplitter_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TreeLeftColumn.Width = new GridLength(64, GridUnitType.Star);
        TreeRightColumn.Width = new GridLength(36, GridUnitType.Star);
        ApplyResponsiveSplitBounds(TreeMainSplitGrid.ActualWidth);
        e.Handled = true;
    }

    private void ApplyResponsiveSplitBounds(double containerWidth)
    {
        if (containerWidth <= 0)
        {
            return;
        }

        DrawerResizeBounds bounds = DrawerResizeRangeHelper.CalculateBounds(
            containerWidth,
            SplitterWidth,
            PreferredLeftMinWidth,
            PreferredRightMinWidth,
            HardRightMaxWidth,
            MaxRightRatio);

        TreeLeftColumn.MinWidth = bounds.LeftMinWidth;
        TreeRightColumn.MinWidth = bounds.RightMinWidth;
        TreeRightColumn.MaxWidth = bounds.RightMaxWidth;
    }
}
