namespace MoveWithSymlinkWPF.Helpers;

/// <summary>
/// 右侧面板（如 AI 分析助手抽屉、目录详情卡）可拖动宽度的动态边界约束结果。
/// </summary>
public readonly record struct DrawerResizeBounds(
    double LeftMinWidth,
    double RightMinWidth,
    double RightMaxWidth);

/// <summary>
/// 计算并夹紧左右分栏中右侧抽屉的合法宽度范围：
/// 1. 保证左侧主体保留最小可读宽度，不被过度挤压；
/// 2. 限制右侧最大占比（默认 ≤ 55%）与绝对阅读上限（默认 ≤ 920px），最大化窗口时大幅释放阅读空间；
/// 3. 窗口极窄时平滑回退，防止左右最小宽度之和超过容器可用宽度造成右侧溢出裁切。
/// </summary>
public static class DrawerResizeRangeHelper
{
    public static DrawerResizeBounds CalculateBounds(
        double containerWidth,
        double splitterWidth,
        double preferredLeftMinWidth,
        double preferredRightMinWidth,
        double hardRightMaxWidth = 920d,
        double maxRightRatio = 0.55d)
    {
        if (double.IsNaN(containerWidth) || double.IsInfinity(containerWidth) || containerWidth <= 0)
        {
            return new DrawerResizeBounds(
                preferredLeftMinWidth,
                preferredRightMinWidth,
                Math.Max(preferredRightMinWidth, hardRightMaxWidth));
        }

        double availableWidth = Math.Max(0, containerWidth - Math.Max(0, splitterWidth));
        if (availableWidth <= 0)
        {
            return new DrawerResizeBounds(0, 0, 0);
        }

        double ratio = Math.Clamp(maxRightRatio, 0.25d, 0.85d);

        // 常规与宽屏/最大化场景：左侧严格保底，右侧在 [minRight, min(hardMax, available - minLeft, available * ratio)] 内自由拖动
        if (availableWidth >= preferredLeftMinWidth + preferredRightMinWidth)
        {
            double maxByLeft = availableWidth - preferredLeftMinWidth;
            double maxByRatio = availableWidth * ratio;
            double effectiveRightMax = Math.Min(hardRightMaxWidth, Math.Min(maxByLeft, maxByRatio));
            effectiveRightMax = Math.Max(preferredRightMinWidth, effectiveRightMax);

            return new DrawerResizeBounds(
                LeftMinWidth: preferredLeftMinWidth,
                RightMinWidth: preferredRightMinWidth,
                RightMaxWidth: effectiveRightMax);
        }

        // 极窄窗口安全回退：确保 LeftMinWidth + RightMinWidth <= availableWidth，绝不把右侧发送按钮裁切出窗口
        double fallbackRightMin = Math.Min(preferredRightMinWidth, Math.Max(200d, availableWidth * 0.36d));
        fallbackRightMin = Math.Min(fallbackRightMin, availableWidth);
        double fallbackRightMax = Math.Max(fallbackRightMin, Math.Min(hardRightMaxWidth, availableWidth * ratio));
        fallbackRightMax = Math.Min(fallbackRightMax, availableWidth);
        double fallbackLeftMin = Math.Max(0d, Math.Min(preferredLeftMinWidth, availableWidth - fallbackRightMin));

        return new DrawerResizeBounds(
            LeftMinWidth: fallbackLeftMin,
            RightMinWidth: fallbackRightMin,
            RightMaxWidth: fallbackRightMax);
    }

    public static double ClampWidth(double desiredWidth, DrawerResizeBounds bounds)
    {
        if (bounds.RightMaxWidth <= 0)
        {
            return 0;
        }

        double min = Math.Min(bounds.RightMinWidth, bounds.RightMaxWidth);
        return Math.Clamp(desiredWidth, min, bounds.RightMaxWidth);
    }
}
