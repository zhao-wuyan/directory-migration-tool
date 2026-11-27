using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MoveWithSymlinkWPF.Controls;

/// <summary>
/// 长按确认按钮语义色板。
/// </summary>
public enum HoldButtonTone
{
    Danger,
    Warning,
    Primary
}

/// <summary>
/// 免打字防误触长按确认按钮：
/// - 当 IsHoldRequired = true 时，需按住左键（或聚焦后按住 Space 键）达到 HoldDurationSeconds 才触发确认，
///   期间按钮内部平滑填充进度条并显示剩余秒数，中途松手或指针移出立即中断归零；
/// - 当 IsHoldRequired = false 时，表现为常规单击按钮；
/// - 永不响应 Enter 键，确保弹窗默认「取消」按钮的键盘安全红线。
/// </summary>
public partial class HoldConfirmButton : UserControl
{
    private static readonly SolidColorBrush DisabledBg = Freeze(Color.FromRgb(0xF1, 0xF5, 0xF9));
    private static readonly SolidColorBrush DisabledBorder = Freeze(Color.FromRgb(0xE2, 0xE8, 0xF0));
    private static readonly SolidColorBrush DisabledFg = Freeze(Color.FromRgb(0x94, 0xA3, 0xB8));

    private static readonly SolidColorBrush DangerIdleBg = Freeze(Color.FromRgb(0xFE, 0xF2, 0xF2));
    private static readonly SolidColorBrush DangerHoverBg = Freeze(Color.FromRgb(0xFE, 0xE2, 0xE2));
    private static readonly SolidColorBrush DangerIdleBorder = Freeze(Color.FromRgb(0xFC, 0xA5, 0xA5));
    private static readonly SolidColorBrush DangerHoverBorder = Freeze(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush DangerFg = Freeze(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly SolidColorBrush DangerFillBg = Freeze(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush DangerFillBorder = Freeze(Color.FromRgb(0xB9, 0x1C, 0x1C));

    private static readonly SolidColorBrush WarningIdleBg = Freeze(Color.FromRgb(0xFF, 0xFB, 0xEB));
    private static readonly SolidColorBrush WarningHoverBg = Freeze(Color.FromRgb(0xFE, 0xF3, 0xC7));
    private static readonly SolidColorBrush WarningIdleBorder = Freeze(Color.FromRgb(0xFC, 0xD3, 0x4D));
    private static readonly SolidColorBrush WarningHoverBorder = Freeze(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush WarningFg = Freeze(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly SolidColorBrush WarningFillBg = Freeze(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush WarningFillBorder = Freeze(Color.FromRgb(0xB4, 0x53, 0x09));

    private static readonly SolidColorBrush PrimaryIdleBg = Freeze(Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush PrimaryHoverBg = Freeze(Color.FromRgb(0x00, 0x5A, 0x9E));
    private static readonly SolidColorBrush PrimaryPressBg = Freeze(Color.FromRgb(0x00, 0x45, 0x78));
    private static readonly SolidColorBrush WhiteFg = Freeze(Colors.White);

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(HoldConfirmButton),
        new PropertyMetadata("长按确认", OnVisualPropertyChanged));

    public static readonly DependencyProperty HoldingLabelFormatProperty = DependencyProperty.Register(
        nameof(HoldingLabelFormat), typeof(string), typeof(HoldConfirmButton),
        new PropertyMetadata("保持按住… {0:0.0}s", OnVisualPropertyChanged));

    public static readonly DependencyProperty DisabledLabelProperty = DependencyProperty.Register(
        nameof(DisabledLabel), typeof(string), typeof(HoldConfirmButton),
        new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    public static readonly DependencyProperty HoldDurationSecondsProperty = DependencyProperty.Register(
        nameof(HoldDurationSeconds), typeof(double), typeof(HoldConfirmButton),
        new PropertyMetadata(1.2, OnVisualPropertyChanged));

    public static readonly DependencyProperty IsHoldRequiredProperty = DependencyProperty.Register(
        nameof(IsHoldRequired), typeof(bool), typeof(HoldConfirmButton),
        new PropertyMetadata(true, OnVisualPropertyChanged));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(HoldButtonTone), typeof(HoldConfirmButton),
        new PropertyMetadata(HoldButtonTone.Danger, OnVisualPropertyChanged));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(HoldConfirmButton),
        new PropertyMetadata(null));

    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(
        nameof(CommandParameter), typeof(object), typeof(HoldConfirmButton),
        new PropertyMetadata(null));

    private readonly DispatcherTimer _holdTimer;
    private long _holdStartTimestamp;
    private bool _isHolding;
    private double _holdProgress;
    private bool _isDirectPressActive;
    private bool _isSpacePressActive;

    public event EventHandler? Confirmed;

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string HoldingLabelFormat
    {
        get => (string)GetValue(HoldingLabelFormatProperty);
        set => SetValue(HoldingLabelFormatProperty, value);
    }

    public string DisabledLabel
    {
        get => (string)GetValue(DisabledLabelProperty);
        set => SetValue(DisabledLabelProperty, value);
    }

    public double HoldDurationSeconds
    {
        get => (double)GetValue(HoldDurationSecondsProperty);
        set => SetValue(HoldDurationSecondsProperty, value);
    }

    public bool IsHoldRequired
    {
        get => (bool)GetValue(IsHoldRequiredProperty);
        set => SetValue(IsHoldRequiredProperty, value);
    }

    public HoldButtonTone Tone
    {
        get => (HoldButtonTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public bool IsHolding => _isHolding;

    public double HoldProgress => _holdProgress;

    public HoldConfirmButton()
    {
        InitializeComponent();

        _holdTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _holdTimer.Tick += OnHoldTimerTick;

        IsEnabledChanged += (_, _) =>
        {
            if (!IsEnabled)
            {
                CancelHold();
                _isDirectPressActive = false;
                _isSpacePressActive = false;
            }
            UpdateVisualState();
        };

        SurfaceGrid.SizeChanged += (_, _) => UpdateGeometryClips();
        Unloaded += (_, _) => CancelHold();

        UpdateVisualState();
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HoldConfirmButton btn)
        {
            btn.UpdateVisualState();
        }
    }

    private bool CanInvoke()
    {
        if (!IsEnabled) return false;
        ICommand? cmd = Command;
        return cmd is null || cmd.CanExecute(CommandParameter);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateVisualState();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_isHolding && !IsMouseCaptured)
        {
            CancelHold();
        }
        UpdateVisualState();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!CanInvoke()) return;

        e.Handled = true;
        Focus();
        CaptureMouse();

        if (!IsHoldRequired)
        {
            _isDirectPressActive = true;
            UpdateVisualState();
            return;
        }

        StartHold();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isHolding && !_isDirectPressActive) return;

        bool inside = IsPointInside(e.GetPosition(this));
        if (_isHolding && !inside)
        {
            CancelHold();
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
        else if (_isDirectPressActive)
        {
            UpdateVisualState();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool inside = IsPointInside(e.GetPosition(this));

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (!IsHoldRequired)
        {
            bool shouldInvoke = _isDirectPressActive && inside && CanInvoke();
            _isDirectPressActive = false;
            UpdateVisualState();
            if (shouldInvoke)
            {
                e.Handled = true;
                InvokeConfirmed();
            }
            return;
        }

        if (_isHolding)
        {
            e.Handled = true;
            CancelHold();
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _isDirectPressActive = false;
        if (_isHolding)
        {
            CancelHold();
        }
        UpdateVisualState();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // 严禁响应 Enter 键，避免与弹窗默认安全取消键冲突或误回车删除
        if (e.Key != Key.Space || !CanInvoke()) return;

        e.Handled = true;
        if (e.IsRepeat) return;

        if (!IsHoldRequired)
        {
            _isSpacePressActive = true;
            UpdateVisualState();
            return;
        }

        StartHold();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key != Key.Space) return;

        e.Handled = true;
        if (!IsHoldRequired)
        {
            bool shouldInvoke = _isSpacePressActive && CanInvoke();
            _isSpacePressActive = false;
            UpdateVisualState();
            if (shouldInvoke)
            {
                InvokeConfirmed();
            }
            return;
        }

        if (_isHolding)
        {
            CancelHold();
        }
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _isSpacePressActive = false;
        if (_isHolding && !IsMouseCaptured)
        {
            CancelHold();
        }
        UpdateVisualState();
    }

    private bool IsPointInside(Point point)
    {
        return point.X >= 0 && point.Y >= 0 && point.X <= ActualWidth && point.Y <= ActualHeight;
    }

    private void StartHold()
    {
        _holdStartTimestamp = Stopwatch.GetTimestamp();
        _isHolding = true;
        _holdProgress = 0;
        UpdateVisualState();
        UpdateGeometryClips();
        _holdTimer.Start();
    }

    public void CancelHold()
    {
        _holdTimer.Stop();
        _isHolding = false;
        _holdProgress = 0;
        UpdateGeometryClips();
        UpdateVisualState();
    }

    private void OnHoldTimerTick(object? sender, EventArgs e)
    {
        if (!_isHolding || !CanInvoke())
        {
            CancelHold();
            return;
        }

        double elapsed = Stopwatch.GetElapsedTime(_holdStartTimestamp).TotalSeconds;
        AdvanceHoldByElapsedSeconds(elapsed);
    }

    /// <summary>
    /// 按已按住时长推进进度；达到 HoldDurationSeconds 时完成并触发确认（亦供单元测试确定性验证）。
    /// </summary>
    public bool AdvanceHoldByElapsedSeconds(double elapsedSeconds)
    {
        if (!CanInvoke())
        {
            CancelHold();
            return false;
        }

        if (!IsHoldRequired)
        {
            InvokeConfirmed();
            return true;
        }

        double duration = Math.Max(0.2, HoldDurationSeconds);
        _isHolding = true;
        _holdProgress = Math.Clamp(elapsedSeconds / duration, 0.0, 1.0);
        UpdateGeometryClips();

        if (_holdProgress >= 1.0)
        {
            _holdTimer.Stop();
            _isHolding = false;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
            UpdateVisualState();
            InvokeConfirmed();
            return true;
        }

        double remaining = Math.Max(0.1, duration - elapsedSeconds);
        string holdingText = string.Format(HoldingLabelFormat, remaining);
        BaseLabelText.Text = holdingText;
        OverlayLabelText.Text = holdingText;
        return false;
    }

    private void InvokeConfirmed()
    {
        Confirmed?.Invoke(this, EventArgs.Empty);
        ICommand? cmd = Command;
        if (cmd is not null && cmd.CanExecute(CommandParameter))
        {
            cmd.Execute(CommandParameter);
        }
    }

    private void UpdateGeometryClips()
    {
        double width = Math.Max(0, SurfaceGrid.ActualWidth);
        double height = Math.Max(0, SurfaceGrid.ActualHeight);
        SurfaceClipRect.Rect = new Rect(0, 0, width, height);
        FillClipRect.Rect = new Rect(0, 0, width * _holdProgress, height);
    }

    private void UpdateVisualState()
    {
        if (!IsEnabled)
        {
            Cursor = Cursors.Arrow;
            RootBorder.Background = DisabledBg;
            RootBorder.BorderBrush = DisabledBorder;
            BaseLabelText.Foreground = DisabledFg;
            FillBackgroundBorder.Background = DisabledBg;
            string disabledText = string.IsNullOrWhiteSpace(DisabledLabel) ? Label : DisabledLabel;
            BaseLabelText.Text = disabledText;
            OverlayLabelText.Text = disabledText;
            return;
        }

        Cursor = Cursors.Hand;

        string currentText = _isHolding
            ? string.Format(HoldingLabelFormat, Math.Max(0.1, Math.Max(0.2, HoldDurationSeconds) * (1.0 - _holdProgress)))
            : Label;
        BaseLabelText.Text = currentText;
        OverlayLabelText.Text = currentText;

        bool pressed = _isDirectPressActive || _isSpacePressActive;
        switch (Tone)
        {
            case HoldButtonTone.Primary:
                RootBorder.Background = pressed
                    ? PrimaryPressBg
                    : (IsMouseOver ? PrimaryHoverBg : PrimaryIdleBg);
                RootBorder.BorderBrush = RootBorder.Background;
                BaseLabelText.Foreground = WhiteFg;
                FillBackgroundBorder.Background = PrimaryPressBg;
                break;

            case HoldButtonTone.Warning:
                RootBorder.Background = _isHolding || IsMouseOver ? WarningHoverBg : WarningIdleBg;
                RootBorder.BorderBrush = _isHolding ? WarningFillBorder : (IsMouseOver ? WarningHoverBorder : WarningIdleBorder);
                BaseLabelText.Foreground = WarningFg;
                FillBackgroundBorder.Background = WarningFillBg;
                break;

            default: // HoldButtonTone.Danger
                RootBorder.Background = _isHolding || IsMouseOver ? DangerHoverBg : DangerIdleBg;
                RootBorder.BorderBrush = _isHolding ? DangerFillBorder : (IsMouseOver ? DangerHoverBorder : DangerIdleBorder);
                BaseLabelText.Foreground = DangerFg;
                FillBackgroundBorder.Background = DangerFillBg;
                break;
        }
    }
}
