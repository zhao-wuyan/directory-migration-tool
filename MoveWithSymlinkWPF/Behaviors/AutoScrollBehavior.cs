using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Xaml.Behaviors;

namespace MoveWithSymlinkWPF.Behaviors;

/// <summary>
/// ScrollViewer 自动滚动行为
/// 默认自动滚动到最新内容，用户向上滚动一定距离后取消锁定，滚回底部后重新锁定
/// </summary>
public class AutoScrollBehavior : Behavior<ScrollViewer>
{
    private bool _autoScroll = true;
    private const double UnlockThreshold = 50; // 向上滚动超过50像素则取消锁定

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        AssociatedObject.ScrollChanged -= OnScrollChanged;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var scrollViewer = AssociatedObject;

        // 如果内容高度变化（新增日志）
        if (e.ExtentHeightChange > 0)
        {
            // 如果处于自动滚动模式，滚动到底部
            if (_autoScroll)
            {
                scrollViewer.ScrollToEnd();
            }
        }
        
        // 检测用户滚动行为
        if (e.ExtentHeightChange == 0) // 只有在内容高度不变时才检测用户滚动
        {
            // 计算距离底部的距离
            double offset = scrollViewer.VerticalOffset;
            double viewport = scrollViewer.ViewportHeight;
            double extent = scrollViewer.ExtentHeight;
            double distanceFromBottom = extent - (offset + viewport);

            // 如果用户向上滚动超过阈值，取消自动滚动
            if (distanceFromBottom > UnlockThreshold && _autoScroll)
            {
                _autoScroll = false;
            }
            // 如果用户滚动到底部（容差5像素），重新启用自动滚动
            else if (distanceFromBottom <= 5 && !_autoScroll)
            {
                _autoScroll = true;
            }
        }
    }
}

