using MigrationCore.Models.Ai;

namespace MigrationCore.Services.Ai;

/// <summary>
/// 共享只读分析工具底座契约（二期工具内置 AI 助手与四期外部本地 MCP 共用）。
/// 架构红线：本接口及实现严禁引用或调用任何迁移、清理、还原、删文件或命令执行器，
/// 严禁读取任何磁盘文件内容，仅暴露脱敏元数据与只读建议收件箱提交能力。
/// </summary>
public interface IAiAnalysisToolset
{
    AiCapabilitiesResponse GetCapabilities();

    AiSystemEnvironmentResponse GetSystemEnvironment();

    AiScanListResponse ListScans(string? cursor = null, int pageSize = 20);

    AiScanSummaryResponse GetScanSummary(Guid scanId);

    AiDirectoryChildrenResponse ListDirectoryChildren(Guid scanId, int nodeId, string? cursor = null, int pageSize = 30, long minSizeMb = 0);

    AiMigrationCandidatesResponse ListMigrationCandidates(Guid scanId, string? cursor = null, int pageSize = 30, int? parentNodeId = null);

    AiCandidateDetailsResponse GetCandidateDetails(Guid scanId, int nodeId);

    AiUnknownDirectoryMetadataResponse InspectUnknownDirectoryMetadata(Guid scanId, int nodeId);

    AiMigrationPolicyResponse GetMigrationPolicy();

    AiMigrationProposalReceipt SubmitMigrationProposal(AiMigrationProposalRequest request);

    AiProposalStatusResponse GetProposalStatus(Guid proposalId);
}
