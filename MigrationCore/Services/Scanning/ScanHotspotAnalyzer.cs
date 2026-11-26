using System.Collections.Frozen;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services.Scanning;

/// <summary>
/// 热点目录采集遍历配置（统一磁盘扫描页 UI 与 AI 只读工具底座的不重叠大目录热点口径）。
/// </summary>
public sealed record HotspotTraversalOptions(
    int MaxL0Depth = 10,
    int MaxL3Depth = 10,
    bool UseMaxLogicalOrAllocatedSize = false,
    bool DescendKnownAppRootNames = true,
    bool StrictL0ContainerFilter = false,
    int? StartNodeId = null,
    SafetyPolicyEnvironment? PolicyEnvironment = null)
{
    public static HotspotTraversalOptions DiskScanUi { get; } = new(
        MaxL0Depth: 10,
        MaxL3Depth: 10,
        UseMaxLogicalOrAllocatedSize: false,
        DescendKnownAppRootNames: true,
        StrictL0ContainerFilter: false);

    public static HotspotTraversalOptions ForAiToolset(int? startNodeId = null, SafetyPolicyEnvironment? policyEnvironment = null) => new(
        MaxL0Depth: 5,
        MaxL3Depth: 4,
        UseMaxLogicalOrAllocatedSize: true,
        DescendKnownAppRootNames: false,
        StrictL0ContainerFilter: true,
        StartNodeId: startNodeId,
        PolicyEnvironment: policyEnvironment);
}

/// <summary>
/// 共享不重叠大目录热点分析器（单一事实源，服务于 DiskScanViewModel 与 ReadOnlyAiAnalysisToolset）。
/// 规则：
/// 1. 命中 L1（推荐迁移）或 L2（原生引导）时直接产出并停止下钻，绝不父子重复计数；
/// 2. 命中 L0 系统容器（Users / AppData / Program Files 等）时仅下钻到第三方应用数据层，跳过 OS 内核黑盒；
/// 3. 命中 L3 普通包装目录（Programs / Apps / Games / AI_Workspace）、已知应用根或单一 >=85% 主导子目录时自动下钻。
/// </summary>
public static class ScanHotspotAnalyzer
{
    private static readonly FrozenSet<string> NonDescendableOsBlackBoxNames =
        ProtectedPathVocabulary.CreateIgnoreCaseSet(["Windows", "WinSxS", "Temp", "Microsoft", "Packages"]);

    private static readonly FrozenSet<string> StrictDescendableL0ContainerNames =
        ProtectedPathVocabulary.CreateIgnoreCaseSet(["Users", "AppData", "Local", "Roaming", "LocalLow", "Program Files", "Program Files (x86)", "ProgramData", "Programs"]);

    private static readonly FrozenSet<string> WrapperDirectoryNames =
        ProtectedPathVocabulary.CreateIgnoreCaseSet(["Programs", "Apps", "Games", "AI_Workspace"]);

    private static readonly Lazy<FrozenSet<string>> KnownAppRootNames = new(() =>
        ProtectedPathVocabulary.CreateIgnoreCaseSet(
            BuiltInApplicationRules.All.SelectMany(r => r.PathContainsMarkers.Concat(r.InstallDetectionNames))));

