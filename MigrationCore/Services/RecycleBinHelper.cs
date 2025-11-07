using Microsoft.VisualBasic.FileIO;

namespace MigrationCore.Services;

/// <summary>
/// 回收站辅助类 - 提供将文件/文件夹移入回收站的功能
/// </summary>
public static class RecycleBinHelper
{
    /// <summary>
    /// 将目录移入回收站
    /// </summary>
    /// <param name="directoryPath">要移入回收站的目录路径</param>
    /// <returns>是否成功</returns>
    public static bool MoveDirectoryToRecycleBin(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return false;

        try
        {
            FileSystem.DeleteDirectory(
                directoryPath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将文件移入回收站
    /// </summary>
    /// <param name="filePath">要移入回收站的文件路径</param>
    /// <returns>是否成功</returns>
    public static bool MoveFileToRecycleBin(string filePath)
    {
        if (!File.Exists(filePath))
            return false;

        try
        {
            FileSystem.DeleteFile(
                filePath,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

