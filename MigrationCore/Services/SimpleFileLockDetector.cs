using System.Diagnostics;

namespace MigrationCore.Services;

/// <summary>
/// 简化版文件占用检测服务
/// 通过测试文件夹的实际操作能力来判断是否有文件被占用
/// 确保迁移过程能够顺利进行重命名和删除操作
/// </summary>
public static class SimpleFileLockDetector
{
    /// <summary>
    /// 检测是否可以继续迁移操作
    /// </summary>
    /// <param name="sourcePath">源路径</param>
    /// <param name="targetPath">目标路径</param>
    /// <param name="errorMessage">错误信息</param>
    /// <param name="logProgress">日志进度回调</param>
    /// <returns>是否可以继续迁移</returns>
    public static bool CanProceedWithMigration(string sourcePath, string targetPath, out string errorMessage, IProgress<string>? logProgress = null)
    {
        errorMessage = string.Empty;
        logProgress?.Report("进入文件占用检测");
        // 使用Windows自身的检测机制：直接测试目录重命名
        if (!TestDirectoryRename(sourcePath, out string renameError))
        {
            errorMessage = $"源目录无法操作：{renameError}";

            // 尝试使用 handle.exe 获取占用进程信息
            var lockingProcesses = HandleHelper.GetProcessesLockingPath(sourcePath);
            if (lockingProcesses != null && lockingProcesses.Count > 0)
            {
                var formattedInfo = HandleHelper.FormatHandleInfo(lockingProcesses);
                errorMessage += "\n\n" + formattedInfo;

#if DEBUG
                Console.WriteLine($"[SimpleFileLockDetector] 最终错误消息长度: {errorMessage.Length}");
                Console.WriteLine($"[SimpleFileLockDetector] 最终错误消息:\n{errorMessage}");
#endif
            }

            return false;
        }

        // 检测目标路径是否可写
        if (!TestFolderWriteAccess(targetPath, out string targetError))
        {
            errorMessage = $"目标目录不可写：{targetError}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 检测是否可以继续还原操作
    /// </summary>
    /// <param name="sourcePath">源路径（符号链接）</param>
    /// <param name="targetPath">目标路径（数据源）</param>
    /// <param name="errorMessage">错误信息</param>
    /// <param name="logProgress">日志进度回调</param>
    /// <returns>是否可以继续还原</returns>
    public static bool CanProceedWithRestore(string sourcePath, string targetPath, out string errorMessage, IProgress<string>? logProgress = null)
    {
        errorMessage = string.Empty;

        // 还原操作需要删除符号链接，所以需要检测源路径的父目录
        string? sourceParent = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrEmpty(sourceParent))
        {
            errorMessage = "无法解析源路径的父目录";
            return false;
        }

        if (!TestFolderOperation(sourceParent, out string sourceError))
        {
            errorMessage = $"源路径所在目录无法操作：{sourceError}\n\n请关闭可能占用该目录的程序后重试。";

            // 尝试使用 handle.exe 获取占用进程信息
            var lockingProcesses = HandleHelper.GetProcessesLockingPath(sourcePath);
            if (lockingProcesses != null && lockingProcesses.Count > 0)
            {
                errorMessage += "\n\n" + HandleHelper.FormatHandleInfo(lockingProcesses);
            }

            return false;
        }

        // 检测目标路径是否可读（还原时需要从目标读取数据）
        if (!TestFolderReadAccess(targetPath, out string targetError))
        {
            errorMessage = $"目标目录不可读：{targetError}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 测试文件夹操作权限（重命名/删除）
    /// </summary>
    /// <param name="folderPath">文件夹路径</param>
    /// <param name="error">错误信息</param>
    /// <returns>是否可以操作</returns>
    private static bool TestFolderOperation(string folderPath, out string error)
    {
        error = string.Empty;

        if (!Directory.Exists(folderPath))
        {
            error = "目录不存在";
            return false;
        }

        try
        {
            // 测试1：创建临时文件
            string testFile = Path.Combine(folderPath, $"~migration_test_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "test");

            // 测试2：尝试重命名文件
            string renamedFile = testFile + "_renamed";
            File.Move(testFile, renamedFile);

            // 测试3：删除文件
            File.Delete(renamedFile);

            // 测试4：测试文件夹属性修改
            var attrs = File.GetAttributes(folderPath);
            File.SetAttributes(folderPath, attrs);

#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 文件夹操作测试通过: {folderPath}");
#endif
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = "权限不足，可能有程序正在使用此目录";
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 权限不足: {folderPath} - {ex.Message}");
#endif
            return false;
        }
        catch (IOException ex)
        {
            // IOException 通常表示文件被占用
            error = $"文件被占用：{ex.Message}";
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 文件被占用: {folderPath} - {ex.Message}");
#endif
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 其他错误: {folderPath} - {ex.Message}");
#endif
            return false;
        }
    }

    /// <summary>
    /// 测试文件夹写入权限
    /// </summary>
    /// <param name="folderPath">文件夹路径</param>
    /// <param name="error">错误信息</param>
    /// <returns>是否可写</returns>
    private static bool TestFolderWriteAccess(string folderPath, out string error)
    {
        error = string.Empty;

        try
        {
            // 确保目标目录存在
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            // 测试写入权限
            string testFile = Path.Combine(folderPath, $"~migration_test_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);

#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 写入权限测试通过: {folderPath}");
#endif
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"权限不足，无法写入目标目录：{ex.Message}";
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 写入权限不足: {folderPath} - {ex.Message}");
#endif
            return false;
        }
        catch (IOException ex)
        {
            error = $"目标目录不可写：{ex.Message}";
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 目标目录不可写: {folderPath} - {ex.Message}");
#endif
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 写入测试其他错误: {folderPath} - {ex.Message}");
#endif
            return false;
        }
    }

    /// <summary>
    /// 测试文件夹读取权限
    /// </summary>
    /// <param name="folderPath">文件夹路径</param>
    /// <param name="error">错误信息</param>
    /// <returns>是否可读</returns>
    private static bool TestFolderReadAccess(string folderPath, out string error)
    {
        error = string.Empty;

        if (!Directory.Exists(folderPath))
        {
            error = "目录不存在";
            return false;
        }

        try
        {
            // 测试读取权限
            var files = Directory.GetFiles(folderPath, "*", SearchOption.TopDirectoryOnly);
            var dirs = Directory.GetDirectories(folderPath, "*", SearchOption.TopDirectoryOnly);

            // 尝试读取第一个文件（如果存在）
            if (files.Length > 0)
            {
                var firstFile = files[0];
                using var fs = new FileStream(firstFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 读取权限测试通过: {folderPath}");
#endif
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"权限不足，无法读取目标目录：{ex.Message}";
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 读取权限不足: {folderPath} - {ex.Message}");
#endif
            return false;
        }
        catch (IOException ex)
        {
            error = $"目标目录不可读：{ex.Message}";
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 目标目录不可读: {folderPath} - {ex.Message}");
#endif
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 读取测试其他错误: {folderPath} - {ex.Message}");
#endif
            return false;
        }
    }

    /// <summary>
    /// 使用Directory.Move测试目录是否可以重命名（Windows最严格的占用检测）
    /// </summary>
    /// <param name="directoryPath">目录路径</param>
    /// <param name="error">错误信息</param>
    /// <returns>是否可以重命名</returns>
    private static bool TestDirectoryRename(string directoryPath, out string error)
    {
        error = string.Empty;
        
        if (!Directory.Exists(directoryPath))
        {
            error = "目录不存在";
            return false;
        }
        
        string originalName = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string? parentDir = Path.GetDirectoryName(directoryPath);
        
        if (string.IsNullOrEmpty(parentDir))
        {
            error = "无法解析父目录";
            return false;
        }
        
        string tempName = $"{originalName}_lock_test_{Guid.NewGuid():N}";
        string tempPath = Path.Combine(parentDir, tempName);
        
        try
        {
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 测试目录重命名: {directoryPath} -> {tempPath}");
#endif
            
            // 使用Windows自身的严格检测机制：尝试重命名目录
            Directory.Move(directoryPath, tempPath);
            
            // 立即改回原名
            Directory.Move(tempPath, directoryPath);
            
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 目录重命名测试通过: {directoryPath}");
#endif
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"权限不足，可能有程序正在使用此目录：{ex.Message}";
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 目录重命名权限不足: {directoryPath} - {ex.Message}");
#endif
            return false;
        }
        catch (IOException ex)
        {
            // IOException 是Windows检测到文件占用的主要方式
            error = $"目录被占用或锁定：{ex.Message}";
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 目录被占用: {directoryPath} - {ex.Message}");
#endif
            return false;
        }
        catch (Exception ex)
        {
            error = $"重命名测试失败：{ex.Message}";
#if DEBUG
            Console.WriteLine($"[SimpleFileLockDetector] 重命名测试异常: {directoryPath} - {ex.Message}");
#endif
            return false;
        }
        finally
        {
            // 确保临时目录被清理（如果重命名成功但改回失败）
            try
            {
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, true);
                }
            }
            catch
            {
                // 忽略清理错误
            }
        }
    }
}