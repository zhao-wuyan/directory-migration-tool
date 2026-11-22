using System.Text;

namespace MigrationCore.Services.Scanning;

/// <summary>
/// 分页连续 UTF-8 名称池（每页固定 4 MiB），避免百万级扫描节点为每个文件名分配托管 string 或触发 LOH 动态倍增拷贝。
/// </summary>
public sealed class Utf8NamePool
{
    public const int PageSize = 4 * 1024 * 1024;

    private readonly List<byte[]> _pages = [new byte[PageSize]];
    private int _currentPageOffset;

    public int PageCount => _pages.Count;
    public long TotalUsedBytes => (long)(_pages.Count - 1) * PageSize + _currentPageOffset;

    public (byte PageIndex, int Offset, ushort Length) AddName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return (0, 0, 0);
        }

        int byteCount = Encoding.UTF8.GetByteCount(name);
        if (byteCount > ushort.MaxValue)
        {
            // 极端超长名称截断到 ushort 上限内保持结构紧凑
            byteCount = ushort.MaxValue;
        }

        EnsureCapacity(byteCount);
        int pageIndex = _pages.Count - 1;
        if (pageIndex > byte.MaxValue)
        {
            throw new InvalidOperationException("UTF-8 名称池超出最大页数上限（1 GiB）。");
        }

        byte[] page = _pages[pageIndex];
        int offset = _currentPageOffset;
        int written = Encoding.UTF8.GetBytes(name, page.AsSpan(offset, Math.Min(page.Length - offset, byteCount)));
        _currentPageOffset += written;
        return ((byte)pageIndex, offset, (ushort)written);
    }

    /// <summary>
    /// 直接从 MFT $FILE_NAME 原始 UTF-16LE 字节流转码写入 UTF-8 名称池，避免中间 string 分配。
    /// </summary>
    public (byte PageIndex, int Offset, ushort Length) AddUtf16LeBytes(ReadOnlySpan<byte> utf16LeBytes)
    {
        if (utf16LeBytes.IsEmpty)
        {
            return (0, 0, 0);
        }

        int charCount = utf16LeBytes.Length / 2;
        if (charCount == 0)
        {
            return (0, 0, 0);
        }

        Span<char> chars = charCount <= 512 ? stackalloc char[charCount] : new char[charCount];
        Encoding.Unicode.GetChars(utf16LeBytes[..(charCount * 2)], chars);
        return AddName(chars);
    }

    public string GetString(byte pageIndex, int offset, ushort length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        byte[] page = _pages[pageIndex];
        return Encoding.UTF8.GetString(page, offset, length);
    }

    private void EnsureCapacity(int requiredBytes)
    {
        if (requiredBytes > PageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBytes), "单个名称长度超过名称池单页大小。");
        }

        if (_currentPageOffset + requiredBytes > PageSize)
        {
            _pages.Add(new byte[PageSize]);
            _currentPageOffset = 0;
        }
    }
}
