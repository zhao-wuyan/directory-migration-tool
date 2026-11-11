using MigrationCore.Models;

namespace MigrationCore.Services;

/// <summary>仅在实体与事务身份可确认时修复链接，不自动清理任何数据副本。</summary>
public class RepairService(MigrationConfig config)
{
    public Task<MigrationResult> ExecuteAsync(IProgress<MigrationProgress>? progress = null,
        IProgress<string>? logProgress = null, CancellationToken cancellationToken = default) =>
        MigrationWorkflowHelper.ExecuteAsync(config, MigrationMode.Repair, progress, logProgress, cancellationToken);
}
