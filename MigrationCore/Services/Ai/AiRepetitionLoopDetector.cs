using System.Text;

namespace MigrationCore.Services.Ai;

/// <summary>
/// 流式模型复读/死循环实时检测器（同时适用于 reasoning_content 思考流与正文输出流）：
/// 1. 检测单字符高频连续重复（如「的的的的…」连续 24 次以上，或纯分隔符号连续 120 次以上）；
/// 2. 检测词组/短句/段落的末尾连续周期复读（滑动窗口尾部周期匹配）；
/// 3. 检测规范化行/句在同一轮内的高频重复（防带递增序号或换行隔开的复读）；
/// 4. 引入持续观察窗口（默认 5 秒）：首次命中仅进入观察，若持续 5 秒仍在重复才触发介入；中途恢复正常新内容则自动解除观察，避免误杀；
/// 5. 显式避开合法 Markdown 表格分隔线、缩进空白等常见排版。
/// </summary>
public sealed class AiRepetitionLoopDetector
{
    public static readonly TimeSpan DefaultSustainDuration = TimeSpan.FromSeconds(5);

    private const int MaxWindowChars = 3000;
    private const int SingleCharRepeatThreshold = 24;
    private const int SeparatorCharRepeatThreshold = 120;
    private const int RepeatedLineThreshold = 10;
    private const int RecoveryNonRepeatingCharsThreshold = 40;

    private readonly TimeSpan _sustainDuration;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly StringBuilder _window = new();
    private readonly Dictionary<string, int> _normalizedLineCounts = new(StringComparer.Ordinal);
    private readonly StringBuilder _currentLine = new();

    private DateTimeOffset? _suspectedSinceUtc;
    private string? _suspectedReason;
    private int _nonRepeatingCharsSinceSuspected;

    public bool IsLoopDetected { get; private set; }
    public bool IsSuspectingLoop => _suspectedSinceUtc.HasValue && !IsLoopDetected;
    public string? DetectedReason { get; private set; }

    public AiRepetitionLoopDetector(
        TimeSpan? sustainDuration = null,
        Func<DateTimeOffset>? utcNowProvider = null)
    {
        _sustainDuration = sustainDuration is { } d && d >= TimeSpan.Zero ? d : DefaultSustainDuration;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 追加一段流式文本并检测是否持续陷入死循环（需持续达到观察窗口时长才触发介入）。
    /// </summary>
    public bool Feed(string? delta, out string? reason)
    {
        if (IsLoopDetected)
        {
            reason = DetectedReason;
            return true;
        }

        if (string.IsNullOrEmpty(delta))
        {
            reason = null;
            return false;
        }

        DateTimeOffset now = _utcNowProvider();

        _window.Append(delta);
        if (_window.Length > MaxWindowChars)
        {
            _window.Remove(0, _window.Length - MaxWindowChars);
        }

        // 1. 按行/句统计规范化重复频次（仅当本次 delta 刚结束的句子本身达到高频阈值才算当前命中）
        string? lineLoopReason = null;
        for (int i = 0; i < delta.Length; i++)
        {
            char c = delta[i];
            if (c == '\n' || c == '。' || c == '！' || c == '？' || c == ';')
            {
                if (TryRecordCompletedSegment(_currentLine.ToString(), out string? segReason))
                {
                    lineLoopReason = segReason;
                }
                _currentLine.Clear();
            }
            else if (c != '\r')
            {
                if (_currentLine.Length < 400)
                {
                    _currentLine.Append(c);
                }
            }
        }

        string snapshot = _window.ToString();

        // 2. 判定当下是否处于重复状态
        string? activeReason = null;
        if (TryDetectSingleCharRun(snapshot, out string? charReason))
        {
            activeReason = charReason;
        }
        else if (TryDetectTrailingPeriodLoop(snapshot, out string? periodReason))
        {
            activeReason = periodReason;
        }
        else if (lineLoopReason is not null)
        {
            activeReason = lineLoopReason;
        }

        if (activeReason is not null)
        {
            _nonRepeatingCharsSinceSuspected = 0;
            _suspectedReason = activeReason;

            if (_suspectedSinceUtc is null)
            {
                _suspectedSinceUtc = now;
                if (_sustainDuration == TimeSpan.Zero)
                {
                    return Trigger(activeReason, out reason);
                }
                reason = null;
                return false;
            }

            if (now - _suspectedSinceUtc.Value >= _sustainDuration)
            {
                string sustainText = _sustainDuration.TotalSeconds >= 1
                    ? $"持续超过 {Math.Round(_sustainDuration.TotalSeconds, 1):0.#} 秒"
                    : $"持续超过 {Math.Round(_sustainDuration.TotalMilliseconds):0} 毫秒";
                return Trigger($"{activeReason}，且{sustainText}仍在重复", out reason);
            }

            reason = null;
            return false;
        }

        // 当前分片未命中重复：若此前处于疑似观察期，累计非空白正常新字符；达到恢复阈值则说明模型已自行跳出复读，解除观察
        if (_suspectedSinceUtc is not null)
        {
            int nonWs = 0;
            for (int i = 0; i < delta.Length; i++)
            {
                if (!char.IsWhiteSpace(delta[i])) nonWs++;
            }

            _nonRepeatingCharsSinceSuspected += nonWs;
            if (_nonRepeatingCharsSinceSuspected >= RecoveryNonRepeatingCharsThreshold)
            {
                _suspectedSinceUtc = null;
                _suspectedReason = null;
                _nonRepeatingCharsSinceSuspected = 0;
            }
        }

        reason = null;
        return false;
    }

    /// <summary>
    /// 裁剪文本末尾被复读污染的冗余部分，保留前面已生成的有效内容。
    /// </summary>
    public static string TrimTrailingRepetition(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 24)
        {
            return text;
        }

        string trimmed = text.TrimEnd();
        int n = trimmed.Length;
        int maxPeriod = Math.Min(200, n / 3);

        for (int p = 1; p <= maxPeriod; p++)
        {
            int reps = CountTrailingRepeats(trimmed, p);
            int minReps = p == 1 ? 12 : p <= 6 ? 8 : p <= 40 ? 5 : 4;
            if (reps >= minReps)
            {
                int keepEnd = n - (reps - 1) * p;
                if (keepEnd > 0 && keepEnd < n)
                {
                    return trimmed[..keepEnd].TrimEnd();
                }
            }
        }

        return trimmed;
    }

