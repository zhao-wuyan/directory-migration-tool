using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MigrationCore.Models;

namespace MoveWithSymlinkWPF.Converters;

/// <summary>
/// 错误类型到图标转换器
/// </summary>
public class ErrorTypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string errorMessage)
        {
            // 根据错误消息内容判断错误类型
            if (string.IsNullOrEmpty(errorMessage))
                return string.Empty;
                
            var lowerError = errorMessage.ToLower();
            
            if (lowerError.Contains("access") || lowerError.Contains("denied") || lowerError.Contains("permission") || lowerError.Contains("权限"))
                return "🔒";
            else if (lowerError.Contains("space") || lowerError.Contains("disk") || lowerError.Contains("空间") || lowerError.Contains("磁盘"))
                return "💾";
            else if (lowerError.Contains("lock") || lowerError.Contains("used") || lowerError.Contains("占用") || lowerError.Contains("in use"))
                return "📁";
            else if (lowerError.Contains("network") || lowerError.Contains("connection") || lowerError.Contains("网络") || lowerError.Contains("连接"))
                return "🌐";
            else if (lowerError.Contains("system") || lowerError.Contains("critical") || lowerError.Contains("系统") || lowerError.Contains("严重"))
                return "⚠️";
            else
                return "❓";
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 错误类型到颜色转换器
/// </summary>
public class ErrorTypeToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 支持ErrorType枚举
        if (value is ErrorType errorType)
        {
            return errorType switch
            {
                ErrorType.Permission => new SolidColorBrush(Color.FromRgb(59, 130, 246)), // 蓝色
                ErrorType.DiskSpace => new SolidColorBrush(Color.FromRgb(251, 146, 60)), // 橙色
                ErrorType.FileInUse => new SolidColorBrush(Color.FromRgb(250, 204, 21)), // 黄色
                ErrorType.Network => new SolidColorBrush(Color.FromRgb(168, 85, 247)), // 紫色
                ErrorType.System => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // 红色
                ErrorType.Unknown => new SolidColorBrush(Color.FromRgb(107, 114, 128)), // 灰色
                _ => new SolidColorBrush(Colors.Transparent)
            };
        }

        // 向后兼容：支持字符串错误消息
        if (value is string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return new SolidColorBrush(Colors.Transparent);

            var lowerError = errorMessage.ToLower();

            if (lowerError.Contains("access") || lowerError.Contains("denied") || lowerError.Contains("permission") || lowerError.Contains("权限"))
                return new SolidColorBrush(Color.FromRgb(59, 130, 246)); // 蓝色
            else if (lowerError.Contains("space") || lowerError.Contains("disk") || lowerError.Contains("空间") || lowerError.Contains("磁盘"))
                return new SolidColorBrush(Color.FromRgb(251, 146, 60)); // 橙色
            else if (lowerError.Contains("lock") || lowerError.Contains("used") || lowerError.Contains("占用") || lowerError.Contains("in use"))
                return new SolidColorBrush(Color.FromRgb(250, 204, 21)); // 黄色
            else if (lowerError.Contains("network") || lowerError.Contains("connection") || lowerError.Contains("网络") || lowerError.Contains("连接"))
                return new SolidColorBrush(Color.FromRgb(168, 85, 247)); // 紫色
            else if (lowerError.Contains("system") || lowerError.Contains("critical") || lowerError.Contains("系统") || lowerError.Contains("严重"))
                return new SolidColorBrush(Color.FromRgb(239, 68, 68)); // 红色
            else
                return new SolidColorBrush(Color.FromRgb(107, 114, 128)); // 灰色
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 错误类型到边框颜色转换器
/// </summary>
public class ErrorTypeToBorderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is QuickMigrateTaskStatus status)
        {
            switch (status)
            {
                case QuickMigrateTaskStatus.Failed:
                    return new SolidColorBrush(Color.FromRgb(220, 38, 38)); // 红色边框
                case QuickMigrateTaskStatus.Completed:
                    return new SolidColorBrush(Color.FromRgb(34, 197, 94)); // 绿色边框
                case QuickMigrateTaskStatus.InProgress:
                    return new SolidColorBrush(Color.FromRgb(59, 130, 246)); // 蓝色边框
                default:
                    return new SolidColorBrush(Color.FromRgb(224, 224, 224)); // 默认灰色边框
            }
        }
        return new SolidColorBrush(Color.FromRgb(224, 224, 224));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 错误状态可见性转换器
/// </summary>
public class ErrorStateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is QuickMigrateTaskStatus status)
        {
            return status == QuickMigrateTaskStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 错误消息到简短描述转换器
/// </summary>
public class ErrorMessageToShortDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return string.Empty;
                
            // 提取关键错误信息，限制长度
            var lowerError = errorMessage.ToLower();
            
            if (lowerError.Contains("access") || lowerError.Contains("denied") || lowerError.Contains("permission") || lowerError.Contains("权限"))
                return "权限不足";
            else if (lowerError.Contains("space") || lowerError.Contains("disk") || lowerError.Contains("空间") || lowerError.Contains("磁盘"))
                return "磁盘空间不足";
            else if (lowerError.Contains("lock") || lowerError.Contains("used") || lowerError.Contains("占用") || lowerError.Contains("in use"))
                return "文件被占用";
            else if (lowerError.Contains("network") || lowerError.Contains("connection") || lowerError.Contains("网络") || lowerError.Contains("连接"))
                return "网络连接问题";
            else if (lowerError.Contains("system") || lowerError.Contains("critical") || lowerError.Contains("系统") || lowerError.Contains("严重"))
                return "系统错误";
            else
            {
                // 截取前30个字符作为简短描述
                return errorMessage.Length > 30 ? errorMessage.Substring(0, 30) + "..." : errorMessage;
            }
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 任务状态到样式名称转换器
/// </summary>
public class TaskStatusToStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is QuickMigrateTaskStatus status)
        {
            return status switch
            {
                QuickMigrateTaskStatus.Failed => "FailedTaskCardStyle",
                QuickMigrateTaskStatus.Completed => "CompletedTaskCardStyle",
                QuickMigrateTaskStatus.InProgress => "InProgressTaskCardStyle",
                _ => "NormalTaskCardStyle"
            };
        }
        return "NormalTaskCardStyle";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 错误类型到详细描述转换器
