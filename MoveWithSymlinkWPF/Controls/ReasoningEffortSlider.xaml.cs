using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MigrationCore.Models.Ai;

namespace MoveWithSymlinkWPF.Controls;

/// <summary>
/// 思考等级（reasoning_effort）Fluent 亮色融冰能量滑块控件：
/// 顶部「自动默认」胶囊 + 7 档单调滑轨（关闭 → 极低 → 低 → 中 → 高 → 超高 → 极限），
/// 支持无级连续拖动、松手弹簧阻尼吸附、动态色温像素点阵火焰、鼠标滚轮与方向键切换，
/// 并在收起时立即解绑 CompositionTarget.Rendering 实现 0% CPU 休眠。
/// </summary>
public partial class ReasoningEffortSlider : UserControl
{
    private const double ThumbWidth = 28d;
    private const string ReferenceRepoUrl = "https://github.com/2768651338/dsh-effort-slider";

    private static readonly AiReasoningEffort[] ManualLevels =
    [
        AiReasoningEffort.None,
        AiReasoningEffort.Minimal,
        AiReasoningEffort.Low,
        AiReasoningEffort.Medium,
        AiReasoningEffort.High,
        AiReasoningEffort.XHigh,
        AiReasoningEffort.Max
    ];

    public static readonly DependencyProperty SelectedEffortProperty = DependencyProperty.Register(
        nameof(SelectedEffort),
        typeof(AiReasoningEffort),
        typeof(ReasoningEffortSlider),
        new FrameworkPropertyMetadata(
            AiReasoningEffort.Default,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedEffortChanged));

    public static readonly DependencyProperty SelectEffortCommandProperty = DependencyProperty.Register(
        nameof(SelectEffortCommand),
        typeof(ICommand),
        typeof(ReasoningEffortSlider),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(ReasoningEffortSlider),
        new PropertyMetadata(false, OnIsActiveChanged));

    public AiReasoningEffort SelectedEffort
    {
        get => (AiReasoningEffort)GetValue(SelectedEffortProperty);
        set => SetValue(SelectedEffortProperty, value);
    }

    public ICommand? SelectEffortCommand
    {
        get => (ICommand?)GetValue(SelectEffortCommandProperty);
        set => SetValue(SelectEffortCommandProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>用户点击右上角关闭按钮时触发。</summary>
    public event EventHandler? CloseRequested;

    private readonly EffortEnergyRenderer _renderer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TextBlock[] _tickLabels = new TextBlock[ManualLevels.Length];
    private readonly Ellipse[] _trackDots = new Ellipse[ManualLevels.Length];

    private bool _isRenderingHooked;
    private bool _isDragging;
    private bool _isHoveringTrack;
    private double _lastRenderSec;
    private double _rawPos = 0.5;
    private double _displayPos = 0.5;
    private double _snapTarget = 0.5;
    private double _snapVelocity;
    private AiReasoningEffort _previewEffort = AiReasoningEffort.Default;
    private AiReasoningEffort _lastAnimatedEffort = (AiReasoningEffort)(-1);

    public ReasoningEffortSlider()
    {
        InitializeComponent();
        EnergyImage.Source = _renderer.Bitmap;
        BuildTicksAndDots();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;

        SyncVisuals(SelectedEffort, snapImmediately: true, animateText: false);
    }

    private static void OnSelectedEffortChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ReasoningEffortSlider slider && e.NewValue is AiReasoningEffort effort)
        {
            if (!slider._isDragging)
            {
                slider.SyncVisuals(effort, snapImmediately: false, animateText: slider.IsLoaded);
            }
        }
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ReasoningEffortSlider slider)
        {
            bool active = e.NewValue is true;
            if (active)
            {
                slider.SyncVisuals(slider.SelectedEffort, snapImmediately: true, animateText: false);
                slider._renderer.Reignite(slider._displayPos);
                // 先渲染一帧首屏，避免 Popup 刚展开首帧空白
                slider._renderer.RenderFrame(slider._clock.Elapsed.TotalSeconds, 0.016, slider._displayPos, slider._previewEffort, false);
            }
            slider.UpdateRenderLoopAttachment();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LayoutTicksAndDots();
        SyncVisuals(SelectedEffort, snapImmediately: true, animateText: false);
        UpdateRenderLoopAttachment();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachRenderLoop();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateRenderLoopAttachment();
    }

    private void UpdateRenderLoopAttachment()
    {
        bool shouldRun = IsActive && IsVisible && IsLoaded;
        if (shouldRun && !_isRenderingHooked)
        {
            _lastRenderSec = _clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnCompositionTargetRendering;
            _isRenderingHooked = true;
        }
        else if (!shouldRun && _isRenderingHooked)
        {
            DetachRenderLoop();
        }
    }

