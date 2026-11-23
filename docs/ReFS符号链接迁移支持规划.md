# ReFS 符号链接迁移支持规划（暂缓实施）

> 状态：**规划已记录，暂不实施**  
> 前置基础：梯度二并发低占用目录扫描（`DirectoryWalkScanner`，基于 `GetFileInformationByHandleEx(FileFullDirectoryInfo)` + 介质自适应并发 + `THREAD_MODE_BACKGROUND_BEGIN` 后台低 I/O 优先级）已完成，已具备 ReFS / Win11 Dev Drive 的高速只读扫描与真实 `AllocationSize` / `ReparseTag` 采集能力。

---

## 1. 当前边界与现状

当前版本出于安全基线保守原则，仅允许在**本地固定 NTFS 卷**之间执行符号链接迁移；ReFS 卷（含 Windows 11 Dev Drive）目前定位为**高速低占用只读浏览**，禁止直接作为迁移源盘或目标盘。

---

## 2. 未来放开 ReFS 迁移时的代码改造清单

正式启动 ReFS 迁移支持时，需要联动修改以下 3 个核心触点：

### 2.1 解耦“MFT 扫描能力”与“符号链接迁移能力”
- **涉及文件**：
  - `MigrationCore/Models/Scanning/ScanModels.cs`（`ScanVolumeInfo`）
  - `MigrationCore/Services/Scanning/DiskScanService.cs`（`ListVolumes()`）
  - `MoveWithSymlinkWPF/ViewModels/DiskScanViewModel.cs`（`VolumeCardItem` 徽章、推荐目标盘与文案）
  - `MigrationCore/Services/Scanning/ScanSnapshotDevCache.cs`（若变更 `ScanVolumeInfo` 字段需同步提升缓存 `FormatVersion`）
- **改动目标**：
  - 拆分 `SupportsRawMft`（仅代表能否走 NTFS 原始 MFT 主路径）与 `SupportsSymlinkMigration`（代表该卷是否允许作为符号链接迁移的源/目标盘，例如本地固定 `NTFS` 或 `ReFS`）。
  - UI 上区分展示“扫描方式（MFT 高速 / 低占用并发扫描）”与“迁移支持状态”，不再因 ReFS 没有 MFT 就标成“仅只读浏览”。
  - 仍需严守架构约束：**普通并发遍历绝不得冒充 NTFS MFT 主路径**。

### 2.2 放宽 L0 卷准入策略
- **涉及文件**：
  - `MigrationCore/Services/Safety/MigrationSafetyPolicy.cs`（`EvaluateTargetPath` 及相关卷能力校验，当前第 551–572 行）
- **改动目标**：
  - 将本地固定卷白名单从仅 `NTFS` 扩展为 `NTFS` 与 `ReFS`（依然拒绝 `exFAT`、`FAT32`、网络映射盘、可移动介质等不支持稳定符号链接/重解析点语义的卷）。

### 2.3 扫描快照规划准入与执行前实时复核
- **涉及文件**：
  - `MigrationCore/Models/Scanning/ScanModels.cs`（`ScanSnapshot.CanUseForMigrationPlanning`）
  - `MigrationCore/Services/MigrationPlanService.cs` 及执行前实时复核链路
- **改动目标**：
  - 确认完整的 ReFS `DirectoryWalkFallback` 快照允许进入迁移规划阶段，同时强制保持 `RequiresLiveReverification = true`，在真正复制与切换符号链接前逐项实时复核源/目标路径状态、权限与句柄占用。

---

## 3. ReFS 特有安全风险与实施前必评项

放开前必须逐项验证以下 ReFS 与 NTFS 的架构差异：

1. **硬链接闭环检查（无 MFT 拓扑）**
   - ReFS 不走原始 MFT，目录遍历阶段无法获知全卷硬链接拓扑（`LinkCount` 默认记为 1）。
   - 迁移执行前实时复核必须通过 Win32 `GetFileInformationByHandle` / `FileStandardInfo` 检查 `NumberOfLinks > 1`；若发现无法证明完全闭环于迁移子树内部的硬链接，按 `SubtreeHardLinkKind.UnverifiedExternal` 保守拦截，防止跨卷迁移导致空间膨胀或破坏共享语义。
2. **128-bit File ID 与卷身份识别**
   - ReFS 使用 128 位 `FILE_ID_128`（NTFS 为 64 位）。
   - 需核查 `PathIdentityService`（`VolumeGuid`、`VolumeSerial`、文件/目录身份比对）在 ReFS 卷上读取是否稳定无截断。
3. **块克隆（Block Cloning / CoW）与空间释放预期**
   - ReFS 支持同卷写时分配块克隆，多个文件可共享物理数据块；目录内文件移出源卷后，若源卷其他目录仍引用共享块，源盘实际释放空间可能小于该目录的 `AllocatedBytes`。
   - 需评估是否在 ReFS 源盘提示“因 ReFS 块克隆特性，源盘实际释放空间以文件系统回收为准”，同时目标盘空间校验仍按完整复制大小保守计算。
4. **符号链接、重命名快检与 ACL 复制兼容性**
   - 验证 `CreateSymbolicLink`、`mklink /D`、目录重命名快检（`Directory.Move`）、占用句柄查询、以及 `Robocopy` 复制参数在以下 3 种组合中的行为：
     - `NTFS → ReFS`
     - `ReFS → NTFS`
     - `ReFS → ReFS`
   - 特别留意 Windows 11 Dev Drive（ReFS）与防病毒过滤驱动在重命名和句柄检测时的差异。

---

## 4. 验收与回归测试基线

未来正式实现后，建议在 `test/MigrationSafetyHarness` 中基于真实 VHDX 补齐以下自动化验证再合入主干：
- `NTFS ↔ ReFS` 与 `ReFS ↔ ReFS` 双向迁移、完整性核对、原位备份与可逆回滚测试；
- ReFS 目录包含硬链接、目录联接（Junction）、符号链接、稀疏文件与无权访问子目录时的拦截与告警测试；
- 大目录并发扫描与复制期间的磁盘负载观测，确认后台低 I/O 优先级下不打满磁盘影响前台交互。
