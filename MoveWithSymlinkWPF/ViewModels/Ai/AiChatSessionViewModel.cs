using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Ai;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Ai;
using MigrationCore.Services.Scanning;
using MoveWithSymlinkWPF.Helpers;

namespace MoveWithSymlinkWPF.ViewModels;

/// <summary>
/// 单场景独立隔离的 AI 会话视图模型（综合诊断 / 磁盘扫描 / 垃圾清理各自持有一份实例）。
/// 保证：
/// 1. 切换页面与入口时对话流、输入框草稿与卡片状态永久保活不重置；
/// 2. 每个场景持有独立 CancellationTokenSource，支持后台真正并发分析互不打断；
/// 3. 按入口场景定制 3 个快捷推荐按钮、空态文案、输入框占位词与缺盘前置检测卡片。
/// </summary>
public partial class AiChatSessionViewModel : ObservableObject
{
    private readonly AiAssistantViewModel _host;
    private CancellationTokenSource? _analysisCts;
    private AiOverallTimeoutController? _activeTimeoutController;
    private AiConversationTurnItem? _activeAnalysisTurn;
    private DispatcherTimer? _timeoutCountdownTimer;
    private DateTimeOffset _timeoutWarningDeadlineUtc;

    public AiAssistantViewModel Host => _host;
    public AiChatScene Scene { get; }

    public ObservableCollection<AiConversationTurnItem> ConversationTurns { get; } = [];

