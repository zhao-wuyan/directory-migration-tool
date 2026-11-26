using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Views;

public partial class CleanupPage : UserControl
{
    private const double SplitterWidth = 10d;
    private const double PreferredLeftMinWidth = 560d;
    private const double PreferredDrawerMinWidth = 300d;
    private const double DefaultDrawerWidth = 368d;

    private double _savedDrawerWidth = DefaultDrawerWidth;

    public CleanupViewModel ViewModel { get; }

    public CleanupPage()
    {
        InitializeComponent();
        ViewModel = new CleanupViewModel();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupViewModel.IsAiDrawerOpen))
        {
            ApplyAiDrawerLayout();
        }
    }

    private void CleanupRootGrid_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyAiDrawerLayout();
    }

    private void AiDrawerSplitter_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel.IsAiDrawerOpen && AiDrawerColumn.ActualWidth > 0)
        {
            _savedDrawerWidth = AiDrawerColumn.ActualWidth;
        }
    }

    private void ApplyAiDrawerLayout()
    {
        if (!ViewModel.IsAiDrawerOpen)
        {
            MainWorkspaceColumn.MinWidth = 0;
            AiDrawerSplitterColumn.Width = new GridLength(0);
            AiDrawerColumn.MinWidth = 0;
            AiDrawerColumn.MaxWidth = double.PositiveInfinity;
            AiDrawerColumn.Width = new GridLength(0);
            return;
        }

        DrawerResizeBounds bounds = DrawerResizeRangeHelper.CalculateBounds(
            CleanupRootGrid.ActualWidth,
            SplitterWidth,
            PreferredLeftMinWidth,
            PreferredDrawerMinWidth);

        MainWorkspaceColumn.MinWidth = bounds.LeftMinWidth;
        AiDrawerSplitterColumn.Width = new GridLength(SplitterWidth);
        AiDrawerColumn.MinWidth = bounds.RightMinWidth;
        AiDrawerColumn.MaxWidth = bounds.RightMaxWidth;
        double clamped = DrawerResizeRangeHelper.ClampWidth(_savedDrawerWidth, bounds);
        AiDrawerColumn.Width = new GridLength(clamped, GridUnitType.Pixel);
    }

    private void RuleCard_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CleanupRuleItemViewModel rule })
        {
            ViewModel.SelectRuleCommand.Execute(rule);
            e.Handled = true;
        }
    }

    private void RuleCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        // 点击勾选框时同时选中该卡片到右侧抽屉，方便立即阅读风险说明与明细
        if (sender is FrameworkElement { DataContext: CleanupRuleItemViewModel rule })
        {
            ViewModel.SelectRuleCommand.Execute(rule);
        }
        e.Handled = true;
    }

    private void GroupHeader_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CleanupRuleGroupViewModel group })
        {
            ViewModel.ToggleExpandGroupCommand.Execute(group);
            e.Handled = true;
        }
    }

    private void GroupCheckBox_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CleanupRuleGroupViewModel group } cb)
        {
            ViewModel.ToggleGroupCheckCommand.Execute(group);
            cb.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
        }
        e.Handled = true;
    }
}
