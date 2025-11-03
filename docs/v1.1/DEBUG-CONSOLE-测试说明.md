# Debug 控制台输出测试说明

## 功能概述
Debug 版本现在包含完整的控制台输出功能，用于实时查看应用运行日志和调试信息。

## 已实现的功能

### 1. 控制台窗口自动分配
- Debug 配置下，应用启动时自动分配控制台窗口
- 显示启动信息：版本号、工作目录、时间戳

### 2. 全局异常捕获
- 捕获未处理异常 (UnhandledException)
- 捕获 UI 线程异常 (DispatcherUnhandledException)
- 捕获异步任务异常 (UnobservedTaskException)
- 所有异常都会输出到控制台，包含完整堆栈跟踪

### 3. 操作日志实时输出
- 所有 `AddLog` 调用会同时输出到控制台和 UI
- MainViewModel 和 QuickMigrateViewModel 的关键操作都会记录

### 4. 调试信息输出
- ViewModel 初始化
- 命令执行（按钮点击）
- 配置加载
- 文件选择
- 迁移进度

## 发布 Debug 版本

```powershell
# 发布 Debug 版本（不增加版本号）
.\publish.ps1 -m debug -s

# 或使用完整参数名
.\publish.ps1 -Mode debug -SkipVersionIncrement
```

## 测试步骤

### 1. 启动应用
运行：`MoveWithSymlinkWPF\bin\publish\win-x64-debug\目录迁移工具-v1.0.3-debug.exe`

预期看到：
```
=== Debug Console Enabled ===
Application started with console output
=============================

Application Version: v1.0.3
Working Directory: C:\ProjectDev\project\moveFloder\MoveWithSymlinkWPF\bin\publish\win-x64-debug
Timestamp: 2025-10-31 22:xx:xx

Global exception handlers registered

[22:xx:xx] MainViewModel initialized
[22:xx:xx] Version: v1.0.3
```

### 2. 测试按钮点击
点击任何按钮（如"选择源目录"），控制台应输出：
```
[22:xx:xx] BrowseSource command triggered
[22:xx:xx] Source path selected: C:\xxx\xxx
```

### 3. 测试一键迁移功能
切换到"一键迁移"标签，控制台应输出：
```
[22:xx:xx] QuickMigrateViewModel: LoadConfigAsync started
[22:xx:xx] Config loaded: Success
[22:xx:xx] Config loaded successfully, scanning tasks...
```

### 4. 测试迁移操作
执行迁移任务时，所有日志会实时输出：
```
[22:xx:xx] 开始一键迁移，共 3 个任务
[22:xx:xx] [任务名] 开始迁移
[22:xx:xx]   正在复制文件...
[22:xx:xx]   创建符号链接...
[22:xx:xx] [任务名] ✅ 迁移成功
```

### 5. 测试异常捕获
如果发生错误，控制台会显示：
```
=== DISPATCHER EXCEPTION ===
[22:xx:xx] 源路径不存在
Stack Trace:
   at ...
============================
```

## 调试技巧

1. **保持控制台窗口可见**：将控制台和应用窗口并排放置
2. **查看实时日志**：操作时观察控制台输出
3. **复制错误信息**：从控制台复制完整的错误堆栈
4. **滚动查看历史**：控制台可以滚动查看之前的日志

## 注意事项

1. **仅 Debug 版本有效**：Release 版本不会显示控制台
2. **需要 .NET 8.0 Desktop Runtime**：Debug 版本是框架依赖的
3. **控制台输出仅在 DEBUG 编译符号存在时生效**
4. **控制台窗口关闭会终止应用**

## 文件对比

### Debug vs Release

| 特性 | Debug 版本 | Release 版本 |
|------|-----------|-------------|
| 控制台窗口 | ✓ 有 | ✗ 无 |
| 调试符号 | ✓ 包含 (.pdb) | ✗ 移除 |
| 异常捕获 | ✓ 全局捕获 | 标准处理 |
| 文件大小 | ~3-5 MB | ~2-3 MB |
| 优化 | 无优化 | 完全优化 |
| 适用场景 | 开发调试 | 生产使用 |

## 故障排除

### 问题：控制台没有出现
- 确认运行的是 `-debug.exe` 文件
- 检查是否使用 `-c Debug` 配置发布
- 验证 `OutputType` 在 Debug 配置下为 `Exe`

### 问题：控制台出现但无日志输出
- 检查全局异常处理器是否注册
- 确认 `#if DEBUG` 代码块存在
- 验证应用确实在执行操作

### 问题：异常未被捕获
- 某些类型的异常可能需要特殊处理
- 检查异常是否在 try-catch 块中被静默吞掉
- 添加更多的 Console.WriteLine 追踪执行流程



