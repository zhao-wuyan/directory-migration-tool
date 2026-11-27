namespace MigrationCore.Models.Ai;

/// <summary>
/// 内置 AI 模型服务商预设模板（严格对齐 docs/untitled.pen 画板 10）。
/// </summary>
public enum AiProviderPreset
{
    DeepSeek,
    Qwen,
    OpenAi,
    Ollama,
    LmStudio,
    Custom
}

/// <summary>
/// AI 回复语言偏好（引导模型推理、过程说明与最终结论使用的语言）。
/// </summary>
public enum AiResponseLanguage
{
    Chinese = 0,
    English = 1
}

/// <summary>
/// 通用模型思考等级（对应 OpenAI 兼容协议 reasoning_effort：默认自适应，或 none / minimal / low / medium / high / xhigh / max）。
/// </summary>
public enum AiReasoningEffort
{
    Default = 0,
    None = 1,
    Minimal = 2,
    Low = 3,
    Medium = 4,
    High = 5,
    XHigh = 6,
    Max = 7
}

/// <summary>
/// 预设模板元数据。
/// </summary>
public sealed record AiProviderPresetDefinition(
    AiProviderPreset Preset,
    string DisplayName,
    string DefaultEndpoint,
    string DefaultModel,
    bool IsLocalByDefault,
    bool RequiresApiKey,
    string HintText);

/// <summary>
/// 持久化到受 ACL 保护本地配置目录的 AI 模型配置（API Key 仅以 Windows DPAPI 密文保存）。
/// </summary>
public sealed record AiProviderSettings
{
    public const int SchemaVersionCurrent = 3;
    /// <summary>默认工具调用步数上限（0 表示不限制，由防死循环检测、重复调用拦截与整体超时兜底保护）。</summary>
    public const int DefaultMaxToolSteps = 0;
    /// <summary>用户可配置的最大步数及极端失控时的物理安全熔断上限。</summary>
    public const int HardMaxToolSteps = 200;
    /// <summary>动态无响应空闲超时默认秒数（仅当连续无任何思考/正文/工具分片输出时触发，有输出自动续期）。</summary>
    public const int DefaultTimeoutSeconds = 90;
    public const int HardMaxTimeoutSeconds = 300;
    /// <summary>单次分析整体安全兜底上限默认与范围（默认 20 分钟 = 1200 秒，支持 2~120 分钟）。</summary>
    public const int MinOverallTimeoutMinutes = 2;
    public const int DefaultOverallTimeoutMinutes = 20;
    public const int MaxOverallTimeoutMinutes = 120;
    public const int DefaultOverallTimeoutSeconds = DefaultOverallTimeoutMinutes * 60;
    public const int HardMaxOverallTimeoutSeconds = MaxOverallTimeoutMinutes * 60;
    /// <summary>AI 对话与分析正文默认字号与安全范围（px）。</summary>
    public const double DefaultChatFontSize = 11.5;
    public const double MinChatFontSize = 10.0;
    public const double MaxChatFontSize = 16.0;
    /// <summary>模型上下文窗口大小（0 表示自动：优先从 API 自动探测，探测不到时按型号估算；手动范围 2K ~ 2M tokens）。</summary>
    public const int DefaultContextWindowTokens = 0;
    public const int MinContextWindowTokens = 2_048;
    public const int MaxContextWindowTokens = 2_097_152;