    private bool Trigger(string msg, out string? reason)
    {
        IsLoopDetected = true;
        DetectedReason = msg;
        reason = msg;
        return true;
    }

    private bool TryRecordCompletedSegment(string rawSegment, out string? reason)
    {
        reason = null;
        string normalized = NormalizeSegmentForCounting(rawSegment);
        if (normalized.Length < 8 || IsPureStructuralOrSeparator(normalized))
        {
            return false;
        }

        _normalizedLineCounts.TryGetValue(normalized, out int count);
        count++;
        _normalizedLineCounts[normalized] = count;

        if (count >= RepeatedLineThreshold)
        {
            string preview = normalized.Length > 24 ? normalized[..24] + "…" : normalized;
            reason = $"同一语句「{preview}」在单轮内高频重复达 {count} 次";
            return true;
        }

        return false;
    }

    private static bool TryDetectSingleCharRun(string text, out string? reason)
    {
        reason = null;
        int len = text.Length;
        if (len < SingleCharRepeatThreshold)
        {
            return false;
        }

        char last = text[len - 1];
        if (char.IsWhiteSpace(last))
        {
            return false;
        }

        int run = 1;
        for (int i = len - 2; i >= 0 && text[i] == last; i--)
        {
            run++;
        }

        bool isCommonSeparator = last is '-' or '=' or '_' or '*' or '#' or '|' or '.' or '─' or '━';
        int threshold = isCommonSeparator ? SeparatorCharRepeatThreshold : SingleCharRepeatThreshold;

        if (run >= threshold)
        {
            reason = $"单字符「{last}」连续重复 {run} 次";
            return true;
        }

        return false;
    }

    private static bool TryDetectTrailingPeriodLoop(string text, out string? reason)
    {
        reason = null;
        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end < 24)
        {
            return false;
        }

        int maxPeriod = Math.Min(200, end / 5);
        for (int p = 2; p <= maxPeriod; p++)
        {
            int requiredRepeats = p <= 6 ? 12 : p <= 40 ? 8 : 5;
            if (p * requiredRepeats > end)
            {
                continue;
            }

            if (text[end - 1] != text[end - 1 - p] || text[end - p] != text[end - 2 * p])
            {
                continue;
            }

            int reps = CountTrailingRepeats(text.AsSpan(0, end), p);
            if (reps < requiredRepeats)
            {
                continue;
            }

            ReadOnlySpan<char> pattern = text.AsSpan(end - p, p);
            if (IsPureStructuralOrSeparator(pattern))
            {
                if (reps * p < 240)
                {
                    continue;
                }
            }

            string preview = pattern.ToString().Replace('\n', ' ').Trim();
            if (preview.Length > 28) preview = preview[..28] + "…";
            reason = $"片段「{preview}」连续循环重复 {reps} 次";
            return true;
        }

        return false;
    }

    private static int CountTrailingRepeats(ReadOnlySpan<char> text, int period)
    {
        int n = text.Length;
        if (period <= 0 || n < period * 2)
        {
            return 1;
        }

        ReadOnlySpan<char> unit = text.Slice(n - period, period);
        int count = 1;
        int pos = n - period * 2;
        while (pos >= 0 && text.Slice(pos, period).SequenceEqual(unit))
        {
            count++;
            pos -= period;
        }
        return count;
    }

    private static string NormalizeSegmentForCounting(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0) return string.Empty;

        int idx = 0;
        while (idx < trimmed.Length && (char.IsDigit(trimmed[idx]) || trimmed[idx] is '.' or ')' or '-' or '*' or '•' or ' '))
        {
            idx++;
        }

        var sb = new StringBuilder(trimmed.Length - idx);
        for (int i = idx; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static bool IsPureStructuralOrSeparator(ReadOnlySpan<char> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (!char.IsWhiteSpace(c)
                && c is not ('-' or '=' or '_' or '*' or '#' or '|' or '.' or ':' or '/' or '\\' or '─' or '━'))
            {
                return false;
            }
        }
        return true;
    }
}
