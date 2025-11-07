# 迁移服务重构说明

## 重构目标

将 `MigrationService`（手动模式）和 `ReversibleMigrationService`（一键迁移模式）中的重复代码抽取为公共组件，提高代码复用性和可维护性，同时保持两种模式的行为完全一致。

## 重构内容

### 1. CopyOperationExecutor - 复制进度执行器

**文件位置**: `MigrationCore/Services/CopyOperationExecutor.cs`

**功能**: 封装文件复制操作的完整流程，包括：
- Robocopy 进程启动与参数配置
- 实时解析 robocopy 百分比输出
- 目录大小监控与增量计算
- 指数移动平均速度平滑（平滑系数 0.3）
- 10%-90% 进度映射到总流程
- 取消操作与进程树终止

**重构前**: 该逻辑在两个服务中各有一份完全相同的副本（约 350 行代码）

**重构后**: 抽取为独立类，两个服务通过实例化该类来执行复制操作

**使用示例**:
```csharp
var executor = new CopyOperationExecutor(
    sourceDir,
    targetDir,
    stats,
    robocopyThreads,
    sampleMilliseconds,
    actionName: "复制");

await executor.ExecuteAsync(progress, logProgress, cancellationToken);
```

### 2. MigrationWorkflowHelper - 迁移流程帮助类

**文件位置**: `MigrationCore/Services/MigrationWorkflowHelper.cs`

**功能**: 提供迁移流程中的通用静态方法：

#### 2.1 CreateSymbolicLinkWithBackupAsync
- 备份源目录（时间戳命名）
- 创建符号链接（P/Invoke 优先，失败则回退到 cmd mklink）
- 验证符号链接可访问性
- 返回备份路径供后续清理或回滚

#### 2.2 VerifySymbolicLinkAsync
- 验证路径是符号链接
- 验证符号链接可访问

#### 2.3 CleanupBackupAsync
- 删除备份目录
- 异常处理并记录日志

#### 2.4 RollbackMigrationAsync
- 删除符号链接
- 恢复备份目录
- 清理迁移标记文件

#### 2.5 ScanDirectoryAsync & ReportScanResults
- 目录扫描并返回统计信息
- 格式化输出扫描结果

**重构前**: 这些方法在两个服务中分别实现，逻辑相同但独立维护

**重构后**: 抽取为静态方法，确保两个服务使用完全一致的逻辑

## 重构影响分析

### 对外接口 - 无变化
- `MigrationService.ExecuteMigrationAsync()` 签名和行为不变
- `ReversibleMigrationService.ExecuteAsync()` 签名和行为不变
- 所有进度回调、日志输出保持一致

### 内部实现 - 简化
- `MigrationService`: 减少约 400 行代码
- `ReversibleMigrationService`: 减少约 400 行代码
- 新增 `CopyOperationExecutor`: 约 400 行
- 新增 `MigrationWorkflowHelper`: 约 180 行

**净代码量**: 减少约 220 行（去除重复）

### 测试验证要点

#### 手动模式（MigrationService）
1. ✅ 6 阶段流程完整执行
2. ✅ 进度百分比映射正确（0% → 5% → 10%-90% → 93% → 96% → 100%）
3. ✅ 复制速度平滑显示
4. ✅ 符号链接创建与验证
5. ✅ 备份清理
6. ✅ 异常回滚

#### 一键迁移模式（ReversibleMigrationService）
1. ✅ 迁移模式与手动模式行为一致
2. ✅ 还原模式正常工作
3. ✅ 多任务串行执行进度独立
4. ✅ 取消操作正常响应

## 代码复用关系图

```
┌─────────────────────────┐
│  MigrationService       │
│  (手动模式)              │
└───────┬─────────────────┘
        │
        ├─── uses ──→ CopyOperationExecutor
        │              (复制进度逻辑)
        │
        └─── uses ──→ MigrationWorkflowHelper
                       (符号链接、备份、扫描)
                              ↑
                              │ uses
                              │
┌─────────────────────────────┴───┐
│  ReversibleMigrationService     │
│  (一键迁移模式)                  │
└─────────────────────────────────┘
```

## 后续维护建议

1. **复制逻辑修改**: 统一在 `CopyOperationExecutor` 中修改
2. **符号链接逻辑修改**: 统一在 `MigrationWorkflowHelper` 中修改
3. **新增通用流程**: 考虑添加到 `MigrationWorkflowHelper`
4. **测试**: 修改后需同时测试手动模式和一键迁移模式

## 重构日期

2025-11-07