    [ObservableProperty]
    private string _userPromptInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendPrompt))]
    [NotifyPropertyChangedFor(nameof(SessionTabBadgeText))]
    private bool _isAnalyzing;

    // ---- 单次分析接近总耗时上限时的底部延长限时询问提示 ----
    [ObservableProperty]
    private bool _showTimeoutExtensionPrompt;

    [ObservableProperty]
    private string _timeoutCountdownBadgeText = string.Empty;

    [ObservableProperty]
    private string _timeoutPromptDescription = string.Empty;

    // ---- 本会话上下文占用圆环与调用统计（点击输入框右侧圆环展开浮层卡） ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextUsagePercent))]
    [NotifyPropertyChangedFor(nameof(ContextUsagePercentText))]
    [NotifyPropertyChangedFor(nameof(ContextUsageSummaryText))]
    [NotifyPropertyChangedFor(nameof(ContextRingStrokeColor))]
    [NotifyPropertyChangedFor(nameof(ContextRingArcData))]
    [NotifyPropertyChangedFor(nameof(ContextRingArcGeometry))]
    [NotifyPropertyChangedFor(nameof(ContextRingToolTip))]
    private int _contextUsedTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextUsagePercent))]
    [NotifyPropertyChangedFor(nameof(ContextUsagePercentText))]
    [NotifyPropertyChangedFor(nameof(ContextUsageSummaryText))]
    [NotifyPropertyChangedFor(nameof(ContextRingStrokeColor))]
    [NotifyPropertyChangedFor(nameof(ContextRingArcData))]
    [NotifyPropertyChangedFor(nameof(ContextRingArcGeometry))]
    [NotifyPropertyChangedFor(nameof(ContextRingToolTip))]
    private int _contextWindowTokens = 65_536;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageSourceBadgeText))]
    [NotifyPropertyChangedFor(nameof(UsageSourceBadgeBg))]
    [NotifyPropertyChangedFor(nameof(UsageSourceBadgeFg))]
    private bool _contextUsageIsExact;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageSourceBadgeText))]
    [NotifyPropertyChangedFor(nameof(UsageSourceBadgeBg))]
    [NotifyPropertyChangedFor(nameof(UsageSourceBadgeFg))]
    [NotifyPropertyChangedFor(nameof(ContextRingToolTip))]
    [NotifyPropertyChangedFor(nameof(SessionCumulativeSummaryText))]
    [NotifyPropertyChangedFor(nameof(LastSpeedText))]
    [NotifyPropertyChangedFor(nameof(FirstTokenLatencyText))]
    [NotifyPropertyChangedFor(nameof(LastDurationText))]
    [NotifyPropertyChangedFor(nameof(SpeedCardLabel))]
    [NotifyPropertyChangedFor(nameof(SpeedCardToolTip))]
    private bool _hasTelemetry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedCardLabel))]
    [NotifyPropertyChangedFor(nameof(SpeedCardToolTip))]
    private bool _isTelemetryStreaming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedCardToolTip))]
    private double _lastAverageTokensPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionCumulativeSummaryText))]
    private int _sessionTotalInputTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionCumulativeSummaryText))]
    private int _sessionTotalOutputTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionCumulativeSummaryText))]
    private int _sessionRequestCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionCumulativeSummaryText))]
    private int _sessionToolCallCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSpeedText))]
    private double _lastTokensPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirstTokenLatencyText))]
    private long _lastFirstTokenMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastDurationText))]
    private long _lastElapsedMs;

    private int _baselineSessionInputTokens;
    private int _baselineSessionOutputTokens;
    private int _baselineSessionRequests;
    private int _baselineSessionToolCalls;

    public double ContextUsagePercent => ContextWindowTokens > 0
        ? Math.Clamp(Math.Round(ContextUsedTokens * 100.0 / ContextWindowTokens, 1), 0, 100)
        : 0;

    public string ContextUsagePercentText => $"{ContextUsagePercent:0.#}%";

    public string ContextUsageSummaryText => HasTelemetry
        ? $"{FormatTokenCount(ContextUsedTokens)} / {FormatTokenCount(ContextWindowTokens)} tokens"
        : $"0 / {FormatTokenCount(ContextWindowTokens)} tokens";

    public string UsageSourceBadgeText => !HasTelemetry
        ? "暂无调用"
        : ContextUsageIsExact ? "API 实测" : "本地估算";

    public string UsageSourceBadgeBg => !HasTelemetry ? "#F1F5F9"
        : ContextUsageIsExact ? "#ECFDF5" : "#FFFBEB";

    public string UsageSourceBadgeFg => !HasTelemetry ? "#64748B"
        : ContextUsageIsExact ? "#0F7B4B" : "#B45309";

    public string ContextRingStrokeColor => !HasTelemetry || ContextUsedTokens <= 0 ? "#CBD5E1"
        : ContextUsagePercent >= 85 ? "#C42B1C"
        : ContextUsagePercent >= 65 ? "#D97706"
        : "#0078D4";

    public string ContextRingArcData => BuildContextRingArcGeometry(ContextUsagePercent);

    public Geometry ContextRingArcGeometry
    {
        get
        {
            string data = ContextRingArcData;
            if (string.IsNullOrEmpty(data)) return Geometry.Empty;
            Geometry parsed = Geometry.Parse(data);
            if (parsed.CanFreeze) parsed.Freeze();
            return parsed;
        }
    }

    public string ContextRingToolTip => !HasTelemetry
        ? "上下文占用：0% · 点击查看 AI 调用统计"
        : $"上下文占用 {ContextUsagePercent:0.#}%（{FormatTokenCount(ContextUsedTokens)} / {FormatTokenCount(ContextWindowTokens)}）· 速度 {LastSpeedText} · 点击查看详细统计";

    public string SpeedCardLabel => IsTelemetryStreaming ? "实时速度" : "生成速度";

    public string SpeedCardToolTip
    {
        get
        {
            if (!HasTelemetry || LastTokensPerSecond <= 0)
            {
                return "流式生成期间按最近 2 秒滑动窗口实时刷新（不含首字等待与工具执行耗时）";
            }
            string avgPart = LastAverageTokensPerSecond > 0
                ? $" · 本次累计均速 {LastAverageTokensPerSecond:F1} tok/s"
                : string.Empty;
            return IsTelemetryStreaming
                ? $"当前流式实时解码速度（最近 2 秒滑动窗口，不含首字等待）{avgPart}"
                : $"最近一轮纯解码生成速度（不含首字等待与工具耗时）{avgPart}";
        }
    }

    public string LastSpeedText => HasTelemetry && LastTokensPerSecond > 0
        ? $"{LastTokensPerSecond:F1} tok/s"
        : "—";

    public string FirstTokenLatencyText => HasTelemetry && LastFirstTokenMs > 0
        ? (LastFirstTokenMs >= 1000 ? $"{LastFirstTokenMs / 1000.0:F2} s" : $"{LastFirstTokenMs} ms")
        : "—";

    public string LastDurationText => HasTelemetry && LastElapsedMs > 0
        ? (LastElapsedMs >= 1000 ? $"{LastElapsedMs / 1000.0:F1} s" : $"{LastElapsedMs} ms")
        : "—";

    public string SessionCumulativeSummaryText => HasTelemetry
        ? $"输入 {FormatTokenCount(SessionTotalInputTokens)} · 输出 {FormatTokenCount(SessionTotalOutputTokens)} · {SessionRequestCount} 次请求 · {SessionToolCallCount} 次工具"
        : "本会话尚未发起模型调用";

    public string ContextWindowSourceText => _host.ContextWindowSourceText;

    internal void ApplyContextWindowTokens(int tokens)
    {
        if (tokens >= AiProviderSettings.MinContextWindowTokens)
        {
            ContextWindowTokens = tokens;
        }
        OnPropertyChanged(nameof(ContextWindowSourceText));
    }

    public bool CanSendPrompt => !IsAnalyzing;
    public bool HasConversationTurns => ConversationTurns.Count > 0;
    public string DisclaimerText => _host.DisclaimerText;

    // ---- 本会话固定吸底「建议汇总与统一审阅弹窗」快捷条（长回复未滚到底部也能一键打开弹窗） ----
    public int SessionProposalCount => ConversationTurns.Sum(t => t.ProposalTotalCount);
    public int SessionPendingProposalCount => ConversationTurns.Sum(t => t.PendingActionableProposalCount);
    public bool HasSessionProposalCards => SessionProposalCount > 0;

    public string SessionProposalStickySummaryText => SessionPendingProposalCount > 0
        ? $"已生成 {SessionProposalCount} 条 AI 建议 · {SessionPendingProposalCount} 项待审阅添加"
        : $"已生成 {SessionProposalCount} 条 AI 建议 · 点击可统一查看详情";

    public string SessionProposalStickyButtonText => SessionPendingProposalCount > 0
        ? "统一查看与添加 ↗"
        : "统一查看 ↗";

    // ---- 按入口场景定制的界面文案与 3 个快捷推荐胶囊 ----
    public string SceneLabel => Scene switch
    {
        AiChatScene.DiskScan => "磁盘扫描",
        AiChatScene.Cleanup => "垃圾清理",
        _ => "综合诊断"
    };

    public string SessionTabBadgeText
    {
        get
        {
            if (IsAnalyzing) return $"{SceneLabel} · 分析中";
            return ConversationTurns.Count > 0 ? $"{SceneLabel} ({ConversationTurns.Count})" : SceneLabel;
        }
    }

    public string QuickPrompt1Title => Scene switch
    {
        AiChatScene.DiskScan => "分析大目录",
        AiChatScene.Cleanup => "解读谨慎风险",
        _ => "全盘腾空间"
    };

    public string QuickPrompt1Key => Scene switch
    {
        AiChatScene.DiskScan => "DiskScan_LargeDirs",
        AiChatScene.Cleanup => "Cleanup_CautionRisks",
        _ => "General_GlobalSpacePlan"
    };

    public string QuickPrompt1ToolTip => Scene switch
    {
        AiChatScene.DiskScan => "按空间收益梳理当前已扫磁盘大目录，生成可审阅的 L1/L3 迁移建议卡",
        AiChatScene.Cleanup => "重点解读三档清理中「谨慎清理」项的风险原因、影响范围与保护策略",
        _ => "联合各盘余量、备份/隔离区、垃圾清理与大目录迁移，给出从易到难的组合路线图"
    };

    public string QuickPrompt2Title => Scene switch
    {
        AiChatScene.DiskScan => "诊断未知占用",
        AiChatScene.Cleanup => "推荐清理顺序",
        _ => "迁移 vs 清理"
    };

    public string QuickPrompt2Key => Scene switch
    {
        AiChatScene.DiskScan => "DiskScan_UnknownDirs",
        AiChatScene.Cleanup => "Cleanup_Priority",
        _ => "General_MigrateVsClean"
    };

    public string QuickPrompt2ToolTip => Scene switch
    {
        AiChatScene.DiskScan => "使用只读元数据画像（后缀+修改时间）诊断不认识的大目录用途",
        AiChatScene.Cleanup => "根据本次三档清理扫描结果，按安全性与释放空间排序推荐清理顺序",
        _ => "跨模块分流空间大户：明确哪些该清理、哪些该安全迁移、哪些建议走软件原生设置"
    };

    public string QuickPrompt3Title => Scene switch
    {
        AiChatScene.DiskScan => "可迁移避坑",
        AiChatScene.Cleanup => "排查规则外残留",
        _ => "本机环境体检"
    };

    public string QuickPrompt3Key => Scene switch
    {
        AiChatScene.DiskScan => "DiskScan_MigrateCheck",
        AiChatScene.Cleanup => "Cleanup_OutOfRuleResidual",
        _ => "General_EnvHealthCheck"
    };

    public string QuickPrompt3ToolTip => Scene switch
    {
        AiChatScene.DiskScan => "排查 L2 原生迁移软件（Docker/WSL/OneDrive 等）与高风险目录，避免误用符号链接",
        AiChatScene.Cleanup => "结合磁盘扫描快照排查规则库外的疑似残留目录，提交到「AI 疑似残留」人工复核",
        _ => "无需先扫盘，直接基于本机各盘余量、NTFS 权限及备份/隔离区给出空间体检建议"
    };

    public string EmptyStateTitle => Scene switch
    {
        AiChatScene.DiskScan => "磁盘扫描 · 只读 AI 迁移顾问（零执行权）",
        AiChatScene.Cleanup => "垃圾清理 · 只读 AI 清理顾问（零执行权）",
        _ => "全局空间联合诊断已就绪（零执行权）"
    };

    public string EmptyStateDescription => Scene switch
    {
        AiChatScene.DiskScan => "本会话仅服务「磁盘扫描」场景且与垃圾清理会话隔离。点击上方按钮或直接提问，AI 将分析脱敏目录结构并生成待审阅迁移建议卡片。",
        AiChatScene.Cleanup => "本会话仅服务「垃圾清理」场景且与磁盘扫描会话隔离。AI 可解读三档清理风险、排优先级，或结合你勾选扫描的盘符排查规则外疑似残留。",
        _ => "综合诊断同时掌握本机各盘余量、迁移备份/隔离区占用、垃圾清理与多盘扫描结果，帮你制定最优腾空间组合方案；顶部也可随时切回扫描/清理会话宽屏续聊。"
    };

    public string InputPlaceholder => Scene switch
    {
        AiChatScene.DiskScan => "追问大目录用途、迁移风险或 L2 原生迁移方式...",
        AiChatScene.Cleanup => "追问某项清理规则的风险，或让 AI 排查规则外残留...",
        _ => "例如：C 盘快满了先清什么再迁什么？或直接提问..."
    };

    public AiChatSessionViewModel(AiAssistantViewModel host, AiChatScene scene)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        Scene = scene;
        ConversationTurns.CollectionChanged += OnConversationTurnsCollectionChanged;
    }

    private void OnConversationTurnsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (AiConversationTurnItem oldTurn in e.OldItems)
            {
                oldTurn.PropertyChanged -= OnTurnPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (AiConversationTurnItem newTurn in e.NewItems)
            {
                newTurn.PropertyChanged += OnTurnPropertyChanged;
            }
        }
        OnPropertyChanged(nameof(HasConversationTurns));
        OnPropertyChanged(nameof(SessionTabBadgeText));
        NotifySessionProposalSummaryChanged();
        _host.NotifySessionActivityChanged(this);
    }

    private void OnTurnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AiConversationTurnItem.HasProposalCards)
            or nameof(AiConversationTurnItem.ProposalTotalCount)
            or nameof(AiConversationTurnItem.PendingActionableProposalCount))
        {
            NotifySessionProposalSummaryChanged();
        }
    }

    internal void NotifySessionProposalSummaryChanged()
    {
        OnPropertyChanged(nameof(SessionProposalCount));
        OnPropertyChanged(nameof(SessionPendingProposalCount));
        OnPropertyChanged(nameof(HasSessionProposalCards));
        OnPropertyChanged(nameof(SessionProposalStickySummaryText));
        OnPropertyChanged(nameof(SessionProposalStickyButtonText));
    }

    [RelayCommand]
    private void OpenSessionProposalsReview()
    {
        List<AiProposalCardItem> cards = ConversationTurns.SelectMany(t => t.ProposalCards).ToList();
        if (cards.Count == 0) return;

        _host.OpenProposalsReviewForCards(
            cards,
            $"{SceneLabel} · AI 建议统一审阅与添加",
            $"本会话共汇总 {cards.Count} 条 AI 结构化建议，请核实路径与理由后勾选加入对应清单（绝不立即执行）。");
    }

    [RelayCommand]
    private Task RunQuickPromptAsync(string? quickType)
    {
        string key = quickType ?? string.Empty;

        // 先做零等待前置扫描状态检测：缺少必要快照时绝不自动扫盘，直接弹交互确认卡片让用户勾选确认
        if (TryBuildPreFlightScanCard(key, out string userVisiblePrompt, out AiScanActionProposal preFlightProposal))
        {
            if (!_host.ValidateDraftConfigForSession(out _, out _))
            {
                return Task.CompletedTask;
            }

            AppendInteractiveScanPromptTurn(userVisiblePrompt, preFlightProposal);
            return Task.CompletedTask;
        }

        string prompt = ResolveQuickPromptText(key);
        UserPromptInput = prompt;
        return SendPromptAsync();
    }

    private bool TryBuildPreFlightScanCard(string key, out string userPrompt, out AiScanActionProposal proposal)
    {
        userPrompt = ResolveQuickPromptText(key);
        proposal = default!;

        bool hasDisk = _host.HasAuthorizedSnapshot;
        bool hasCleanup = _host.HasAuthorizedCleanupSnapshot;
        string[] defaultDrives = _host.GetDefaultRecommendedDrivesForScanCard();

        switch (key)
        {
            case "DiskScan_LargeDirs":
            case "DiskScan_UnknownDirs":
            case "DiskScan_MigrateCheck":
            case "LargeDirs":
            case "UnknownDirs":
                if (!hasDisk)
                {
                    proposal = new AiScanActionProposal(
                        Guid.NewGuid(),
                        AiScanActionKind.DiskScan,
                        defaultDrives,
                        "当前尚未扫描任何磁盘。请在下方勾选要分析的盘符并开始只读扫描（AI 绝不会自动扫描），扫完后将自动继续分析。",
                        IncludeCleanupScan: false);
                    return true;
                }
                return false;

            case "Cleanup_CautionRisks":
            case "Cleanup_Priority":
                if (!hasCleanup)
                {
                    proposal = new AiScanActionProposal(
                        Guid.NewGuid(),
                        AiScanActionKind.CleanupScan,
                        [],
                        "当前尚未执行垃圾清理规则扫描，AI 暂时看不到三档清理项与谨慎项明细。点击下方按钮完成只读扫描后将自动为你解读。",
                        IncludeCleanupScan: true);
                    return true;
                }
                return false;

            case "Cleanup_OutOfRuleResidual":
                if (!hasDisk)
                {
                    AiScanActionKind kind = hasCleanup ? AiScanActionKind.DiskScan : AiScanActionKind.Both;
                    string reason = hasCleanup
                        ? "排查规则库外的「AI 疑似残留」需要目标磁盘的目录结构快照。请确认需要扫描哪几块硬盘（默认仅勾选空间较紧张的盘符，避免全盘耗时），扫完后自动继续排查。"
                        : "当前既未执行垃圾清理扫描，也未扫描磁盘目录结构。建议先勾选要排查的盘符并同步扫描内置清理规则，完成后 AI 将联合排查规则外疑似残留。";
                    proposal = new AiScanActionProposal(
                        Guid.NewGuid(),
                        kind,
                        defaultDrives,
                        reason,
                        IncludeCleanupScan: !hasCleanup);
                    return true;
                }
                return false;

            case "General_GlobalSpacePlan":
            case "General_MigrateVsClean":
            case "MigrateVsClean":
                if (!hasDisk && !hasCleanup)
                {
                    proposal = new AiScanActionProposal(
                        Guid.NewGuid(),
                        AiScanActionKind.Both,
                        defaultDrives,
                        "制定全盘腾空间组合方案需要垃圾清理扫描与至少一块重点磁盘的目录扫描快照。请在下方确认要扫描的盘符与清理规则，扫完后自动生成综合方案。",
                        IncludeCleanupScan: true);
                    return true;
                }
                return false;

            case "General_EnvHealthCheck":
            default:
                return false;
        }
    }

    private static string ResolveQuickPromptText(string key) => key switch
    {
        "DiskScan_LargeDirs" or "LargeDirs" =>
            "请优先查看当前已扫描磁盘的顶层大目录与头部候选（按需逐层钻入大目录，不要拉全量），按空间收益给出优先迁移建议（严禁建议 L0 系统目录），并提交可审阅的建议卡片。",
        "DiskScan_UnknownDirs" or "UnknownDirs" =>
            "请聚焦当前已扫描磁盘的头部未知大占用目录（可按需层级钻入定位真正的大子目录），使用只读元数据画像分析它们的后缀与修改时间分布，判断哪些适合迁出、哪些疑似旧缓存残留。",
        "DiskScan_MigrateCheck" =>
            "请核查当前已扫描磁盘的头部大目录，重点区分哪些属于 L1 推荐符号链接迁移、哪些属于 L2 应走软件原生迁移设置（如 Docker/WSL2/OneDrive/游戏平台等）、哪些命中 L0 或外部硬链接不可迁移，并给出清晰避坑清单与建议卡片。",
        "Cleanup_CautionRisks" =>
            "请读取当前垃圾清理扫描结果，重点聚焦「谨慎清理（Caution）」和体积较大的「按需清理」项，逐一解释风险原因、可能影响、进程占用与保护策略，帮我判断哪些值得清、哪些建议跳过（严禁降低任何规则档位）。",
        "Cleanup_Priority" =>
            "请基于当前垃圾清理扫描结果，按「最安全优先、释放空间最大」的原则为我排出推荐清理顺序，明确标注哪些属于同卷可恢复隔离、哪些走官方系统接口以及需要先关闭哪些占用进程，并提交规则排序建议卡片。",
        "Cleanup_OutOfRuleResidual" =>
            "请结合当前已授权的磁盘扫描快照与垃圾清理结果，重点钻入体积较大且不在内置清理规则库内的目录，使用只读元数据画像排查疑似旧缓存、卸载残留或废弃构建目录，调用 submit_cleanup_proposal(kind='AiSuspectedResidual') 提交到「AI 疑似残留」供我人工复核。",
        "General_GlobalSpacePlan" =>
            "请从全局视角为我制定一套「全盘腾空间组合方案」：先检查待确认释放的迁移备份与清理隔离区、再结合垃圾清理三档结果与已扫描磁盘的大目录迁移候选，按从易到难排序给出可执行步骤与结构化建议卡片。",
        "General_MigrateVsClean" or "MigrateVsClean" =>
            "请联合当前已扫描的磁盘大目录与垃圾清理结果，帮我按「可直接清理释放」「适合安全迁移（L1/L3）」「走软件原生设置（L2）」「疑似规则外残留需人工核实」四类分流梳理空间大户，并给出结构化建议卡片。",
        "General_EnvHealthCheck" =>
            "请先调用 get_system_environment 获取本机各盘余量、文件系统、符号链接权限、特殊文件夹位置以及迁移备份/清理隔离区占用情况，为我做一次本机空间健康体检，并说明下一步最推荐先扫描哪块盘或先处理哪些留存空间。",
        _ => "请分析当前已授权的空间与扫描数据并给出安全建议。"
    };

    private void AppendInteractiveScanPromptTurn(string userPrompt, AiScanActionProposal proposal)
    {
        var turn = new AiConversationTurnItem
        {
            UserPrompt = userPrompt,
            MaxSteps = _host.EffectiveConfiguredMaxSteps,
            StatusBadgeText = "等待确认扫描",
            StatusBadgeFg = "#0078D4"
        };
        turn.TimelineItems.Add(new AiAssistantMessageStepItem
        {
            Text = proposal.Reason,
            IsStreaming = false
        });
        turn.ScanActionCards.Add(_host.CreateScanActionCard(proposal, userPrompt));
        ConversationTurns.Add(turn);
    }

    [RelayCommand]
    private Task SendPromptAsync()
    {
        string prompt = UserPromptInput.Trim();
        if (string.IsNullOrWhiteSpace(prompt)) return Task.CompletedTask;
        return ExecutePromptInternalAsync(prompt, clearInputBox: true);
    }

    internal async Task ExecutePromptInternalAsync(string prompt, bool clearInputBox)
    {
        if (IsAnalyzing || string.IsNullOrWhiteSpace(prompt)) return;

        if (!_host.PrepareExecutionSettings(out AiProviderSettings draft, out string? plainKey))
        {
            return;
        }

        if (clearInputBox)
        {
            UserPromptInput = string.Empty;
        }

        IsAnalyzing = true;
        _host.NotifySessionActivityChanged(this);

        var history = ConversationTurns
            .SelectMany(t => new[]
            {
                new AiConversationTurn(true, t.UserPrompt),
                new AiConversationTurn(false, t.AssistantText)
            })
            .Where(h => !string.IsNullOrWhiteSpace(h.Content))
            .ToList();

        var turnItem = new AiConversationTurnItem
        {
            UserPrompt = prompt,
            MaxSteps = draft.EffectiveMaxToolSteps,
            StatusBadgeText = "正在分析…",
            StatusBadgeFg = "#0078D4"
        };
        ConversationTurns.Add(turnItem);

        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        _analysisCts = new CancellationTokenSource();

        var timeoutController = new AiOverallTimeoutController();
        _activeTimeoutController = timeoutController;
        _activeAnalysisTurn = turnItem;
        HideTimeoutExtensionPrompt();

        _baselineSessionInputTokens = SessionTotalInputTokens;
        _baselineSessionOutputTokens = SessionTotalOutputTokens;
        _baselineSessionRequests = SessionRequestCount;
        _baselineSessionToolCalls = SessionToolCallCount;

        var progress = new InlineProgress<AiAgentRunEvent>(ev => HandleAgentRunEvent(turnItem, ev));

        try
        {
            Guid[] authorizedIds = _host.Toolset.GetAuthorizedSnapshots().Select(s => s.ScanId).ToArray();
            AiAgentAnalysisResult result = await _host.AgentService.RunAnalysisAsync(
                draft,
                plainKey,
                new AiAgentAnalysisRequest(
                    ActiveScanId: _host.ActiveScanId,
                    UserPrompt: prompt,
                    History: history,
                    ActiveCleanupScanId: _host.ActiveCleanupScanId,
                    Scene: Scene,
                    AuthorizedScanIds: authorizedIds,
                    OverallTimeoutController: timeoutController),
                progress,
                _analysisCts.Token);

            _host.DispatchToUi(() =>
            {
                if (result.Telemetry is not null)
                {
                    ApplyTelemetryUpdate(result.Telemetry);
                }

                FinalizeActiveTimelineSteps(turnItem, result.WasCancelled ? "已停止" : "已中断");
                turnItem.StepsUsed = result.StepsUsed;
                if (!string.IsNullOrWhiteSpace(result.AssistantMessage))
                {
                    turnItem.AssistantText = result.AssistantMessage;
                    SyncFinalAssistantMessageToTimeline(turnItem, result.AssistantMessage);
                }

                // 兜底补齐：确保工具返回的所有迁移与清理建议均已写入本轮摘要卡（按 ProposalId 去重，避免重复追加）
                if (result.Proposals.Length > 0)
                {
                    var existingIds = turnItem.ProposalCards.Select(c => c.ProposalId).ToHashSet();
                    foreach (AiMigrationProposalReceipt prop in result.Proposals)
                    {
                        if (!existingIds.Contains(prop.ProposalId))
                        {
                            _host.AppendProposalCards(turnItem, prop);
                            existingIds.Add(prop.ProposalId);
                        }
                    }
                }
                if (result.CleanupProposals is { Length: > 0 })
                {
                    var existingIds = turnItem.ProposalCards.Select(c => c.ProposalId).ToHashSet();
                    foreach (AiCleanupProposalReceipt cprop in result.CleanupProposals)
                    {
                        if (!existingIds.Contains(cprop.ProposalId))
                        {
                            _host.AppendCleanupProposalCards(turnItem, cprop);
                            existingIds.Add(cprop.ProposalId);
                        }
                    }
                }
                NotifySessionProposalSummaryChanged();

                if (result.WasCancelled)
                {
                    turnItem.StatusBadgeText = "已手动停止";
                    turnItem.StatusBadgeFg = "#B45309";
                    turnItem.ErrorText = result.ErrorMessage ?? "分析已停止。";
                }
                else if (!result.Succeeded)
                {
                    turnItem.StatusBadgeText = "分析失败";
                    turnItem.StatusBadgeFg = "#C42B1C";
                    turnItem.ErrorText = result.ErrorMessage ?? "模型调用失败。";
                }
                else if (turnItem.HasScanActionCards && string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    turnItem.StatusBadgeText = $"等待确认扫描 · {result.ElapsedMilliseconds / 1000.0:F1}s";
                    turnItem.StatusBadgeFg = "#0078D4";
                }
                else
                {
                    turnItem.StatusBadgeText = result.HitStepLimit
                        ? $"已达上限 · {result.ElapsedMilliseconds / 1000.0:F1}s"
                        : $"已完成 · {result.ElapsedMilliseconds / 1000.0:F1}s";
                    turnItem.StatusBadgeFg = result.HitStepLimit ? "#B45309" : "#0F7B4B";
                }
            });
        }
        finally
        {
            _host.DispatchToUi(() =>
            {
                HideTimeoutExtensionPrompt();
                _activeTimeoutController = null;
                _activeAnalysisTurn = null;
                IsTelemetryStreaming = false;
                IsAnalyzing = false;
                _host.NotifySessionActivityChanged(this);
            });
        }
    }

    [RelayCommand]
    private void ExtendAnalysisTimeout(string? minutesParam)
    {
        if (!int.TryParse(minutesParam?.Trim(), out int minutes) || minutes <= 0)
        {
            minutes = 10;
        }

        AiOverallTimeoutController? controller = _activeTimeoutController;
        if (controller is null)
        {
            HideTimeoutExtensionPrompt();
            return;
        }

        if (controller.TryExtend(TimeSpan.FromMinutes(minutes), out _, out TimeSpan newBudget))
        {
            HideTimeoutExtensionPrompt();
            string budgetLabel = newBudget.TotalMinutes >= 1
                ? $"{Math.Round(newBudget.TotalMinutes):0} 分钟"
                : $"{Math.Round(newBudget.TotalSeconds):0} 秒";
            _activeAnalysisTurn?.TimelineItems.Add(new AiNoticeStepItem
            {
                Prefix = "⏱",
                Message = $"已将本次分析总耗时上限延长 +{minutes} 分钟（当前上限约 {budgetLabel}），继续分析中…",
                Foreground = "#0078D4"
            });
        }
        else
        {
            HideTimeoutExtensionPrompt();
        }
    }

    [RelayCommand]
    private void DismissTimeoutExtensionPrompt()
    {
        HideTimeoutExtensionPrompt();
    }

    internal void ShowTimeoutApproachingPrompt(AiTimeoutApproachingInfo info, AiOverallTimeoutController? overrideController = null, AiConversationTurnItem? overrideTurn = null)
    {
        if (overrideController is not null) _activeTimeoutController = overrideController;
        if (overrideTurn is not null) _activeAnalysisTurn = overrideTurn;

        _timeoutWarningDeadlineUtc = info.DeadlineUtc;
        string budgetLabel = info.CurrentTotalBudget.TotalMinutes >= 1
            ? $"{Math.Round(info.CurrentTotalBudget.TotalMinutes):0} 分钟"
            : $"{Math.Round(info.CurrentTotalBudget.TotalSeconds):0} 秒";

        TimeoutPromptDescription = $"当前单次分析限时 {budgetLabel} 即将到达，到期将自动收敛或安全中止。是否需要延长本次限时？";
        UpdateTimeoutCountdownBadge();
        ShowTimeoutExtensionPrompt = true;

        if (Application.Current?.Dispatcher is not null)
        {
            _timeoutCountdownTimer ??= new DispatcherTimer(DispatcherPriority.Normal, Application.Current.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timeoutCountdownTimer.Tick -= OnTimeoutCountdownTick;
            _timeoutCountdownTimer.Tick += OnTimeoutCountdownTick;
            _timeoutCountdownTimer.Start();
        }
    }

    private void HideTimeoutExtensionPrompt()
    {
        ShowTimeoutExtensionPrompt = false;
        TimeoutCountdownBadgeText = string.Empty;
        TimeoutPromptDescription = string.Empty;
        if (_timeoutCountdownTimer is not null)
        {
            _timeoutCountdownTimer.Stop();
            _timeoutCountdownTimer.Tick -= OnTimeoutCountdownTick;
        }
    }

    private void OnTimeoutCountdownTick(object? sender, EventArgs e)
    {
        if (!ShowTimeoutExtensionPrompt)
        {
            _timeoutCountdownTimer?.Stop();
            return;
        }
        UpdateTimeoutCountdownBadge();
    }

    private void UpdateTimeoutCountdownBadge()
    {
        TimeSpan rem = _timeoutWarningDeadlineUtc - DateTimeOffset.UtcNow;
        if (rem <= TimeSpan.Zero)
        {
            TimeoutCountdownBadgeText = "即将到期";
            return;
        }

        int totalSeconds = (int)Math.Ceiling(rem.TotalSeconds);
        int mins = totalSeconds / 60;
        int secs = totalSeconds % 60;
        TimeoutCountdownBadgeText = $"剩余 {mins:00}:{secs:00}";
    }

    [RelayCommand]
    private void StopAnalysis()
    {
        _analysisCts?.Cancel();
    }

    [RelayCommand]
    private void ClearConversation()
    {
        if (IsAnalyzing) return;
        foreach (AiConversationTurnItem turn in ConversationTurns)
        {
            turn.PropertyChanged -= OnTurnPropertyChanged;
            foreach (AiScanActionCardItem card in turn.ScanActionCards)
            {
                card.ActiveScanCts?.Cancel();
                card.ActiveScanCts?.Dispose();
                card.ActiveScanCts = null;
            }
        }
        ConversationTurns.Clear();
        HideTimeoutExtensionPrompt();
        NotifySessionProposalSummaryChanged();
        ResetTelemetry();
    }

    private void ResetTelemetry()
    {
        ContextUsedTokens = 0;
        ContextUsageIsExact = false;
        HasTelemetry = false;
        SessionTotalInputTokens = 0;
        SessionTotalOutputTokens = 0;
        SessionRequestCount = 0;
        SessionToolCallCount = 0;
        LastTokensPerSecond = 0;
        LastAverageTokensPerSecond = 0;
        IsTelemetryStreaming = false;
        LastFirstTokenMs = 0;
        LastElapsedMs = 0;
        _baselineSessionInputTokens = 0;
        _baselineSessionOutputTokens = 0;
        _baselineSessionRequests = 0;
        _baselineSessionToolCalls = 0;
    }

    private void ApplyTelemetryUpdate(AiCallTelemetry telemetry)
    {
        HasTelemetry = true;
        ContextUsedTokens = telemetry.LastTotalTokens;
        if (telemetry.EstimatedContextWindowTokens > 0)
        {
            ContextWindowTokens = telemetry.EstimatedContextWindowTokens;
        }
        OnPropertyChanged(nameof(ContextWindowSourceText));
        ContextUsageIsExact = telemetry.IsExactUsageFromApi;
        SessionTotalInputTokens = _baselineSessionInputTokens + telemetry.TotalInputTokensThisRun;
        SessionTotalOutputTokens = _baselineSessionOutputTokens + telemetry.TotalOutputTokensThisRun;
        SessionRequestCount = _baselineSessionRequests + telemetry.ModelRequestCount;
        SessionToolCallCount = _baselineSessionToolCalls + telemetry.ToolCallCount;
        IsTelemetryStreaming = telemetry.IsStreaming;
        LastAverageTokensPerSecond = telemetry.AverageTokensPerSecond;
        LastTokensPerSecond = telemetry.TokensPerSecond;
        LastFirstTokenMs = telemetry.FirstTokenMilliseconds;
        LastElapsedMs = telemetry.ElapsedMilliseconds;
    }

    public static string BuildContextRingArcGeometry(double percent)
    {
        double clamped = Math.Clamp(percent, 0, 100);
        if (clamped <= 0.1) return string.Empty;

        const double cx = 9.0;
        const double cy = 9.0;
        const double r = 6.8;

        // 接近 100% 时用两段半圆闭合避免 SVG 起点=终点退化
        if (clamped >= 99.5)
        {
            return FormattableString.Invariant(
                $"M {cx:0.##},{cy - r:0.##} A {r:0.##},{r:0.##} 0 1 1 {cx:0.##},{cy + r:0.##} A {r:0.##},{r:0.##} 0 1 1 {cx:0.##},{cy - r:0.##}");
        }

        double angleRad = (clamped / 100.0 * 360.0 - 90.0) * Math.PI / 180.0;
        double endX = cx + r * Math.Cos(angleRad);
        double endY = cy + r * Math.Sin(angleRad);
        int largeArc = clamped > 50.0 ? 1 : 0;

        return FormattableString.Invariant(
            $"M {cx:0.##},{cy - r:0.##} A {r:0.##},{r:0.##} 0 {largeArc} 1 {endX:0.##},{endY:0.##}");
    }

    internal static string FormatTokenCount(int tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F2}M";
        if (tokens >= 10_000) return $"{tokens / 1_000.0:F1}K";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F2}K";
        return tokens.ToString("N0");
    }

    [RelayCommand]
    private async Task StartScanCardAsync(AiScanActionCardItem? card)
    {
        if (card is null || !card.CanStartScan) return;

        AiScanDriveOptionItem[] selectedDrives = card.AllDrives.Where(d => d.IsChecked).ToArray();
        bool runCleanup = card.ShowCleanupOption && card.IncludeCleanupScan;

        // 仅复用已有新鲜快照（未勾新盘也未勾清理）
        if (!runCleanup && selectedDrives.Length == 0)
        {
            bool reusedDisk = card.ShowDrivePicker && _host.AuthorizeFreshCachedSnapshots();
            bool reusedCleanup = card.ShowCleanupOption && _host.HasAuthorizedCleanupSnapshot;
            if (reusedDisk || reusedCleanup)
            {
                card.IsCompleted = true;
                card.StatusMessage = reusedDisk && reusedCleanup
                    ? "✓ 已复用最新磁盘与垃圾清理扫描快照。"
                    : reusedCleanup
                        ? "✓ 已复用最新垃圾清理扫描快照。"
                        : "✓ 已复用内存中已有的新鲜磁盘扫描快照。";
                if (card.AutoContinueAfterScan)
                {
                    await ContinueAfterScanCardAsync(card);
                }
            }
            else
            {
                card.ErrorMessage = "未找到可复用的新鲜扫描快照，请至少勾选 1 块磁盘或垃圾清理扫描。";
            }
            return;
        }

        card.ErrorMessage = string.Empty;
        card.IsCancelled = false;
        card.IsScanning = true;
        card.ProgressIsIndeterminate = true;
        card.ProgressPercent = 0;

        card.ActiveScanCts?.Cancel();
        card.ActiveScanCts?.Dispose();
        card.ActiveScanCts = new CancellationTokenSource();
        CancellationToken ct = card.ActiveScanCts.Token;

        int totalSteps = (runCleanup ? 1 : 0) + selectedDrives.Length;
        int currentStep = 0;
        var completedSummaries = new List<string>();

        try
        {
            if (runCleanup)
            {
                currentStep++;
                card.StatusMessage = $"({currentStep}/{totalSteps}) 正在执行垃圾清理规则扫描…";
                card.ProgressIsIndeterminate = false;
                card.ProgressPercent = 2;

                CleanupScanSnapshot? cleanupSnap = await _host.ExecuteCleanupScanForCardAsync(
                    new Progress<(double Percent, string Message)>(p => _host.DispatchToUi(() =>
                    {
                        card.ProgressIsIndeterminate = false;
                        card.ProgressPercent = Math.Clamp(p.Percent, 2, 100);
                        card.StatusMessage = $"({currentStep}/{totalSteps}) {p.Message}";
                    })),
                    ct);

                if (cleanupSnap is not null)
                {
                    completedSummaries.Add($"垃圾清理（{FileStatsService.FormatBytes(cleanupSnap.TotalReclaimableBytes)}）");
                }
            }

            foreach (AiScanDriveOptionItem drive in selectedDrives)
            {
                ct.ThrowIfCancellationRequested();
                currentStep++;
                card.StatusMessage = $"({currentStep}/{totalSteps}) 正在扫描 {drive.DriveLetter}…";
                card.ProgressIsIndeterminate = false;
                card.ProgressPercent = 5;

                ScanSnapshot? diskSnap = await _host.ExecuteDiskScanForCardAsync(
                    drive.DriveRoot,
                    new Progress<(double Percent, string Message)>(p => _host.DispatchToUi(() =>
                    {
                        card.ProgressIsIndeterminate = false;
                        card.ProgressPercent = Math.Clamp(p.Percent, 0, 100);
                        card.StatusMessage = $"({currentStep}/{totalSteps}) {drive.DriveLetter} · {p.Message}";
                    })),
                    ct);

                if (diskSnap is not null)
                {
                    double sec = Math.Max(0.1, (diskSnap.CompletedAtUtc - diskSnap.StartedAtUtc).TotalSeconds);
                    completedSummaries.Add($"{drive.DriveLetter}（{sec:F1}s）");
                }
            }

            card.ProgressPercent = 100;
            card.ProgressIsIndeterminate = false;
            card.IsScanning = false;
            card.IsCompleted = true;
            card.StatusMessage = completedSummaries.Count > 0
                ? $"✓ 已完成扫描：{string.Join("、", completedSummaries)}"
                : "✓ 扫描已完成";

            if (card.AutoContinueAfterScan && !string.IsNullOrWhiteSpace(card.OriginalUserPrompt))
            {
                await ContinueAfterScanCardAsync(card);
            }
        }
        catch (OperationCanceledException)
        {
            card.IsScanning = false;
            card.IsCancelled = true;
            card.StatusMessage = "已取消本次扫描，可重新勾选后再开始。";
        }
        catch (Exception ex)
        {
            card.IsScanning = false;
            card.ErrorMessage = $"扫描未能完成：{ex.Message}";
        }
        finally
        {
            card.ActiveScanCts?.Dispose();
            card.ActiveScanCts = null;
        }
    }

    [RelayCommand]
    private void CancelScanCard(AiScanActionCardItem? card)
    {
        if (card is null) return;
        if (card.IsScanning)
        {
            card.ActiveScanCts?.Cancel();
            return;
        }
        if (card.IsExternalScanRunning)
        {
            _host.CancelExternalScanForCard(card);
        }
    }

    internal void SyncExternalCleanupScanState(bool isScanning, double percent, string message)
    {
        foreach (AiConversationTurnItem turn in ConversationTurns)
        {
            foreach (AiScanActionCardItem card in turn.ScanActionCards)
            {
                card.ApplyExternalCleanupScanState(isScanning, percent, message);
            }
        }
    }

    internal void SyncExternalDiskScanState(string driveRoot, bool isScanning, double percent, string message)
    {
        foreach (AiConversationTurnItem turn in ConversationTurns)
        {
            foreach (AiScanActionCardItem card in turn.ScanActionCards)
            {
                card.ApplyExternalDiskScanState(driveRoot, isScanning, percent, message);
            }
        }
    }

    internal void SyncExternalCleanupSnapshot(CleanupScanSnapshot snapshot)
    {
        var ruleById = new Dictionary<string, CleanupRuleScanResult>(StringComparer.OrdinalIgnoreCase);
        foreach (CleanupRuleScanResult r in snapshot.Rules)
        {
            ruleById[r.Rule.RuleId] = r;
        }

        foreach (AiConversationTurnItem turn in ConversationTurns)
        {
            foreach (AiScanActionCardItem card in turn.ScanActionCards)
            {
                card.ApplyExternalCleanupSnapshot(snapshot);
            }

            foreach (AiProposalCardItem card in turn.ProposalCards)
            {
                if (!card.IsCleanupRuleAdvice || card.IsBlockedByPolicy || string.IsNullOrWhiteSpace(card.RuleId))
                {
                    continue;
                }

                if (!ruleById.TryGetValue(card.RuleId, out CleanupRuleScanResult? liveRule))
                {
                    continue;
                }

                bool hasRemaining = liveRule.IsAvailable || liveRule.TotalBytes > 0 || liveRule.MatchedCount > 0;
                if (!hasRemaining)
                {
                    card.IsCleanupRuleSelected = false;
                    if (!card.IsCleanupRuleCleaned)
                    {
                        card.CleanupRuleCleanedStatusText = "✓ 已清理";
                    }
                    card.IsCleanupRuleCleaned = true;
                }
                else if (liveRule.TotalBytes > 0 && card.IsCleanupRuleCleaned)
                {
                    card.IsCleanupRuleCleaned = false;
                }
            }
        }
    }

    internal void SyncExternalCleanupExecution(CleanupScanSnapshot updatedSnapshot, CleanupExecutionResult executionResult)
    {
        var postRules = new Dictionary<string, CleanupRuleScanResult>(StringComparer.OrdinalIgnoreCase);
        foreach (CleanupRuleScanResult r in updatedSnapshot.Rules)
        {
            postRules[r.Rule.RuleId] = r;
        }

        var execByRuleId = new Dictionary<string, CleanupExecutionItemResult>(StringComparer.OrdinalIgnoreCase);
        foreach (CleanupExecutionItemResult item in executionResult.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.TargetId))
            {
                execByRuleId[item.TargetId] = item;
            }
        }

        foreach (AiConversationTurnItem turn in ConversationTurns)
        {
            foreach (AiProposalCardItem card in turn.ProposalCards)
            {
                if (!card.IsCleanupRuleAdvice || card.IsBlockedByPolicy || string.IsNullOrWhiteSpace(card.RuleId))
                {
                    continue;
                }

                if (!execByRuleId.TryGetValue(card.RuleId, out CleanupExecutionItemResult? execItem))
                {
                    continue;
                }

                bool executed = execItem.Status is CleanupItemExecutionStatus.Quarantined
                    or CleanupItemExecutionStatus.Recycled
                    or CleanupItemExecutionStatus.ReclaimedByOfficialApi
                    or CleanupItemExecutionStatus.PermanentlyDeleted;
                if (!executed)
                {
                    continue;
                }

                card.IsCleanupRuleSelected = false;

                postRules.TryGetValue(card.RuleId, out CleanupRuleScanResult? postRule);
                bool postHasRemaining = postRule is not null
                    && (postRule.IsAvailable || postRule.TotalBytes > 0 || postRule.MatchedCount > 0);
                bool officialZeroSizeReclaimed = execItem.Status == CleanupItemExecutionStatus.ReclaimedByOfficialApi
                    && (postRule is null || (postRule.Rule.Channel == CleanupExecutionChannel.OfficialSystemApi && postRule.TotalBytes == 0));

                if (!postHasRemaining || officialZeroSizeReclaimed)
                {
                    card.CleanupRuleCleanedStatusText = execItem.Status switch
                    {
                        CleanupItemExecutionStatus.Quarantined => "✓ 已同卷隔离",
                        CleanupItemExecutionStatus.Recycled => "✓ 已移入回收站",
                        CleanupItemExecutionStatus.PermanentlyDeleted => "✓ 已直接删除释放",
                        _ => "✓ 已清理"
                    };
                    card.IsCleanupRuleCleaned = true;
                }
            }
        }
    }

    internal void SyncExternalDiskSnapshot(ScanSnapshot snapshot)
    {
        foreach (AiConversationTurnItem turn in ConversationTurns)
        {
            foreach (AiScanActionCardItem card in turn.ScanActionCards)
            {
                card.ApplyExternalDiskSnapshot(snapshot);
            }
        }
    }

    [RelayCommand]
    private Task ContinueAfterScanCardAsync(AiScanActionCardItem? card)
    {
        if (card is null || IsAnalyzing) return Task.CompletedTask;
        string resumePrompt = string.IsNullOrWhiteSpace(card.OriginalUserPrompt)
            ? "我已完成所需扫描，请基于最新扫描快照继续分析并给出建议。"
            : $"我已完成所需扫描，请基于最新扫描快照继续回答刚才的问题：{card.OriginalUserPrompt}";
        return ExecutePromptInternalAsync(resumePrompt, clearInputBox: false);
    }

    public void SyncAdoptedPlanItems(Guid? planScanId, IReadOnlyCollection<int> planNodeIds, IReadOnlyCollection<string> planSourcePaths)
    {
        foreach (AiConversationTurnItem turn in ConversationTurns)
        {
            foreach (AiProposalCardItem card in turn.ProposalCards)
            {
                if (!card.IsAdoptableToMigrationPlan || card.IsInvalidated) continue;
                card.IsAdopted = _host.IsCardCurrentlyInPlan(card.ScanId, card.NodeId, card.ActualLocalPath);
            }
        }
    }

    public void SyncProposalInvalidations()
    {
        foreach (AiConversationTurnItem turn in ConversationTurns)
        {
            foreach (AiProposalCardItem card in turn.ProposalCards)
            {
                if (card.IsCleanupResidualProposal)
                {
                    AiCleanupCandidateStatus? residualStatus = _host.Toolset.Inbox.GetCleanupResidualCandidateStatus(
                        card.CandidateId, card.ScanId, card.NodeId);
                    if (residualStatus is AiCleanupCandidateStatus st)
                    {
                        switch (st)
                        {
                            case AiCleanupCandidateStatus.ProposedAwaitingAdoption:
                                card.IsResidualAdopted = false;
                                card.IsResidualQuarantined = false;
                                break;
                            case AiCleanupCandidateStatus.PendingManualReview:
                                card.IsResidualAdopted = true;
                                card.IsResidualQuarantined = false;
                                break;
                            case AiCleanupCandidateStatus.Quarantined:
                                card.IsResidualAdopted = true;
                                card.IsResidualQuarantined = true;
                                break;
                            case AiCleanupCandidateStatus.Invalidated:
                                card.IsInvalidated = true;
                                break;
                        }
                    }
                    continue;
                }

                AiProposalStatusResponse? status = _host.Toolset.Inbox.GetStatus(card.ProposalId);
                if (status is not null && status.Status == AiProposalStatus.Invalidated)
                {
                    card.IsInvalidated = true;
                }
            }
        }
    }

    private void HandleAgentRunEvent(AiConversationTurnItem turn, AiAgentRunEvent ev)
    {
        _host.DispatchToUi(() =>
        {
            if (ev.CurrentStep > 0)
            {
                turn.StepsUsed = ev.CurrentStep;
            }

            switch (ev.Kind)
            {
                case AiAgentEventKind.ThinkingDelta when !string.IsNullOrEmpty(ev.TextDelta):
                    if (turn.TimelineItems.Count > 0
                        && turn.TimelineItems[^1] is AiThinkingStepItem activeThinking
                        && activeThinking.IsStreaming)
                    {
                        activeThinking.Text += ev.TextDelta;
                    }
                    else
                    {
                        CloseActiveStreamingSteps(turn);
                        turn.TimelineItems.Add(new AiThinkingStepItem
                        {
                            Text = ev.TextDelta,
                            IsStreaming = true
                        });
                        if (!string.IsNullOrEmpty(turn.ThinkingText))
                        {
                            turn.ThinkingText += "\n\n";
                        }
                    }

                    turn.ThinkingText += ev.TextDelta;
                    break;

                case AiAgentEventKind.AssistantTextDelta when !string.IsNullOrEmpty(ev.TextDelta):
                    if (turn.TimelineItems.Count > 0
                        && turn.TimelineItems[^1] is AiAssistantMessageStepItem activeMessage
                        && activeMessage.IsStreaming)
                    {
                        activeMessage.Text += ev.TextDelta;
                    }
                    else if (!string.IsNullOrWhiteSpace(ev.TextDelta))
                    {
                        CloseActiveStreamingSteps(turn);
                        turn.TimelineItems.Add(new AiAssistantMessageStepItem
                        {
                            Text = ev.TextDelta.TrimStart('\r', '\n'),
                            IsStreaming = true
                        });
                    }

                    turn.AssistantText += ev.TextDelta;
                    break;

                case AiAgentEventKind.ToolCallStarted when ev.TraceEntry is not null:
                    CloseActiveStreamingSteps(turn);
                    turn.HasToolActivity = true;
                    turn.StatusBadgeText = ev.MaxSteps > 0
                        ? $"调用中 · {ev.CurrentStep}/{ev.MaxSteps}"
                        : $"调用中 · 第 {ev.CurrentStep} 步";
                    turn.StatusBadgeFg = "#0078D4";
                    AiToolBatchStepItem startBatch = GetOrCreateTrailingToolBatch(turn);
                    startBatch.AddItem(new AiToolCallStepItem
                    {
                        StepNumber = ev.TraceEntry.StepNumber,
                        ToolName = ev.TraceEntry.ToolName,
                        FriendlyLabel = AiToolCallStepItem.ResolveFriendlyLabel(ev.TraceEntry.ToolName),
                        ArgumentsSummary = ev.TraceEntry.ArgumentsSummary ?? string.Empty,
                        ResultSummary = "执行中…",
                        IsRunning = true,
                        Succeeded = true,
                        IsExpanded = false
                    });
                    break;

                case AiAgentEventKind.ToolCallCompleted when ev.TraceEntry is not null:
                    CloseActiveStreamingSteps(turn);
                    turn.HasToolActivity = true;
                    string argDisplay = string.IsNullOrWhiteSpace(ev.TraceEntry.ArgumentsSummary)
                        ? string.Empty
                        : ev.TraceEntry.ArgumentsSummary;
                    turn.Traces.Add(new AiTraceRowItem
                    {
                        Prefix = ev.TraceEntry.Succeeded ? "✓" : "✕",
                        Text = $"{ev.TraceEntry.ToolName}({argDisplay}) → {ev.TraceEntry.ResultSummary}",
                        Foreground = ev.TraceEntry.Succeeded ? "#64748B" : "#C42B1C"
                    });

                    (AiToolBatchStepItem? ownerBatch, AiToolCallStepItem? runningStep) =
                        FindMatchingRunningToolStep(turn, ev.TraceEntry.StepNumber, ev.TraceEntry.ToolName);
                    if (runningStep is not null)
                    {
                        runningStep.IsRunning = false;
                        runningStep.Succeeded = ev.TraceEntry.Succeeded;
                        runningStep.ResultSummary = ev.TraceEntry.ResultSummary;
                        runningStep.ResultPayload = ev.TraceEntry.ResultPayload ?? string.Empty;
                        runningStep.ElapsedMilliseconds = ev.TraceEntry.ElapsedMilliseconds;
                        ownerBatch?.RefreshSummary();
                    }
                    else
                    {
                        AiToolBatchStepItem batch = GetOrCreateTrailingToolBatch(turn);
                        batch.AddItem(new AiToolCallStepItem
                        {
                            StepNumber = ev.TraceEntry.StepNumber,
                            ToolName = ev.TraceEntry.ToolName,
                            FriendlyLabel = AiToolCallStepItem.ResolveFriendlyLabel(ev.TraceEntry.ToolName),
                            ArgumentsSummary = argDisplay,
                            ResultSummary = ev.TraceEntry.ResultSummary,
                            ResultPayload = ev.TraceEntry.ResultPayload ?? string.Empty,
                            ElapsedMilliseconds = ev.TraceEntry.ElapsedMilliseconds,
                            IsRunning = false,
                            Succeeded = ev.TraceEntry.Succeeded,
                            IsExpanded = false
                        });
                    }
                    break;

                case AiAgentEventKind.ProposalRecorded when ev.ProposalReceipt is not null:
                    _host.AppendProposalCards(turn, ev.ProposalReceipt);
                    break;

                case AiAgentEventKind.ProposalRecorded when ev.CleanupProposalReceipt is not null:
                    _host.AppendCleanupProposalCards(turn, ev.CleanupProposalReceipt);
                    break;

                case AiAgentEventKind.ScanActionRequested when ev.ScanActionProposal is not null:
                    turn.ScanActionCards.Add(_host.CreateScanActionCard(ev.ScanActionProposal, turn.UserPrompt));
                    break;

                case AiAgentEventKind.TelemetryUpdated when ev.Telemetry is not null:
                    ApplyTelemetryUpdate(ev.Telemetry);
                    break;

                case AiAgentEventKind.TimeoutApproaching when ev.TimeoutApproaching is not null:
                    ShowTimeoutApproachingPrompt(ev.TimeoutApproaching);
                    break;

                case AiAgentEventKind.Warning when !string.IsNullOrWhiteSpace(ev.Message):
                    if (turn.TimelineItems.Count > 0 && turn.TimelineItems[^1] is AiThinkingStepItem pollutedThinking)
                    {
                        pollutedThinking.Text = AiRepetitionLoopDetector.TrimTrailingRepetition(pollutedThinking.Text);
                        pollutedThinking.IsStreaming = false;
                    }
                    else if (turn.TimelineItems.Count > 0 && turn.TimelineItems[^1] is AiAssistantMessageStepItem pollutedMsg && pollutedMsg.IsStreaming)
                    {
                        turn.TimelineItems.RemoveAt(turn.TimelineItems.Count - 1);
                    }
                    turn.HasToolActivity = true;
                    turn.StatusBadgeText = "检测到复读 · 重试中…";
                    turn.StatusBadgeFg = "#B45309";
                    turn.AssistantText = string.Empty;
                    turn.ThinkingText = AiRepetitionLoopDetector.TrimTrailingRepetition(turn.ThinkingText);
                    turn.Traces.Add(new AiTraceRowItem
                    {
                        Prefix = "⚠",
                        Text = ev.Message,
                        Foreground = "#B45309"
                    });
                    turn.TimelineItems.Add(new AiNoticeStepItem
                    {
                        Prefix = "⚠",
                        Message = ev.Message,
                        Foreground = "#B45309"
                    });
                    break;
            }
        });
    }

    private static void CloseActiveStreamingSteps(AiConversationTurnItem turn)
    {
        if (turn.TimelineItems.Count == 0) return;

        if (turn.TimelineItems[^1] is AiThinkingStepItem thinking && thinking.IsStreaming)
        {
            thinking.IsStreaming = false;
        }
        else if (turn.TimelineItems[^1] is AiAssistantMessageStepItem msg && msg.IsStreaming)
        {
            msg.IsStreaming = false;
        }
    }

    private static void SyncFinalAssistantMessageToTimeline(AiConversationTurnItem turn, string cleanedFinalMessage)
    {
        if (turn.TimelineItems.Count > 0 && turn.TimelineItems[^1] is AiAssistantMessageStepItem lastMsg)
        {
            lastMsg.Text = cleanedFinalMessage;
            lastMsg.IsStreaming = false;
            return;
        }

        turn.TimelineItems.Add(new AiAssistantMessageStepItem
        {
            Text = cleanedFinalMessage,
            IsStreaming = false
        });
    }

    private static AiToolBatchStepItem GetOrCreateTrailingToolBatch(AiConversationTurnItem turn)
    {
        if (turn.TimelineItems.Count > 0 && turn.TimelineItems[^1] is AiToolBatchStepItem existing)
        {
            return existing;
        }

        var batch = new AiToolBatchStepItem();
        turn.TimelineItems.Add(batch);
        return batch;
    }

    private static (AiToolBatchStepItem? Batch, AiToolCallStepItem? Step) FindMatchingRunningToolStep(
        AiConversationTurnItem turn, int stepNumber, string toolName)
    {
        for (int i = turn.TimelineItems.Count - 1; i >= 0; i--)
        {
            if (turn.TimelineItems[i] is AiToolBatchStepItem batch)
            {
                for (int j = batch.Items.Count - 1; j >= 0; j--)
                {
                    AiToolCallStepItem step = batch.Items[j];
                    if (step.IsRunning
                        && (step.StepNumber == stepNumber || string.Equals(step.ToolName, toolName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return (batch, step);
                    }
                }
            }
        }
        return (null, null);
    }

    private static void FinalizeActiveTimelineSteps(AiConversationTurnItem turn, string interruptedReason)
    {
        foreach (AiTimelineStepItem item in turn.TimelineItems)
        {
            if (item is AiThinkingStepItem thinking && thinking.IsStreaming)
            {
                thinking.IsStreaming = false;
            }
            else if (item is AiAssistantMessageStepItem msg && msg.IsStreaming)
            {
                msg.IsStreaming = false;
            }
            else if (item is AiToolBatchStepItem batch)
            {
                bool changed = false;
                foreach (AiToolCallStepItem tool in batch.Items)
                {
                    if (tool.IsRunning)
                    {
                        tool.IsRunning = false;
                        tool.Succeeded = false;
                        tool.ResultSummary = interruptedReason;
                        changed = true;
                    }
                }
                if (changed)
                {
                    batch.RefreshSummary();
                }
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
