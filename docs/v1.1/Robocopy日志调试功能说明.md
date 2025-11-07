# Robocopy 日志调试功能说明

## 功能概述

在 **Debug 模式**下，程序会实时捕获并输出 Robocopy 的标准输出和错误输出，方便开发者调试和排查问题。

## 功能特性

### 1. 仅在 Debug 模式启用

使用条件编译指令 `#if DEBUG`，确保日志功能仅在 Debug 构建时启用，不影响 Release 版本的性能和输出。

```csharp
#if DEBUG
    // Debug 模式下异步读取并打印 Robocopy 日志
    ...
#endif
```

### 2. 进度监控调试日志（新增）

在 Debug 模式下，每次进度更新时都会输出详细的调试信息到 Visual Studio 输出窗口：

```csharp
#if DEBUG
    System.Diagnostics.Debug.WriteLine(
        $"[Progress-{actionName}] " +
        $"Actual: {actualCopiedBytes}, " +
        $"Displayed: {displayedBytes}, " +
        $"Delta: {actualDeltaBytes}, " +
        $"Speed: {speed}, " +
        $"Percent: {percent:F1}%, " +
        $"NoChange: {noChangeCount}");
#endif
```

**输出示例**：
```
[Progress-还原] Actual: 2.50 GB, Displayed: 1.20 GB, Delta: 512.00 MB, Speed: 256.00 MB/s, Percent: 20.5%, NoChange: 0
[Progress-还原] Actual: 3.00 GB, Displayed: 1.80 GB, Delta: 500.00 MB, Speed: 250.00 MB/s, Percent: 25.0%, NoChange: 0
[Progress-还原] Actual: 12.02 GB, Displayed: 12.02 GB, Delta: 0 B, Speed: 0 B/s, Percent: 100.0%, NoChange: 10
```

这样可以：
- ✅ 查看实际扫描到的字节数（Actual）
- ✅ 查看显示给用户的字节数（Displayed）
- ✅ 查看每次采样的增量（Delta）
- ✅ 查看计算出的速度（Speed）
- ✅ 诊断进度停滞问题（NoChange）

### 3. 异步日志读取

使用独立的异步任务读取 Robocopy 的输出流，不会阻塞主进度监控逻辑。

```csharp
_ = Task.Run(async () =>
{
    while (!process.StandardOutput.EndOfStream)
    {
        string? line = await process.StandardOutput.ReadLineAsync();
        if (!string.IsNullOrWhiteSpace(line))
        {
            logProgress?.Report($"[Robocopy] {line}");
            System.Diagnostics.Debug.WriteLine($"[Robocopy] {line}");
        }
    }
});
```

### 4. 双通道输出

Robocopy 日志会同时输出到两个地方：

1. **UI 日志窗口**：通过 `logProgress?.Report()` 显示给用户（带 `[Robocopy]` 前缀）
2. **Visual Studio 输出窗口**：通过 `System.Diagnostics.Debug.WriteLine()` 输出到 IDE

进度调试日志仅输出到 Visual Studio 输出窗口（带 `[Progress]` 前缀），不会显示在 UI 中。

### 5. 错误输出单独处理

标准错误流（StandardError）会被单独捕获，并标记为 `[Robocopy Error]`，方便识别问题。

## 输出格式

### 1. Robocopy 标准输出

```
[Robocopy] -------------------------------------------------------------------------------
[Robocopy]    ROBOCOPY     ::     Robust File Copy for Windows
[Robocopy] -------------------------------------------------------------------------------
[Robocopy] 
[Robocopy]   Started : 2025年1月7日 14:30:45
[Robocopy]    Source : C:\Source\
[Robocopy]      Dest : D:\Target\
[Robocopy] 
[Robocopy]     Files : *.*
[Robocopy] 
[Robocopy]   Options : /COPYALL /DCOPY:DAT /MIR /R:0 /W:0 /XJ /MT:8
[Robocopy] 
[Robocopy] -------------------------------------------------------------------------------
```

### 2. Robocopy 错误输出

```
[Robocopy Error] ERROR: Access Denied
[Robocopy Error] File: C:\Source\locked.file
```

### 3. 进度调试日志（仅 VS 输出窗口）

