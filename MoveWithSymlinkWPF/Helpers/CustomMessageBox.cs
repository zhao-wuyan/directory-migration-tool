using System.Windows;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.Helpers;

/// <summary>
/// 自定义消息框辅助类 - 替代系统 MessageBox
/// </summary>
public static class CustomMessageBox
{
    /// <summary>
    /// 显示信息对话框
    /// </summary>
    public static void ShowInformation(string message, string title = "提示")
    {
        MessageWindow.Show(message, title, MessageType.Information, MessageButtons.OK);
    }

    /// <summary>
    /// 显示警告对话框
    /// </summary>
    public static void ShowWarning(string message, string title = "警告")
    {
        MessageWindow.Show(message, title, MessageType.Warning, MessageButtons.OK);
    }

    /// <summary>
    /// 显示错误对话框
    /// </summary>
    public static void ShowError(string message, string title = "错误")
    {
        MessageWindow.Show(message, title, MessageType.Error, MessageButtons.OK);
    }

    /// <summary>
    /// 显示成功对话框
    /// </summary>
    public static void ShowSuccess(string message, string title = "成功")
    {
        MessageWindow.Show(message, title, MessageType.Success, MessageButtons.OK);
    }

    /// <summary>
    /// 显示确认对话框（是/否）
    /// </summary>
    public static bool ShowQuestion(string message, string title = "确认")
    {
        var result = MessageWindow.Show(message, title, MessageType.Question, MessageButtons.YesNo);
        return result == MessageResult.Yes;
    }

    /// <summary>
    /// 显示确认对话框（确定/取消）
    /// </summary>
    public static bool ShowConfirm(string message, string title = "确认")
    {
        var result = MessageWindow.Show(message, title, MessageType.Question, MessageButtons.OKCancel);
        return result == MessageResult.OK;
    }

    /// <summary>
    /// 显示自定义对话框
    /// </summary>
    public static MessageResult Show(
        string message,
        string title = "提示",
        MessageType type = MessageType.Information,
        MessageButtons buttons = MessageButtons.OK)
    {
        return MessageWindow.Show(message, title, type, buttons);
    }
}
