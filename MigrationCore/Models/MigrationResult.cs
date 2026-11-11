namespace MigrationCore.Models;

/// <summary>
/// 迁移结果
/// </summary>
public class MigrationResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 错误消息（如果失败）
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 源路径（链接路径）
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// 目标路径（实际存储路径）
    /// </summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// 文件统计信息
    /// </summary>
    public FileStats? Stats { get; set; }

    public Guid? TransactionId { get; set; }
    public MigrationStage Stage { get; set; } = MigrationStage.Prepared;
    public string? BackupPath { get; set; }
    public BackupState? BackupState { get; set; }
    public VerificationSummary? VerificationSummary { get; set; }
    public RollbackOutcome RollbackOutcome { get; set; } = RollbackOutcome.NotRequired;
    public string[] RecoveryPaths { get; set; } = [];
}

