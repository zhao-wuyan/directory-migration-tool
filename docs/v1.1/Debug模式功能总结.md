# Debug 模式功能总结

## 概述

在 Debug 构建模式下，软件提供了增强的调试功能，帮助开发者快速定位和解决问题。

## 新增的 Debug 功能

### 1. Robocopy 日志实时输出

**功能**：捕获并显示 Robocopy 的标准输出和错误输出。

**输出位置**：
- ✅ UI 日志窗口（带 `[Robocopy]` 前缀）
- ✅ Visual Studio 输出窗口（Debug 窗口）

**实现方式**：
```csharp
#if DEBUG
    // 重定向标准输出和错误输出
    RedirectStandardOutput = true,
    RedirectStandardError = true
    
    // 异步读取并打印日志
    _ = Task.Run(async () => { ... });
#endif
```

### 2. 详细的文件复制日志

**功能**：在 Debug 模式下移除 Robocopy 的输出抑制参数。

**区别**：

| 参数 | Release 模式 | Debug 模式 | 说明 |
|------|-------------|-----------|------|
| `/NFL` | ✅ 启用 | ❌ 禁用 | 不列出文件（Release）/ 列出每个文件（Debug） |
| `/NDL` | ✅ 启用 | ❌ 禁用 | 不列出目录（Release）/ 列出每个目录（Debug） |
| `/NP` | ✅ 启用 | ❌ 禁用 | 不显示进度（Release）/ 显示进度百分比（Debug） |

**实现方式**：
```csharp
var robocopyArgs = new List<string>
{
    // ... 其他参数 ...
#if !DEBUG
    "/NFL",  // 仅在 Release 模式添加
    "/NDL",
    "/NP",
#endif
    // ... 其他参数 ...
};
```

## 使用指南

### 如何启用 Debug 模式

#### 方法 1: Visual Studio

1. 打开解决方案
2. 在工具栏选择 **Debug** 配置（而不是 Release）
3. 按 F5 启动调试

#### 方法 2: 命令行编译

```bash
# Debug 模式
dotnet build -c Debug

# Release 模式
dotnet build -c Release
```

### 如何查看 Robocopy 日志

#### 在 Visual Studio 中

1. 启动调试（Debug 配置）
2. 打开 **输出** 窗口（视图 -> 输出，或 Ctrl+Alt+O）
3. 在输出窗口搜索 `[Robocopy]`

#### 在 UI 日志窗口中

Debug 模式下，所有 Robocopy 输出也会显示在应用的日志文本框中。

### 日志格式说明

```
[Robocopy] 标准输出内容
[Robocopy Error] 错误输出内容
[Robocopy-复制] 复制操作的输出（ReversibleMigrationService）
[Robocopy-还原] 还原操作的输出（ReversibleMigrationService）
```

## 调试场景示例

### 场景 1: 查看正在复制的文件

**问题**：想知道当前正在复制哪些文件。

**解决方案**：使用 Debug 模式，查看日志：

```
[Robocopy] 	    New File  	  104857600	game.iso
[Robocopy] 	45%
[Robocopy] 	    New File  	   52428800	video.mp4
[Robocopy] 	80%
```

### 场景 2: 排查复制失败原因

**问题**：部分文件复制失败，不知道原因。

**解决方案**：查看错误日志：

```
[Robocopy Error] ERROR 5 (0x00000005) Copying File C:\Source\locked.dat
[Robocopy Error] Access is denied.
```

**结论**：权限不足。

### 场景 3: 验证 Robocopy 参数

**问题**：不确定传递给 Robocopy 的参数是否正确。

**解决方案**：查看日志开头的 Options 行：

```
[Robocopy]   Options : /COPYALL /DCOPY:DAT /MIR /R:0 /W:0 /XJ /Z /ZB /MT:8
```

### 场景 4: 分析复制性能

**问题**：想了解实际复制速度。

**解决方案**：查看 Robocopy 的最终统计：

