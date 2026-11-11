using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>手动迁移与其他入口共用同一事务安全流程。</summary>
public class MigrationService(MigrationConfig config)
{
    public Task<MigrationResult> ExecuteMigrationAsync(IProgress<MigrationProgress>? progress = null,
        IProgress<string>? logProgress = null, CancellationToken cancellationToken = default) =>
        MigrationWorkflowHelper.ExecuteAsync(config, MigrationMode.Migrate, progress, logProgress, cancellationToken);
}
