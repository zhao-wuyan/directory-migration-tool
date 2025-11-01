using System.Diagnostics;
using System.Text.Json;
using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>
/// 修复服务 - 基于现有目标目录重建符号链接（不复制数据）
/// </summary>
public class RepairService
{
    private const string RepairInfoFile = ".xinghe-repair.info";
    
    private readonly MigrationConfig _config;
    private readonly bool _autoCleanupBackup;
    private string? _backupPath;

    public RepairService(MigrationConfig config, bool autoCleanupBackup = false)
    {
        _config = config;
        _autoCleanupBackup = autoCleanupBackup;
    }

    /// <summary>
    /// 执行修复操作
    /// </summary>
    public async Task<MigrationResult> ExecuteAsync(
        IProgress<MigrationProgress>? progress = null,
        IProgress<string>? logProgress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new MigrationResult
        {
            SourcePath = _config.SourcePath,
            TargetPath = _config.TargetPath
        };

        try
        {
            // Phase 1: 路径解析与验证
            ReportPhase(progress, logProgress, 1, "路径解析与验证");
            await ValidatePathsAsync(logProgress);

            // Phase 2: 替换源为符号链接
            ReportPhase(progress, logProgress, 2, "处理源路径并创建符号链接");
            await ReplaceSourceWithSymlinkAsync(logProgress);

            // Phase 3: 验证符号链接
            ReportPhase(progress, logProgress, 3, "验证符号链接");
            await VerifySymbolicLinkAsync(logProgress);

            // Phase 4: 清理备份（可选）
            ReportPhase(progress, logProgress, 4, "清理备份");
            await CleanupBackupAsync(logProgress);

            result.Success = true;
            logProgress?.Report("✅ 修复完成！");

            progress?.Report(new MigrationProgress
            {
                CurrentPhase = 4,
                PhaseDescription = "完成",
                PercentComplete = 100,
                Message = "修复完成"
            });

            return result;
        }
        catch (Exception ex)
        {
            logProgress?.Report($"❌ 错误: {ex.Message}");
            result.Success = false;
            result.ErrorMessage = ex.Message;

            try
            {
                await RollbackAsync(logProgress);
            }
            catch (Exception rollbackEx)
            {
                logProgress?.Report($"⚠️ 回滚失败: {rollbackEx.Message}");
            }

            return result;
        }
    }

    private void ReportPhase(IProgress<MigrationProgress>? progress, IProgress<string>? logProgress, 
        int phase, string description)
    {
        logProgress?.Report($"[{phase}/4] {description}");
        
        int percentComplete = phase switch
        {
            1 => 10,
            2 => 40,
            3 => 70,
            4 => 90,
            _ => 0
        };

        progress?.Report(new MigrationProgress
        {
            CurrentPhase = phase,
            PhaseDescription = description,
            PercentComplete = percentComplete,
            Message = description
        });
    }

    private async Task ValidatePathsAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            logProgress?.Report("验证路径...");

            // 验证目标路径
            var (isValidTarget, targetError) = PathValidator.ValidateTargetPath(_config.TargetPath);
            if (!isValidTarget)
                throw new InvalidOperationException(targetError);

            // 目标必须存在
            if (!Directory.Exists(_config.TargetPath))
                throw new InvalidOperationException($"目标目录不存在: {_config.TargetPath}");

            // 验证路径关系（源与目标不能相同或互为子目录）
            var (isValidRelation, relationError) = PathValidator.ValidatePathRelation(_config.SourcePath, _config.TargetPath);
            if (!isValidRelation)
                throw new InvalidOperationException(relationError);

            // 权限提示
            if (!PathValidator.IsAdministrator())
            {
                logProgress?.Report("⚠️ 当前非管理员权限，若未启用开发者模式，创建符号链接可能失败");
            }

