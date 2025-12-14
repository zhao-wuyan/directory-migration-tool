using System.Windows;

namespace MoveWithSymlinkWPF.Views;

/// <summary>
/// 错误消息对话框窗口
/// </summary>
public partial class ErrorMessageWindow : Window
{
    public string ErrorMessage { get; set; }
    public bool ShouldRetry { get; private set; }

    public ErrorMessageWindow(string errorMessage, string additionalMessage = "")
    {
        InitializeComponent();

        // 组合错误消息和附加消息
        if (!string.IsNullOrEmpty(additionalMessage))
        {
            ErrorMessage = $"{errorMessage}\n\n{additionalMessage}\n\n点击\"确定\"重新检测，点击\"取消\"中止迁移。";
        }
        else
        {
            ErrorMessage = errorMessage;
        }

        DataContext = this;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        ShouldRetry = true;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ShouldRetry = false;
        DialogResult = false;
        Close();
    }
}
