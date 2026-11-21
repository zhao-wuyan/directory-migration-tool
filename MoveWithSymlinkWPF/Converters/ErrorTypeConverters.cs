using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MigrationCore.Models;

namespace MoveWithSymlinkWPF.Converters;

internal static class ErrorTypeConverterHelper
{
    public static ErrorType Resolve(object? value) => value switch
    {
        ErrorType errorType => errorType,
        string errorMessage when !string.IsNullOrWhiteSpace(errorMessage) => ErrorTypeClassifier.Classify(errorMessage),
        _ => ErrorType.None
    };

    public static string GetShortTitle(ErrorType errorType) => errorType switch
    {
        ErrorType.Permission => "权限不足",
        ErrorType.DiskSpace => "磁盘空间不足",
        ErrorType.FileInUse => "文件被占用",
        ErrorType.Network => "网络连接问题",
        ErrorType.System => "系统错误",
        ErrorType.DriveUnavailable => "驱动器不可用",
        ErrorType.PathInvalid => "路径或目录无效",
        ErrorType.TargetConflict => "目标目录冲突",
        ErrorType.SymlinkError => "符号链接错误",
        ErrorType.TransactionMismatch => "事务或身份冲突",
        ErrorType.VerificationFailed => "数据核对失败",
        ErrorType.CopyFailed => "文件复制失败",
        ErrorType.Unknown => "未知错误",
        _ => "错误"
    };
}

/// <summary>
/// 错误类型到图标转换器
/// </summary>
public class ErrorTypeToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var errorType = ErrorTypeConverterHelper.Resolve(value);
        if (errorType == ErrorType.None)
            return value is ErrorType ? "❌" : string.Empty;

        return errorType switch
        {
            ErrorType.Permission => "🔒",
            ErrorType.DiskSpace => "💾",
            ErrorType.FileInUse => "📁",
            ErrorType.Network => "🌐",
            ErrorType.System => "⚠️",
            ErrorType.DriveUnavailable => "💿",
            ErrorType.PathInvalid => "📂",
            ErrorType.TargetConflict => "🚫",
            ErrorType.SymlinkError => "🔗",
            ErrorType.TransactionMismatch => "🧾",
            ErrorType.VerificationFailed => "🔍",
            ErrorType.CopyFailed => "📋",
            ErrorType.Unknown => "❓",
            _ => "❌"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 错误类型到颜色转换器（高对比度配色，适配白字徽章与浮窗边框）
/// </summary>
public class ErrorTypeToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var errorType = ErrorTypeConverterHelper.Resolve(value);
        if (errorType == ErrorType.None && value is not ErrorType)
            return new SolidColorBrush(Colors.Transparent);

        return errorType switch
        {
            ErrorType.Permission => new SolidColorBrush(Color.FromRgb(37, 99, 235)),         // 蓝色 #2563EB
            ErrorType.DriveUnavailable => new SolidColorBrush(Color.FromRgb(234, 88, 12)),   // 橙红 #EA580C
            ErrorType.DiskSpace => new SolidColorBrush(Color.FromRgb(217, 119, 6)),          // 深橙 #D97706
            ErrorType.FileInUse => new SolidColorBrush(Color.FromRgb(202, 138, 4)),          // 深琥珀 #CA8A04
            ErrorType.PathInvalid => new SolidColorBrush(Color.FromRgb(180, 83, 9)),         // 棕橙 #B45309
            ErrorType.TargetConflict => new SolidColorBrush(Color.FromRgb(225, 29, 72)),     // 玫红 #E11D48
            ErrorType.SymlinkError => new SolidColorBrush(Color.FromRgb(79, 70, 229)),       // 靛蓝 #4F46E5
            ErrorType.TransactionMismatch => new SolidColorBrush(Color.FromRgb(13, 148, 136)), // 青绿 #0D9488
            ErrorType.VerificationFailed => new SolidColorBrush(Color.FromRgb(190, 18, 60)), // 深绯红 #BE123C
            ErrorType.CopyFailed => new SolidColorBrush(Color.FromRgb(220, 38, 38)),         // 红色 #DC2626
            ErrorType.Network => new SolidColorBrush(Color.FromRgb(124, 58, 237)),           // 紫色 #7C3AED
            ErrorType.System => new SolidColorBrush(Color.FromRgb(185, 28, 28)),             // 暗红 #B91C1C
            _ => new SolidColorBrush(Color.FromRgb(75, 85, 99))                              // 深灰 #4B5563
        };
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
/// 错误消息到简短描述转换器（卡片上的错误徽章文案）
/// </summary>
public class ErrorMessageToShortDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string errorMessage || string.IsNullOrWhiteSpace(errorMessage))
            return string.Empty;

        string primary = ErrorTypeClassifier.ExtractPrimaryMessage(errorMessage);
        string firstLine = primary.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? primary;

        // 若首行本身是简短直观的中文错误（如“目标驱动器不可用”“源目录不存在”），直接展示更清晰
        if (firstLine.Length <= 16 && firstLine.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF))
            return firstLine;

        var errorType = ErrorTypeClassifier.Classify(errorMessage);
        if (errorType != ErrorType.Unknown && errorType != ErrorType.None)
            return ErrorTypeConverterHelper.GetShortTitle(errorType);

        return firstLine.Length > 26 ? firstLine[..26] + "..." : firstLine;
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
/// 错误类型到标题描述转换器
/// </summary>
public class ErrorTypeToDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var errorType = ErrorTypeConverterHelper.Resolve(value);
        if (errorType == ErrorType.None && value is not ErrorType)
            return string.Empty;

        return ErrorTypeConverterHelper.GetShortTitle(errorType);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 错误类型到建议解决方案转换器
