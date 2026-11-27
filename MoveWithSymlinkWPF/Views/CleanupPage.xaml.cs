using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private CleanupRuleItemViewModel? _pendingScrollToRule;

    public CleanupViewModel ViewModel { get; }

    public CleanupPage()
    {
        InitializeComponent();
        ViewModel = new CleanupViewModel();
        DataContext = ViewModel;
        ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        ViewModel.RuleLocateRequested += ViewModel_OnRuleLocateRequested;
        IsVisibleChanged += CleanupPage_OnIsVisibleChanged;
    }

    private void ViewModel_OnRuleLocateRequested(object? sender, CleanupRuleItemViewModel rule)
    {
        _pendingScrollToRule = rule;
        if (IsVisible)
        {
            ScheduleScrollToPendingRule();
        }
    }

    private void CleanupPage_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _pendingScrollToRule is not null)
        {
            ScheduleScrollToPendingRule();
        }
    }

    private void ScheduleScrollToPendingRule()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            CleanupRuleItemViewModel? target = _pendingScrollToRule;
            if (target is null || !IsVisible) return;

            RulesEntriesItemsControl.UpdateLayout();
            RulesListScrollViewer.UpdateLayout();
            FrameworkElement? cardElement = FindRuleCardElement(RulesEntriesItemsControl, target);
            if (cardElement is null) return;

            _pendingScrollToRule = null;
            try
            {
                GeneralTransform transform = cardElement.TransformToAncestor(RulesListScrollViewer);
                Point relativeTop = transform.Transform(new Point(0, 0));
                double absoluteY = RulesListScrollViewer.VerticalOffset + relativeTop.Y;
                double viewportBias = Math.Max(24d, RulesListScrollViewer.ViewportHeight * 0.22);
                double desiredOffset = Math.Clamp(absoluteY - viewportBias, 0d, RulesListScrollViewer.ScrollableHeight);
                RulesListScrollViewer.ScrollToVerticalOffset(desiredOffset);
            }
            catch
            {
                cardElement.BringIntoView();
            }
        });
    }

    private static FrameworkElement? FindRuleCardElement(DependencyObject root, CleanupRuleItemViewModel target)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject current = stack.Pop();
            if (current is Border border && ReferenceEquals(border.DataContext, target))
            {
                return border;
            }

            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int i = count - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(current, i));
            }
        }
        return null;
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
