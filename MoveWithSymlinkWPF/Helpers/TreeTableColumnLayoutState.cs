using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MoveWithSymlinkWPF.Helpers;

/// <summary>
/// 磁盘扫描目录树表格的响应式列宽状态（作为页面静态资源供表头与虚拟化行模板 O(1) 直接绑定，零视觉树向上遍历）：
/// 宽屏时展示独立左对齐的浅灰「用途说明」列，消除目录名长短不一与层级缩进带来的视觉参差；
/// 窄屏或右侧 AI 抽屉拉宽时自动平滑收起，保证目录名称列始终清晰可读。
/// </summary>
public sealed class TreeTableColumnLayoutState : ObservableObject
{
    private const double FixedColumnsAndChromeWidth = 326d; // 实际占用(82) + 占比(96) + 准入(108) + 内边距(24) + 滚动条余量(16)
    private const double ShowSummaryMinTableWidth = 700d;
    private const double MinSummaryColumnWidth = 160d;
    private const double MaxSummaryColumnWidth = 320d;
    private const double SummaryShareOfRemaining = 0.42d;

    private double _lastPixelWidth = -1d;
    private GridLength _summaryColumnWidth = new(0d);
    private Visibility _summaryColumnVisibility = Visibility.Collapsed;

    public GridLength SummaryColumnWidth
    {
        get => _summaryColumnWidth;
        private set => SetProperty(ref _summaryColumnWidth, value);
    }

    public Visibility SummaryColumnVisibility
    {
        get => _summaryColumnVisibility;
        private set => SetProperty(ref _summaryColumnVisibility, value);
    }

    public void UpdateForTableWidth(double tableWidth)
    {
        double targetPixels = CalculateSummaryColumnWidth(tableWidth);
        if (Math.Abs(targetPixels - _lastPixelWidth) < 0.5d)
        {
            return;
        }

        _lastPixelWidth = targetPixels;
        SummaryColumnWidth = new GridLength(targetPixels, GridUnitType.Pixel);
        SummaryColumnVisibility = targetPixels > 0d ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 纯函数计算响应式用途说明列宽（像素，4px 量化避免拖动分隔条时频繁触发布局抖动）。
    /// 返回 0 表示当前表格宽度不足，应自动隐藏该列。
    /// </summary>
    public static double CalculateSummaryColumnWidth(double tableWidth)
    {
        if (double.IsNaN(tableWidth) || double.IsInfinity(tableWidth) || tableWidth < ShowSummaryMinTableWidth)
        {
            return 0d;
        }

        double remaining = tableWidth - FixedColumnsAndChromeWidth;
        double raw = remaining * SummaryShareOfRemaining;
        double quantized = Math.Round(raw / 4d) * 4d;
        return Math.Clamp(quantized, MinSummaryColumnWidth, MaxSummaryColumnWidth);
    }
}
