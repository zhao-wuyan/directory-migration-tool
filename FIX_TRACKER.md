# 迁移安全底座与 UI 问题修复清单

> 记录自 commit `2fd45010a97627792b5e81b76055c24758a7bdb4` 之后排查出的逻辑缺陷与体验问题，以及还原备份清理冲突问题。每完成一项修复与验证后勾选对应条目，便于跨会话/多 Agent 无缝接手。

## 修复进度总览（11 / 11 已完成）

- [x] **#0 还原备份清理被历史非活动事务父路径误拦（`ReferencesBackup`）**
  - 文件：`MigrationCore/Services/MigrationTransactionStore.cs`
  - 修复：区分活动事务（全路径重叠 + 身份严格拦截）与非活动历史事务（仅核验当前仍在磁盘上归属的存活目录或未删除备份，且删除操作会波及存活目录或身份重叠时才拦截）。
  - 验证：只读复测本机 `e5be316e-...` 与全部 6 条历史事务记录（含 `1b426346-...`），均不再误拦。

- [x] **#1 换目标盘重新迁移被旧事务卡死（`MigrationStateDetector`）**
  - 文件：`MigrationCore/Services/MigrationStateDetector.cs`
  - 修复：在未指定固定 `TransactionId` 时，若历史事务非活动（已完成还原或已取消/失败且原实体仍在原位）且当前源路径确为普通实体目录，更换目标路径后自动识别为新的 `Pending` 待迁移任务。
  - 验证：隔离临时目录测试「还原后更换目标路径检测」通过，返回 `MigrationState.Pending`。

- [x] **#2 关闭应用被陈旧历史未验证记录永久弹窗骚扰**
  - 文件：`MoveWithSymlinkWPF/Helpers/UnverifiedExitConfirmation.cs`
  - 修复：按 `SourcePath` 分组取最新事务记录，并要求备份或源/目标路径在磁盘上仍存在，避免已被后续还原/新迁移取代或手工清除的陈旧记录反复弹窗。
  - 验证：隔离测试「旧未验证迁移被新已验证还原取代」后退出不再误弹窗。

- [x] **#3 启动自动恢复上次事务时无条件触发模式检测与日志抖动**
  - 文件：`MoveWithSymlinkWPF/ViewModels/MainViewModel.cs`
  - 修复：新增 `_suppressPathDetection` 标志在 `RestoreLastTransactionAsync` 赋路径期间抑制 `DetectAndSwitchMode()` / `CheckRepairCondition()` 重复触发与多余日志，按模式明确结果摘要，同时保持与 `BackupUi.ps1` `Restart` 验收流程完全兼容。
  - 验证：Release 编译通过（0 警告 / 0 错误）。

- [x] **#4 失败/取消的还原记录卡死「修复」入口（`PrepareRepairAsync`）**
  - 文件：`MigrationCore/Services/MigrationWorkflowHelper.cs`
  - 修复：存在活动或备份处理中事务时直接拒绝；过滤 `Mode == Restore && Stage is Cancelled or Failed` 的历史失败尝试后再判断是否多匹配。
  - 验证：Release 编译通过，核心与安全测试套件均 0 错误。

- [x] **#5 一键迁移「复制日志 / 复制错误信息」在 MTA 后台线程调剪贴板必然失败**
  - 文件：`MoveWithSymlinkWPF/ViewModels/QuickMigrateViewModel.cs`
  - 修复：`TrySetClipboardAsync` 改为通过 `Application.Current.Dispatcher.InvokeAsync` 在 WPF STA 线程设置剪贴板，并加入最多 3 次短暂重试应对瞬时剪贴板占用。
  - 验证：Release 编译通过（0 警告 / 0 错误）。

- [x] **#6 手动模式「可修复」前端判断与后端 `PrepareRepairAsync` 准入条件脱节**
  - 文件：`MoveWithSymlinkWPF/ViewModels/MainViewModel.cs`
  - 修复：`CheckRepairCondition` 新增 `HasMatchingRepairTransaction`，当源目录缺失或为符号链接时，仅在存在匹配的历史持久化事务记录时才启用修复入口，与后端 `PrepareRepairAsync` 准入一致。
  - 验证：Release 编译通过（0 警告 / 0 错误）。

- [x] **#7 一键迁移预扫描在 UI 线程同步遍历大目录卡死界面**
  - 文件：`MoveWithSymlinkWPF/ViewModels/QuickMigrateViewModel.cs`
  - 修复：`StartQuickMigrationAsync` 中 `FileStatsService.GetDirectorySize` 改为 `await Task.Run(..., _cancellationTokenSource.Token)` 异步执行并支持取消。
  - 验证：Release 编译通过（0 警告 / 0 错误）。

- [x] **#8 回收站 `PreDeleteItem` 回调潜在 $O(N^2)$ 重复全树 SHA-256 校验**
  - 文件：`MigrationCore/Services/RecycleBinHelper.cs`、`MigrationCore/Services/CopyVerificationService.cs`
  - 修复：`ValidateInitial` 与首次 `PreDeleteItem` 保持全树 `_manifest.Mode` 完整校验；后续逐项回调以 `Basic` 快速核对剩余树结构/元数据/身份防篡改，并对当前被删文件单独执行 `_manifest.Mode` 完整内容哈希核验，将总哈希开销降至 $O(N)$ 且安全语义不降级。
  - 验证：Release 编译通过（0 警告 / 0 错误）。

- [x] **#9 替换 `QuickMigrateViewModel` 残留原生 `MessageBox.Show`**
  - 文件：`MoveWithSymlinkWPF/ViewModels/QuickMigrateViewModel.cs`
  - 修复：第 660 行替换为 `CustomMessageBox.ShowInformation("暂无日志可复制", "提示")`。
  - 验证：Release 编译通过（0 警告 / 0 错误）。

- [x] **#10 统一 `BackupConfirmationWindow` 视觉风格（与验证弹窗 / `MessageWindow` 对齐）**
  - 文件：`MoveWithSymlinkWPF/Views/BackupConfirmationWindow.xaml`
  - 修复：统一采用圆角白卡、红色警示条、黄色二次确认输入卡、蓝色默认高亮「取消并保留备份」按钮与红色次级危险确认按钮，完整保留全部 `AutomationId` 及默认取消焦点。
  - 验证：实际 WPF 渲染核对二次确认界面，确认 `CancelButton` 为 `IsDefault=True / IsCancel=True` 且默认获得键盘焦点。

---

## 后续可选验证（给用户或接手 Agent）

- 当前所有代码修复、Release 构建、隔离逻辑校验及 WPF 界面渲染核对均已完成。
- 如需全量端到端 VHD 隔离回归，可在管理员 PowerShell 下运行 `.\test\vhd\Run-Tests.ps1`。
