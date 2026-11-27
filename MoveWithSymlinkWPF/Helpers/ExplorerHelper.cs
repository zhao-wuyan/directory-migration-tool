using System.Diagnostics;
using System.IO;

namespace MoveWithSymlinkWPF.Helpers;

public enum ExplorerLaunchKind
{
    None,
    OpenDirectory,
    SelectFile,
    OpenExistingAncestor
}

public sealed record ExplorerLaunchPlan(
    bool CanLaunch,
    ExplorerLaunchKind Kind,
    string ResolvedPath,
    string Arguments,
    string? ErrorMessage);

/// <summary>
/// 统一封装在 Windows 资源管理器中打开目录或定位单个文件的逻辑：
/// - 单个文件：使用 <c>/select,"&lt;path&gt;"</c> 打开所在文件夹并高亮选中该文件；
/// - 文件夹：直接打开该目录（自动规范化非根目录末尾反斜杠避免引号转义问题）；
/// - 尚未创建的目标路径（可选）：向上查找最近已存在的祖先目录打开。
/// </summary>
public static class ExplorerHelper
{
    public static ExplorerLaunchPlan ResolveLaunchPlan(
        string? path,
        bool fallbackToExistingAncestor = false,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ExplorerLaunchPlan(false, ExplorerLaunchKind.None, string.Empty, string.Empty, "路径为空，无法打开资源管理器。");
        }

        Func<string, bool> checkFile = fileExists ?? File.Exists;
        Func<string, bool> checkDir = directoryExists ?? Directory.Exists;

        string trimmed = path.Trim();
        if (checkFile(trimmed))
        {
            return new ExplorerLaunchPlan(true, ExplorerLaunchKind.SelectFile, trimmed, $"/select,\"{trimmed}\"", null);
        }

        if (checkDir(trimmed))
        {
            return new ExplorerLaunchPlan(true, ExplorerLaunchKind.OpenDirectory, trimmed, FormatDirectoryArgument(trimmed), null);
        }

        if (fallbackToExistingAncestor)
        {
            string? current = Path.TrimEndingDirectorySeparator(trimmed);
            int guard = 0;
            while (!string.IsNullOrWhiteSpace(current) && guard++ < 64)
            {
                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (checkDir(parent))
                {
                    return new ExplorerLaunchPlan(true, ExplorerLaunchKind.OpenExistingAncestor, parent, FormatDirectoryArgument(parent), null);
                }

                current = parent;
            }
        }

        return new ExplorerLaunchPlan(false, ExplorerLaunchKind.None, trimmed, string.Empty, $"文件或目录已不存在：{trimmed}");
    }

    public static bool OpenInExplorer(string? path, bool fallbackToExistingAncestor = false, bool showWarningOnFailure = true)
    {
        ExplorerLaunchPlan plan = ResolveLaunchPlan(path, fallbackToExistingAncestor);
        if (!plan.CanLaunch)
        {
            if (showWarningOnFailure && !string.IsNullOrWhiteSpace(plan.ErrorMessage))
            {
                CustomMessageBox.ShowWarning(plan.ErrorMessage, "无法打开");
            }
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", plan.Arguments) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            if (showWarningOnFailure)
            {
                CustomMessageBox.ShowWarning($"无法打开资源管理器：{ex.Message}", "提示");
            }
            return false;
        }
    }

    private static string FormatDirectoryArgument(string directoryPath)
    {
        string clean = Path.TrimEndingDirectorySeparator(directoryPath);
        // 卷根目录（如 C:\）保留末尾反斜杠且不含空格，不加引号传参以避免 \" 转义歧义
        if (clean.EndsWith('\\'))
        {
            return clean;
        }
        return $"\"{clean}\"";
    }
}