    public int SchemaVersion { get; init; } = SchemaVersionCurrent;
    public AiProviderPreset Preset { get; init; } = AiProviderPreset.DeepSeek;
    public string Endpoint { get; init; } = "https://api.deepseek.com/v1";
    public string ModelName { get; init; } = "deepseek-chat";
    /// <summary>工具调用步数上限（0 表示不限制；1~200 表示按指定上限截断）。</summary>
    public int MaxToolSteps { get; init; } = DefaultMaxToolSteps;
    /// <summary>动态空闲无响应超时（秒）。</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
    /// <summary>单次分析整体兜底安全上限（秒，默认 1200 秒 = 20 分钟）。</summary>
    public int OverallTimeoutSeconds { get; init; } = DefaultOverallTimeoutSeconds;
    /// <summary>用户配置的上下文窗口大小（tokens；0 表示自动探测/估算）。</summary>
    public int ContextWindowTokens { get; init; } = DefaultContextWindowTokens;
    /// <summary>最近一次从 API 自动探测到的模型上下文窗口大小（tokens）。</summary>
    public int? DetectedContextWindowTokens { get; init; }
    /// <summary>最近一次自动探测的来源端点（如 /api/v0/models、/api/ps、/v1/models）。</summary>
    public string? DetectedContextWindowSource { get; init; }
    /// <summary>最近一次自动探测对应的 Endpoint|ModelName 标识，切换模型或端点后自动失效。</summary>
    public string? DetectedContextWindowTargetKey { get; init; }
    /// <summary>AI 回复语言偏好（默认简体中文，可选英文）。</summary>
    public AiResponseLanguage ResponseLanguage { get; init; } = AiResponseLanguage.Chinese;
    /// <summary>通用模型思考等级（默认自适应不强传，可选 none / minimal / low / medium / high / xhigh / max）。</summary>
    public AiReasoningEffort ReasoningEffort { get; init; } = AiReasoningEffort.Default;
    /// <summary>AI 对话正文默认字号（10.0 ~ 16.0，默认 11.5）。</summary>
    public double ChatFontSize { get; init; } = DefaultChatFontSize;
    public bool CloudPrivacyAcknowledged { get; init; }
    public string? EncryptedApiKeyBase64 { get; init; }
    public string? MaskedApiKeyPreview { get; init; }
    public DateTimeOffset? LastConnectionTestedUtc { get; init; }
    public bool LastConnectionSucceeded { get; init; }
    public bool LastConnectionSupportsToolCalling { get; init; }
    public string? LastConnectionSummary { get; init; }

    /// <summary>规范化后的配置步数上限（0 = 不限；1~HardMaxToolSteps = 显式上限）。</summary>
    public int EffectiveMaxToolSteps => MaxToolSteps <= 0 ? 0 : Math.Clamp(MaxToolSteps, 1, HardMaxToolSteps);
    /// <summary>是否处于不限工具调用步数模式。</summary>
    public bool IsToolStepUnlimited => EffectiveMaxToolSteps == 0;
    /// <summary>运行时实际采用的安全上限（不限模式下仍保留物理防崩熔断上限）。</summary>
    public int RuntimeMaxToolSteps => IsToolStepUnlimited ? HardMaxToolSteps : EffectiveMaxToolSteps;
    public int EffectiveTimeoutSeconds => Math.Clamp(TimeoutSeconds, 15, HardMaxTimeoutSeconds);
    public int EffectiveOverallTimeoutSeconds => Math.Clamp(
        OverallTimeoutSeconds,
        Math.Max(MinOverallTimeoutMinutes * 60, EffectiveTimeoutSeconds),
        HardMaxOverallTimeoutSeconds);
    public int EffectiveOverallTimeoutMinutes => (int)Math.Clamp(
        Math.Round(EffectiveOverallTimeoutSeconds / 60.0),
        MinOverallTimeoutMinutes,
        MaxOverallTimeoutMinutes);
    public double EffectiveChatFontSize => double.IsFinite(ChatFontSize) && ChatFontSize > 0
        ? Math.Clamp(Math.Round(ChatFontSize * 2.0) / 2.0, MinChatFontSize, MaxChatFontSize)
        : DefaultChatFontSize;

    /// <summary>规范化后的手动上下文窗口配置（0 表示自动探测/估算；大于 0 表示用户手动指定）。</summary>
    public int EffectiveConfiguredContextWindowTokens => ContextWindowTokens <= 0
        ? 0
        : Math.Clamp(ContextWindowTokens, MinContextWindowTokens, MaxContextWindowTokens);

    public bool IsContextWindowAuto => EffectiveConfiguredContextWindowTokens == 0;

    public string BuildModelTargetKey() =>
        $"{(Endpoint ?? string.Empty).Trim().TrimEnd('/')}|{(ModelName ?? string.Empty).Trim()}";

    public int? GetValidDetectedContextWindowTokens()
    {
        if (DetectedContextWindowTokens is int detected
            && detected >= MinContextWindowTokens
            && detected <= MaxContextWindowTokens
            && string.Equals(DetectedContextWindowTargetKey, BuildModelTargetKey(), StringComparison.OrdinalIgnoreCase))
        {
            return detected;
        }
        return null;
    }

