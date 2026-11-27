using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;

namespace MoveWithSymlinkWPF.ViewModels;

/// <summary>
/// 预冻结画刷缓存：消除 WPF 弹窗首次布局时大量十六进制字符串 BrushConverter 反射解析与非冻结画刷分配。
/// </summary>
internal static class FrozenBrushCache
{
    private static readonly ConcurrentDictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Brush Get(string hex) => Cache.GetOrAdd(hex, static h =>
    {
        var color = (Color)ColorConverter.ConvertFromString(h);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    });
}

/// <summary>
/// 统一审阅弹框中的单条建议行视图模型（独立持有弹窗内临时勾选态，默认绝不勾选）。
/// </summary>
public partial class AiProposalReviewRowItem : ObservableObject
{
    private readonly AiProposalReviewViewModel _owner;
    private readonly Action _selectionChangedCallback;

    public AiProposalCardItem Card { get; }

    public string DisplayName => Card.DisplayName;
    public string SanitizedPath => Card.SanitizedPath;
    public string ActualLocalPath => Card.ActualLocalPath;
    public string PathToolTip => string.IsNullOrWhiteSpace(Card.ActualLocalPath) ? Card.SanitizedPath : Card.ActualLocalPath;
    public bool HasPathText => !string.IsNullOrWhiteSpace(Card.SanitizedPath);
    public long SizeBytes => Card.SizeBytes;
    public string SizeText => Card.SizeText;
    public string Reason => Card.Reason;
    public string RiskWarning => Card.RiskWarning;
    public bool HasRiskWarning => HasMeaningfulRiskWarning(Card);
    public AiProposalCategoryKind CategoryKind => Card.CategoryKind;
    public bool IsBlockedByPolicy => Card.IsBlockedByPolicy;
    public bool IsActionCompleted => Card.IsActionCompleted;
    public bool CanSelect => Card.CanBatchSelectInModal;
    public bool ShowCheckBox => CanSelect;
    public bool ShowCompletedGlyph => !CanSelect && IsActionCompleted && !IsBlockedByPolicy;
    public bool ShowBlockedGlyph => IsBlockedByPolicy;
    public bool ShowReadOnlyGlyph => !CanSelect && !IsActionCompleted && !IsBlockedByPolicy;
    public bool ShowStatusBadge => IsBlockedByPolicy || Card.IsInvalidated || IsActionCompleted || !CanSelect;

    private static bool HasMeaningfulRiskWarning(AiProposalCardItem card)
    {
        if (string.IsNullOrWhiteSpace(card.RiskWarning)) return false;
        string trimmed = card.RiskWarning.Trim();
        if (trimmed is "无" or "无。" or "无风险" or "无风险。" or "暂无" or "None" or "N/A" or "-")
        {
            return false;
        }
        // 非谨慎内置规则中，AI 常在风险栏重复写“无。仅清 >24h...”，此时避免冗余占位
        if (card.IsCleanupRuleAdvice
            && card.OriginalRuleTier != CleanupRecommendationTier.Caution
            && (trimmed.StartsWith("无。", StringComparison.Ordinal)
                || trimmed.StartsWith("无，", StringComparison.Ordinal)
                || trimmed.StartsWith("无 ", StringComparison.Ordinal)
                || trimmed.StartsWith("无风险", StringComparison.Ordinal)
                || trimmed.StartsWith("零风险", StringComparison.Ordinal)))
        {
            return false;
        }
        return true;
    }

    public string CategoryBadgeText => Card.CompactCategoryBadgeText;
    public string CategoryBadgeBg => Card.CardBg;
    public string CategoryBadgeBorder => Card.CardBorder;
    public string CategoryBadgeFg => Card.CardAccentFg;
    public string AccentStripeColor => Card.CategoryKind switch
    {
        AiProposalCategoryKind.Blocked => "#DC2626",
        AiProposalCategoryKind.Migration => Card.AdmissionLevel == MigrationCore.Models.Safety.MigrationAdmissionLevel.ManualRiskConfirm ? "#F59E0B" : "#10B981",
        AiProposalCategoryKind.CleanupRule => Card.OriginalRuleTier switch
        {
            CleanupRecommendationTier.Recommended => "#10B981",
            CleanupRecommendationTier.Caution => "#F59E0B",
            _ => "#0078D4"
        },
        _ => "#F59E0B"
    };