```
[Robocopy]    Speed :           67,108,864 Bytes/sec.
[Robocopy]    Speed :            3,840.000 MegaBytes/min.
```

## 性能影响对比

### Release 模式（生产环境）

- ✅ 不重定向输出流
- ✅ 不创建日志读取任务
- ✅ 使用 `/NFL /NDL /NP` 减少 Robocopy 输出
- ✅ **零调试开销**
- ✅ 最佳性能

### Debug 模式（开发环境）

- ⚠️ 重定向输出流
- ⚠️ 创建异步日志读取任务
- ⚠️ Robocopy 输出详细文件列表
- ℹ️ 轻微性能开销（通常可忽略）
- ✅ 便于调试

## 最佳实践

### ✅ 推荐做法

1. **开发阶段**：使用 Debug 配置，方便查看详细日志
2. **测试阶段**：使用 Debug 和 Release 交替测试
3. **发布版本**：使用 Release 配置，确保最佳性能
4. **问题排查**：如果用户报告问题，可以要求提供 Debug 版本的日志

### ❌ 不推荐做法

1. 不要在生产环境使用 Debug 版本（性能损失）
2. 不要在 Release 版本中期望看到详细日志（不会输出）
3. 不要依赖 Debug 日志作为功能的一部分（条件编译）

## 代码修改位置

### 修改的文件

1. **MigrationCore/Services/MigrationService.cs**
   - 添加输出流重定向（Debug 模式）
   - 添加异步日志读取任务（Debug 模式）
   - 条件移除 `/NFL /NDL /NP` 参数（Debug 模式）

2. **MigrationCore/Services/ReversibleMigrationService.cs**
   - 添加输出流重定向（Debug 模式）
   - 添加异步日志读取任务（Debug 模式）
   - 条件移除 `/NFL /NDL /NP` 参数（Debug 模式）
   - 日志带操作类型标记（复制/还原）

### 关键代码片段

```csharp
// 1. 重定向输出
var processStartInfo = new ProcessStartInfo
{
    // ...
    RedirectStandardOutput = true,
    RedirectStandardError = true
};

// 2. Debug 模式下读取日志
#if DEBUG
_ = Task.Run(async () =>
{
    while (!process.StandardOutput.EndOfStream)
    {
        string? line = await process.StandardOutput.ReadLineAsync();
        logProgress?.Report($"[Robocopy] {line}");
        Debug.WriteLine($"[Robocopy] {line}");
    }
});
#endif

// 3. 条件添加参数
var robocopyArgs = new List<string>
{
    // ...
#if !DEBUG
    "/NFL",
    "/NDL",
    "/NP",
#endif
    // ...
};
```

## 与用户的沟通

### 如何向用户解释

**用户询问**："为什么 Debug 版本运行慢一些？"

**回答示例**：
> Debug 版本包含了额外的诊断功能，用于帮助我们排查问题。它会记录详细的文件复制日志，因此速度会稍微慢一些。正式发布的 Release 版本已经移除了这些调试功能，性能是最优的。

### 如何请求 Debug 日志

**场景**：用户报告了一个难以重现的问题。

**回复模板**：
> 感谢您的反馈！为了帮助我们定位问题，能否请您：
> 1. 下载 Debug 版本（附上链接）
> 2. 重现问题
> 3. 复制日志窗口中的所有内容（特别是带 `[Robocopy]` 或 `[Robocopy Error]` 的行）
> 4. 发送给我们
> 
> 这将帮助我们快速找到问题所在。

## 总结

通过在 Debug 模式下添加详细的 Robocopy 日志输出，我们可以：

- ✅ 快速定位文件复制问题
- ✅ 验证 Robocopy 参数是否正确
- ✅ 分析复制性能
- ✅ 排查权限和路径问题
- ✅ 不影响 Release 版本的性能

这是一个**零成本的调试增强**，既保证了生产环境的性能，又为开发阶段提供了强大的诊断能力！🔍✨