    private void DetachRenderLoop()
    {
        if (_isRenderingHooked)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _isRenderingHooked = false;
        }
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastRenderSec, 0.001, 0.05);
        _lastRenderSec = now;

        // 松手后的弹性弹簧阻尼吸附（stiffness = 240, damping = 20）
        if (!_isDragging)
        {
            double diff = _snapTarget - _displayPos;
            if (Math.Abs(diff) > 0.0005 || Math.Abs(_snapVelocity) > 0.001)
            {
                double acc = diff * 240.0 - _snapVelocity * 20.0;
                _snapVelocity += acc * dt;
                _displayPos = Math.Clamp(_displayPos + _snapVelocity * dt, 0.0, 1.0);
                UpdateThumbAndLightLayout();
            }
            else if (Math.Abs(_displayPos - _snapTarget) > double.Epsilon)
            {
                _displayPos = _snapTarget;
                _snapVelocity = 0;
                UpdateThumbAndLightLayout();
            }
        }

        _renderer.RenderFrame(now, dt, _displayPos, _previewEffort, _isDragging);
    }

    private void BuildTicksAndDots()
    {
        TickLabelsCanvas.Children.Clear();
        DotsCanvas.Children.Clear();

        for (int i = 0; i < ManualLevels.Length; i++)
        {
            AiReasoningEffort level = ManualLevels[i];
            EffortVisualSpec spec = EffortEnergyRenderer.GetVisualSpec(level);

            var tb = new TextBlock
            {
                Text = spec.ZhLabel,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                Cursor = Cursors.Hand,
                Tag = level
            };
            tb.MouseLeftButtonDown += TickLabel_OnMouseLeftButtonDown;
            _tickLabels[i] = tb;
            TickLabelsCanvas.Children.Add(tb);

            var dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = new SolidColorBrush(Color.FromArgb(0x58, 0x94, 0xA3, 0xB8)),
                Tag = level
            };
            _trackDots[i] = dot;
            DotsCanvas.Children.Add(dot);
        }
    }

    private void TrackSlot_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TrackInnerGrid.ActualWidth > 0 && TrackInnerGrid.ActualHeight > 0)
        {
            TrackInnerGrid.Clip = new RectangleGeometry(
                new Rect(0, 0, TrackInnerGrid.ActualWidth, TrackInnerGrid.ActualHeight),
                8,
                8);
        }

        LayoutTicksAndDots();
        UpdateThumbAndLightLayout();
    }

    private void LayoutTicksAndDots()
    {
        double trackWidth = TrackInnerGrid.ActualWidth;
        if (trackWidth <= ThumbWidth)
        {
            trackWidth = 264;
        }

        double half = ThumbWidth * 0.5;
        double span = Math.Max(1.0, trackWidth - ThumbWidth);
        double trackHeight = TrackInnerGrid.ActualHeight > 0 ? TrackInnerGrid.ActualHeight : 34.0;

        for (int i = 0; i < ManualLevels.Length; i++)
        {
            double ratio = i / (double)(ManualLevels.Length - 1);
            double centerX = half + ratio * span;

            TextBlock tb = _tickLabels[i];
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double labelW = tb.DesiredSize.Width;
            Canvas.SetLeft(tb, Math.Clamp(centerX - labelW * 0.5, 0, Math.Max(0, trackWidth - labelW)));
            Canvas.SetTop(tb, 0);

            Ellipse dot = _trackDots[i];
            Canvas.SetLeft(dot, centerX - dot.Width * 0.5);
            Canvas.SetTop(dot, (trackHeight - dot.Height) * 0.5);
        }
    }

    private void UpdateThumbAndLightLayout()
    {
        double trackWidth = TrackInnerGrid.ActualWidth;
        if (trackWidth <= ThumbWidth)
        {
            trackWidth = 264;
        }

        double half = ThumbWidth * 0.5;
        double span = Math.Max(1.0, trackWidth - ThumbWidth);
        double centerX = half + _displayPos * span;

        Canvas.SetLeft(ThumbBorder, centerX - half);
        Canvas.SetLeft(PointLightEllipse, centerX - PointLightEllipse.Width * 0.5);
    }

    private void SyncVisuals(AiReasoningEffort effort, bool snapImmediately, bool animateText)
    {
        _previewEffort = effort;
        EffortVisualSpec spec = EffortEnergyRenderer.GetVisualSpec(effort);
        bool isAuto = effort == AiReasoningEffort.Default;

        if (!_isDragging)
        {
            _snapTarget = isAuto ? 0.5 : GetManualRatio(effort);
            if (snapImmediately)
            {
                _displayPos = _snapTarget;
                _rawPos = _snapTarget;
                _snapVelocity = 0;
            }
        }

        Color accentColor = ParseColor(spec.AccentHex);
        Color dotColor = ParseColor(spec.PillDotHex);
        var accentBrush = new SolidColorBrush(accentColor);

        // 标题档位文字与模糊上浮动画
        StatusZhText.Text = spec.ZhLabel;
        StatusEnText.Text = spec.EnLabel;
        StatusZhText.Foreground = accentBrush;
        StatusEnText.Foreground = accentBrush;

        if (animateText && _lastAnimatedEffort != effort && TryFindResource("StatusPopStoryboard") is Storyboard sb)
        {
            sb.Begin(this, isControllable: false);
        }
        _lastAnimatedEffort = effort;

        // 顶部自动默认胶囊状态
        AutoDefaultButton.Tag = isAuto ? "Active" : string.Empty;
        AutoChipDot.Fill = new SolidColorBrush(isAuto ? Color.FromRgb(0x02, 0x84, 0xC7) : Color.FromRgb(0x94, 0xA3, 0xB8));

        // 协议值与档位短描述
        string? wire = AiProviderSettings.ToReasoningEffortWireValue(effort);
        WireValueText.Text = wire is null ? "reasoning_effort = auto" : $"reasoning_effort = \"{wire}\"";
        DescMiniText.Text = spec.ShortDesc;
        DescMiniText.Foreground = accentBrush;

        // 刻度高亮与轨道圆点
        var mutedBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        var mutedDotBrush = new SolidColorBrush(Color.FromArgb(0x58, 0x94, 0xA3, 0xB8));
        for (int i = 0; i < ManualLevels.Length; i++)
        {
            bool active = !isAuto && ManualLevels[i] == effort;
            _tickLabels[i].Foreground = active ? accentBrush : mutedBrush;
            _tickLabels[i].FontWeight = active ? FontWeights.Bold : FontWeights.SemiBold;
            _trackDots[i].Fill = active ? Brushes.White : mutedDotBrush;
            _trackDots[i].Effect = active
                ? new DropShadowEffect { Color = dotColor, BlurRadius = 7, ShadowDepth = 0, Opacity = 0.9 }
                : null;
        }

        // Thumb 外观（自动模式下呈现冰蓝半透待接管态，手动模式下金属磨砂+色温光环）
        ThumbBorder.Opacity = isAuto ? 0.78 : 1.0;
        ThumbGradStart.Color = isAuto ? Color.FromRgb(0xE0, 0xF2, 0xFE) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        ThumbGradMid.Color = isAuto ? Color.FromRgb(0xBA, 0xE6, 0xFD) : Color.FromRgb(0xEC, 0xE7, 0xF8);
        ThumbGradEnd.Color = isAuto ? Color.FromRgb(0x7D, 0xD3, 0xFC) : Color.FromRgb(0xD5, 0xC9, 0xEC);
        ThumbCoreBar.Background = new SolidColorBrush(dotColor);
        ThumbGlowEffect.Color = dotColor;
        ThumbGlowEffect.BlurRadius = _isDragging ? 20 : (effort is AiReasoningEffort.High or AiReasoningEffort.XHigh or AiReasoningEffort.Max ? 16 : 11);
        ThumbGlowEffect.Opacity = effort == AiReasoningEffort.None ? 0.25 : (_isDragging ? 0.8 : 0.5);

        // 点光源与外圈微光色温
        PointLightInnerStop.Color = Color.FromArgb(0x58, dotColor.R, dotColor.G, dotColor.B);
        PointLightMidStop.Color = Color.FromArgb(0x1E, dotColor.R, dotColor.G, dotColor.B);
        OuterGlowStopA.Color = dotColor;
        OuterGlowStopB.Color = accentColor;
        OuterGlowBorder.Opacity = effort switch
        {
            AiReasoningEffort.None => 0.08,
            AiReasoningEffort.High or AiReasoningEffort.XHigh or AiReasoningEffort.Max => 0.34,
            _ => 0.20
        };

        UpdateThumbAndLightLayout();
    }

    private void CommitEffort(AiReasoningEffort effort, bool animateText = true)
    {
        string param = effort.ToString();
        if (SelectEffortCommand is { } cmd && cmd.CanExecute(param))
        {
            cmd.Execute(param);
        }
        else
        {
            SelectedEffort = effort;
        }

        SyncVisuals(effort, snapImmediately: false, animateText: animateText);
    }

    private double PointToRatio(Point p)
    {
        double trackWidth = TrackInnerGrid.ActualWidth;
        if (trackWidth <= ThumbWidth)
        {
            return 0.5;
        }

        double half = ThumbWidth * 0.5;
        double span = Math.Max(1.0, trackWidth - ThumbWidth);
        return Math.Clamp((p.X - half) / span, 0.0, 1.0);
    }

    private static AiReasoningEffort RatioToManualEffort(double ratio)
    {
        int idx = (int)Math.Round(Math.Clamp(ratio, 0.0, 1.0) * (ManualLevels.Length - 1));
        return ManualLevels[Math.Clamp(idx, 0, ManualLevels.Length - 1)];
    }

    private static double GetManualRatio(AiReasoningEffort effort)
    {
        int idx = Array.IndexOf(ManualLevels, effort);
        if (idx < 0)
        {
            return 0.5;
        }
        return idx / (double)(ManualLevels.Length - 1);
    }

    private void AnimateThumbScale(double targetScale)
    {
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var animX = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        var animY = new DoubleAnimation(targetScale, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        ThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        ThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void UpdatePointLightVisibility()
    {
        double targetOpacity = (_isDragging || _isHoveringTrack) ? 0.9 : 0.0;
        PointLightEllipse.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(targetOpacity, TimeSpan.FromMilliseconds(180)));
    }

    private void TrackSlot_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TrackSlotBorder.Focus();
        _isDragging = true;
        TrackSlotBorder.CaptureMouse();
        AnimateThumbScale(0.91);
        UpdatePointLightVisibility();

        double ratio = PointToRatio(e.GetPosition(TrackInnerGrid));
        _rawPos = ratio;
        _displayPos = ratio;
        _snapTarget = ratio;
        _snapVelocity = 0;

        AiReasoningEffort nearest = RatioToManualEffort(ratio);
        SyncVisuals(nearest, snapImmediately: false, animateText: nearest != _previewEffort);
        UpdateThumbAndLightLayout();
        e.Handled = true;
    }

    private void TrackSlot_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        double ratio = PointToRatio(e.GetPosition(TrackInnerGrid));
        _rawPos = ratio;
        _displayPos = ratio;
        _snapTarget = ratio;

        AiReasoningEffort nearest = RatioToManualEffort(ratio);
        if (nearest != _previewEffort)
        {
            SyncVisuals(nearest, snapImmediately: false, animateText: true);
        }
        UpdateThumbAndLightLayout();
    }

    private void TrackSlot_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        FinishDrag();
        e.Handled = true;
    }

    private void TrackSlot_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            FinishDrag();
        }
    }

    private void FinishDrag()
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        if (TrackSlotBorder.IsMouseCaptured)
        {
            TrackSlotBorder.ReleaseMouseCapture();
        }
        AnimateThumbScale(1.0);
        UpdatePointLightVisibility();

        AiReasoningEffort snapped = RatioToManualEffort(_rawPos);
        _snapTarget = GetManualRatio(snapped);
        CommitEffort(snapped, animateText: snapped != _previewEffort);
    }

    private void TrackSlot_OnMouseEnter(object sender, MouseEventArgs e)
    {
        _isHoveringTrack = true;
        UpdatePointLightVisibility();
    }

    private void TrackSlot_OnMouseLeave(object sender, MouseEventArgs e)
    {
        _isHoveringTrack = false;
        UpdatePointLightVisibility();
    }

    private void TrackSlot_OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        StepManualEffort(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void TrackSlot_OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right:
            case Key.Up:
                StepManualEffort(1);
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Down:
                StepManualEffort(-1);
                e.Handled = true;
                break;
            case Key.Home:
                CommitEffort(ManualLevels[0]);
                e.Handled = true;
                break;
            case Key.End:
                CommitEffort(ManualLevels[^1]);
                e.Handled = true;
                break;
        }
    }

    private void StepManualEffort(int delta)
    {
        int idx = Array.IndexOf(ManualLevels, _previewEffort);
        if (idx < 0)
        {
            idx = delta > 0 ? 3 : 2; // 从自动默认微调时，顺滑落入低/中档
        }
        else
        {
            idx = Math.Clamp(idx + delta, 0, ManualLevels.Length - 1);
        }
        CommitEffort(ManualLevels[idx]);
    }

    private void TickLabel_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock { Tag: AiReasoningEffort effort })
        {
            CommitEffort(effort);
            e.Handled = true;
        }
    }

    private void AutoDefaultButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedEffort == AiReasoningEffort.Default)
        {
            CommitEffort(AiReasoningEffort.Medium);
        }
        else
        {
            CommitEffort(AiReasoningEffort.Default);
        }
    }

    private void ClosePopupButton_OnClick(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CreditLinkButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReferenceRepoUrl) { UseShellExecute = true });
        }
        catch
        {
            // 无默认浏览器或受限环境时静默忽略
        }
    }

    private static Color ParseColor(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);
}