/// </summary>
public class ErrorTypeToSolutionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var errorType = ErrorTypeConverterHelper.Resolve(value);
        if (errorType == ErrorType.None && value is not ErrorType)
            return string.Empty;

        return errorType switch
        {
            ErrorType.Permission => "• 请以管理员身份重新运行本工具\n• 检查当前账号对源目录与目标目录的读写及安全权限\n• 关闭可能锁定权限的杀毒软件或安全防护拦截后重试",
            ErrorType.DriveUnavailable => "• 检查目标磁盘（分区、移动硬盘或 U 盘）是否已连接并就绪\n• 核对顶部的「统一目标目录」或任务目标路径是否指向有效盘符\n• 若磁盘盘符已变更，请点击「浏览…」重新选择目标目录",
            ErrorType.DiskSpace => "• 清理目标磁盘，释放足够可用空间（需预留约 10% 余量）\n• 选择其他剩余空间充足的目标驱动器\n• 若需还原，请确认源目录所在磁盘有足够空间容纳数据",
            ErrorType.FileInUse => "• 关闭正在使用该目录的程序后重试（如文件资源管理器、编辑器、数据库、游戏/客户端等）\n• 查看下方「执行日志」确认占用进程，或在任务管理器中结束相关进程\n• 若仍被后台服务占用，可重启电脑后再执行",
            ErrorType.PathInvalid => "• 检查源目录与目标目录是否存在且路径格式正确\n• 避免使用磁盘根目录、系统关键目录或源/目标互相包含的路径\n• 点击右上角「刷新扫描」后重新选择有效目录",
            ErrorType.TargetConflict => "• 目标目录必须为空或尚未创建，工具不会覆盖已有文件\n• 请先核对目标目录内容，确认无重要数据后清空或移走\n• 或更换一个新的空目标目录路径后再试",
            ErrorType.SymlinkError => "• 请以管理员身份运行本工具，或在 Windows 设置中开启「开发者模式」\n• 核对目录类型：迁移要求源为普通实体目录，还原要求源为已登记符号链接\n• 若目录内包含未知的符号链接/联接点（重解析点），请先人工核对处理",
            ErrorType.TransactionMismatch => "• 检查当前「统一目标目录」是否与原迁移记录的实际目标路径一致\n• 若存在未完成的迁移、还原或备份清理操作，请先完成或核对后再试\n• 如目录曾被手动移动或修改，请先核对磁盘实际数据与备份路径",
            ErrorType.VerificationFailed => "• 复制或切换期间源/目标内容可能发生了变化，工具已保留全部副本未删除数据\n• 请暂停云盘同步、下载工具或持续写入该目录的程序后重试\n• 查看下方「执行日志」中的具体差异项并人工核对",
            ErrorType.CopyFailed => "• 查看下方「执行日志」中的 Robocopy 退出码和具体失败文件\n• 检查是否存在文件占用、权限受限、路径过长或磁盘读写错误\n• 排除问题后重试，原目录与临时副本均已完整保留",
            ErrorType.Network => "• 检查网络连接或共享盘/NAS 映射是否正常在线\n• 确认目标路径可正常读写，必要时改为本地 NTFS 磁盘\n• 排查防火墙或网络适配器后重试",
            ErrorType.System => "• 避免操作 Windows、Program Files 等系统关键目录\n• 检查系统资源与杀毒软件拦截情况，必要时以管理员身份重启工具\n• 若磁盘存在底层错误，可先检查磁盘健康状态（chkdsk）",
            _ => "• 展开下方「执行日志」查看完整报错与上下文\n• 核对源目录、目标目录及磁盘状态是否正常\n• 确认环境无异常后重试，原数据均已安全保留"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