```
[Progress] Actual: 512.00 MB, Displayed: 256.00 MB, Delta: 128.00 MB, Speed: 64.00 MB/s, Percent: 15.2%, NoChange: 0
[Progress] Actual: 640.00 MB, Displayed: 384.00 MB, Delta: 128.00 MB, Speed: 64.00 MB/s, Percent: 18.5%, NoChange: 0
[Progress] Actual: 768.00 MB, Displayed: 512.00 MB, Delta: 128.00 MB, Speed: 64.00 MB/s, Percent: 21.8%, NoChange: 0
```

**字段说明**：
- `Actual`: 文件系统报告的实际字节数（可能包含预分配）
- `Displayed`: 显示给用户的字节数（通过速度累加计算）
- `Delta`: 本次采样的增量
- `Speed`: 平滑后的速度
- `Percent`: 当前进度百分比
- `NoChange`: 连续无变化的次数（用于检测停滞）

### 4. 可逆迁移服务标记

在 `ReversibleMigrationService` 中，日志会标记操作类型：

```
[Robocopy-复制] ...
[Robocopy-还原] ...
[Robocopy-复制 Error] ...
[Progress-复制] ...
[Progress-还原] ...
```

## 使用方法

### 1. 在 Visual Studio 中调试

1. 设置构建配置为 **Debug**
2. 启动调试（F5）
3. 开始迁移操作
4. 查看 **输出窗口**（视图 -> 输出，或 Ctrl+Alt+O）
5. 在输出窗口中筛选包含 `[Robocopy]` 的行

### 2. 在 UI 日志窗口查看

Debug 模式下，Robocopy 的输出也会显示在应用的日志文本框中，带有 `[Robocopy]` 前缀。

### 3. 设置日志过滤

如果日志太多，可以在 Visual Studio 输出窗口中使用搜索框：

- 搜索 `[Robocopy]` - 查看所有 Robocopy 日志
- 搜索 `[Robocopy Error]` - 仅查看错误
- 搜索 `[Robocopy-复制]` - 查看复制操作的日志
- 搜索 `[Robocopy-还原]` - 查看还原操作的日志

## 技术实现

### 重定向标准输出

```csharp
var processStartInfo = new ProcessStartInfo
{
    FileName = "robocopy.exe",
    Arguments = string.Join(" ", robocopyArgs),
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardOutput = true,   // 重定向标准输出
    RedirectStandardError = true     // 重定向错误输出
};
```

### 异步读取日志

```csharp
_ = Task.Run(async () =>
{
    try
    {
        while (!process.StandardOutput.EndOfStream)
        {
            string? line = await process.StandardOutput.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                logProgress?.Report($"[Robocopy] {line}");
                System.Diagnostics.Debug.WriteLine($"[Robocopy] {line}");
            }
        }
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[Robocopy Log Error] {ex.Message}");
    }
});
```

## 性能影响

### Release 模式

- ❌ 日志功能**完全禁用**（条件编译）
- ✅ 不重定向输出流
- ✅ 不创建日志读取任务
- ✅ **零性能开销**

### Debug 模式

- ✅ 启用日志功能
- ⚠️ 轻微性能开销（异步读取）
- ⚠️ 可能增加内存使用（日志缓冲）
- ℹ️ 对大多数场景影响可忽略

## 常见问题

### Q1: 为什么在 Release 版本看不到日志？

**A**: 日志功能使用 `#if DEBUG` 条件编译，仅在 Debug 构建时启用。这是为了避免影响生产环境的性能和输出。

### Q2: 日志会不会影响复制速度？

**A**: 在 Debug 模式下可能有轻微影响，但由于是异步读取，影响很小。在 Release 模式下完全没有影响。

### Q3: 可以把日志保存到文件吗？

**A**: 当前实现仅输出到 UI 和 Debug 窗口。如果需要保存到文件，可以修改 `logProgress?.Report()` 的实现，或在接收日志的地方添加文件写入逻辑。

### Q4: Robocopy 的参数 `/NFL /NDL /NP` 会影响日志吗？

**A**: 会的。这些参数的含义：

- `/NFL` - No File List（不列出文件）
- `/NDL` - No Directory List（不列出目录）
- `/NP` - No Progress（不显示进度百分比）

**我们已经自动处理了这个问题！**

在 Debug 模式下，程序会**自动移除**这些参数，以便输出详细的日志：

