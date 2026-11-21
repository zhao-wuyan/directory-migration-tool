using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MoveWithSymlinkWPF.Views;

/// <summary>
/// 消息类型枚举
/// </summary>
public enum MessageType
{
    /// <summary>信息</summary>
    Information,
    /// <summary>警告</summary>
    Warning,
    /// <summary>错误</summary>
    Error,
    /// <summary>确认</summary>
    Question,
    /// <summary>成功</summary>
    Success
}

/// <summary>
/// 按钮配置枚举
/// </summary>
public enum MessageButtons
{
    /// <summary>确定</summary>
    OK,
    /// <summary>确定和取消</summary>
    OKCancel,
    /// <summary>是和否</summary>
    YesNo,
    /// <summary>是、否和取消</summary>
    YesNoCancel
}

/// <summary>
/// 对话框结果枚举
/// </summary>
public enum MessageResult
{
    None,
    OK,
    Cancel,
    Yes,
    No
}

/// <summary>
/// 通用消息对话框窗口
/// </summary>
public partial class MessageWindow : Window
{
    public MessageResult Result { get; private set; } = MessageResult.None;
    private Button? _defaultButton;

    private MessageWindow(
        string title,
        string message,
        MessageType type,
        MessageButtons buttons,
        MessageResult defaultResult,
        string? yesText,
        string? noText)
    {
        InitializeComponent();

        Title = title;
        MessageTextBlock.Text = message;
        if (!string.IsNullOrWhiteSpace(yesText)) YesButton.Content = yesText;
        if (!string.IsNullOrWhiteSpace(noText)) NoButton.Content = noText;

        // 设置图标和颜色
        ConfigureMessageType(type);

        // 配置按钮与默认高亮
        ConfigureButtons(buttons, defaultResult);

        Loaded += (_, _) =>
        {
            if (_defaultButton is null) return;
            FocusManager.SetFocusedElement(this, _defaultButton);
            _defaultButton.Focus();
            Keyboard.Focus(_defaultButton);
        };

        DataContext = this;
    }

    /// <summary>
    /// 显示消息对话框（静态方法）
    /// </summary>
    public static MessageResult Show(
        string message,
        string title = "提示",
        MessageType type = MessageType.Information,
        MessageButtons buttons = MessageButtons.OK,
        Window? owner = null,
        MessageResult defaultResult = MessageResult.None,
        string? yesText = null,
        string? noText = null)
    {
        var window = new MessageWindow(title, message, type, buttons, defaultResult, yesText, noText);

        var resolvedOwner = owner ?? Application.Current?.MainWindow;
        if (resolvedOwner is not null && resolvedOwner != window && resolvedOwner.IsLoaded)
        {
            window.Owner = resolvedOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
        return window.Result;
    }

    private void ConfigureMessageType(MessageType type)
    {
        switch (type)
        {
            case MessageType.Information:
                IconTextBlock.Text = "ℹ";
                IconEllipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078D4"));
                break;
            case MessageType.Warning:
                IconTextBlock.Text = "!";
                IconEllipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D97706"));
                break;
            case MessageType.Error:
                IconTextBlock.Text = "✖";
                IconEllipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626"));
                break;
            case MessageType.Question:
                IconTextBlock.Text = "?";
                IconEllipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0284C7"));
                break;
            case MessageType.Success:
                IconTextBlock.Text = "✓";
                IconEllipse.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A"));
                break;
        }
    }

    private void ConfigureButtons(MessageButtons buttons, MessageResult defaultResult)
    {
        var primaryStyle = (Style)FindResource("PrimaryButtonStyle");
        var secondaryStyle = (Style)FindResource("SecondaryButtonStyle");

        // 隐藏并重置所有按钮
        foreach (var button in new[] { OKButton, CancelButton, YesButton, NoButton })
        {
            button.Visibility = Visibility.Collapsed;
            button.IsDefault = false;
            button.IsCancel = false;
            button.Style = secondaryStyle;
        }

        switch (buttons)
        {
            case MessageButtons.OK:
                OKButton.Visibility = Visibility.Visible;
                SetDefaultButton(OKButton, primaryStyle);
                OKButton.IsCancel = true;
                break;
            case MessageButtons.OKCancel:
                OKButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                CancelButton.IsCancel = true;
                SetDefaultButton(defaultResult == MessageResult.Cancel ? CancelButton : OKButton, primaryStyle);
                break;
            case MessageButtons.YesNo:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                NoButton.IsCancel = true;
                SetDefaultButton(defaultResult == MessageResult.No ? NoButton : YesButton, primaryStyle);
                break;
            case MessageButtons.YesNoCancel:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                CancelButton.IsCancel = true;
                var target = defaultResult switch
                {
                    MessageResult.No => NoButton,
                    MessageResult.Cancel => CancelButton,
                    _ => YesButton
                };
                SetDefaultButton(target, primaryStyle);
                break;
        }
    }

    private void SetDefaultButton(Button button, Style primaryStyle)
    {
        button.Style = primaryStyle;
        button.IsDefault = true;
        _defaultButton = button;
    }

    private void OKButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageResult.OK;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageResult.Cancel;
        DialogResult = false;
        Close();
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageResult.Yes;
        DialogResult = true;
        Close();
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageResult.No;
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageResult.Cancel;
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }
}
