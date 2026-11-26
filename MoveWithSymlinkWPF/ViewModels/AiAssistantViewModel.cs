using System.Collections.ObjectModel;
using System.Windows;
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
/// 内置 AI 助手主视图模型：
/// 1. 管理 3 个完全独立、可后台并发运行且切页保活的场景会话（综合诊断 / 磁盘扫描 / 垃圾清理）；
/// 2. 驱动独立 AI 工作台左栏「分析上下文（各盘余量 + 双快照 + 备份/隔离区 + 建议汇总）| 模型配置」智能切换；
/// 3. 复用 SharedWorkspaceStateCache，消除跨页反复枚举磁盘与隔离区的开销。
/// </summary>
public partial class AiAssistantViewModel : ObservableObject
{
    private readonly ReadOnlyAiAnalysisToolset _toolset;
    private readonly AiProviderConfigStore _configStore;
    private readonly BuiltInAiAgentService _agentService;
    private readonly SharedWorkspaceStateCache _stateCache;
    private readonly Func<Guid, int, string?, (bool Added, string Message)>? _adoptToPlanCallback;
    private readonly Func<Guid, int, bool>? _locateNodeInTreeCallback;
    private readonly Action? _openDiskScanCallback;
    private readonly Action? _openAiSettingsCallback;
    private readonly Action? _closeDrawerCallback;
    private readonly Action? _openCleanupResidualCallback;
    private readonly Action? _openCleanupCallback;
    private readonly Action? _openBackupCenterCallback;
    private readonly Action? _openQuarantineCallback;
    private readonly Func<string, IProgress<(double Percent, string Message)>, CancellationToken, Task<ScanSnapshot?>>? _scanDriveCallback;
    private readonly Func<IProgress<string>, CancellationToken, Task<CleanupScanSnapshot?>>? _scanCleanupCallback;

    private AiProviderSettings _savedSettings;
    private CancellationTokenSource? _connectionTestCts;
    private Guid? _lastSyncedPlanScanId;
    private HashSet<int> _currentPlanNodeIds = [];
    private HashSet<string> _currentPlanSourcePaths = new(StringComparer.OrdinalIgnoreCase);

    public ReadOnlyAiAnalysisToolset Toolset => _toolset;
    internal BuiltInAiAgentService AgentService => _agentService;