```csharp
var robocopyArgs = new List<string>
{
    // ... 其他参数 ...
#if !DEBUG
    // Release 模式下减少输出，提高性能
    "/NFL",  // No File List
    "/NDL",  // No Directory List
    "/NP",   // No Progress
#endif
    // ... 其他参数 ...
};
```

这样在 Debug 模式下你可以看到每个文件的复制情况，而在 Release 模式下保持高性能。

## 调试示例

### Debug 模式 vs Release 模式输出对比

#### Debug 模式（详细输出）

```
[Robocopy] -------------------------------------------------------------------------------
[Robocopy]    ROBOCOPY     ::     Robust File Copy for Windows
[Robocopy] -------------------------------------------------------------------------------
[Robocopy] 
[Robocopy]   Started : 2025年1月7日 14:30:45
[Robocopy]    Source : C:\GameData\
[Robocopy]      Dest : D:\GameData\
[Robocopy] 
[Robocopy]     Files : *.*
[Robocopy] 
[Robocopy]   Options : /COPYALL /DCOPY:DAT /MIR /R:0 /W:0 /XJ /Z /ZB /MT:8
[Robocopy] 
[Robocopy] -------------------------------------------------------------------------------
[Robocopy] 
[Robocopy] 	  New Dir          1	C:\GameData\SaveData\
[Robocopy] 	    New File  	       1024	game.save
[Robocopy] 	    New File  	    5242880	level1.dat
[Robocopy] 	    New File  	   10485760	level2.dat
[Robocopy] 	100%
[Robocopy] 	  New Dir          2	C:\GameData\Screenshots\
[Robocopy] 	    New File  	    2097152	screenshot_001.png
[Robocopy] 	    New File  	    1835008	screenshot_002.png
[Robocopy] 	95%
[Robocopy] 
[Robocopy] -------------------------------------------------------------------------------
[Robocopy] 
[Robocopy]                Total    Copied   Skipped  Mismatch    FAILED    Extras
[Robocopy]     Dirs :         2         2         0         0         0         0
[Robocopy]    Files :         5         5         0         0         0         0
[Robocopy]    Bytes :    19.4 MB    19.4 MB         0         0         0         0
[Robocopy]    Times :   0:00:15   0:00:15   0:00:00   0:00:00
[Robocopy] 
[Robocopy]    Speed :            1,310,720 Bytes/sec.
[Robocopy]    Speed :               75.000 MegaBytes/min.
[Robocopy] 
[Robocopy]    Ended : 2025年1月7日 14:31:00
```

**优点**：可以看到每个文件和目录的复制情况，便于调试。

#### Release 模式（简洁输出）

```
[Robocopy] -------------------------------------------------------------------------------
[Robocopy]    ROBOCOPY     ::     Robust File Copy for Windows
[Robocopy] -------------------------------------------------------------------------------
[Robocopy] 
[Robocopy]   Started : 2025年1月7日 14:30:45
[Robocopy]    Source : C:\GameData\
[Robocopy]      Dest : D:\GameData\
[Robocopy] 
[Robocopy]     Files : *.*
[Robocopy] 
[Robocopy]   Options : /COPYALL /DCOPY:DAT /MIR /R:0 /W:0 /XJ /NFL /NDL /NP /Z /ZB /MT:8
[Robocopy] 
[Robocopy] -------------------------------------------------------------------------------
[Robocopy] 
[Robocopy] -------------------------------------------------------------------------------
[Robocopy] 
[Robocopy]                Total    Copied   Skipped  Mismatch    FAILED    Extras
[Robocopy]     Dirs :         2         2         0         0         0         0
[Robocopy]    Files :         5         5         0         0         0         0
[Robocopy]    Bytes :    19.4 MB    19.4 MB         0         0         0         0
[Robocopy]    Times :   0:00:15   0:00:15   0:00:00   0:00:00
[Robocopy] 
[Robocopy]    Speed :            1,310,720 Bytes/sec.
[Robocopy]    Speed :               75.000 MegaBytes/min.
[Robocopy] 
[Robocopy]    Ended : 2025年1月7日 14:31:00
```

**优点**：输出简洁，不会因为大量日志影响性能。

### 示例 1: 权限问题

```
[Robocopy] 
[Robocopy Error] ERROR 5 (0x00000005) Copying File C:\Source\important.dat
[Robocopy Error] Access is denied.
[Robocopy] 
[Robocopy] Files :       100        99         1         0         0         0
[Robocopy] Bytes :     1.5 GB   1.5 GB        0         0         0         0
```

