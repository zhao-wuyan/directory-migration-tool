using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>双向迁移始终保留原始备份及还原前的数据目录。</summary>
public class ReversibleMigrationService(MigrationConfig config, MigrationMode mode = MigrationMode.Migrate)
{
    public Task<MigrationResult> ExecuteAsync(IProgress<MigrationProgress>? progress = null,
        IProgress<string>? logProgress = null, CancellationToken cancellationToken = default) =>
        MigrationWorkflowHelper.ExecuteAsync(config, mode, progress, logProgress, cancellationToken);
}
