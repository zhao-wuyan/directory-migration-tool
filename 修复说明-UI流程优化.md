# UI 流程优化说明

## 修改时间
2025-10-22

## 修改内容

### 问题描述
原有的 UI 流程需要优化，使操作更加流畅。

### 修改方案

#### 1. 步骤 1 - 选择路径
- **原设计**：有"下一步"按钮，点击后进入步骤 2
- **新设计**：改为"开始扫描"按钮，点击后直接验证并扫描，然后切换到步骤 2

#### 2. 步骤 2 - 扫描分析
- **原设计**：显示扫描结果，有"开始扫描"按钮
- **新设计**：
  - 移除"开始扫描"按钮（已移至步骤 1）
  - 添加"开始迁移"按钮（原本在步骤 3）
  - 点击"开始迁移"后切换到步骤 3 并开始迁移

#### 3. 步骤 3 - 执行迁移
- **原设计**：有"开始迁移"按钮和"查看结果"按钮
- **新设计**：
  - 移除"开始迁移"按钮（已移至步骤 2）
  - 保留"取消"按钮（迁移进行中时显示）
  - 改为"完成"按钮（迁移完成时显示），点击后进入步骤 4

#### 4. 步骤 4 - 完成
- **原设计**：有"完成"按钮（用于重置），显示完整的日志框
- **新设计**：
  - 改为"关闭"按钮，点击后关闭应用程序
  - 移除日志框（日志在步骤 3 已经可以查看）

### 技术实现

#### MainWindow.xaml 修改
1. 步骤 1：按钮绑定到 `StartScanFromStep1Command`
2. 步骤 2：按钮绑定到 `StartMigrationFromStep2Command`
3. 步骤 3：完成按钮绑定到 `ViewResultCommand`
4. 步骤 4：关闭按钮绑定到 `CloseApplicationCommand`，移除日志显示区域

#### MainViewModel.cs 修改
1. 新增 `StartScanFromStep1Command`：
   - 执行路径验证
   - 切换到步骤 2
   - 自动开始扫描

2. 新增 `StartMigrationFromStep2Command`：
   - 切换到步骤 3
   - 开始执行迁移

3. 新增 `CloseApplicationCommand`：
   - 调用 `Application.Current.Shutdown()` 关闭应用

4. 移除不再需要的命令：
   - `ValidateAndProceedCommand`（被 `StartScanFromStep1Command` 替代）
   - `ScanAndProceedCommand`（功能整合到 `StartScanFromStep1Command`）
   - `BackToStep2Command`（不再需要）
   - `ResetCommand`（被 `CloseApplicationCommand` 替代）

### 优势
1. **流程更顺畅**：减少不必要的页面切换，操作更直接
2. **符合用户习惯**：每个步骤都有明确的操作按钮
3. **减少冗余**：移除重复的日志显示
4. **更清晰的退出**：最后一步直接关闭应用，而不是重置到开始

### 测试结果
- ✅ 编译成功，无错误
- ✅ 无 Linter 警告
- ✅ 所有命令绑定正确

## 作者
诏无言