    public Brush CategoryBadgeBgBrush => FrozenBrushCache.Get(CategoryBadgeBg);
    public Brush CategoryBadgeBorderBrush => FrozenBrushCache.Get(CategoryBadgeBorder);
    public Brush CategoryBadgeFgBrush => FrozenBrushCache.Get(CategoryBadgeFg);
    public Brush AccentStripeBrush => FrozenBrushCache.Get(AccentStripeColor);

    public string StatusBadgeText => Card.CompactStatusText;
    public string StatusBadgeBg => Card.CompactStatusBg;
    public string StatusBadgeBorder => Card.CompactStatusBorder;
    public string StatusBadgeFg => Card.CompactStatusFg;
    public Brush StatusBadgeBgBrush => FrozenBrushCache.Get(StatusBadgeBg);
    public Brush StatusBadgeBorderBrush => FrozenBrushCache.Get(StatusBadgeBorder);
    public Brush StatusBadgeFgBrush => FrozenBrushCache.Get(StatusBadgeFg);

    public string RowBg => IsBlockedByPolicy
        ? "#FEF2F2"
        : IsSelected
            ? "#F0F7FF"
            : IsActionCompleted
                ? "#F8FAFC"
                : "#FFFFFF";

    public string RowBorder => IsBlockedByPolicy
        ? "#FECACA"
        : IsSelected
            ? "#93C5FD"
            : "#E2E8F0";

    public Brush RowBgBrush => FrozenBrushCache.Get(RowBg);
    public Brush RowBorderBrush => FrozenBrushCache.Get(RowBorder);

    public string NonCheckboxGlyph => IsBlockedByPolicy
        ? "⊘"
        : IsActionCompleted
            ? "✓"
            : "ℹ";

    public Brush NonCheckboxGlyphBrush => FrozenBrushCache.Get(
        IsBlockedByPolicy ? "#C42B1C" : IsActionCompleted ? "#0F7B4B" : "#64748B");

    public bool ShowNonCheckboxGlyph => !CanSelect;

    public bool CanOpenInExplorer => Card.CanOpenInExplorer;
    public bool CanLocateInTree => Card.CanLocateInTree;
    public bool CanLocateCleanupRule => Card.CanLocateCleanupRule;

    public string LocateCleanupRuleButtonText => Card.IsCleanupRuleCleaned
        ? "清理页查看"
        : Card.OriginalRuleTier == CleanupRecommendationTier.Caution
            ? "清理页核对（3档谨慎）"
            : "清理页定位";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowBg))]
    [NotifyPropertyChangedFor(nameof(RowBorder))]
    [NotifyPropertyChangedFor(nameof(RowBgBrush))]
    [NotifyPropertyChangedFor(nameof(RowBorderBrush))]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect)
        {
            _isSelected = false;
            OnPropertyChanged(nameof(IsSelected));
            return;
        }
        _selectionChangedCallback();
    }

    public AiProposalReviewRowItem(AiProposalReviewViewModel owner, AiProposalCardItem card, Action selectionChangedCallback)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Card = card ?? throw new ArgumentNullException(nameof(card));
        _selectionChangedCallback = selectionChangedCallback ?? throw new ArgumentNullException(nameof(selectionChangedCallback));
        Card.PropertyChanged += OnCardPropertyChanged;
    }

    [RelayCommand]
    private void OpenDirectoryInExplorer() => _owner.OpenDirectoryInExplorerCommand.Execute(this);

    [RelayCommand]
    private void LocateInTree() => _owner.LocateInTreeCommand.Execute(this);

    [RelayCommand]
    private void LocateCleanupRule() => _owner.LocateCleanupRuleCommand.Execute(this);

    public void Detach()
    {
        Card.PropertyChanged -= OnCardPropertyChanged;
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!CanSelect && IsSelected)
        {
            IsSelected = false;
        }
        OnPropertyChanged(nameof(IsActionCompleted));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(ShowCheckBox));
        OnPropertyChanged(nameof(ShowCompletedGlyph));
        OnPropertyChanged(nameof(ShowBlockedGlyph));
        OnPropertyChanged(nameof(ShowReadOnlyGlyph));
        OnPropertyChanged(nameof(ShowNonCheckboxGlyph));
        OnPropertyChanged(nameof(NonCheckboxGlyph));
        OnPropertyChanged(nameof(NonCheckboxGlyphBrush));
        OnPropertyChanged(nameof(ShowStatusBadge));
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(StatusBadgeBg));
        OnPropertyChanged(nameof(StatusBadgeBorder));
        OnPropertyChanged(nameof(StatusBadgeFg));
        OnPropertyChanged(nameof(StatusBadgeBgBrush));
        OnPropertyChanged(nameof(StatusBadgeBorderBrush));
        OnPropertyChanged(nameof(StatusBadgeFgBrush));
        OnPropertyChanged(nameof(RowBg));
        OnPropertyChanged(nameof(RowBorder));
        OnPropertyChanged(nameof(RowBgBrush));
        OnPropertyChanged(nameof(RowBorderBrush));
        OnPropertyChanged(nameof(LocateCleanupRuleButtonText));
        _selectionChangedCallback();
    }
}