    // ---- 3 个完全隔离、可并发运行的独立场景会话 ----
    public AiChatSessionViewModel GeneralSession { get; }
    public AiChatSessionViewModel DiskScanSession { get; }
    public AiChatSessionViewModel CleanupSession { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralSessionActive))]
    [NotifyPropertyChangedFor(nameof(IsDiskScanSessionActive))]
    [NotifyPropertyChangedFor(nameof(IsCleanupSessionActive))]
    [NotifyPropertyChangedFor(nameof(GeneralSessionChipTag))]
    [NotifyPropertyChangedFor(nameof(DiskScanSessionChipTag))]
    [NotifyPropertyChangedFor(nameof(CleanupSessionChipTag))]
    [NotifyPropertyChangedFor(nameof(ConversationTurns))]
    [NotifyPropertyChangedFor(nameof(UserPromptInput))]
    [NotifyPropertyChangedFor(nameof(IsAnalyzing))]
    [NotifyPropertyChangedFor(nameof(CanSendPrompt))]
    [NotifyPropertyChangedFor(nameof(HasConversationTurns))]
    private AiChatSessionViewModel _activeStandaloneSession;

    public bool IsGeneralSessionActive => ReferenceEquals(ActiveStandaloneSession, GeneralSession);
    public bool IsDiskScanSessionActive => ReferenceEquals(ActiveStandaloneSession, DiskScanSession);
    public bool IsCleanupSessionActive => ReferenceEquals(ActiveStandaloneSession, CleanupSession);
    public string GeneralSessionChipTag => IsGeneralSessionActive ? "Active" : string.Empty;
    public string DiskScanSessionChipTag => IsDiskScanSessionActive ? "Active" : string.Empty;
    public string CleanupSessionChipTag => IsCleanupSessionActive ? "Active" : string.Empty;

    // ---- 独立 AI 页左栏标签切换（未配模型默认进配置页，配好默认进全局分析上下文） ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContextTabActive))]
    [NotifyPropertyChangedFor(nameof(ContextTabChipTag))]
    [NotifyPropertyChangedFor(nameof(ConfigTabChipTag))]
    private bool _isConfigTabActive;

    public bool IsContextTabActive => !IsConfigTabActive;
    public string ContextTabChipTag => IsContextTabActive ? "Active" : string.Empty;
    public string ConfigTabChipTag => IsConfigTabActive ? "Active" : string.Empty;

    public ObservableCollection<AiPresetChipItem> PresetChips { get; } = [];
    public ObservableCollection<AiContextHotspotRow> ContextHotspots { get; } = [];
    public ObservableCollection<AiSharedDriveRowItem> SharedDriveRows { get; } = [];
    public ObservableCollection<AiAuthorizedScanRowItem> AuthorizedDiskScans { get; } = [];

    // ---- 当前共享扫描快照上下文（磁盘多盘快照 + 垃圾清理快照 + 备份/隔离区摘要） ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthorizedSnapshot))]
    private Guid? _activeScanId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthorizedCleanupSnapshot))]
    private Guid? _activeCleanupScanId;

    public bool HasAuthorizedSnapshot => ActiveScanId.HasValue;
    public bool HasAuthorizedCleanupSnapshot => ActiveCleanupScanId.HasValue;

    [ObservableProperty]
    private string _snapshotContextTitle = "当前尚未授权磁盘扫描快照";

    [ObservableProperty]
    private string _snapshotContextMeta = "完成任意磁盘扫描后，支持多盘快照共存并自动脱敏共享。";

    [ObservableProperty]
    private string _cleanupSnapshotTitle = "尚未执行垃圾清理扫描";

    [ObservableProperty]
    private string _cleanupSnapshotMeta = "执行垃圾清理扫描后，AI 可解读三档风险与清理顺序。";

    [ObservableProperty]
    private string _backupSummaryTitle = "迁移备份：暂无待确认项";

    [ObservableProperty]
    private string _quarantineSummaryTitle = "清理隔离区：当前为空";

    [ObservableProperty]
    private bool _hasReclaimableBackupOrQuarantine;

    [ObservableProperty]
    private string _proposalInboxSummaryText = "本轮尚未生成结构化建议卡片";

    [ObservableProperty]
    private bool _hasAnyProposalSummary;

    [ObservableProperty]
    private bool _isSnapshotSharingEnabled = true;

    private readonly Dictionary<string, ScanSnapshot> _latestKnownSnapshotsByDrive = new(StringComparer.OrdinalIgnoreCase);

    // ---- BYOK 模型配置表单 ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginBadgeText))]
    [NotifyPropertyChangedFor(nameof(IsLocalEndpoint))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeBg))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeBorder))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeFg))]
    [NotifyPropertyChangedFor(nameof(PrivacyNoticeText))]
    [NotifyPropertyChangedFor(nameof(PrivacyNoticeFg))]
    private AiProviderPreset _selectedPreset = AiProviderPreset.DeepSeek;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginBadgeText))]
    [NotifyPropertyChangedFor(nameof(IsLocalEndpoint))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeBg))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeBorder))]
    [NotifyPropertyChangedFor(nameof(OriginBadgeFg))]
    [NotifyPropertyChangedFor(nameof(PrivacyNoticeText))]
    [NotifyPropertyChangedFor(nameof(PrivacyNoticeFg))]
    private string _endpoint = "https://api.deepseek.com/v1";

    [ObservableProperty]
    private string _modelName = "deepseek-chat";

    [ObservableProperty]
    private int _maxToolSteps = AiProviderSettings.DefaultMaxToolSteps;

    [ObservableProperty]
    private int _timeoutSeconds = AiProviderSettings.DefaultTimeoutSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChineseLanguageChipTag))]
    [NotifyPropertyChangedFor(nameof(EnglishLanguageChipTag))]
    private AiResponseLanguage _responseLanguage = AiResponseLanguage.Chinese;

    public string ChineseLanguageChipTag => ResponseLanguage == AiResponseLanguage.Chinese ? "Active" : string.Empty;
    public string EnglishLanguageChipTag => ResponseLanguage == AiResponseLanguage.English ? "Active" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedApiKey))]
    [NotifyPropertyChangedFor(nameof(ApiKeyStatusHint))]
    private string _maskedApiKeyPreview = string.Empty;

    public bool HasSavedApiKey => !string.IsNullOrWhiteSpace(MaskedApiKeyPreview);
    public bool IsLocalEndpoint => AiProviderSettings.IsLoopbackEndpoint(Endpoint);
    public int EffectiveConfiguredMaxSteps => BuildDraftSettings().EffectiveMaxToolSteps;

    public string OriginBadgeText => BuildDraftSettings().GetOriginBadgeText();
    public string OriginBadgeBg => IsLocalEndpoint ? "#ECFDF5" : "#FFFBEB";
    public string OriginBadgeBorder => IsLocalEndpoint ? "#A7F3D0" : "#FDE68A";
    public string OriginBadgeFg => IsLocalEndpoint ? "#0F7B4B" : "#B45309";

    public string PrivacyNoticeText => IsLocalEndpoint
        ? "本地推理端点：目录结构元数据 100% 留在你本机，不发送至任何外部云端服务。"
        : "云端仅发送脱敏目录元数据，永不读文件内容；需 100% 离线请选 Ollama。";

    public string PrivacyNoticeFg => IsLocalEndpoint ? "#0F7B4B" : "#B45309";

    public string ApiKeyStatusHint => IsLocalEndpoint
        ? "本地模型通常无需 API Key（可留空直接测试连接）"
        : HasSavedApiKey
            ? $"{MaskedApiKeyPreview}（Windows DPAPI 当前用户加密保存，留空则沿用已存密钥）"
            : "输入后将以 Windows DPAPI 当前用户加密保存，绝不明文落盘";

    [ObservableProperty]
    private string _connectionBadgeText = "尚未测试连接";

    [ObservableProperty]
    private string _connectionBadgeBg = "#F8FAFC";

    [ObservableProperty]
    private string _connectionBadgeBorder = "#E2E8F0";

    [ObservableProperty]
    private string _connectionBadgeFg = "#64748B";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigFeedback))]
    private string _configFeedbackMessage = string.Empty;

    public bool HasConfigFeedback => !string.IsNullOrWhiteSpace(ConfigFeedbackMessage);

    [ObservableProperty]
    private bool _isTestingConnection;

    // ---- 向后兼容转发属性与命令（指向当前活动独立会话，保证现有单测与外部调用完全兼容） ----
    public ObservableCollection<AiConversationTurnItem> ConversationTurns => ActiveStandaloneSession.ConversationTurns;

    public string UserPromptInput
    {
        get => ActiveStandaloneSession.UserPromptInput;
        set
        {
            if (ActiveStandaloneSession.UserPromptInput != value)
            {
                ActiveStandaloneSession.UserPromptInput = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsAnalyzing => ActiveStandaloneSession.IsAnalyzing;
    public bool IsAnySessionAnalyzing => GeneralSession.IsAnalyzing || DiskScanSession.IsAnalyzing || CleanupSession.IsAnalyzing;
    public bool CanSendPrompt => ActiveStandaloneSession.CanSendPrompt;
    public bool HasConversationTurns => ActiveStandaloneSession.HasConversationTurns;
    public string DisclaimerText => "AI 建议仅供参考，请用户自行判断，本工具不对 AI 误判负责。";

    public Func<string?>? PendingApiKeyAccessor { get; set; }
    public Action? ClearPendingApiKeyBoxAction { get; set; }

    public AiAssistantViewModel(
        ReadOnlyAiAnalysisToolset? toolset = null,
        AiProviderConfigStore? configStore = null,
        BuiltInAiAgentService? agentService = null,
        Func<Guid, int, string?, (bool Added, string Message)>? adoptToPlanCallback = null,
        Func<Guid, int, bool>? locateNodeInTreeCallback = null,
        Action? openDiskScanCallback = null,
        Action? openAiSettingsCallback = null,
        Action? closeDrawerCallback = null,
        Action? openCleanupResidualCallback = null,
        Action? openCleanupCallback = null,
        Action? openBackupCenterCallback = null,
        Action? openQuarantineCallback = null,
        Func<string, IProgress<(double Percent, string Message)>, CancellationToken, Task<ScanSnapshot?>>? scanDriveCallback = null,
        Func<IProgress<string>, CancellationToken, Task<CleanupScanSnapshot?>>? scanCleanupCallback = null,
        SharedWorkspaceStateCache? stateCache = null)
    {
        _toolset = toolset ?? new ReadOnlyAiAnalysisToolset();
        _configStore = configStore ?? new AiProviderConfigStore();
        _agentService = agentService ?? new BuiltInAiAgentService(_toolset);
        _stateCache = stateCache ?? SharedWorkspaceStateCache.Shared;
        _adoptToPlanCallback = adoptToPlanCallback;
        _locateNodeInTreeCallback = locateNodeInTreeCallback;
        _openDiskScanCallback = openDiskScanCallback;
        _openAiSettingsCallback = openAiSettingsCallback;
        _closeDrawerCallback = closeDrawerCallback;
        _openCleanupResidualCallback = openCleanupResidualCallback;
        _openCleanupCallback = openCleanupCallback;
        _openBackupCenterCallback = openBackupCenterCallback;
        _openQuarantineCallback = openQuarantineCallback;
        _scanDriveCallback = scanDriveCallback;
        _scanCleanupCallback = scanCleanupCallback;

        GeneralSession = new AiChatSessionViewModel(this, AiChatScene.General);
        DiskScanSession = new AiChatSessionViewModel(this, AiChatScene.DiskScan);
        CleanupSession = new AiChatSessionViewModel(this, AiChatScene.Cleanup);
        _activeStandaloneSession = GeneralSession;

        foreach (AiProviderPresetDefinition def in AiProviderSettings.Presets)
        {
            PresetChips.Add(new AiPresetChipItem(def));
        }

        _savedSettings = _configStore.Load();
        ApplySettingsToForm(_savedSettings);

        // 未配置模型时默认落到配置页；已配置好模型时默认落到「分析上下文」驾驶舱
        IsConfigTabActive = !IsSettingsReadyForUse(_savedSettings);

        _toolset.AuthorizedScansChanged += (_, _) => DispatchToUi(RefreshContextSummary);
        _toolset.AuthorizedCleanupScansChanged += (_, _) => DispatchToUi(RefreshCleanupContextSummary);
        _toolset.Inbox.InboxChanged += (_, _) => DispatchToUi(OnInboxStateChanged);
        _toolset.Inbox.CleanupProposalsChanged += (_, _) => DispatchToUi(RefreshProposalInboxSummary);
        _stateCache.StateUpdated += (_, snap) => DispatchToUi(() => ApplySharedStateSnapshot(snap));

        // 零阻塞首帧：若已有共享缓存直接填充，否则后台异步刷新一次
        WorkspaceStateSnapshot? cachedState = _stateCache.GetCachedOrNull();
        if (cachedState is not null)
        {
            ApplySharedStateSnapshot(cachedState);
        }
        else
        {
            _ = OnEnteredAiPageAsync();
        }
        RefreshContextSummary();
        RefreshCleanupContextSummary();
        RefreshProposalInboxSummary();
    }

    private static bool IsSettingsReadyForUse(AiProviderSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint) || string.IsNullOrWhiteSpace(settings.ModelName))
        {
            return false;
        }
        return settings.IsLocalEndpoint || settings.HasSavedApiKey;
    }

    /// <summary>
    /// 进入独立 AI 助手页时触发的轻量刷新（新鲜期内 0ms 复用共享缓存，绝不卡顿 UI）。
    /// </summary>
    public async Task OnEnteredAiPageAsync(bool force = false)
    {
        try
        {
            WorkspaceStateSnapshot snap = await _stateCache.GetOrRefreshAsync(force: force).ConfigureAwait(false);
            DispatchToUi(() => ApplySharedStateSnapshot(snap));
        }
        catch
        {
        }
    }

    [RelayCommand]
    private Task RefreshSharedContextAsync() => OnEnteredAiPageAsync(force: true);

    [RelayCommand]
    private void SelectLeftTab(string? tabKey)
    {
        IsConfigTabActive = string.Equals(tabKey, "Config", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void SelectStandaloneSession(string? sceneKey)
    {
        ActiveStandaloneSession = sceneKey?.Trim() switch
        {
            "DiskScan" => DiskScanSession,
            "Cleanup" => CleanupSession,
            _ => GeneralSession
        };
    }

    internal void NotifySessionActivityChanged(AiChatSessionViewModel session)
    {
        if (ReferenceEquals(session, ActiveStandaloneSession))
        {
            OnPropertyChanged(nameof(IsAnalyzing));
            OnPropertyChanged(nameof(CanSendPrompt));
            OnPropertyChanged(nameof(HasConversationTurns));
            OnPropertyChanged(nameof(UserPromptInput));
        }
        OnPropertyChanged(nameof(IsAnySessionAnalyzing));
    }

    /// <summary>
    /// 接收来自磁盘扫描模块的新快照并同步脱敏上下文（同盘符替换、不同盘符多盘共存）。
    /// </summary>
    public void SyncScanSnapshot(ScanSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _latestKnownSnapshotsByDrive.Clear();
            _toolset.RevokeSnapshot();
            RefreshContextSummary();
            return;
        }

        string driveRoot = VolumeSnapshotCache.NormalizeDriveRoot(snapshot.Volume.DriveRoot);
        _latestKnownSnapshotsByDrive[driveRoot] = snapshot;

        if (IsSnapshotSharingEnabled)
        {
            _toolset.AuthorizeSnapshot(snapshot, replacePreviousSnapshots: false);
        }
        else
        {
            RefreshContextSummary();
        }
    }

    /// <summary>
    /// 同步垃圾清理扫描快照到共享只读 AI 工具底座（三期联合分析）。
    /// </summary>
    public void SyncCleanupScanSnapshot(CleanupScanSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _toolset.RevokeCleanupSnapshot();
            ActiveCleanupScanId = null;
            RefreshCleanupContextSummary();
            return;
        }

        _toolset.AuthorizeCleanupSnapshot(snapshot, replacePrevious: true);
        ActiveCleanupScanId = snapshot.CleanupScanId;
        RefreshCleanupContextSummary();
    }

    [RelayCommand]
    private void ToggleSnapshotSharing()
    {
        if (IsSnapshotSharingEnabled)
        {
            IsSnapshotSharingEnabled = false;
            _toolset.RevokeSnapshot();
            ConfigFeedbackMessage = "已撤销当前会话的磁盘扫描快照共享，旧 AI 建议已全部失效。";
        }
        else
        {
            IsSnapshotSharingEnabled = true;
            foreach (ScanSnapshot snap in _latestKnownSnapshotsByDrive.Values)
            {
                _toolset.AuthorizeSnapshot(snap, replacePreviousSnapshots: false);
            }
            ConfigFeedbackMessage = _latestKnownSnapshotsByDrive.Count > 0
                ? $"已重新授权 {_latestKnownSnapshotsByDrive.Count} 块磁盘的脱敏扫描快照供 AI 只读分析。"
                : "已恢复扫描快照共享授权。";
        }
        RefreshContextSummary();
    }

    [RelayCommand]
    private void RevokeSingleDiskScan(AiAuthorizedScanRowItem? row)
    {
        if (row is null) return;
        _toolset.RevokeSnapshot(row.ScanId);
    }

    // ---- 转发到当前活动独立会话的兼容命令 ----
    [RelayCommand]
    private Task RunQuickPromptAsync(string? quickType) =>
        ActiveStandaloneSession.RunQuickPromptCommand.ExecuteAsync(quickType);

    [RelayCommand]
    private Task SendPromptAsync() =>
        ActiveStandaloneSession.SendPromptCommand.ExecuteAsync(null);

    [RelayCommand]
    private void StopAnalysis()
    {
        GeneralSession.StopAnalysisCommand.Execute(null);
        DiskScanSession.StopAnalysisCommand.Execute(null);
        CleanupSession.StopAnalysisCommand.Execute(null);
    }

    [RelayCommand]
    private void ClearConversation() =>
        ActiveStandaloneSession.ClearConversationCommand.Execute(null);

    [RelayCommand]
    private void AdoptProposalCard(AiProposalCardItem? card)
    {
        if (card is null || !card.CanAdopt) return;

        if (_adoptToPlanCallback is null)
        {
            CustomMessageBox.ShowInformation("当前未连接到磁盘扫描迁移清单，请先在「磁盘扫描」页完成扫描后再加入清单。", "提示");
            return;
        }

        var (added, message) = _adoptToPlanCallback(card.ScanId, card.NodeId, card.SuggestedTargetPath);
        if (added)
        {
            card.IsAdopted = true;
            _toolset.Inbox.UpdateStatus(card.ProposalId, AiProposalStatus.AdoptedToPlan,
                $"用户已将「{card.DisplayName}」加入待迁移清单（未执行迁移，需在清单页预检并手动点击开始）。");
            ConfigFeedbackMessage = message;
        }
        else
        {
            CustomMessageBox.ShowWarning(message, "无法加入待迁移清单");
        }
    }

    [RelayCommand]
    private void LocateProposalInTree(AiProposalCardItem? card)
    {
        if (card is null) return;
        if (_locateNodeInTreeCallback is null || !_locateNodeInTreeCallback(card.ScanId, card.NodeId))
        {
            CustomMessageBox.ShowInformation("未能在当前目录树中定位该节点，可能扫描快照已切换或已释放。", "定位提示");
        }
    }

    [RelayCommand]
    private void OpenCleanupResidualReview(AiProposalCardItem? card)
    {
        if (card is null) return;
        if (_openCleanupResidualCallback is not null)
        {
            _openCleanupResidualCallback();
            return;
        }
        LocateProposalInTree(card);
    }

    [RelayCommand]
    private void OpenDiskScan() => _openDiskScanCallback?.Invoke();

    [RelayCommand]
    private void OpenCleanup() => _openCleanupCallback?.Invoke();

    [RelayCommand]
    private void OpenBackupCenter() => _openBackupCenterCallback?.Invoke();

    [RelayCommand]
    private void OpenQuarantine() => _openQuarantineCallback?.Invoke();

    [RelayCommand]
    private void OpenAiSettings()
    {
        IsConfigTabActive = true;
        _openAiSettingsCallback?.Invoke();
    }

    [RelayCommand]
    private void CloseDrawer() => _closeDrawerCallback?.Invoke();

    [RelayCommand]
    private void InspectToolResult(AiToolCallStepItem? step)
    {
        if (step is null || !step.HasResultPayload) return;

        var dialog = new MoveWithSymlinkWPF.Views.AiToolPayloadWindow(step);
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
        {
            dialog.Owner = owner;
            dialog.Icon = owner.Icon;
        }
        dialog.ShowDialog();
    }

    // ---- 供 3 个独立会话复用的配置验证、扫描卡片构造与卡片执行 ----

    internal bool ValidateDraftConfigForSession(out AiProviderSettings draft, out string? plainKey)
    {
        string? validationError = ValidateDraftConfig(out draft, out plainKey);
        if (validationError is not null)
        {
            CustomMessageBox.ShowWarning($"请先完成 AI 模型配置：\n{validationError}", "模型未配置");
            IsConfigTabActive = true;
            _openAiSettingsCallback?.Invoke();
            return false;
        }
        return true;
    }

    internal bool PrepareExecutionSettings(out AiProviderSettings draft, out string? plainKey)
    {
        if (!ValidateDraftConfigForSession(out draft, out plainKey))
        {
            return false;
        }

        if (!EnsureCloudPrivacyAcknowledged(draft, out bool acknowledgedNow))
        {
            return false;
        }
        if (acknowledgedNow)
        {
            draft = draft with { CloudPrivacyAcknowledged = true };
            _savedSettings = _configStore.Save(draft, plainApiKey: null);
        }

        return true;
    }

    internal string[] GetDefaultRecommendedDrivesForScanCard()
    {
        WorkspaceStateSnapshot state = _stateCache.GetCachedOrNull() ?? _stateCache.GetOrRefreshSync();
        SharedDriveTelemetry[] fixedDrives = state.Drives.Where(d => d.IsFixedDrive).ToArray();
        if (fixedDrives.Length == 0) return ["C:"];

        // 优先推荐系统盘与空间最紧张（≥85%）的 1~2 块盘，避免多硬盘全部默认勾选导致耗时
        var recommended = new List<string>();
        SharedDriveTelemetry? sys = fixedDrives.FirstOrDefault(d => d.IsSystemDrive);
        if (sys is not null)
        {
            recommended.Add(sys.DriveLetter);
        }

        foreach (SharedDriveTelemetry tight in fixedDrives
            .Where(d => !d.IsSystemDrive && d.UsedPercent >= 85)
            .OrderByDescending(d => d.UsedPercent)
            .Take(1))
        {
            recommended.Add(tight.DriveLetter);
        }

        if (recommended.Count == 0)
        {
            recommended.Add(fixedDrives[0].DriveLetter);
        }

        return recommended.ToArray();
    }

    internal AiScanActionCardItem CreateScanActionCard(AiScanActionProposal proposal, string originalUserPrompt)
    {
        bool showDrivePicker = proposal.Kind is AiScanActionKind.DiskScan or AiScanActionKind.Both;
        bool showCleanupOption = proposal.Kind is AiScanActionKind.CleanupScan or AiScanActionKind.Both;

        var card = new AiScanActionCardItem
        {
            RequestId = proposal.RequestId,
            Kind = proposal.Kind,
            Reason = proposal.Reason,
            OriginalUserPrompt = originalUserPrompt,
            ShowDrivePicker = showDrivePicker,
            ShowCleanupOption = showCleanupOption,
            IncludeCleanupScan = showCleanupOption && (proposal.IncludeCleanupScan || !HasAuthorizedCleanupSnapshot),
            AutoContinueAfterScan = true
        };

        if (showDrivePicker)
        {
            WorkspaceStateSnapshot state = _stateCache.GetCachedOrNull() ?? _stateCache.GetOrRefreshSync();
            SharedDriveTelemetry[] fixedDrives = state.Drives.Where(d => d.IsFixedDrive).ToArray();

            var recSet = new HashSet<string>(
                proposal.RecommendedDrives.Length > 0 ? proposal.RecommendedDrives : GetDefaultRecommendedDrivesForScanCard(),
                StringComparer.OrdinalIgnoreCase);

            var authorizedByDrive = _toolset.GetAuthorizedSnapshots()
                .GroupBy(s => VolumeSnapshotCache.NormalizeDriveRoot(s.Volume.DriveRoot), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var options = new List<AiScanDriveOptionItem>(fixedDrives.Length);
            foreach (SharedDriveTelemetry drive in fixedDrives)
            {
                bool isRec = recSet.Contains(drive.DriveLetter) || recSet.Contains(drive.DriveRoot);
                bool hasFresh = false;
                string ageText = string.Empty;

                if (authorizedByDrive.TryGetValue(drive.DriveRoot, out ScanSnapshot? authSnap))
                {
                    hasFresh = true;
                    int mins = Math.Max(0, (int)Math.Round((DateTimeOffset.UtcNow - authSnap.CompletedAtUtc).TotalMinutes));
                    ageText = mins <= 0 ? "刚刚" : $"{mins}分前";
                }
                else
                {
                    VolumeSnapshotLookup? cached = VolumeSnapshotCache.Shared.TryGet(drive.DriveRoot, runLiveProbe: false, touchAccessTime: false);
                    if (cached is { IsFresh: true })
                    {
                        hasFresh = true;
                        int mins = Math.Max(0, (int)Math.Round(cached.Age.TotalMinutes));
                        ageText = mins <= 0 ? "刚刚" : $"{mins}分前";
                    }
                }

                // 智能克制默认勾选：仅默认勾选 AI 推荐且尚无新鲜快照的盘（绝不默认全选所有硬盘）
                bool defaultChecked = isRec && !hasFresh;

                options.Add(new AiScanDriveOptionItem(card.NotifySelectionChanged)
                {
                    DriveRoot = drive.DriveRoot,
                    DriveLetter = drive.DriveLetter,
                    VolumeLabel = drive.VolumeLabel,
                    TotalBytes = drive.TotalBytes,
                    FreeBytes = drive.FreeBytes,
                    UsedPercent = drive.UsedPercent,
                    IsSystemDrive = drive.IsSystemDrive,
                    SupportsRawMft = drive.SupportsRawMft,
                    IsAiRecommended = isRec,
                    HasFreshSnapshot = hasFresh,
                    FreshSnapshotAgeText = ageText,
                    IsChecked = defaultChecked
                });
            }

            // 排序：AI 推荐盘 → 系统盘 → 空间紧张盘(≥85%) → 已用占比降序 → 盘符升序
            List<AiScanDriveOptionItem> ordered = options
                .OrderByDescending(o => o.IsAiRecommended)
                .ThenByDescending(o => o.IsSystemDrive)
                .ThenByDescending(o => o.UsedPercent >= 85)
                .ThenByDescending(o => o.UsedPercent)
                .ThenBy(o => o.DriveLetter, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 若全部推荐盘都无新鲜快照且未勾任何一项，保底勾选排序第一项（系统盘/最紧张盘）
            if (ordered.Count > 0 && ordered.All(o => !o.IsChecked) && ordered.All(o => !o.HasFreshSnapshot))
            {
                ordered[0].IsChecked = true;
            }

            card.PopulateDrives(ordered);
        }

        return card;
    }

    internal bool AuthorizeFreshCachedSnapshots()
    {
        bool anyAuthorized = false;
        foreach (VolumeSnapshotLookup item in VolumeSnapshotCache.Shared.ListActive(runLiveProbe: false))
        {
            if (item.IsFresh)
            {
                SyncScanSnapshot(item.Entry.Snapshot);
                anyAuthorized = true;
            }
        }
        return anyAuthorized || HasAuthorizedSnapshot;
    }

    internal async Task<ScanSnapshot?> ExecuteDiskScanForCardAsync(
        string driveRoot,
        IProgress<(double Percent, string Message)> progress,
        CancellationToken ct)
    {
        ScanSnapshot? snapshot;
        if (_scanDriveCallback is not null)
        {
            snapshot = await _scanDriveCallback(driveRoot, progress, ct).ConfigureAwait(false);
        }
        else
        {
            var svc = new DiskScanService();
            var scanProgress = new Progress<ScanProgress>(p =>
            {
                double pct = p.Percent ?? 50;
                progress.Report((pct, p.Message ?? "扫描目录结构中…"));
            });
            snapshot = await svc.ScanVolumeAsync(driveRoot, allowFallbackToDirectoryWalk: true, scanProgress, ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                VolumeSnapshotCache.Shared.Put(snapshot, driveRoot.TrimEnd('\\'), Math.Max(0.1, (snapshot.CompletedAtUtc - snapshot.StartedAtUtc).TotalSeconds), 0, 0, 0);
            }
        }

        if (snapshot is not null)
        {
            DispatchToUi(() => SyncScanSnapshot(snapshot));
            _stateCache.Invalidate();
            _ = OnEnteredAiPageAsync(force: true);
        }

        return snapshot;
    }

    internal async Task<CleanupScanSnapshot?> ExecuteCleanupScanForCardAsync(
        IProgress<string> progress,
        CancellationToken ct)
    {
        CleanupScanSnapshot? snapshot = null;
        if (_scanCleanupCallback is not null)
        {
            snapshot = await _scanCleanupCallback(progress, ct).ConfigureAwait(false);
        }

        if (snapshot is not null)
        {
            DispatchToUi(() => SyncCleanupScanSnapshot(snapshot));
            _stateCache.Invalidate();
            _ = OnEnteredAiPageAsync(force: true);
        }

        return snapshot;
    }

    /// <summary>
    /// 双向同步当前待迁移清单状态到所有 3 个独立会话的 AI 建议卡片。
    /// </summary>
    public void SyncAdoptedPlanItems(Guid? planScanId, IReadOnlyCollection<int> planNodeIds, IReadOnlyCollection<string> planSourcePaths)
    {
        DispatchToUi(() =>
        {
            _lastSyncedPlanScanId = planScanId;
            _currentPlanNodeIds = planNodeIds.ToHashSet();
            _currentPlanSourcePaths = new HashSet<string>(planSourcePaths, StringComparer.OrdinalIgnoreCase);

            GeneralSession.SyncAdoptedPlanItems(planScanId, planNodeIds, planSourcePaths);
            DiskScanSession.SyncAdoptedPlanItems(planScanId, planNodeIds, planSourcePaths);
            CleanupSession.SyncAdoptedPlanItems(planScanId, planNodeIds, planSourcePaths);
        });
    }

    internal bool IsCardCurrentlyInPlan(Guid cardScanId, int cardNodeId, string actualLocalPath)
    {
        if (!string.IsNullOrWhiteSpace(actualLocalPath) && _currentPlanSourcePaths.Contains(actualLocalPath))
        {
            return true;
        }
        return _lastSyncedPlanScanId == cardScanId && _currentPlanNodeIds.Contains(cardNodeId);
    }

    internal void AppendProposalCards(AiConversationTurnItem turn, AiMigrationProposalReceipt receipt)
    {
        foreach (AiMigrationProposalItemRecord item in receipt.Items)
        {
            if (item.Verdict is not (AiProposalItemVerdict.Accepted or AiProposalItemVerdict.RejectedByPolicy))
            {
                continue;
            }

            bool isAdoptable = item.IsAdoptableToMigrationPlan;
            bool isAlreadyInPlan = isAdoptable && IsCardCurrentlyInPlan(receipt.ScanId, item.NodeId, item.ActualLocalPath);

            turn.ProposalCards.Add(new AiProposalCardItem
            {
                ProposalId = receipt.ProposalId,
                ScanId = receipt.ScanId,
                NodeId = item.NodeId,
                DisplayName = item.DisplayName,
                SanitizedPath = item.SanitizedPath,
                ActualLocalPath = item.ActualLocalPath,
                SizeText = FileStatsService.FormatBytes(Math.Max(item.UniqueAllocatedBytes, item.LogicalBytes)),
                Reason = item.Verdict == AiProposalItemVerdict.RejectedByPolicy
                    ? item.VerdictMessage
                    : item.Reason,
                VerdictMessage = item.VerdictMessage,
                SuggestedTargetPath = item.SuggestedTargetPath,
                AdmissionLevel = item.AdmissionLevel,
                Verdict = item.Verdict,
                IsAdoptableToMigrationPlan = isAdoptable,
                IsAdopted = isAlreadyInPlan,
                IsSuspectedCacheAdvisory = item.SuggestedCategory.Equals("SuspectedCacheAdvisory", StringComparison.OrdinalIgnoreCase),
                IsNativeGuideAdvisory = item.SuggestedCategory.Equals("NativeGuideAdvisory", StringComparison.OrdinalIgnoreCase)
            });
        }
    }

    internal void AppendCleanupProposalCards(AiConversationTurnItem turn, AiCleanupProposalReceipt receipt)
    {
        foreach (AiCleanupProposalItemRecord item in receipt.Items)
        {
            if (item.Verdict is not (AiCleanupProposalItemVerdict.AcceptedRuleAdvice
                or AiCleanupProposalItemVerdict.AcceptedResidualQuarantineOnly
                or AiCleanupProposalItemVerdict.HardBlockedByExclusion))
            {
                continue;
            }

            bool isBlocked = item.Verdict == AiCleanupProposalItemVerdict.HardBlockedByExclusion;
            bool isResidual = item.Verdict == AiCleanupProposalItemVerdict.AcceptedResidualQuarantineOnly;
            bool isRuleAdvice = item.Verdict == AiCleanupProposalItemVerdict.AcceptedRuleAdvice;

            turn.ProposalCards.Add(new AiProposalCardItem
            {
                ProposalId = receipt.ProposalId,
                ScanId = receipt.ScanId ?? Guid.Empty,
                NodeId = item.NodeId ?? 0,
                DisplayName = item.DisplayName,
                SanitizedPath = item.SanitizedPath,
                ActualLocalPath = item.ActualLocalPath,
                SizeText = item.SizeBytes > 0 ? FileStatsService.FormatBytes(item.SizeBytes) : "官方接口",
                Reason = isBlocked ? item.VerdictMessage : item.Reason,
                VerdictMessage = item.VerdictMessage,
                AdmissionLevel = isBlocked ? MigrationAdmissionLevel.HardDenied : MigrationAdmissionLevel.ManualRiskConfirm,
                Verdict = isBlocked ? AiProposalItemVerdict.RejectedByPolicy : AiProposalItemVerdict.Accepted,
                IsAdoptableToMigrationPlan = false,
                IsCleanupResidualProposal = isResidual,
                IsCleanupRuleAdvice = isRuleAdvice
            });
        }
    }

    private void ApplySharedStateSnapshot(WorkspaceStateSnapshot state)
    {
        var authorizedDrives = _toolset.GetAuthorizedSnapshots()
            .Select(s => VolumeSnapshotCache.NormalizeDriveRoot(s.Volume.DriveRoot))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        SharedDriveRows.Clear();
        foreach (SharedDriveTelemetry d in state.Drives
            .Where(x => x.IsFixedDrive)
            .OrderByDescending(x => x.IsSystemDrive)
            .ThenByDescending(x => x.UsedPercent >= 85)
            .ThenBy(x => x.DriveLetter, StringComparer.OrdinalIgnoreCase))
        {
            bool isAuth = authorizedDrives.Contains(d.DriveRoot);
            bool isTight = d.UsedPercent >= 85;
            SharedDriveRows.Add(new AiSharedDriveRowItem
            {
                DriveRoot = d.DriveRoot,
                DriveLetter = d.DriveLetter,
                TitleText = string.IsNullOrWhiteSpace(d.VolumeLabel)
                    ? (d.IsSystemDrive ? $"{d.DriveLetter} 系统盘" : $"{d.DriveLetter} 本地盘")
                    : $"{d.DriveLetter} {d.VolumeLabel}",
                SpaceMetaText = $"剩 {FileStatsService.FormatBytes(d.FreeBytes)} / {FileStatsService.FormatBytes(d.TotalBytes)}（已用 {d.UsedPercent:F0}%）",
                UsedPercent = d.UsedPercent,
                BarColor = isTight ? "#C42B1C" : (d.UsedPercent >= 70 ? "#D97706" : "#0078D4"),
                StatusBadgeText = isAuth ? "已扫 ✓" : (isTight ? "空间紧张" : (d.SupportsSymlinkMigrationTarget ? "可作目标" : d.FileSystem)),
                StatusBadgeBg = isAuth ? "#ECFDF5" : (isTight ? "#FEF2F2" : "#F1F5F9"),
                StatusBadgeBorder = isAuth ? "#A7F3D0" : (isTight ? "#FECACA" : "#E2E8F0"),
                StatusBadgeFg = isAuth ? "#0F7B4B" : (isTight ? "#C42B1C" : "#475569"),
                IsSystemDrive = d.IsSystemDrive,
                HasAuthorizedSnapshot = isAuth
            });
        }

        if (state.Backups.UnverifiedBackupCount > 0 || state.Backups.RecoveryRequiredCount > 0)
        {
            string sizePart = state.Backups.HasEstimatedBytes
                ? $" · 约 {FileStatsService.FormatBytes(state.Backups.EstimatedBackupBytes)}"
                : string.Empty;
            string recoveryPart = state.Backups.RecoveryRequiredCount > 0
                ? $"（含 {state.Backups.RecoveryRequiredCount} 项中断待核验）"
                : string.Empty;
            BackupSummaryTitle = $"迁移备份：{state.Backups.UnverifiedBackupCount} 项待确认释放{sizePart}{recoveryPart}";
        }
        else
        {
            BackupSummaryTitle = "迁移备份：暂无待确认释放项";
        }

        QuarantineSummaryTitle = state.Quarantine.TotalBatchCount > 0
            ? $"清理隔离区：{state.Quarantine.TotalBatchCount} 批（共 {FileStatsService.FormatBytes(state.Quarantine.TotalBytes)} 可释放）"
            : "清理隔离区：当前为空";

        HasReclaimableBackupOrQuarantine = state.Backups.UnverifiedBackupCount > 0
            || state.Backups.RecoveryRequiredCount > 0
            || state.Quarantine.TotalBatchCount > 0;
    }

    private void RefreshContextSummary()
    {
        ContextHotspots.Clear();
        AuthorizedDiskScans.Clear();

        IReadOnlyList<ScanSnapshot> snapshots = _toolset.GetAuthorizedSnapshots();
        if (snapshots.Count == 0)
        {
            ActiveScanId = null;
            SnapshotContextTitle = IsSnapshotSharingEnabled
                ? "磁盘扫描快照：尚未扫描"
                : "磁盘扫描快照：已暂停共享";
            SnapshotContextMeta = IsSnapshotSharingEnabled
                ? "扫描任意磁盘后支持多盘共存授权，AI 绝不会自动扫描。"
                : "可随时重新开启共享授权。";
            WorkspaceStateSnapshot? st = _stateCache.GetCachedOrNull();
            if (st is not null) ApplySharedStateSnapshot(st);
            return;
        }

        ScanSnapshot latest = snapshots[0];
        ActiveScanId = latest.ScanId;

        foreach (ScanSnapshot s in snapshots)
        {
            string d = s.Volume.DriveRoot.TrimEnd('\\');
            AuthorizedDiskScans.Add(new AiAuthorizedScanRowItem
            {
                ScanId = s.ScanId,
                DriveLetter = d,
                SummaryText = $"{d} · 已用 {FileStatsService.FormatBytes(s.Volume.UsedBytes)}"
            });
        }

        string drivesText = string.Join(" / ", snapshots.Select(s => s.Volume.DriveRoot.TrimEnd('\\')));
        SnapshotContextTitle = snapshots.Count == 1
            ? $"磁盘扫描快照 · {drivesText} 盘（已脱敏共享）"
            : $"磁盘扫描快照 · {drivesText} 共 {snapshots.Count} 块盘（已脱敏共享）";
        SnapshotContextMeta = $"最新扫描 {FileStatsService.FormatBytes(latest.Volume.UsedBytes)} · 仅共享目录结构元数据";

        AiMigrationCandidatesResponse candidates = _toolset.ListMigrationCandidates(latest.ScanId, pageSize: 5);
        foreach (AiMigrationCandidateDto c in candidates.Candidates.Take(4))
        {
            (string badge, string bg, string border, string fg) = c.AdmissionLevel switch
            {
                MigrationAdmissionLevel.VerifiedEligible => ("L1 推荐迁移", "#ECFDF5", "#A7F3D0", "#0F7B4B"),
                MigrationAdmissionLevel.VendorNativeRecommended => ("L2 原生引导", "#EBF5FF", "#BFDBFE", "#0078D4"),
                _ => ("未知大占用 · 可诊断", "#FFFBEB", "#FDE68A", "#B45309")
            };

            ContextHotspots.Add(new AiContextHotspotRow
            {
                NodeId = c.NodeId,
                DisplayName = c.DisplayName,
                SanitizedPath = c.SanitizedPath,
                SizeText = FileStatsService.FormatBytes(Math.Max(c.UniqueAllocatedBytes, c.LogicalBytes)),
                Level = c.AdmissionLevel,
                BadgeText = badge,
                BadgeBg = bg,
                BadgeBorder = border,
                BadgeFg = fg
            });
        }

        WorkspaceStateSnapshot? cachedState = _stateCache.GetCachedOrNull();
        if (cachedState is not null) ApplySharedStateSnapshot(cachedState);
    }

    private void RefreshCleanupContextSummary()
    {
        CleanupScanSnapshot? snap = _toolset.GetLatestAuthorizedCleanupSnapshot();
        if (snap is null)
        {
            ActiveCleanupScanId = null;
            CleanupSnapshotTitle = "垃圾清理快照：尚未扫描";
            CleanupSnapshotMeta = "完成三档规则扫描后，AI 可直接解读谨慎项风险与清理顺序。";
            return;
        }

        ActiveCleanupScanId = snap.CleanupScanId;
        int availCount = snap.Rules.Count(r => r.IsAvailable);
        CleanupSnapshotTitle = $"垃圾清理快照 · 可清理 {FileStatsService.FormatBytes(snap.TotalReclaimableBytes)}（{availCount} 项可用）";
        CleanupSnapshotMeta = $"建议 {FileStatsService.FormatBytes(snap.RecommendedBytes)} · 按需 {FileStatsService.FormatBytes(snap.OnDemandBytes)} · 谨慎 {FileStatsService.FormatBytes(snap.CautionBytes)}";
    }

    private void OnInboxStateChanged()
    {
        GeneralSession.SyncProposalInvalidations();
        DiskScanSession.SyncProposalInvalidations();
        CleanupSession.SyncProposalInvalidations();
        RefreshProposalInboxSummary();
    }

    private void RefreshProposalInboxSummary()
    {
        int migrationAccepted = _toolset.Inbox.GetAllReceipts()
            .Where(r => r.Status != AiProposalStatus.Invalidated)
            .Sum(r => r.AcceptedCount);
        int residualPending = _toolset.Inbox.GetCleanupResidualCandidates()
            .Count(c => c.Status == AiCleanupCandidateStatus.PendingManualReview);

        if (migrationAccepted == 0 && residualPending == 0)
        {
            HasAnyProposalSummary = false;
            ProposalInboxSummaryText = "本轮尚未生成结构化建议卡片";
            return;
        }

        HasAnyProposalSummary = true;
        var parts = new List<string>();
        if (migrationAccepted > 0) parts.Add($"待审阅迁移建议 {migrationAccepted} 条");
        if (residualPending > 0) parts.Add($"规则外 AI 疑似残留 {residualPending} 条");
        ProposalInboxSummaryText = string.Join(" · ", parts);
    }

    internal void DispatchToUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            if (!dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                try
                {
                    dispatcher.Invoke(action);
                }
                catch (TaskCanceledException)
                {
                }
                return;
            }
        }

        action();
    }
}
