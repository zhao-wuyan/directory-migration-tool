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

    public void WriteTo(BinaryWriter writer)
    {
        writer.Write(_pages.Count);
        writer.Write(_currentPageOffset);
        for (int i = 0; i < _pages.Count; i++)
        {
            int used = i == _pages.Count - 1 ? _currentPageOffset : PageSize;
            writer.Write(used);
            if (used > 0)
            {
                writer.Write(_pages[i], 0, used);
            }
        }
    }

    public static Utf8NamePool ReadFrom(BinaryReader reader)
    {
        int pageCount = reader.ReadInt32();
        int currentOffset = reader.ReadInt32();
        if (pageCount <= 0 || pageCount > byte.MaxValue + 1 || currentOffset < 0 || currentOffset > PageSize)
        {
            throw new InvalidDataException("UTF-8 名称池缓存头无效。");
        }

        var pool = new Utf8NamePool();
        pool._pages.Clear();
        for (int i = 0; i < pageCount; i++)
        {
            int used = reader.ReadInt32();
            if (used < 0 || used > PageSize)
            {
                throw new InvalidDataException("UTF-8 名称池页长度无效。");
            }
            byte[] page = new byte[PageSize];
            if (used > 0)
            {
                int totalRead = 0;
                while (totalRead < used)
                {
                    int n = reader.Read(page, totalRead, used - totalRead);
                    if (n <= 0) throw new EndOfStreamException("UTF-8 名称池缓存数据截断。");
                    totalRead += n;
                }
            }
            pool._pages.Add(page);
        }
        pool._currentPageOffset = currentOffset;
        return pool;
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
