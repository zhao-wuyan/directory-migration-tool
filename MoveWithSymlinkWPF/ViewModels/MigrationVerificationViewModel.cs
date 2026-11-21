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
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _currentPathStatus = "正在读取实际路径状态……";
    [ObservableProperty] private string _targetProperties = "正在统计当前目标目录……";
    [ObservableProperty] private string _deletionResultText = "尚未处理备份；选择方式本身不会删除，仍需主动进入双确认。";
    [ObservableProperty] private int _backupModeIndex = 1;
    public string Name => Record is null ? "迁移验证" : Path.GetFileName(Path.TrimEndingDirectorySeparator(Record.SourcePath));
    public string SourcePath => Record?.SourcePath ?? "";
    public string TargetPath => Record?.TargetPath ?? "";
    public string BackupPath => Record?.BackupPath ?? "未记录原始备份";
    public string ModeText => Record is null ? "正在加载" : $"操作：{Record.Mode switch { MigrationMode.Migrate => "迁移", MigrationMode.Restore => "还原", MigrationMode.Repair => "修复", _ => "未知操作" }}\n事务编号：{Record.TransactionId}";
    public string VerificationStatus => Record is null ? "尚未加载事务" :
        $"{_displayTask.TransactionStatusText}\n{_displayTask.BackupStatusText}"
        + (Record.LastError is null ? "" : $"\n记录错误：{Record.LastError}");
    public string VerificationSummaryText => Record?.VerificationSummary is not { } summary
        ? "没有已保存的核对结果，不能据此判断一致。"
        : $"程序核对：{summary.Description}\n结果：{(summary.IsMatch ? "通过（不代表所属软件可正常使用）" : "未通过")}\n{string.Join(Environment.NewLine, summary.Mismatches)}";
    public string RecordTimes => Record is null ? "" : $"记录更新时间：{FormatTime(Record.UpdatedAtUtc)}\n用户确认时间：{(Record.UserVerifiedAtUtc is { } time ? FormatTime(time) : "未确认")}";
    public bool CanAcknowledge => !IsBusy && Record is { UserVerifiedAtUtc: null,
        Stage: MigrationStage.AwaitingUserVerification, BackupDeletionInProgress: false, VerificationSummary.IsMatch: true };
    public bool CanManageBackup => !IsBusy && BackupModeIndex is 1 or 2 && Record is {
        Stage: MigrationStage.AwaitingUserVerification, UserVerifiedAtUtc: not null, BackupPath: not null,
        BackupIdentity: not null, BackupManifestDigest: not null, BackupDeletionInProgress: false }
        && Record.BackupState is BackupState.UserVerified or BackupState.DeleteRequested or BackupState.DeleteFailed;
    public bool CanOpenPaths => !IsBusy && Record is not null;
    partial void OnIsBusyChanged(bool value) => NotifyAvailability();
    partial void OnBackupModeIndexChanged(int value) => OnPropertyChanged(nameof(CanManageBackup));

    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(CanAcknowledge));
        OnPropertyChanged(nameof(CanManageBackup));
        OnPropertyChanged(nameof(CanOpenPaths));
    }

    public async Task ReloadRecordAsync()
    {
        Record = await Task.Run(() => _store.LoadAsync(transactionId))
            ?? throw new IOException("迁移事务记录不存在；不能确认验证或处理备份。");
        _displayTask.Transaction = Record;
        _displayTask.BackupPath = Record.BackupPath;
        foreach (string property in new[] { nameof(Name), nameof(SourcePath), nameof(TargetPath), nameof(BackupPath), nameof(ModeText), nameof(VerificationStatus), nameof(VerificationSummaryText), nameof(RecordTimes) })
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
            var inspection = await Task.Run(() => (Paths: InspectPaths(record), Properties: InspectTarget(record.TargetPath)));
            CurrentPathStatus = inspection.Paths;
            TargetProperties = inspection.Properties;
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

    public void ShowDeletionResult(BackupDeletionResult result)
    {
        var text = new StringBuilder($"实际处理结果：{result.Status}；完成时间：{(result.CompletedAtUtc is { } time ? FormatTime(time) : "未完成")}\n");
        if (result.Error is not null) text.AppendLine("错误：" + result.Error);
        text.AppendLine($"逐项结果共 {result.Items.Length} 项：");
        foreach (var item in result.Items)
            text.AppendLine($"{item.Outcome}：{item.Path}{(item.Error is null ? "" : "；" + item.Error)}");
        DeletionResultText = text.ToString();
    }

    private static string InspectPaths(MigrationTransactionRecord record)
    {
        var text = new StringBuilder();
        foreach (var (label, path, expected) in new[] { ("原路径", record.SourcePath, record.Mode == MigrationMode.Restore ? record.StagingIdentity : record.LinkIdentity), ("目标", record.TargetPath, record.TargetIdentity) })
        {
            try
            {
                MigrationRuntime.AssertDataPath(path, allowLink: true);
                var service = new PathIdentityService();
                var identity = service.ReadEntry(path, followLink: false);
                string kind = identity.IsReparsePoint ? "重解析点/链接" : identity.IsDirectory ? "普通目录" : "文件（非目录）";
                text.AppendLine($"{label}当前为{kind}；与已记录身份：{(expected is null ? "无可比较记录" : identity == expected ? "相符" : "不相符")}");
                if (identity.IsReparsePoint)
                {
                    var resolved = new DirectoryInfo(path).ResolveLinkTarget(true);
                    text.AppendLine($"实际解析到：{resolved?.FullName ?? "无法解析"}");
                    text.AppendLine($"链接最终身份与记录目标：{(record.TargetIdentity is null ? "无记录" : service.ReadDirectory(path, true) == record.TargetIdentity ? "相符" : "不相符")}");
                }
            }
            catch (Exception ex) { text.AppendLine($"{label}实际状态读取失败：{ex.Message}"); }
        }
        return text.ToString();
    }

    private static string InspectTarget(string path)
    {
        var errors = new List<string>();
        long files = 0, directories = 0, bytes = 0, skipped = 0;
        string attributes;
        var identity = new PathIdentityService();
        try
        {
            MigrationRuntime.AssertDataPath(path);
            if (identity.ReadDirectory(path, false).IsReparsePoint)
                throw new IOException("目标目录已变为重解析点，未跟随统计。");
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
        catch (Exception ex) { return "目标属性/统计读取失败：" + ex.Message; }
        return $"{attributes}\n当前文件数：{files:N0}；子目录数：{directories:N0}；逻辑大小：{bytes:N0} 字节\n跳过重解析点：{skipped:N0}（不跟随）；统计时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n"
            + (errors.Count == 0 ? "统计完成。" : $"部分统计失败，共 {errors.Count} 项；以上仅为已读取部分：\n" + string.Join(Environment.NewLine, errors))
            + "\n这是当前目标目录属性与统计，不是已保存的迁移核对，也不代表内容一致或所属软件运行正常。";
    }

    private static string FormatTime(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
}