**分析**：有一个文件因权限问题未能复制。

### 示例 2: 路径太长

```
[Robocopy Error] ERROR 206 (0x000000CE) Copying File
[Robocopy Error] The filename or extension is too long.
```

**分析**：文件路径超过 Windows 260 字符限制。

### 示例 3: 正常完成

```
[Robocopy] 
[Robocopy]                Total    Copied   Skipped  Mismatch    FAILED    Extras
[Robocopy]     Dirs :       150       150         0         0         0         0
[Robocopy]    Files :      5000      5000         0         0         0         0
[Robocopy]    Bytes :   100.5 GB  100.5 GB         0         0         0         0
[Robocopy]    Times :   0:25:30   0:25:30   0:00:00   0:00:00
[Robocopy] 
[Robocopy]    Speed :           67,108,864 Bytes/sec.
[Robocopy]    Speed :            3,840.000 MegaBytes/min.
[Robocopy] 
[Robocopy]    Ended : 2025年1月7日 14:56:15
```

**分析**：所有文件成功复制，总耗时 25 分 30 秒。

### 示例 4: 诊断 GUI 卡住问题（你遇到的问题）

**问题**：GUI 显示卡住，不确定是真的卡住还是进度更新有问题。

**现象**：
```
[11:27:36] 开始还原文件 (robocopy)...
[11:27:36] [Robocopy-还原] 开始时间: 2025年11月7日 11:27:36
[11:27:41] [Robocopy-还原] ------------------------------------------------------------------------------
[11:28:16] [Robocopy-还原] 已结束: 2025年11月7日 11:27:41
```

注意：`11:27:41` 到 `11:28:16` 之间没有任何日志！

**解决方案**：查看 Visual Studio 输出窗口中的进度调试日志：

```
[Progress-还原] Actual: 12.02 GB, Displayed: 120.00 MB, Delta: 12.02 GB, Speed: 0 B/s, Percent: 10.5%, NoChange: 0
[Progress-还原] Actual: 12.02 GB, Displayed: 120.00 MB, Delta: 0 B, Speed: 0 B/s, Percent: 10.5%, NoChange: 1
[Progress-还原] Actual: 12.02 GB, Displayed: 120.00 MB, Delta: 0 B, Speed: 0 B/s, Percent: 10.5%, NoChange: 2
...
[Progress-还原] Actual: 12.02 GB, Displayed: 120.00 MB, Delta: 0 B, Speed: 0 B/s, Percent: 10.5%, NoChange: 10
```

**分析**：
1. `Actual` 瞬间跳到 12.02 GB（文件系统预分配）
2. `Displayed` 从 120 MB 开始（速度累加法的初始值）
3. `Delta = 0` 说明文件系统报告没有变化
4. `NoChange` 持续增加，达到 10 后显示"正在处理..."
5. 实际上 Robocopy 正在后台写入，但文件系统报告延迟

**结论**：
- ✅ 程序没有卡死，进度循环正常运行
- ✅ Robocopy 正常工作（从最终日志可看出在实际写入）
- ❌ 问题是文件系统报告延迟，导致 `GetDirectorySize()` 一直返回相同值
- ⚠️ 速度累加法需要依赖增量（Delta）来计算速度，但 Delta=0 导致显示进度停滞

**改进方向**：
考虑在检测到长时间停滞（NoChange > threshold）但 Robocopy 进程仍在运行时，使用一个最小估算速度来缓慢增长进度，避免用户误以为卡死。

## 修改的文件

1. **MigrationCore/Services/MigrationService.cs**
   - 添加输出流重定向
   - 添加异步日志读取任务（Debug 模式）

2. **MigrationCore/Services/ReversibleMigrationService.cs**
   - 添加输出流重定向
   - 添加异步日志读取任务（Debug 模式）
   - 日志带操作类型标记（复制/还原）

## 总结

通过在 Debug 模式下启用 Robocopy 日志输出，开发者可以：

- ✅ 实时查看 Robocopy 的详细运行信息
- ✅ 快速定位文件复制过程中的问题
- ✅ 了解 Robocopy 的实际执行情况
- ✅ 不影响 Release 版本的性能

这个功能对于调试和优化文件复制逻辑非常有帮助！🔍