    /// <summary>
    /// 按优先级解析当前生效的模型上下文窗口上限：手动设置 &gt; API 自动探测（当前模型匹配） &gt; 型号启发式估算。
    /// </summary>
    public int ResolveEffectiveContextWindowTokens(int fallbackEstimatedTokens, out string sourceLabel)
    {
        if (EffectiveConfiguredContextWindowTokens > 0)
        {
            sourceLabel = "手动设置";
            return EffectiveConfiguredContextWindowTokens;
        }

        if (GetValidDetectedContextWindowTokens() is int detected)
        {
            sourceLabel = string.IsNullOrWhiteSpace(DetectedContextWindowSource)
                ? "API 自动获取"
                : $"API · {DetectedContextWindowSource}";
            return detected;
        }

        sourceLabel = "型号估算";
        return Math.Clamp(fallbackEstimatedTokens, MinContextWindowTokens, MaxContextWindowTokens);
    }

    public string? GetReasoningEffortWireValue() => ToReasoningEffortWireValue(ReasoningEffort);

    public static string? ToReasoningEffortWireValue(AiReasoningEffort effort) => effort switch
    {
        AiReasoningEffort.None => "none",
        AiReasoningEffort.Minimal => "minimal",
        AiReasoningEffort.Low => "low",
        AiReasoningEffort.Medium => "medium",
        AiReasoningEffort.High => "high",
        AiReasoningEffort.XHigh => "xhigh",
        AiReasoningEffort.Max => "max",
        _ => null
    };

    public static string GetReasoningEffortLabel(AiReasoningEffort effort) => effort switch
    {
        AiReasoningEffort.None => "关闭",
        AiReasoningEffort.Minimal => "极低",
        AiReasoningEffort.Low => "低",
        AiReasoningEffort.Medium => "中",
        AiReasoningEffort.High => "高",
        AiReasoningEffort.XHigh => "超高",
        AiReasoningEffort.Max => "极限",
        _ => "默认"
    };
    public bool HasSavedApiKey => !string.IsNullOrWhiteSpace(EncryptedApiKeyBase64);

    /// <summary>
    /// 依据实际 Endpoint Host 是否为本机 loopback 判定是否为本地零外发端点。
    /// </summary>
    public bool IsLocalEndpoint => IsLoopbackEndpoint(Endpoint);

    public static bool IsLoopbackEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return uri.IsLoopback
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }

    public string GetOriginBadgeText()
    {
        string providerLabel = Preset switch
        {
            AiProviderPreset.DeepSeek => "DeepSeek",
            AiProviderPreset.Qwen => "通义千问",
            AiProviderPreset.OpenAi => "OpenAI",
            AiProviderPreset.Ollama => "Ollama",
            AiProviderPreset.LmStudio => "LM Studio",
            _ => IsLocalEndpoint ? "本地端点" : "自定义"
        };
        return IsLocalEndpoint ? $"本地 · {providerLabel}" : $"云端 · {providerLabel}";
    }

    public static IReadOnlyList<AiProviderPresetDefinition> Presets { get; } =
    [
        new(AiProviderPreset.DeepSeek, "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", false, true, "支持 OpenAI 兼容协议与 Tool Calling"),
        new(AiProviderPreset.Qwen, "通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", false, true, "阿里云百炼 OpenAI 兼容接口"),
        new(AiProviderPreset.OpenAi, "OpenAI", "https://api.openai.com/v1", "gpt-4o-mini", false, true, "OpenAI 官方 / 兼容代理接口"),
        new(AiProviderPreset.Ollama, "本地 Ollama", "http://localhost:11434/v1", "qwen2.5:14b", true, false, "本地运行，目录元数据 100% 不出本机（无需 API Key）"),
        new(AiProviderPreset.LmStudio, "LM Studio", "http://localhost:1234/v1", "local-model", true, false, "本地桌面推理服务，零外发（无需 API Key）"),
        new(AiProviderPreset.Custom, "自定义端点", "https://api.example.com/v1", "gpt-4o-mini", false, false, "任意 OpenAI 兼容 /v1/chat/completions 端点")
    ];

    public static AiProviderPresetDefinition GetPresetDefinition(AiProviderPreset preset) =>
        Presets.First(p => p.Preset == preset);
}

/// <summary>
/// 从 API 自动探测模型上下文窗口大小的只读结果。
/// </summary>
public sealed record AiModelContextProbeResult(
    bool Found,
    int? ContextWindowTokens,
    string? SourceEndpoint,
    string DetailMessage);

