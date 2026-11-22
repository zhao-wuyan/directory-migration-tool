using MigrationCore.Models.Safety;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services;

/// <summary>
/// 路径验证服务（已收敛委托至统一 4 级安全策略 MigrationSafetyPolicy，消除手动/一键/扫描入口双标）。
/// </summary>
public static class PathValidator
{
    private static string? GetMutationPathError(string path, bool allowLeafReparseForRestore)
    {
        SafetyPolicyDecision decision = MigrationSafetyPolicy.EvaluateL0HardDeny(
            path,
            allowLeafReparseForRestore: allowLeafReparseForRestore,
            checkFilesystem: false);
        return decision.IsHardDenied ? decision.Summary : null;
    }

    /// <summary>
    /// 判断文件是否为迁移标记文件
    /// </summary>
    private static bool IsMarkerFile(string fileName)
    {
        return fileName.StartsWith(".xinghe-migrate.", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith(".xinghe-reduction.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 检查目录是否包含用户数据（忽略标记文件）。
    /// 失败封闭：当目录无权访问或枚举异常时按“存在不可核实内容”返回 true，禁止把异常目录误判为空目标。
    /// </summary>
    public static bool HasUserContent(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directoryPath)
                .Any(entry => !IsMarkerFile(Path.GetFileName(entry)));
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 验证源路径是否合法（委托给 MigrationSafetyPolicy：L0 硬拒绝一律拦截，L1 放行，L2/L3 附带明确风险/引导提示）。
    /// </summary>
    public static (bool IsValid, string? Error, string? Warning) ValidateSourcePath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return (false, "源路径不能为空", null);

        string? pathError = GetMutationPathError(sourcePath, allowLeafReparseForRestore: false);
        if (pathError is not null) return (false, pathError, null);

        string fullPath = PathIdentityService.Normalize(sourcePath);
        if (!Directory.Exists(fullPath)) return (false, "源目录不存在", null);

        SafetyPolicyDecision decision = MigrationSafetyPolicy.EvaluateSource(
            fullPath,
            matcher: null,
            context: SafetyEvaluationContext.NewMigration,
            checkFilesystem: true);

        if (decision.IsHardDenied)
        {
            return (false, decision.Summary, null);
        }

        string? warning = null;
        if (decision.Level == MigrationAdmissionLevel.VendorNativeRecommended)
        {
            warning = decision.Warnings is { Length: > 0 }
                ? string.Join(Environment.NewLine, decision.Warnings)
                : decision.NativeGuidance ?? decision.Summary;
        }
        else if (decision.Warnings is { Length: > 0 } && decision.Level == MigrationAdmissionLevel.ManualRiskConfirm)
        {
            // 手动模式与一键模式保留清晰非阻塞风险提示，L0 已在上方强制拦截
            warning = decision.Warnings.FirstOrDefault(w => w.Contains("Program Files", StringComparison.OrdinalIgnoreCase));
        }

        return (true, null, warning);
    }

    /// <summary>
    /// 验证目标路径是否合法（委托给 MigrationSafetyPolicy.EvaluateTarget，禁止落入 L0 硬禁区或经重解析祖先绕过）。
    /// </summary>
    public static (bool IsValid, string? Error) ValidateTargetPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (false, "目标路径不能为空");

        try
        {
            string? pathError = GetMutationPathError(targetPath, allowLeafReparseForRestore: true);
            if (pathError is not null) return (false, pathError);

            string fullPath = PathIdentityService.Normalize(targetPath);
            string? parentDir = Path.GetDirectoryName(fullPath);

            if (string.IsNullOrEmpty(parentDir))
                return (false, "无法解析目标路径的父目录");

            SafetyPolicyDecision targetDecision = MigrationSafetyPolicy.EvaluateTarget(fullPath, checkFilesystem: true);
            if (targetDecision.IsHardDenied)
                return (false, targetDecision.Summary);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"目标路径无效: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查目标目录是否为空（严格失败封闭：文件占位、重解析点、权限拒绝或任意异常一律返回 false）。
    /// </summary>
    public static (bool IsEmpty, string? Error) IsTargetDirectoryEmpty(string targetPath)
    {
        try
        {
            var identity = new PathIdentityService();
            var existing = identity.ReadIfExists(targetPath);
            if (existing is null)
            {
                // 经 Win32 句柄确认目录确实不存在，视为空目录（可以创建）
                return (true, null);
            }

            if (existing.IsReparsePoint || !existing.IsDirectory)
            {
                return (false, "目标路径已被文件或重解析点占用，禁止迁移");
            }

            // 直接枚举，避免被内部吞异常掩盖真实访问拒绝原因
            bool hasAnyContent = Directory.EnumerateFileSystemEntries(targetPath)
                .Any(entry => !IsMarkerFile(Path.GetFileName(entry)));

            if (hasAnyContent)
            {
                return (false, "目标目录已存在且不为空，禁止迁移");
            }

            return (true, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "无权访问目标目录");
        }
        catch (Exception ex)
        {
            return (false, $"检查目标目录失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 验证源和目标的关系
    /// </summary>
    public static (bool IsValid, string? Error) ValidatePathRelation(string sourcePath, string targetPath)
    {
        try
        {
            string srcFull = Path.GetFullPath(sourcePath).TrimEnd('\\');
            string dstFull = Path.GetFullPath(targetPath).TrimEnd('\\');

            // 不能相同
            if (srcFull.Equals(dstFull, StringComparison.OrdinalIgnoreCase))
                return (false, "源路径和目标路径不能相同");

            // 目标不能在源内部
            if (dstFull.StartsWith(srcFull + "\\", StringComparison.OrdinalIgnoreCase))
                return (false, "目标路径不能位于源目录内部");
            if (srcFull.StartsWith(dstFull + "\\", StringComparison.OrdinalIgnoreCase))
                return (false, "源路径不能位于目标目录内部");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"路径关系验证失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查是否有管理员权限
    /// </summary>
    public static bool IsAdministrator()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查磁盘空间是否足够
    /// </summary>
    public static (bool IsSufficient, long Available, long Required) CheckDiskSpace(string targetPath, long requiredBytes)
    {
        try
        {
            string root = Path.GetPathRoot(targetPath) ?? "C:\\";
            var driveInfo = new DriveInfo(root);
            
            if (!driveInfo.IsReady)
                return (false, 0, requiredBytes);

            long available = driveInfo.AvailableFreeSpace;
            // 预留 10% 余量
            long required = (long)(requiredBytes * 1.1);
            
            return (available >= required, available, required);
        }
        catch
        {
            return (false, 0, requiredBytes);
        }
    }
}

