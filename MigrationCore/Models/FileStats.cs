namespace MigrationCore.Models;

/// <summary>
/// 文件统计信息
/// </summary>
public class FileStats
{
    /// <summary>
    /// 总字节数
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// 总文件数
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// 大文件数量（超过阈值）
    /// </summary>
    public int LargeFiles { get; set; }
}

