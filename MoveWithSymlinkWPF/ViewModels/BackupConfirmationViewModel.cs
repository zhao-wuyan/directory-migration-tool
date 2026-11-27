using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models;
using MigrationCore.Services;
using MoveWithSymlinkWPF.Helpers;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class BackupConfirmationViewModel(MigrationTransactionRecord record, BackupDeletionRequest request, ConfirmationStep step) : ObservableObject
{
    private Window? _window;
    private bool IsRestoreTargetBackup => BackupManagementService.IsRestoreTargetBackup(record);

    public BackupConfirmationPlan ConfirmationPlan { get; } = ConfirmationPolicy.EvaluateBackupConfirm(request.Mode, step);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTriggerConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isAcknowledged;

    /// <summary>
    /// 完成 UI 核对确认后自动签发给后端服务层的校验名称（免手打，后端安全契约保持不变）。
    /// </summary>
    public string ConfirmationText { get; private set; } = string.Empty;

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

    public bool NeedsName => ConfirmationPlan.RequiresCheckbox;
    public string ExpectedName => BackupManagementService.ExpectedConfirmationName(record);
    public string AcknowledgeText => $"我已核对「{ExpectedName}」，确认永久删除此冻结备份且不可恢复";
    public bool CanTriggerConfirm => !ConfirmationPlan.RequiresCheckbox || IsAcknowledged;
    public bool ConfirmRequiresHold => ConfirmationPlan.Tier != ConfirmationTier.Direct;
    public double ConfirmHoldSeconds => ConfirmationPlan.HoldSeconds > 0 ? ConfirmationPlan.HoldSeconds : ConfirmationPolicy.HighRiskHoldSeconds;

    public string ConfirmText => step == ConfirmationStep.First
        ? "确认并进入第二次确认（尚不删除）"
        : (ConfirmationPlan.RequiresCheckbox
            ? $"按住 {ConfirmHoldSeconds:0.0} 秒永久删除备份"
            : "确认移入回收站");

    public string ConfirmDisabledText => "请先勾选上方确认";

    public void SetWindow(Window window) => _window = window;

    private bool CanConfirm() => CanTriggerConfirm;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!CanTriggerConfirm) return;
        if (ConfirmationPlan.RequiresCheckbox)
        {
            ConfirmationText = ExpectedName;
        }
        if (_window is not null) _window.DialogResult = true;
    }

    [RelayCommand]
    private void Cancel()
    {
        ConfirmationText = string.Empty;
        if (_window is not null) _window.DialogResult = false;
    }
}