/// <summary>
/// 「AI 建议统一审阅与添加」弹窗视图模型：
/// 统一承接迁移建议、AI 疑似残留（需用户确认后才入池）、清理规则建议、L2 原生引导与红线拦截。
/// </summary>
public partial class AiProposalReviewViewModel : ObservableObject
{
    private readonly AiAssistantViewModel _host;
    private readonly List<AiProposalReviewRowItem> _allRows = [];
    private bool _isBatchUpdatingSelection;

    public Action? RequestCloseWindow { get; set; }

    public string WindowTitle { get; }
    public string HeaderSubtitle { get; }

    [ObservableProperty]
    private IReadOnlyList<AiProposalReviewRowItem> _visibleRows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllTabTag))]
    [NotifyPropertyChangedFor(nameof(MigrationTabTag))]
    [NotifyPropertyChangedFor(nameof(ResidualTabTag))]
    [NotifyPropertyChangedFor(nameof(RuleTabTag))]
    [NotifyPropertyChangedFor(nameof(RuleRecommendedTabTag))]
    [NotifyPropertyChangedFor(nameof(RuleOnDemandTabTag))]
    [NotifyPropertyChangedFor(nameof(RuleCautionTabTag))]
    [NotifyPropertyChangedFor(nameof(NativeTabTag))]
    [NotifyPropertyChangedFor(nameof(BlockedTabTag))]
    private string _selectedTabKey = "All";

    public string AllTabTag => SelectedTabKey == "All" ? "Active" : string.Empty;
    public string MigrationTabTag => SelectedTabKey == "Migration" ? "Active" : string.Empty;
    public string ResidualTabTag => SelectedTabKey == "CleanupResidual" ? "Active" : string.Empty;
    public string RuleTabTag => SelectedTabKey == "CleanupRule" ? "Active" : string.Empty;
    public string RuleRecommendedTabTag => SelectedTabKey == "RuleRecommended" ? "Active" : string.Empty;
    public string RuleOnDemandTabTag => SelectedTabKey == "RuleOnDemand" ? "Active" : string.Empty;
    public string RuleCautionTabTag => SelectedTabKey == "RuleCaution" ? "Active" : string.Empty;
    public string NativeTabTag => SelectedTabKey == "NativeGuide" ? "Active" : string.Empty;
    public string BlockedTabTag => SelectedTabKey == "Blocked" ? "Active" : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchKeyword))]
    private string _searchKeyword = string.Empty;

    public bool HasSearchKeyword => !string.IsNullOrWhiteSpace(SearchKeyword);

    [ObservableProperty]
    private bool _onlyShowPendingActionable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedbackBanner))]
    private string _feedbackMessage = string.Empty;

    [ObservableProperty]
    private bool _feedbackIsWarning;

    public bool HasFeedbackBanner => !string.IsNullOrWhiteSpace(FeedbackMessage);
    public string FeedbackBg => FeedbackIsWarning ? "#FFFBEB" : "#ECFDF5";
    public string FeedbackBorder => FeedbackIsWarning ? "#FDE68A" : "#A7F3D0";
    public string FeedbackFg => FeedbackIsWarning ? "#B45309" : "#0F7B4B";

    partial void OnFeedbackIsWarningChanged(bool value)
    {
        OnPropertyChanged(nameof(FeedbackBg));
        OnPropertyChanged(nameof(FeedbackBorder));
        OnPropertyChanged(nameof(FeedbackFg));
    }

    partial void OnSearchKeywordChanged(string value) => RebuildVisibleRows();
    partial void OnOnlyShowPendingActionableChanged(bool value) => RebuildVisibleRows();

    // ---- 统计计数与分类标签 ----
    public int TotalCount => _allRows.Count;
    public int MigrationCount => _allRows.Count(r => r.CategoryKind == AiProposalCategoryKind.Migration);
    public int ResidualCount => _allRows.Count(r => r.CategoryKind == AiProposalCategoryKind.CleanupResidual);
    public int RuleCount => _allRows.Count(r => r.CategoryKind == AiProposalCategoryKind.CleanupRule);
    public int RuleRecommendedCount => _allRows.Count(r => r.CategoryKind == AiProposalCategoryKind.CleanupRule && r.Card.OriginalRuleTier == CleanupRecommendationTier.Recommended);
    public int RuleOnDemandCount => _allRows.Count(r => r.CategoryKind == AiProposalCategoryKind.CleanupRule && r.Card.OriginalRuleTier == CleanupRecommendationTier.OnDemand);
    public int RuleCautionCount => _allRows.Count(r => r.CategoryKind == AiProposalCategoryKind.CleanupRule && r.Card.OriginalRuleTier == CleanupRecommendationTier.Caution);
    public int NativeCount => _allRows.Count(r => r.CategoryKind == AiProposalCategoryKind.NativeGuide);
    public int BlockedCount => _allRows.Count(r => r.CategoryKind == AiProposalCategoryKind.Blocked);

    public bool HasMigrationTab => MigrationCount > 0;
    public bool HasResidualTab => ResidualCount > 0;
    public bool HasRuleTierTabs =>
        (RuleRecommendedCount > 0 ? 1 : 0)
        + (RuleOnDemandCount > 0 ? 1 : 0)
        + (RuleCautionCount > 0 ? 1 : 0) >= 2;
    public bool HasRuleTab => RuleCount > 0 && !HasRuleTierTabs;
    public bool HasRuleRecommendedTab => HasRuleTierTabs && RuleRecommendedCount > 0;
    public bool HasRuleOnDemandTab => HasRuleTierTabs && RuleOnDemandCount > 0;
    public bool HasRuleCautionTab => HasRuleTierTabs && RuleCautionCount > 0;
    public bool HasNativeTab => NativeCount > 0;
    public bool HasBlockedTab => BlockedCount > 0;
    public bool HasMultipleCategoryTabs =>
        HasRuleTierTabs
        || ((HasMigrationTab ? 1 : 0)
            + (HasResidualTab ? 1 : 0)
            + (RuleCount > 0 ? 1 : 0)
            + (HasNativeTab ? 1 : 0)
            + (HasBlockedTab ? 1 : 0) >= 2);

    public string AllTabText => $"全部 ({TotalCount})";
    public string MigrationTabText => $"建议迁移 ({MigrationCount})";
    public string ResidualTabText => $"AI 疑似残留 ({ResidualCount})";
    public string RuleTabText => $"清理规则 ({RuleCount})";
    public string RuleRecommendedTabText => $"1档建议 ({RuleRecommendedCount})";
    public string RuleOnDemandTabText => $"2档按需 ({RuleOnDemandCount})";
    public string RuleCautionTabText => $"3档谨慎 ({RuleCautionCount})";
    public string NativeTabText => $"L2 原生引导 ({NativeCount})";
    public string BlockedTabText => $"已拦截 ({BlockedCount})";

    public string TotalScopeMetaText
    {
        get
        {
            long remainingBytes = _allRows
                .Where(r => !r.IsBlockedByPolicy && !r.Card.IsCleanupRuleCleaned && !r.Card.IsResidualQuarantined && r.SizeBytes > 0)
                .Sum(r => r.SizeBytes);
            int selectable = _allRows.Count(r => r.CanSelect);
            int cleaned = _allRows.Count(r => r.Card.IsCleanupRuleCleaned || r.Card.IsResidualQuarantined);
            string sizePart = remainingBytes > 0
                ? (cleaned > 0 ? $" · 剩余约 {FileStatsService.FormatBytes(remainingBytes)}" : $" · 涉及约 {FileStatsService.FormatBytes(remainingBytes)}")
                : string.Empty;
            string cleanedPart = cleaned > 0 ? $" · 已清理 {cleaned} 项" : string.Empty;
            return $"共 {TotalCount} 条建议{sizePart}{cleanedPart} · 当前可勾选添加 {selectable} 项";
        }
    }

    public bool HasVisibleRows => VisibleRows.Count > 0;
    public bool HasSelectableInVisible => VisibleRows.Any(r => r.CanSelect);
    public bool AreAllVisibleSelectableChecked => HasSelectableInVisible && VisibleRows.Where(r => r.CanSelect).All(r => r.IsSelected);
    public string ToggleSelectVisibleText => AreAllVisibleSelectableChecked ? "取消全选" : "全选当前可添加项";

    public int SelectedCount => _allRows.Count(r => r.IsSelected);
    public bool CanConfirmSelected => SelectedCount > 0;

    public string SelectionSummaryText
    {
        get
        {
            AiProposalReviewRowItem[] chosen = _allRows.Where(r => r.IsSelected).ToArray();
            if (chosen.Length == 0)
            {
                return "默认全不勾选 · 请先核实目录路径与理由后再勾选添加（绝不立即执行迁移或删除）";
            }

            int mig = chosen.Count(r => r.Card.CanAdopt);
            int res = chosen.Count(r => r.Card.CanAdoptResidual);
            int rul = chosen.Count(r => r.Card.CanSelectCleanupRuleInModal);
            long bytes = chosen.Where(r => r.SizeBytes > 0).Sum(r => r.SizeBytes);

            var parts = new List<string>();
            if (mig > 0) parts.Add($"迁移 {mig}");
            if (res > 0) parts.Add($"疑似残留 {res}");
            if (rul > 0) parts.Add($"清理规则 {rul}");
            if (bytes > 0) parts.Add($"约 {FileStatsService.FormatBytes(bytes)}");

            return $"已勾选 {chosen.Length} 项（{string.Join(" · ", parts)}）";
        }
    }

    public string ConfirmButtonText
    {
        get
        {
            AiProposalReviewRowItem[] chosen = _allRows.Where(r => r.IsSelected).ToArray();
            if (chosen.Length == 0) return "请先勾选要添加的项";

            int mig = chosen.Count(r => r.Card.CanAdopt);
            int res = chosen.Count(r => r.Card.CanAdoptResidual);
            int rul = chosen.Count(r => r.Card.CanSelectCleanupRuleInModal);

            int kindCount = (mig > 0 ? 1 : 0) + (res > 0 ? 1 : 0) + (rul > 0 ? 1 : 0);
            if (kindCount > 1) return $"批量添加所选建议 ({chosen.Length})";
            if (mig > 0) return $"加入待迁移清单 ({mig})";
            if (res > 0) return $"加入「AI 疑似残留」列表 ({res})";
            if (rul > 0) return $"勾选至垃圾清理页 ({rul})";
            return $"确认添加 ({chosen.Length})";
        }
    }

    public bool ShowJumpToMigrationButton => _allRows.Any(r => r.Card.IsAdopted);
    public bool ShowJumpToResidualButton => _allRows.Any(r => r.Card.IsCleanupResidualProposal && (r.Card.IsResidualAdopted || r.Card.IsResidualQuarantined));
    public bool ShowJumpToCleanupRulesButton => _allRows.Any(r => r.Card.IsCleanupRuleAdvice);

    public AiProposalReviewViewModel(
        AiAssistantViewModel host,
        IReadOnlyList<AiProposalCardItem> cards,
        string windowTitle,
        string headerSubtitle)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        WindowTitle = windowTitle;
        HeaderSubtitle = headerSubtitle;

        foreach (AiProposalCardItem card in cards
            .OrderBy(c => c.IsBlockedByPolicy ? 2 : (c.CanBatchSelectInModal ? 0 : 1))
            .ThenByDescending(c => c.SizeBytes))
        {
            _allRows.Add(new AiProposalReviewRowItem(this, card, OnSelectionOrItemStateChanged));
        }

        RebuildVisibleRows();
    }

    public void CleanupSubscriptions()
    {
        foreach (AiProposalReviewRowItem row in _allRows)
        {
            row.Detach();
        }
    }

    [RelayCommand]
    private void SelectTab(string? tabKey)
    {
        string normalized = string.IsNullOrWhiteSpace(tabKey) ? "All" : tabKey;
        if (string.Equals(SelectedTabKey, normalized, StringComparison.Ordinal))
        {
            return;
        }
        SelectedTabKey = normalized;
        RebuildVisibleRows();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchKeyword = string.Empty;
    }

    [RelayCommand]
    private void ToggleSelectAllVisible()
    {
        AiProposalReviewRowItem[] selectable = VisibleRows.Where(r => r.CanSelect).ToArray();
        if (selectable.Length == 0) return;

        bool target = !AreAllVisibleSelectableChecked;
        _isBatchUpdatingSelection = true;
        try
        {
            foreach (AiProposalReviewRowItem row in selectable)
            {
                row.IsSelected = target;
            }
        }
        finally
        {
            _isBatchUpdatingSelection = false;
        }
        NotifySelectionDerivedProperties();
    }

    [RelayCommand]
    private void ConfirmSelected()
    {
        AiProposalReviewRowItem[] chosen = _allRows.Where(r => r.IsSelected && r.CanSelect).ToArray();
        if (chosen.Length == 0) return;

        var (migAdded, resAdded, ruleChecked, warnings) = _host.ApplyReviewedProposals(chosen.Select(r => r.Card));

        _isBatchUpdatingSelection = true;
        try
        {
            foreach (AiProposalReviewRowItem row in chosen)
            {
                if (!row.CanSelect)
                {
                    row.IsSelected = false;
                }
            }
        }
        finally
        {
            _isBatchUpdatingSelection = false;
        }

        var summaryParts = new List<string>();
        if (migAdded > 0) summaryParts.Add($"已将 {migAdded} 项加入「待迁移清单」（未立即执行迁移）");
        if (resAdded > 0) summaryParts.Add($"已将 {resAdded} 项加入「AI 疑似残留」复核列表（须前往残留页核实并双勾选隔离）");
        if (ruleChecked > 0) summaryParts.Add($"已在垃圾清理页勾选 {ruleChecked} 条内置规则（未立即清理）");

        if (summaryParts.Count > 0 && warnings.Count == 0)
        {
            FeedbackIsWarning = false;
            FeedbackMessage = "✓ " + string.Join("；", summaryParts) + "。";
        }
        else if (summaryParts.Count > 0)
        {
            FeedbackIsWarning = true;
            FeedbackMessage = string.Join("；", summaryParts) + "。部分条目提示：" + string.Join("；", warnings.Take(2));
        }
        else
        {
            FeedbackIsWarning = true;
            FeedbackMessage = warnings.Count > 0
                ? string.Join("；", warnings.Take(3))
                : "未产生新的可添加变更。";
        }

        if (OnlyShowPendingActionable)
        {
            RebuildVisibleRows();
        }
        else
        {
            NotifySelectionDerivedProperties();
        }
    }

    [RelayCommand]
    private void OpenDirectoryInExplorer(AiProposalReviewRowItem? row)
    {
        if (row is null || !row.CanOpenInExplorer) return;
        _host.OpenDirectoryInExplorer(row.ActualLocalPath);
    }

    [RelayCommand]
    private void LocateInTree(AiProposalReviewRowItem? row)
    {
        if (row is null || !row.CanLocateInTree) return;
        RequestCloseWindow?.Invoke();
        _host.LocateProposalInTreeCommand.Execute(row.Card);
    }

    [RelayCommand]
    private void LocateCleanupRule(AiProposalReviewRowItem? row)
    {
        if (row is null || !row.CanLocateCleanupRule) return;
        if (_host.TryLocateCleanupRule(row.Card.RuleId))
        {
            RequestCloseWindow?.Invoke();
        }
        else
        {
            FeedbackIsWarning = true;
            FeedbackMessage = $"未能在当前垃圾清理扫描快照中定位规则「{row.DisplayName}」，请先前往垃圾清理页执行扫描。";
        }
    }

    [RelayCommand]
    private void JumpToMigrationPlan()
    {
        RequestCloseWindow?.Invoke();
        _host.OpenDiskScanCommand.Execute(null);
    }

    [RelayCommand]
    private void JumpToCleanupResidual()
    {
        RequestCloseWindow?.Invoke();
        _host.OpenCleanupResidualReviewCommand.Execute(null);
    }

    [RelayCommand]
    private void JumpToCleanupPage()
    {
        RequestCloseWindow?.Invoke();
        _host.OpenCleanupCommand.Execute(null);
    }

    [RelayCommand]
    private void CloseDialog()
    {
        RequestCloseWindow?.Invoke();
    }

    private void RebuildVisibleRows()
    {
        string[] keywords = string.IsNullOrWhiteSpace(SearchKeyword)
            ? []
            : SearchKeyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var next = new List<AiProposalReviewRowItem>(_allRows.Count);
        foreach (AiProposalReviewRowItem row in _allRows)
        {
            if (!MatchesSelectedTab(row)) continue;
            if (OnlyShowPendingActionable && !row.CanSelect) continue;

            if (keywords.Length > 0)
            {
                bool allMatched = true;
                foreach (string kw in keywords)
                {
                    bool hit = row.DisplayName.Contains(kw, StringComparison.OrdinalIgnoreCase)
                        || row.SanitizedPath.Contains(kw, StringComparison.OrdinalIgnoreCase)
                        || row.Reason.Contains(kw, StringComparison.OrdinalIgnoreCase)
                        || row.CategoryBadgeText.Contains(kw, StringComparison.OrdinalIgnoreCase);
                    if (!hit)
                    {
                        allMatched = false;
                        break;
                    }
                }
                if (!allMatched) continue;
            }

            next.Add(row);
        }

        // 若筛选结果序列未变化，禁止无谓替换列表触发 WPF 视觉树重排
        bool unchanged = next.Count == VisibleRows.Count;
        if (unchanged)
        {
            for (int i = 0; i < next.Count; i++)
            {
                if (!ReferenceEquals(next[i], VisibleRows[i]))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (!unchanged)
        {
            VisibleRows = next.ToArray();
        }

        OnPropertyChanged(nameof(HasVisibleRows));
        NotifySelectionDerivedProperties();
    }

    private bool MatchesSelectedTab(AiProposalReviewRowItem row) => SelectedTabKey switch
    {
        "Migration" => row.CategoryKind == AiProposalCategoryKind.Migration,
        "CleanupResidual" => row.CategoryKind == AiProposalCategoryKind.CleanupResidual,
        "CleanupRule" => row.CategoryKind == AiProposalCategoryKind.CleanupRule,
        "RuleRecommended" => row.CategoryKind == AiProposalCategoryKind.CleanupRule && row.Card.OriginalRuleTier == CleanupRecommendationTier.Recommended,
        "RuleOnDemand" => row.CategoryKind == AiProposalCategoryKind.CleanupRule && row.Card.OriginalRuleTier == CleanupRecommendationTier.OnDemand,
        "RuleCaution" => row.CategoryKind == AiProposalCategoryKind.CleanupRule && row.Card.OriginalRuleTier == CleanupRecommendationTier.Caution,
        "NativeGuide" => row.CategoryKind == AiProposalCategoryKind.NativeGuide,
        "Blocked" => row.CategoryKind == AiProposalCategoryKind.Blocked,
        _ => true
    };

    private void OnSelectionOrItemStateChanged()
    {
        if (_isBatchUpdatingSelection) return;
        NotifySelectionDerivedProperties();
    }

    private void NotifySelectionDerivedProperties()
    {
        OnPropertyChanged(nameof(TotalScopeMetaText));
        OnPropertyChanged(nameof(HasSelectableInVisible));
        OnPropertyChanged(nameof(AreAllVisibleSelectableChecked));
        OnPropertyChanged(nameof(ToggleSelectVisibleText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanConfirmSelected));
        OnPropertyChanged(nameof(SelectionSummaryText));
        OnPropertyChanged(nameof(ConfirmButtonText));
        OnPropertyChanged(nameof(ShowJumpToMigrationButton));
        OnPropertyChanged(nameof(ShowJumpToResidualButton));
        OnPropertyChanged(nameof(ShowJumpToCleanupRulesButton));
    }
}
