using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Services;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class MigrationVerificationViewModel(Guid transactionId) : ObservableObject
{
    private readonly MigrationTransactionStore _store = new();
    private readonly QuickMigrateTask _displayTask = new();
    public MigrationTransactionRecord? Record { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmVerificationCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmVerificationCommand))]
    private bool _acknowledged;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";
    [ObservableProperty] private string _currentPathStatus = "正在读取实际路径状态……";
    [ObservableProperty] private string _pathHealthShortText = "正在检查…";
    [ObservableProperty] private bool _isPathHealthOk = true;
    [ObservableProperty] private string _targetProperties = "正在统计当前目标目录……";
    [ObservableProperty] private string _targetStatsShortText = "正在统计…";
    [ObservableProperty] private bool _isTargetStatsOk = true;
    [ObservableProperty] private string _deletionResultText = "尚未处理备份；选择方式本身不会删除，仍需主动进入双确认。";
    [ObservableProperty] private bool _hasDeletionFeedback;
    [ObservableProperty] private string _deletionFeedbackShortText = "";
    [ObservableProperty] private int _backupModeIndex = 1;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public string Name => Record is null ? "迁移验证" : Path.GetFileName(Path.TrimEndingDirectorySeparator(Record.SourcePath));
    public string SourcePath => Record?.SourcePath ?? "";
    public string TargetPath => Record?.TargetPath ?? "";
    public string BackupPath => Record?.BackupPath ?? "未记录原始备份";
    public string TransactionIdText => Record?.TransactionId.ToString() ?? "";
    public string OperationBadgeText => Record is null ? "加载中" : Record.Mode switch
    {
        MigrationMode.Migrate => "迁移",
        MigrationMode.Restore => "还原",
        MigrationMode.Repair => "修复",
        _ => "未知"
    };
    public bool IsUserVerified => Record is { UserVerifiedAtUtc: not null, Stage: MigrationStage.AwaitingUserVerification, BackupDeletionInProgress: false };
    public bool IsVerificationError => Record is not null && (Record.BackupDeletionInProgress || Record.Stage != MigrationStage.AwaitingUserVerification || Record.LastError is not null);
    public string VerificationStatusBadge => Record is null ? "加载中"
        : Record.BackupDeletionInProgress ? "备份处理中"
        : Record.Stage != MigrationStage.AwaitingUserVerification ? $"待恢复：{Record.Stage}"
        : Record.UserVerifiedAtUtc is null ? "待用户验证" : "用户已验证";
    public string BackupStatusBadge => Record is null ? "读取中" : _displayTask.BackupState switch
    {
        BackupState.RetainedUnverified => "备份保留",
        BackupState.UserVerified => "备份保留",
        BackupState.DeleteRequested => "待双确认",
        BackupState.Recycled => "已移入回收站",
        BackupState.Deleted => "已永久删除",
        BackupState.PartiallyDeleted => "部分删除",
        BackupState.Missing => "备份缺失",
        BackupState.IdentityChanged => "身份已变化",
        BackupState.DeleteFailed => "处理未完成",
        _ => Record.BackupPath is null ? "无原始备份" : "候选备份"
    };
    public bool IsProgramCheckPassed => Record?.VerificationSummary?.IsMatch == true;
    public string ProgramCheckShortText => Record?.VerificationSummary is not { } summary
        ? "无核对记录"
        : $"{(summary.Mode == VerificationMode.Basic ? "基础项" : "SHA-256")} · {(summary.IsMatch ? "通过" : $"未通过（{summary.Mismatches.Length} 项）")}";
    public string VerifiedTimeShortText => Record is null ? "加载中"
        : Record.UserVerifiedAtUtc is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "尚未确认";
    public string ModeText => Record is null ? "正在加载" : $"操作：{Record.Mode switch { MigrationMode.Migrate => "迁移", MigrationMode.Restore => "还原", MigrationMode.Repair => "修复", _ => "未知操作" }}\n事务编号：{Record.TransactionId}";
    public string VerificationStatus => Record is null ? "尚未加载事务" :
        $"{_displayTask.TransactionStatusText}\n{_displayTask.BackupStatusText}"
        + (Record.LastError is null ? "" : $"\n记录错误：{Record.LastError}");
    public string VerificationSummaryText => Record?.VerificationSummary is not { } summary
        ? "没有已保存的核对结果，不能据此判断一致。"
        : $"程序核对：{summary.Description}\n结果：{(summary.IsMatch ? "通过（不代表所属软件可正常使用）" : "未通过")}"
          + (summary.Mismatches.Length == 0 ? "" : "\n" + string.Join(Environment.NewLine, summary.Mismatches));
    public string VerificationSummaryTooltip => VerificationSummaryText
        + "\n\n程序核对与实际使用验证是两个步骤；完整内容哈希也不能替代所属软件的实际使用验证。";
    public string RecordTimes => Record is null ? "" : $"记录更新时间：{FormatTime(Record.UpdatedAtUtc)}\n用户确认时间：{(Record.UserVerifiedAtUtc is { } time ? FormatTime(time) : "未确认")}";
    public string RecordMetaTooltip => Record is null ? "正在加载事务记录" : $"{ModeText}\n{VerificationStatus}\n{RecordTimes}";
    public bool CanAcknowledge => !IsBusy && Record is { UserVerifiedAtUtc: null,
        Stage: MigrationStage.AwaitingUserVerification, BackupDeletionInProgress: false, VerificationSummary.IsMatch: true };
    public bool CanManageBackup => !IsBusy && BackupModeIndex is 1 or 2 && Record is {
        Stage: MigrationStage.AwaitingUserVerification, UserVerifiedAtUtc: not null, BackupPath: not null,
        BackupIdentity: not null, BackupManifestDigest: not null, BackupDeletionInProgress: false }
        && Record.BackupState is BackupState.UserVerified or BackupState.DeleteRequested or BackupState.DeleteFailed;
    public bool IsRestoreMode => Record?.Mode == MigrationMode.Restore;
    public bool IsRestoreTargetBackup => Record is not null && BackupManagementService.IsRestoreTargetBackup(Record);
    public string ActiveDataMetricCaption => IsRestoreMode ? "已还原目录（当前） ⓘ" : "目标目录（当前） ⓘ";
    public string ActiveDataDetailTitle => IsRestoreMode ? "当前已还原目录属性与统计（非已保存核对）" : "当前目标目录属性与统计（非已保存核对）";
    public string TargetPathLabel => IsRestoreTargetBackup ? "原目标位置" : "目标路径";
    public string TargetPathTooltip => IsRestoreTargetBackup
        ? "原目标位置：还原前的数据目录，现已转为下方还原备份"
        : "目标路径：迁移后的实际数据目录";
    public string BackupPathLabel => IsRestoreMode ? "还原备份" : "原始备份";
    public string BackupPathTooltip => IsRestoreMode
        ? "还原前目标目录备份：验证原路径可正常使用后再按需处理"
        : "原始备份路径：迁移前的原目录备份（查看它不代表迁移已验证）";
    public bool CanOpenPaths => !IsBusy && Record is not null;
    public bool CanOpenTargetPath => CanOpenPaths && (!IsRestoreTargetBackup || Directory.Exists(TargetPath));
    public bool CanOpenBackupPath => CanOpenPaths && Record is { BackupPath: not null }
        && Record.BackupState is not (BackupState.Deleted or BackupState.Recycled or BackupState.Missing);
    partial void OnIsBusyChanged(bool value) => NotifyAvailability();
    partial void OnBackupModeIndexChanged(int value) => OnPropertyChanged(nameof(CanManageBackup));

    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(CanAcknowledge));
        OnPropertyChanged(nameof(CanManageBackup));
        OnPropertyChanged(nameof(CanOpenPaths));
        OnPropertyChanged(nameof(CanOpenTargetPath));
        OnPropertyChanged(nameof(CanOpenBackupPath));
    }

    public async Task ReloadRecordAsync()
    {
        Record = await Task.Run(() => _store.LoadAsync(transactionId))
            ?? throw new IOException("迁移事务记录不存在；不能确认验证或处理备份。");
        _displayTask.Transaction = Record;
        _displayTask.BackupPath = Record.BackupPath;
        if (Record.UserVerifiedAtUtc is not null) Acknowledged = true;
        foreach (string property in new[]
        {
            nameof(Name), nameof(SourcePath), nameof(TargetPath), nameof(BackupPath), nameof(TransactionIdText),
            nameof(OperationBadgeText), nameof(IsUserVerified), nameof(IsVerificationError),
            nameof(IsRestoreMode), nameof(IsRestoreTargetBackup), nameof(ActiveDataMetricCaption),
            nameof(ActiveDataDetailTitle), nameof(TargetPathLabel), nameof(TargetPathTooltip),
            nameof(BackupPathLabel), nameof(BackupPathTooltip),
            nameof(VerificationStatusBadge), nameof(BackupStatusBadge), nameof(IsProgramCheckPassed),
            nameof(ProgramCheckShortText), nameof(VerifiedTimeShortText), nameof(ModeText),
            nameof(VerificationStatus), nameof(VerificationSummaryText), nameof(VerificationSummaryTooltip),
            nameof(RecordTimes), nameof(RecordMetaTooltip)
        })
            OnPropertyChanged(property);
        ConfirmVerificationCommand.NotifyCanExecuteChanged();
        NotifyAvailability();
        if (Record.LastDeletionResult is { } result) ShowDeletionResult(result);
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        ErrorText = "";
        try
        {
            await ReloadRecordAsync();
            var record = Record!;
            string activeDataPath = record.Mode == MigrationMode.Restore ? record.SourcePath : record.TargetPath;
            var inspection = await Task.Run(() => (Paths: InspectPaths(record), Properties: InspectTarget(activeDataPath, record.Mode == MigrationMode.Restore)));
            CurrentPathStatus = inspection.Paths.Detail;
            PathHealthShortText = inspection.Paths.Summary;
            IsPathHealthOk = inspection.Paths.IsHealthy;
            TargetProperties = inspection.Properties.Detail;
            TargetStatsShortText = inspection.Properties.Summary;
            IsTargetStatsOk = inspection.Properties.IsHealthy;
        }
        catch (Exception ex) { ErrorText = "读取迁移信息失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    private bool CanConfirmVerification() => CanAcknowledge && Acknowledged;

    [RelayCommand(CanExecute = nameof(CanConfirmVerification))]
    private async Task ConfirmVerificationAsync()
    {
        IsBusy = true;
        ErrorText = "";
        try
        {
            await ReloadRecordAsync();
            if (Record!.UserVerifiedAtUtc is not null) return;
            if (Record.Stage != MigrationStage.AwaitingUserVerification)
                throw new IOException("当前事务不处于等待用户验证阶段，不能确认。");
            await Task.Run(() => new BackupManagementService().MarkUserVerifiedAsync(transactionId));
            await ReloadRecordAsync();
        }
        catch (Exception ex) { ErrorText = "确认验证失败：" + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenPath(string? kind)
    {
        if (!CanOpenPaths) return;
        try
        {
            string path = kind switch { "source" => SourcePath, "target" => TargetPath, "backup" => Record?.BackupPath ?? throw new IOException("未记录原始备份路径。"), _ => throw new IOException("未知路径类型。") };
            MigrationRuntime.AssertDataPath(path, allowLink: kind == "source");
            _ = new PathIdentityService().ReadDirectory(path, followLink: true);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ErrorText = "打开目录失败：" + ex.Message; }
    }

    private const int MaxInlinePreviewItems = 50;

    public BackupDeletionResult? CurrentDeletionResult { get; private set; }
    public bool HasDeletionResultItems => CurrentDeletionResult is not null;

    public void ShowDeletionCancelled()
    {
        CurrentDeletionResult = null;
        OnPropertyChanged(nameof(HasDeletionResultItems));
        DeletionResultText = "已取消处理备份，未执行删除；已记录的确认及备份保持原状。";
        DeletionFeedbackShortText = "已取消处理备份，原备份保持原状。";
        HasDeletionFeedback = true;
    }

    public void ShowDeletionResult(BackupDeletionResult result)
    {
        CurrentDeletionResult = result;
        OnPropertyChanged(nameof(HasDeletionResultItems));

        var text = new StringBuilder($"实际处理结果：{result.Status}；完成时间：{(result.CompletedAtUtc is { } time ? FormatTime(time) : "未完成")}\n");
        if (result.Error is not null) text.AppendLine("错误：" + result.Error);
        text.AppendLine($"逐项结果共 {result.Items.Length} 项：");

        int previewCount = Math.Min(result.Items.Length, MaxInlinePreviewItems);
        for (int i = 0; i < previewCount; i++)
        {
            var item = result.Items[i];
            text.AppendLine($"{item.Outcome}：{item.Path}{(item.Error is null ? "" : "；" + item.Error)}");
        }
        if (result.Items.Length > MaxInlinePreviewItems)
            text.AppendLine($"...（其余 {result.Items.Length - MaxInlinePreviewItems} 项已省略，请点击「查看明细…」在虚拟化弹窗中浏览与筛选）");

        DeletionResultText = text.ToString();
        string statusText = result.Status switch
        {
            BackupDeletionStatus.Deleted => "已永久删除",
            BackupDeletionStatus.Recycled => "已移入回收站",
            BackupDeletionStatus.Failed => "处理失败",
            BackupDeletionStatus.Partial => "部分处理",
            BackupDeletionStatus.Unsupported => "当前不支持",
            _ => result.Status.ToString()
        };
        DeletionFeedbackShortText = $"最近处理：{statusText}（共 {result.Items.Length} 项）"
            + (result.Error is null ? "" : $" · {result.Error}");
        HasDeletionFeedback = true;
    }

    private static (string Detail, string Summary, bool IsHealthy) InspectPaths(MigrationTransactionRecord record)
    {
        var text = new StringBuilder();
        bool hasIssue = false;
        bool restoreTargetBackup = BackupManagementService.IsRestoreTargetBackup(record);
        var checks = new List<(string Label, string Path, DirectoryIdentity? Expected)>
        {
            (record.Mode == MigrationMode.Restore ? "原路径（已还原）" : "原路径",
                record.SourcePath,
                record.Mode == MigrationMode.Restore ? record.StagingIdentity : record.LinkIdentity)
        };
        if (restoreTargetBackup)
        {
            if (record.BackupState is BackupState.Deleted or BackupState.Recycled)
            {
                text.AppendLine($"还原备份（原目标）已处理：{(record.BackupState == BackupState.Recycled ? "已移入回收站" : "已永久删除")}");
            }
            else if (record.BackupPath is not null)
            {
                checks.Add(("还原备份（原目标）", record.BackupPath, record.BackupIdentity));
            }
        }
        else
        {
            checks.Add(("目标", record.TargetPath, record.TargetIdentity));
        }

        foreach (var (label, path, expected) in checks)
        {
            try
            {
                MigrationRuntime.AssertDataPath(path, allowLink: true);
                var service = new PathIdentityService();
                var identity = service.ReadEntry(path, followLink: false);
                string kind = identity.IsReparsePoint ? "重解析点/链接" : identity.IsDirectory ? "普通目录" : "文件（非目录）";
                bool identityMatch = expected is null || identity == expected;
                if (!identityMatch) hasIssue = true;
                text.AppendLine($"{label}当前为{kind}；与已记录身份：{(expected is null ? "无可比较记录" : identityMatch ? "相符" : "不相符")}");
                if (identity.IsReparsePoint)
                {
                    var resolved = new DirectoryInfo(path).ResolveLinkTarget(true);
                    text.AppendLine($"实际解析到：{resolved?.FullName ?? "无法解析"}");
                    if (resolved is null) hasIssue = true;
                    bool targetMatch = record.TargetIdentity is null || service.ReadDirectory(path, true) == record.TargetIdentity;
                    if (!targetMatch) hasIssue = true;
                    text.AppendLine($"链接最终身份与记录目标：{(record.TargetIdentity is null ? "无记录" : targetMatch ? "相符" : "不相符")}");
                }
            }
            catch (Exception ex)
            {
                hasIssue = true;
                text.AppendLine($"{label}实际状态读取失败：{ex.Message}");
            }
        }
        string summary = hasIssue
            ? "状态存在异常"
            : record.Mode != MigrationMode.Restore
                ? "链接与目标正常"
                : restoreTargetBackup && record.BackupState is BackupState.Deleted or BackupState.Recycled
                    ? "原目录已还原正常"
                    : "原目录与备份正常";
        return (text.ToString().TrimEnd(), summary, !hasIssue);
    }

    private static (string Detail, string Summary, bool IsHealthy) InspectTarget(string path, bool isRestoredSource)
    {
        string subject = isRestoredSource ? "当前已还原目录" : "当前目标目录";
        var errors = new List<string>();
        long files = 0, directories = 0, bytes = 0, skipped = 0;
        string attributes;
        var identity = new PathIdentityService();
        try
        {
            MigrationRuntime.AssertDataPath(path);
            if (identity.ReadDirectory(path, false).IsReparsePoint)
                throw new IOException($"{subject}已变为重解析点，未跟随统计。");
            var root = new DirectoryInfo(path);
            attributes = $"创建：{root.CreationTime:yyyy-MM-dd HH:mm:ss}；修改：{root.LastWriteTime:yyyy-MM-dd HH:mm:ss}\n目录属性：{root.Attributes}";
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.TryPop(out string? current))
            {
                try
                {
                    MigrationRuntime.AssertDataPath(current);
                    if (identity.ReadDirectory(current, false).IsReparsePoint) { skipped++; continue; }
                    foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        try
                        {
                            var entryIdentity = identity.ReadEntry(entry, false);
                            if (entryIdentity.IsReparsePoint) { skipped++; continue; }
                            if (entryIdentity.IsDirectory) { directories++; pending.Push(entry); }
                            else { bytes = checked(bytes + new FileInfo(entry).Length); files++; }
                        }
                        catch (Exception ex) { errors.Add($"{entry}：{ex.Message}"); }
                    }
                }
                catch (Exception ex) { errors.Add($"{current}：{ex.Message}"); }
            }
        }
        catch (Exception ex) { return ($"{subject}属性/统计读取失败：" + ex.Message, "统计读取失败", false); }
        string detail = $"{attributes}\n当前文件数：{files:N0}；子目录数：{directories:N0}；逻辑大小：{bytes:N0} 字节\n跳过重解析点：{skipped:N0}（不跟随）；统计时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n"
            + (errors.Count == 0 ? "统计完成。" : $"部分统计失败，共 {errors.Count} 项；以上仅为已读取部分：\n" + string.Join(Environment.NewLine, errors))
            + $"\n这是{subject}属性与统计，不是已保存的核对结果，也不代表内容一致或所属软件运行正常。";
        string summary = $"{files:N0} 文件 · {directories:N0} 目录 · {FormatBytes(bytes)}"
            + (errors.Count > 0 ? $"（{errors.Count} 项失败）" : "");
        return (detail, summary, errors.Count == 0);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{bytes:N0} B" : $"{value:0.##} {units[unitIndex]}";
    }

    private static string FormatTime(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
}