/// <summary>
/// 连接测试结果。
/// </summary>
public sealed record AiConnectionTestResult(
    bool Success,
    bool SupportsToolCalling,
    long ElapsedMilliseconds,
    string StatusBadgeText,
    string DetailMessage,
    int? DetectedContextWindowTokens = null,
    string? DetectedContextWindowSource = null);

/// <summary>
/// 单次工具调用的可观测轨迹记录（ResultPayload 保存实际返回给模型的脱敏 JSON，供用户点击审计查看）。
/// </summary>
public sealed record AiToolTraceEntry(
    int StepNumber,
    string ToolName,
    string ArgumentsSummary,
    string ResultSummary,
    long ElapsedMilliseconds,
    bool Succeeded,
    string? ResultPayload = null);

/// <summary>
/// AI 助手所处的业务入口场景（驱动独立会话隔离、快捷推荐按钮与系统提示词侧重）。
/// </summary>
public enum AiChatScene
{
    /// <summary>独立 AI 工作台：全盘腾空间综合诊断视角（联合磁盘环境、双快照与备份/隔离区）。</summary>
    General = 0,
    /// <summary>磁盘扫描页抽屉：聚焦大目录定位、L1/L3 安全迁移与 L2 原生引导。</summary>
    DiskScan = 1,
    /// <summary>垃圾清理页抽屉：聚焦三档规则解读、谨慎项风险评估与规则外「AI 疑似残留」排查。</summary>
    Cleanup = 2
}

/// <summary>
/// AI 交互式扫描引导卡片请求的扫描类型（零执行权：仅向用户弹出勾选确认卡片，绝不自动扫描）。
/// </summary>
public enum AiScanActionKind
{
    DiskScan = 0,
    CleanupScan = 1,
    Both = 2
}

/// <summary>
/// AI 发起的交互式扫描确认卡片载荷（仅引导 UI 渲染，用户手动选盘并点击后才开始扫描）。
/// </summary>
public sealed record AiScanActionProposal(
    Guid RequestId,
    AiScanActionKind Kind,
    string[] RecommendedDrives,
    string Reason,
    bool IncludeCleanupScan);

/// <summary>
/// 单次 AI 分析的上下文占用、Token 消耗与生成速度遥测统计。
/// </summary>
public sealed record AiCallTelemetry(
    int LastPromptTokens,
    int LastCompletionTokens,
    int LastTotalTokens,
    int TotalInputTokensThisRun,
    int TotalOutputTokensThisRun,
    int EstimatedContextWindowTokens,
    bool IsExactUsageFromApi,
    int ModelRequestCount,
    int ToolCallCount,
    long ElapsedMilliseconds,
    long FirstTokenMilliseconds,
    double TokensPerSecond,
    bool IsStreaming = false,
    double AverageTokensPerSecond = 0);

public enum AiAgentEventKind
{
    ThinkingDelta,
    AssistantTextDelta,
    ToolCallStarted,
    ToolCallCompleted,
    ProposalRecorded,
    ScanActionRequested,
    TelemetryUpdated,
    TimeoutApproaching,
    Warning,
    Completed,
    Cancelled,
    Error
}

/// <summary>
/// 单次分析接近总耗时上限时向 UI 推送的实时预警载荷（供底部弹出延长限时询问条）。
/// </summary>
public sealed record AiTimeoutApproachingInfo(
    DateTimeOffset DeadlineUtc,
    TimeSpan Remaining,
    TimeSpan CurrentTotalBudget,
    int ExtensionCount);

/// <summary>
/// 线程安全的单次分析总耗时动态延长控制器：允许用户在接近限时上限时无损续期，不中断进行中的流式输出。
/// </summary>
public sealed class AiOverallTimeoutController
{
    private readonly Lock _sync = new();
    private DateTimeOffset _deadlineUtc;
    private TimeSpan _totalBudget;
    private int _extensionCount;
    private bool _isActive;
    private Func<TimeSpan, DateTimeOffset, TimeSpan, bool>? _onExtendCallback;

    public DateTimeOffset DeadlineUtc
    {
        get { lock (_sync) return _deadlineUtc; }
    }

    public TimeSpan TotalBudget
    {
        get { lock (_sync) return _totalBudget; }
    }

