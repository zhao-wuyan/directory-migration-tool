using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Services;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class BackupConfirmationViewModel(MigrationTransactionRecord record, BackupDeletionRequest request, ConfirmationStep step) : ObservableObject
{
    private Window? _window;
    private bool IsRestoreTargetBackup => BackupManagementService.IsRestoreTargetBackup(record);
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _confirmationText = string.Empty;
    public string Title => step == ConfirmationStep.First ? "第一次确认：处理原始备份" : "第二次确认：处理原始备份";
    public string DialogId => step == ConfirmationStep.First ? "BackupConfirmationFirst" : "BackupConfirmationSecond";
    public string BackupPathLabel => IsRestoreTargetBackup ? "还原前目标备份（本次处理对象）" : "原始备份（本次处理对象）";
    public string SourcePathLabel => IsRestoreTargetBackup ? "原路径数据（已还原，保留）" : "源地址";
    public string TargetPathLabel => IsRestoreTargetBackup ? "原目标位置（已转为上方备份）" : "目标数据（保留）";
    public string BackupPath => record.BackupPath!;
    public string SourcePath => record.SourcePath;
    public string TargetPath => record.TargetPath;
    public string VerificationText => record.VerificationSummary?.Description ?? "核对结果不可用";
    public string ModeText => request.Mode == BackupDeletionMode.Permanent ? "永久删除：不可恢复" : "移入回收站：过大或无法回收时保留备份，不永久删除";
    public bool NeedsName => step == ConfirmationStep.Second && request.Mode == BackupDeletionMode.Permanent;
    public string ExpectedName => BackupManagementService.ExpectedConfirmationName(record);
    public string ConfirmText => step == ConfirmationStep.First ? "确认并进入第二次确认（尚不删除）" : "确认处理此原始备份";
    public void SetWindow(Window window) => _window = window;
    private bool CanConfirm() => !NeedsName || ConfirmationText == ExpectedName;
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() { if (_window is not null) _window.DialogResult = true; }
    [RelayCommand]
    private void Cancel() { if (_window is not null) _window.DialogResult = false; }
}