            logProgress?.Report($"源路径: {_config.SourcePath}");
            logProgress?.Report($"目标路径: {_config.TargetPath}");
            logProgress?.Report("✅ 路径验证通过");
        });
    }

    private async Task ReplaceSourceWithSymlinkAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            bool sourceExists = Directory.Exists(_config.SourcePath);
            bool sourceIsSymlink = sourceExists && SymbolicLinkHelper.IsSymbolicLink(_config.SourcePath);

            if (!sourceExists)
            {
                // Case 1: 源不存在，直接创建符号链接
                logProgress?.Report("源路径不存在，直接创建符号链接");
                CreateSymbolicLink(logProgress);
            }
            else if (sourceIsSymlink)
            {
                // Case 2: 源已是符号链接
                var dirInfo = new DirectoryInfo(_config.SourcePath);
                string? currentTarget = dirInfo.LinkTarget;
                string expectedTarget = Path.GetFullPath(_config.TargetPath);

                if (!string.IsNullOrEmpty(currentTarget))
                {
                    string currentTargetFull = Path.GetFullPath(currentTarget);
                    if (string.Equals(currentTargetFull, expectedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        // 符号链接已指向正确目标
                        logProgress?.Report("✅ 符号链接已存在且指向正确目标");
                        return;
                    }
                    else
                    {
                        // 符号链接指向错误目标，删除后重建
                        logProgress?.Report($"符号链接指向错误目标: {currentTarget}");
                        logProgress?.Report("删除旧符号链接并重建...");
                        Directory.Delete(_config.SourcePath);
                        CreateSymbolicLink(logProgress);
                    }
                }
                else
                {
                    // 无法读取链接目标，删除后重建
                    logProgress?.Report("无法读取符号链接目标，删除后重建...");
                    Directory.Delete(_config.SourcePath);
                    CreateSymbolicLink(logProgress);
                }
            }
            else
            {
                // Case 3: 源是普通目录
                bool hasContent = PathValidator.HasUserContent(_config.SourcePath);

                if (!hasContent)
                {
                    // 空目录：直接删除后创建符号链接
                    logProgress?.Report("源是空目录，删除后创建符号链接");
                    Directory.Delete(_config.SourcePath, false);
                    CreateSymbolicLink(logProgress);
                }
                else
                {
                    // 非空目录：备份后创建符号链接
                    logProgress?.Report("源是非空目录，备份后创建符号链接");
                    
                    string parent = Path.GetDirectoryName(_config.SourcePath) 
                        ?? throw new InvalidOperationException("无法解析源目录父路径");
                    string name = Path.GetFileName(_config.SourcePath);
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    _backupPath = Path.Combine(parent, $"{name}.bak_{timestamp}");

                    logProgress?.Report($"备份源目录到: {_backupPath}");
                    Directory.Move(_config.SourcePath, _backupPath);
                    
                    // 创建修复信息标记文件
                    CreateRepairInfoFile(_backupPath, name, _config.TargetPath, logProgress);
                    
                    CreateSymbolicLink(logProgress);
                }
            }
        });
    }

    private void CreateSymbolicLink(IProgress<string>? logProgress)
    {
        logProgress?.Report($"创建符号链接: {_config.SourcePath} -> {_config.TargetPath}");

        // 优先使用 P/Invoke 方法
        bool success = SymbolicLinkHelper.CreateDirectorySymbolicLink(_config.SourcePath, _config.TargetPath);

        if (!success)
        {
            // 备选: 使用 cmd mklink
            logProgress?.Report("P/Invoke 失败，尝试使用 cmd mklink...");
            success = SymbolicLinkHelper.CreateSymbolicLinkViaCmdAsync(_config.SourcePath, _config.TargetPath, out string error);

            if (!success)
            {
                throw new InvalidOperationException($"创建符号链接失败: {error}");
            }
        }

        if (!Directory.Exists(_config.SourcePath))
        {
            throw new InvalidOperationException("符号链接创建后无法访问");
        }

        logProgress?.Report("✅ 符号链接创建成功");
    }

    private async Task VerifySymbolicLinkAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            logProgress?.Report("验证符号链接...");

            if (!SymbolicLinkHelper.IsSymbolicLink(_config.SourcePath))
            {
                throw new InvalidOperationException("创建的对象不是符号链接（重解析点）");
            }

            if (!Directory.Exists(_config.SourcePath))
            {
                throw new InvalidOperationException("符号链接无法访问");
            }

            // 验证链接目标
            var dirInfo = new DirectoryInfo(_config.SourcePath);
            string? linkTarget = dirInfo.LinkTarget;
            
            if (!string.IsNullOrEmpty(linkTarget))
            {
                logProgress?.Report($"符号链接目标: {linkTarget}");
            }

            logProgress?.Report("✅ 符号链接验证成功");
        });
    }

    private async Task CleanupBackupAsync(IProgress<string>? logProgress)
    {
        if (string.IsNullOrEmpty(_backupPath) || !Directory.Exists(_backupPath))
        {
            logProgress?.Report("无需清理备份");
            return;
        }

        await Task.Run(() =>
        {
            if (_autoCleanupBackup)
            {
                // 自动清理模式（QuickMigrate）
                try
                {
                    logProgress?.Report($"清理备份目录: {_backupPath}");
                    Directory.Delete(_backupPath, true);
                    logProgress?.Report("✅ 备份已清理");
                }
                catch (Exception ex)
                {
                    logProgress?.Report($"⚠️ 清理备份失败: {ex.Message}");
                    logProgress?.Report($"   备份目录保留在: {_backupPath}");
                }
            }
            else
            {
                // 手动模式：保留备份，由用户决定
                logProgress?.Report($"⚠️ 已创建备份目录: {_backupPath}");
                logProgress?.Report("   提示：备份目录已保留，您可以稍后手动删除或保留");
            }
        });
    }

    private async Task RollbackAsync(IProgress<string>? logProgress)
    {
        await Task.Run(() =>
        {
            logProgress?.Report("开始回滚...");

            try
            {
                // 如果创建了符号链接，删除它
                if (Directory.Exists(_config.SourcePath) && SymbolicLinkHelper.IsSymbolicLink(_config.SourcePath))
                {
                    logProgress?.Report($"删除符号链接: {_config.SourcePath}");
                    Directory.Delete(_config.SourcePath);
                }

                // 如果有备份，还原它
                if (!string.IsNullOrEmpty(_backupPath) && Directory.Exists(_backupPath))
                {
                    logProgress?.Report($"还原备份: {_backupPath} -> {_config.SourcePath}");
                    Directory.Move(_backupPath, _config.SourcePath);
                }

                logProgress?.Report("✅ 回滚完成");
            }
            catch (Exception ex)
            {
                logProgress?.Report($"❌ 回滚过程出错: {ex.Message}");
                throw;
            }
        });
    }

    /// <summary>
    /// 创建修复信息标记文件
    /// </summary>
    private void CreateRepairInfoFile(string backupPath, string originalName, string targetPath, IProgress<string>? logProgress)
    {
        try
        {
            var repairInfo = new
            {
                RepairTimestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                OriginalDirectoryName = originalName,
                OriginalFullPath = _config.SourcePath,
                TargetPath = targetPath,
                BackupPath = backupPath,
                BackupCreatedAt = DateTime.Now.ToString("yyyyMMdd_HHmmss"),
                RepairMode = "SymbolicLinkRecreation",
                Note = "此备份由修复模式创建。修复模式不复制数据，仅重建符号链接。"
            };

            string infoFilePath = Path.Combine(backupPath, RepairInfoFile);
            string jsonContent = JsonSerializer.Serialize(repairInfo, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            File.WriteAllText(infoFilePath, jsonContent);
            logProgress?.Report($"✅ 已创建修复信息记录: {RepairInfoFile}");
        }
        catch (Exception ex)
        {
            logProgress?.Report($"⚠️ 创建修复信息文件失败: {ex.Message}");
            // 不抛出异常，标记文件创建失败不应影响修复流程
        }
    }
}