    public int ExtensionCount
    {
        get { lock (_sync) return _extensionCount; }
    }

    public bool IsActive
    {
        get { lock (_sync) return _isActive; }
    }

    public TimeSpan Remaining
    {
        get
        {
            lock (_sync)
            {
                TimeSpan rem = _deadlineUtc - DateTimeOffset.UtcNow;
                return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
            }
        }
    }

    internal void Bind(
        DateTimeOffset initialDeadlineUtc,
        TimeSpan initialTotalBudget,
        Func<TimeSpan, DateTimeOffset, TimeSpan, bool> onExtendCallback)
    {
        lock (_sync)
        {
            _deadlineUtc = initialDeadlineUtc;
            _totalBudget = initialTotalBudget;
            _extensionCount = 0;
            _onExtendCallback = onExtendCallback;
            _isActive = true;
        }
    }

    internal void Unbind()
    {
        lock (_sync)
        {
            _isActive = false;
            _onExtendCallback = null;
        }
    }

    public bool TryExtend(TimeSpan additionalDuration, out DateTimeOffset newDeadlineUtc, out TimeSpan newTotalBudget)
    {
        if (additionalDuration <= TimeSpan.Zero)
        {
            lock (_sync)
            {
                newDeadlineUtc = _deadlineUtc;
                newTotalBudget = _totalBudget;
            }
            return false;
        }

        Func<TimeSpan, DateTimeOffset, TimeSpan, bool>? callback;
        TimeSpan newRemaining;
        lock (_sync)
        {
            if (!_isActive || _onExtendCallback is null)
            {
                newDeadlineUtc = _deadlineUtc;
                newTotalBudget = _totalBudget;
                return false;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset basePoint = _deadlineUtc > now ? _deadlineUtc : now;
            newDeadlineUtc = basePoint.Add(additionalDuration);
            newTotalBudget = _totalBudget + additionalDuration;
            newRemaining = newDeadlineUtc - now;
            callback = _onExtendCallback;
        }

        if (!callback(newRemaining, newDeadlineUtc, newTotalBudget))
        {
            lock (_sync)
            {
                newDeadlineUtc = _deadlineUtc;
                newTotalBudget = _totalBudget;
            }
            return false;
        }

        lock (_sync)
        {
            _deadlineUtc = newDeadlineUtc;
            _totalBudget = newTotalBudget;
            _extensionCount++;
        }
        return true;
    }
}

/// <summary>
/// 内置 AI 受控单 Agent 循环向 UI 推送的流式事件。
/// </summary>
public sealed record AiAgentRunEvent(
    AiAgentEventKind Kind,
    int CurrentStep,
    int MaxSteps,
    string? TextDelta = null,
    AiToolTraceEntry? TraceEntry = null,
    AiMigrationProposalReceipt? ProposalReceipt = null,
    string? Message = null,
    AiCleanupProposalReceipt? CleanupProposalReceipt = null,
    AiScanActionProposal? ScanActionProposal = null,
    AiCallTelemetry? Telemetry = null,
    AiTimeoutApproachingInfo? TimeoutApproaching = null);

/// <summary>
/// 对话历史轮次（纯文本）。
/// </summary>
public sealed record AiConversationTurn(
    bool IsUser,
    string Content);

/// <summary>
/// 单次 AI 分析请求。
/// </summary>
public sealed record AiAgentAnalysisRequest(
    Guid? ActiveScanId,
    string UserPrompt,
    IReadOnlyList<AiConversationTurn>? History = null,
    Guid? ActiveCleanupScanId = null,
    AiChatScene Scene = AiChatScene.General,
    IReadOnlyList<Guid>? AuthorizedScanIds = null,
    AiOverallTimeoutController? OverallTimeoutController = null);

/// <summary>
/// 单次 AI 分析最终结果。
/// </summary>
public sealed record AiAgentAnalysisResult(
    bool Succeeded,
    bool WasCancelled,
    bool HitStepLimit,
    string AssistantMessage,
    AiToolTraceEntry[] ToolTraces,
    AiMigrationProposalReceipt[] Proposals,
    int StepsUsed,
    int MaxSteps,
    long ElapsedMilliseconds,
    string? ErrorMessage = null,
    AiCleanupProposalReceipt[]? CleanupProposals = null,
    AiScanActionProposal[]? ScanActionProposals = null,
    AiCallTelemetry? Telemetry = null);
