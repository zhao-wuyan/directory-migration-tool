using System.Collections.ObjectModel;
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

    public AiAssistantViewModel Host => _host;
    public AiChatScene Scene { get; }

    public ObservableCollection<AiConversationTurnItem> ConversationTurns { get; } = [];

    [ObservableProperty]
    private string _userPromptInput = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendPrompt))]
    [NotifyPropertyChangedFor(nameof(SessionTabBadgeText))]
    private bool _isAnalyzing;

    public bool CanSendPrompt => !IsAnalyzing;
    public bool HasConversationTurns => ConversationTurns.Count > 0;
    public string DisclaimerText => _host.DisclaimerText;

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
        ConversationTurns.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasConversationTurns));
            OnPropertyChanged(nameof(SessionTabBadgeText));
            _host.NotifySessionActivityChanged(this);
        };
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
                    AuthorizedScanIds: authorizedIds),
                progress,
                _analysisCts.Token);

            _host.DispatchToUi(() =>
            {
                FinalizeActiveTimelineSteps(turnItem, result.WasCancelled ? "已停止" : "已中断");
                turnItem.StepsUsed = result.StepsUsed;
                if (!string.IsNullOrWhiteSpace(result.AssistantMessage))
                {
                    turnItem.AssistantText = result.AssistantMessage;
                    SyncFinalAssistantMessageToTimeline(turnItem, result.AssistantMessage);
                }

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
                IsAnalyzing = false;
                _host.NotifySessionActivityChanged(this);
            });
        }
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
            foreach (AiScanActionCardItem card in turn.ScanActionCards)
            {
                card.ActiveScanCts?.Cancel();
                card.ActiveScanCts?.Dispose();
                card.ActiveScanCts = null;
            }
        }
        ConversationTurns.Clear();
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
            bool reused = _host.AuthorizeFreshCachedSnapshots();
            if (reused)
            {
                card.IsCompleted = true;
                card.StatusMessage = "✓ 已复用内存中已有的新鲜磁盘扫描快照。";
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
                card.ProgressIsIndeterminate = true;

                CleanupScanSnapshot? cleanupSnap = await _host.ExecuteCleanupScanForCardAsync(
                    new Progress<string>(msg => _host.DispatchToUi(() =>
                        card.StatusMessage = $"({currentStep}/{totalSteps}) {msg}")),
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
        card?.ActiveScanCts?.Cancel();
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