    public static int[] CollectDisjointHotspots(
        ScanSnapshot snapshot,
        ISafetyApplicationMatcher? catalog,
        long minAllocatedBytes,
        int maxCount,
        HotspotTraversalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HotspotTraversalOptions opt = options ?? HotspotTraversalOptions.DiskScanUi;
        var results = new List<(int NodeId, long Size)>();

        long GetSize(in ScanNodeRecord node) =>
            opt.UseMaxLogicalOrAllocatedSize
                ? Math.Max(node.UniqueAllocatedBytes, node.LogicalBytes)
                : node.UniqueAllocatedBytes;

        SafetyPolicyDecision Evaluate(string fullPath) =>
            MigrationSafetyPolicy.EvaluateSource(
                fullPath,
                catalog,
                SafetyEvaluationContext.ScanPreview,
                checkFilesystem: false,
                environment: opt.PolicyEnvironment);

        void Visit(int nodeId, int depth)
        {
            ReadOnlySpan<int> children = snapshot.GetChildrenSpan(nodeId);
            for (int i = 0; i < children.Length; i++)
            {
                int childId = children[i];
                ref readonly ScanNodeRecord child = ref snapshot.Nodes[childId];
                if (!child.IsDirectory || child.IsOrphaned) continue;
                if (MigrationSafetyPolicy.IsHiddenSystemVolumeNode(snapshot, childId)) continue;

                long size = GetSize(in child);
                if (size < minAllocatedBytes) continue;

                string fullPath = snapshot.GetFullPath(childId);
                string name = snapshot.GetNodeName(childId);
                SafetyPolicyDecision decision = Evaluate(fullPath);

                if (decision.Level is MigrationAdmissionLevel.VerifiedEligible or MigrationAdmissionLevel.VendorNativeRecommended)
                {
                    results.Add((childId, size));
                    continue;
                }

                if (decision.IsHardDenied)
                {
                    if (!child.IsReparsePoint && depth < opt.MaxL0Depth && ShouldDescendL0Container(name, fullPath, decision, opt.StrictL0ContainerFilter))
                    {
                        Visit(childId, depth + 1);
                    }
                    continue;
                }

                if (!child.IsReparsePoint && depth < opt.MaxL3Depth
                    && ShouldDescendL3Wrapper(snapshot, childId, name, size, minAllocatedBytes, opt, Evaluate))
                {
                    Visit(childId, depth + 1);
                    continue;
                }

                results.Add((childId, size));
            }
        }

        Visit(opt.StartNodeId ?? snapshot.RootNodeId, depth: 0);

        return results
            .OrderByDescending(x => x.Size)
            .Take(maxCount)
            .Select(x => x.NodeId)
            .ToArray();
    }

    public static bool IsStrictDescendableL0Container(string fullPath, SafetyPolicyDecision decision)
    {
        if (decision.ReasonCodes.Contains(SafetyReasonCodes.L0SystemContainerRoot, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }
        string leaf = Path.GetFileName(fullPath.TrimEnd('\\'));
        return StrictDescendableL0ContainerNames.Contains(leaf);
    }

    private static bool ShouldDescendL0Container(string name, string fullPath, SafetyPolicyDecision decision, bool strictContainerFilter)
    {
        if (strictContainerFilter)
        {
            return IsStrictDescendableL0Container(fullPath, decision);
        }

        if (MigrationSafetyPolicy.IsVolumeRootHardDeniedName(name) || NonDescendableOsBlackBoxNames.Contains(name))
        {
            return false;
        }

        return !fullPath.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldDescendL3Wrapper(
        ScanSnapshot snapshot,
        int nodeId,
        string name,
        long parentSize,
        long minAllocatedBytes,
        HotspotTraversalOptions opt,
        Func<string, SafetyPolicyDecision> evaluate)
    {
        ReadOnlySpan<int> children = snapshot.GetChildrenSpan(nodeId);
        if (children.Length == 0) return false;

        bool isWrapperName = WrapperDirectoryNames.Contains(name);
        long largestChildDirSize = 0;
        int qualifyingChildDirs = 0;

        for (int i = 0; i < children.Length; i++)
        {
            int cid = children[i];
            ref readonly ScanNodeRecord c = ref snapshot.Nodes[cid];
            if (!c.IsDirectory || c.IsOrphaned) continue;

            long cSize = opt.UseMaxLogicalOrAllocatedSize
                ? Math.Max(c.UniqueAllocatedBytes, c.LogicalBytes)
                : c.UniqueAllocatedBytes;
            if (cSize < minAllocatedBytes) continue;

            qualifyingChildDirs++;
            if (cSize > largestChildDirSize) largestChildDirSize = cSize;

            SafetyPolicyDecision cd = evaluate(snapshot.GetFullPath(cid));
            if (cd.Level is MigrationAdmissionLevel.VerifiedEligible or MigrationAdmissionLevel.VendorNativeRecommended)
            {
                return true;
            }
        }

        if (qualifyingChildDirs == 0) return false;
        if (isWrapperName) return true;

        if (opt.DescendKnownAppRootNames && KnownAppRootNames.Value.Contains(name))
        {
            return true;
        }

        return qualifyingChildDirs == 1 && parentSize > 0 && largestChildDirSize * 100.0 / parentSize >= 85.0;
    }
}