/// </summary>
public class ErrorTypeToDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 支持ErrorType枚举
        if (value is ErrorType errorType)
        {
            return errorType switch
            {
                ErrorType.Permission => "权限错误",
                ErrorType.DiskSpace => "磁盘空间不足",
                ErrorType.FileInUse => "文件被占用",
                ErrorType.Network => "网络错误",
                ErrorType.System => "系统错误",
                ErrorType.Unknown => "未知错误",
                _ => "错误"
            };
        }

        // 向后兼容：支持字符串错误消息
        if (value is string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return string.Empty;

            var lowerError = errorMessage.ToLower();

            if (lowerError.Contains("access") || lowerError.Contains("denied") || lowerError.Contains("permission") || lowerError.Contains("权限"))
                return "权限不足：当前文件夹在文件资源管理中打开或用户没有足够的权限执行此操作。";
            else if (lowerError.Contains("space") || lowerError.Contains("disk") || lowerError.Contains("空间") || lowerError.Contains("磁盘"))
                return "磁盘空间不足：目标驱动器没有足够的可用空间来完成迁移操作。";
            else if (lowerError.Contains("lock") || lowerError.Contains("used") || lowerError.Contains("占用") || lowerError.Contains("in use"))
                return "文件被占用：源或目标文件正在被其他程序使用，无法完成操作。";
            else if (lowerError.Contains("network") || lowerError.Contains("connection") || lowerError.Contains("网络") || lowerError.Contains("连接"))
                return "网络连接问题：无法访问网络资源或网络连接不稳定。";
            else if (lowerError.Contains("system") || lowerError.Contains("critical") || lowerError.Contains("系统") || lowerError.Contains("严重"))
                return "系统错误：操作系统或文件系统遇到严重问题。";
            else
                return "未知错误：发生了未预期的错误。";
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 错误类型到解决方案转换器
/// </summary>
public class ErrorTypeToSolutionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 支持ErrorType枚举
        if (value is ErrorType errorType)
        {
            return errorType switch
            {
                ErrorType.Permission => "• 请关闭可能占用该目录的程序后重试。常见占用程序包括：\n• 文件资源管理器（如果打开了该目录）\n• 文本编辑器、办公软件\n• 开发工具、数据库软件\n• 下载软件、压缩软件\n• 同步软件、云存储客户端",
                ErrorType.DiskSpace => "• 清理目标磁盘，释放更多空间\n• 选择其他有足够空间的驱动器\n• 删除不需要的临时文件",
                ErrorType.FileInUse => "• 关闭可能正在使用这些文件的程序\n• 检查任务管理器，结束相关进程\n• 重启计算机后再尝试",
                ErrorType.Network => "• 检查网络连接是否正常\n• 确保网络路径可访问\n• 检查防火墙设置\n• 重启网络适配器",
                ErrorType.System => "• 重启计算机\n• 运行系统文件检查器：sfc /scannow\n• 检查磁盘错误：chkdsk /f\n• 联系系统管理员",
                ErrorType.Unknown => "• 查看详细日志获取更多信息\n• 尝试重启程序\n• 检查系统资源使用情况\n• 如问题持续，请联系技术支持",
                _ => "• 查看详细日志获取更多信息\n• 尝试重启程序"
            };
        }

        // 向后兼容：支持字符串错误消息
        if (value is string errorMessage)
        {
            if (string.IsNullOrEmpty(errorMessage))
                return string.Empty;

            var lowerError = errorMessage.ToLower();

            if (lowerError.Contains("access") || lowerError.Contains("denied") || lowerError.Contains("permission") || lowerError.Contains("权限"))
                return "解决方案：\n请关闭可能占用该目录的程序后重试。常见占用程序包括：\n• 文件资源管理器（如果打开了该目录）\n• 文本编辑器、办公软件\n• 开发工具、数据库软件\n• 下载软件、压缩软件\n• 同步软件、云存储客户端";
            else if (lowerError.Contains("space") || lowerError.Contains("disk") || lowerError.Contains("空间") || lowerError.Contains("磁盘"))
                return "解决方案：\n1. 清理目标磁盘，释放更多空间\n2. 选择其他有足够空间的驱动器\n3. 删除不需要的临时文件和程序";
            else if (lowerError.Contains("lock") || lowerError.Contains("used") || lowerError.Contains("占用") || lowerError.Contains("in use"))
                return "解决方案：\n1. 关闭所有可能正在使用这些文件的程序\n2. 检查任务管理器，结束相关进程\n3. 重启计算机后再尝试";
            else if (lowerError.Contains("network") || lowerError.Contains("connection") || lowerError.Contains("网络") || lowerError.Contains("连接"))
                return "解决方案：\n1. 检查网络连接是否正常\n2. 确保网络路径可访问\n3. 检查防火墙设置\n4. 重启网络适配器";
            else if (lowerError.Contains("system") || lowerError.Contains("critical") || lowerError.Contains("系统") || lowerError.Contains("严重"))
                return "解决方案：\n1. 重启计算机\n2. 运行系统文件检查器：sfc /scannow\n3. 检查磁盘错误：chkdsk /f\n4. 联系系统管理员";
            else
                return "解决方案：\n1. 查看详细日志获取更多信息\n2. 尝试重启程序\n3. 检查系统资源使用情况\n4. 如问题持续，请联系技术支持";
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
