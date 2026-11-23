using System.Text;
using System.Text.RegularExpressions;

namespace MigrationCore.Services.Ai;

/// <summary>
/// 路径脱敏与不可信文本清洗器：
/// 1. 将 Windows 用户目录名（如 C:\Users\Alice\...）统一脱敏为 &lt;USER&gt;，避免向模型暴露本机用户名；
/// 2. 清洗来自文件系统目录名或模型返回文本中的控制字符与潜在协议 URI，防范高完整性进程渲染与 Prompt Injection。
/// </summary>
public static partial class AiPathSanitizer
{
    public const string UserPlaceholder = "<USER>";

    private static readonly string[] PreservedSystemUserFolderNames =
    [
        "Public",
        "Default",
        "Default User",
        "All Users"
    ];

    [GeneratedRegex(@"(?<=\\Users\\)([^\\/:*?""<>|]+)(?=\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsersSegmentRegex();

    [GeneratedRegex(@"(?i)\b(?:file|ms-msdt|search-ms|cmd|powershell|javascript|vbscript|data):[^\s""'<>]*", RegexOptions.CultureInvariant)]
    private static partial Regex DangerousUriSchemeRegex();

    /// <summary>
    /// 对本地路径执行用户名脱敏（保留盘符和目录层级语义，用户名统一替换为 &lt;USER&gt;）。
    /// </summary>
    public static string SanitizePath(string? path, string? currentUserName = null, string? currentUserProfile = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string result = path.Trim();
        string profile = currentUserProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string user = currentUserName ?? Environment.UserName;

        if (!string.IsNullOrWhiteSpace(profile))
        {
            string trimmedProfile = profile.TrimEnd('\\');
            string? profileParent = Path.GetDirectoryName(trimmedProfile);
            string profileLeaf = Path.GetFileName(trimmedProfile);
            if (!string.IsNullOrWhiteSpace(profileParent)
                && !string.IsNullOrWhiteSpace(profileLeaf)
                && !IsPreservedSystemFolder(profileLeaf))
            {
                string maskedPrefix = Path.Combine(profileParent, UserPlaceholder);
                if (result.Equals(trimmedProfile, StringComparison.OrdinalIgnoreCase))
                {
                    result = maskedPrefix;
                }
                else if (result.StartsWith(trimmedProfile + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    result = maskedPrefix + result[trimmedProfile.Length..];
                }
            }
        }

        result = UsersSegmentRegex().Replace(result, match =>
        {
            string segment = match.Value;
            return IsPreservedSystemFolder(segment) || segment.Equals(UserPlaceholder, StringComparison.OrdinalIgnoreCase)
                ? segment
                : UserPlaceholder;
        });

        if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3 && !IsPreservedSystemFolder(user))
        {
            // 仅在独立路径段命中当前用户名时脱敏，避免误伤普通英文单词
            string escaped = Regex.Escape(user);
            result = Regex.Replace(
                result,
                $@"(?<=\\){escaped}(?=\\|$)",
                UserPlaceholder,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return SanitizeUntrustedText(result, maxLength: 512, preserveNewLines: false);
    }

    /// <summary>
    /// 清洗不可信目录名、应用名或 AI 返回正文：移除危险控制字符、屏蔽可疑可执行 URI、强制长度上限。
    /// </summary>
    public static string SanitizeUntrustedText(string? input, int maxLength = 2000, bool preserveNewLines = true)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(input.Length, maxLength));
        for (int i = 0; i < input.Length && sb.Length < maxLength; i++)
        {
            char ch = input[i];
            if (ch == '\r')
            {
                if (preserveNewLines && (i + 1 >= input.Length || input[i + 1] != '\n'))
                {
                    sb.Append('\n');
                }
                continue;
            }
            if (ch == '\n')
            {
                sb.Append(preserveNewLines ? '\n' : ' ');
                continue;
            }
            if (ch == '\t')
            {
                sb.Append(' ');
                continue;
            }
            if (char.IsControl(ch) || ch == '\u200B' || ch == '\u202E' || ch == '\u202D' || ch == '\u2066' || ch == '\u2067' || ch == '\u2069')
            {
                // 过滤控制字符与双向文本欺骗字符（Bidi Override）
                continue;
            }
            sb.Append(ch);
        }

        string cleaned = DangerousUriSchemeRegex().Replace(sb.ToString(), "[已屏蔽协议链接]");
        if (input.Length > maxLength)
        {
            cleaned = cleaned.TrimEnd() + "…";
        }
        return cleaned.Trim();
    }

    /// <summary>
    /// 擦除文本中可能意外出现的 API Key 敏感凭据（用于错误提示与诊断轨迹防泄漏）。
    /// </summary>
    public static string RedactSecret(string? text, string? secret)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result = text;
        if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 6)
        {
            result = result.Replace(secret, "***REDACTED***", StringComparison.Ordinal);
        }
        return result;
    }

    private static bool IsPreservedSystemFolder(string name)
    {
        foreach (string preserved in PreservedSystemUserFolderNames)
        {
            if (name.Equals(preserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
