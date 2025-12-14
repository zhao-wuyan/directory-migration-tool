using System.Windows;

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

    private MessageWindow(string title, string message, MessageType type, MessageButtons buttons)
    {
        InitializeComponent();

        Title = title;
        MessageTextBlock.Text = message;

        // 设置图标和颜色
        ConfigureMessageType(type);

        // 配置按钮
        ConfigureButtons(buttons);

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
        Window? owner = null)
    {
        var window = new MessageWindow(title, message, type, buttons);

        if (owner != null)
        {
            window.Owner = owner;
        }
        else if (Application.Current.MainWindow != null)
        {
            window.Owner = Application.Current.MainWindow;
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
                IconBackground.Background = System.Windows.Media.Brushes.DodgerBlue;
                break;
            case MessageType.Warning:
                IconTextBlock.Text = "⚠";
                IconBackground.Background = System.Windows.Media.Brushes.Orange;
                break;
            case MessageType.Error:
                IconTextBlock.Text = "✖";
                IconBackground.Background = System.Windows.Media.Brushes.Crimson;
                break;
            case MessageType.Question:
                IconTextBlock.Text = "?";
                IconBackground.Background = System.Windows.Media.Brushes.MediumSeaGreen;
                break;
            case MessageType.Success:
                IconTextBlock.Text = "✓";
                IconBackground.Background = System.Windows.Media.Brushes.Green;
                break;
        }
    }

    private void ConfigureButtons(MessageButtons buttons)
    {
        // 隐藏所有按钮
        OKButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        YesButton.Visibility = Visibility.Collapsed;
        NoButton.Visibility = Visibility.Collapsed;

        switch (buttons)
        {
            case MessageButtons.OK:
                OKButton.Visibility = Visibility.Visible;
                OKButton.Focus();
                break;
            case MessageButtons.OKCancel:
                OKButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                OKButton.Focus();
                break;
            case MessageButtons.YesNo:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                YesButton.Focus();
                break;
            case MessageButtons.YesNoCancel:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                YesButton.Focus();
                break;
        }
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
