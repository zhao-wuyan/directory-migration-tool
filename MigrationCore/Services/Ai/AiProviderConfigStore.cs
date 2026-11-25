using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MigrationCore.Models.Ai;

namespace MigrationCore.Services.Ai;

/// <summary>
/// AI 模型配置与凭据安全存储（Windows DPAPI CurrentUser 加密 + 受当前用户 ACL 保护目录 + 原子替换写入）。
/// 严禁明文保存 API Key，严禁将密钥写入诊断日志或命令行参数。
/// </summary>
public sealed class AiProviderConfigStore
{
    private const string ConfigFileName = "provider-config.json";
    private static readonly byte[] DpapiEntropy = "MoveWithSymlinkWPF::Phase2::AiProviderKey::v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false
    };

    private readonly string _directoryPath;
    private readonly string _configPath;

    public AiProviderConfigStore(string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(baseDirectory))
        {
            _directoryPath = baseDirectory;
        }
        else if (MigrationRuntime.IsTestLab)
        {
            _directoryPath = Path.Combine(MigrationRuntime.TransactionDirectory, "Ai");
        }
        else
        {
            _directoryPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MoveWithSymlinkWPF", "State", "Ai");
        }

        _configPath = Path.Combine(_directoryPath, ConfigFileName);
    }

    public string ConfigFilePath => _configPath;

    public AiProviderSettings Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return new AiProviderSettings();
            }

            string json = File.ReadAllText(_configPath, Encoding.UTF8);
            AiProviderSettings? loaded = JsonSerializer.Deserialize<AiProviderSettings>(json, JsonOptions);
            if (loaded is null)
            {
                return new AiProviderSettings();
            }

            // 兼容旧配置（未包含 overallTimeoutSeconds 字段时回退到 20 分钟整体兜底上限）
            int overallTimeout = loaded.OverallTimeoutSeconds <= 0
                ? AiProviderSettings.DefaultOverallTimeoutSeconds
                : loaded.EffectiveOverallTimeoutSeconds;

            // 兼容旧版 v1/v2 配置：旧默认步数为 8 步过紧，平滑迁移为新版默认不限制（0）
            int maxToolSteps = loaded.SchemaVersion < 3 && loaded.MaxToolSteps == 8
                ? AiProviderSettings.DefaultMaxToolSteps
                : loaded.EffectiveMaxToolSteps;

            return loaded with
            {
                MaxToolSteps = maxToolSteps,
                TimeoutSeconds = loaded.EffectiveTimeoutSeconds,
                OverallTimeoutSeconds = overallTimeout
            };
        }
        catch
        {
            return new AiProviderSettings();
        }
    }

    /// <summary>
    /// 保存配置；若 <paramref name="plainApiKey"/> 为 null 则保留已存密文，为空字符串则清除已存密文，非空则经 DPAPI 加密替换。
    /// </summary>
    public AiProviderSettings Save(AiProviderSettings settings, string? plainApiKey = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? encryptedKey = settings.EncryptedApiKeyBase64;
        string? maskedPreview = settings.MaskedApiKeyPreview;

        if (plainApiKey is not null)
        {
            string trimmed = plainApiKey.Trim();
            if (trimmed.Length == 0)
            {
                encryptedKey = null;
                maskedPreview = null;
            }
            else
            {
                encryptedKey = ProtectApiKey(trimmed);
                maskedPreview = MaskApiKey(trimmed);
            }
        }

        AiProviderSettings toPersist = settings with
        {
            SchemaVersion = AiProviderSettings.SchemaVersionCurrent,
            Endpoint = (settings.Endpoint ?? string.Empty).Trim(),
            ModelName = (settings.ModelName ?? string.Empty).Trim(),
            MaxToolSteps = settings.EffectiveMaxToolSteps,
            TimeoutSeconds = settings.EffectiveTimeoutSeconds,
            OverallTimeoutSeconds = settings.EffectiveOverallTimeoutSeconds,
            EncryptedApiKeyBase64 = encryptedKey,
            MaskedApiKeyPreview = maskedPreview
        };

        EnsureProtectedDirectory();
        string json = JsonSerializer.Serialize(toPersist, JsonOptions);
        string tempPath = Path.Combine(_directoryPath, $".{ConfigFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, _configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        return toPersist;
    }

    /// <summary>
    /// 解密当前配置中经 DPAPI 保护的 API Key；无密钥或解密失败时返回 null。
    /// </summary>
    public string? TryUnprotectApiKey(AiProviderSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.EncryptedApiKeyBase64))
        {
            return null;
        }

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(settings.EncryptedApiKeyBase64);
            byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    public static string ProtectApiKey(string plainApiKey)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainApiKey);
        byte[] cipherBytes = ProtectedData.Protect(plainBytes, DpapiEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipherBytes);
    }

    public static string MaskApiKey(string plainApiKey)
    {
        string trimmed = plainApiKey.Trim();
        if (trimmed.Length <= 8)
        {
            return "••••••••";
        }
        string prefix = trimmed.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..3]
            : trimmed[..Math.Min(3, trimmed.Length)];
        string suffix = trimmed[^4..];
        return $"{prefix}••••••••••••{suffix}";
    }

    private void EnsureProtectedDirectory()
    {
        DirectoryInfo dir = Directory.CreateDirectory(_directoryPath);
        try
        {
            SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null) return;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            dir.SetAccessControl(security);
        }
        catch
        {
            // 非管理员/受限测试目录可能无法重写目录 ACL，保留 DPAPI CurrentUser 凭据保护
        }
    }
}
