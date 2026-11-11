using System.Globalization;
using System.Text.Json;
using MigrationCore.Models;

namespace MigrationCore.Services;

// 测试上下文仅在启动时配置；任一验证失败都不得回退生产目录。
public static class MigrationRuntime
{
    private sealed record LabVolume(string Root, DirectoryIdentity Identity);
    private static LabVolume[]? _volumes;
    private static string? _stateDirectory;
    public static string? TestConfigPath { get; private set; }
    public static bool IsTestLab => _volumes is not null;
    public static string TransactionDirectory => _stateDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MoveWithSymlinkWPF", "State", "Transactions");

    public static void ConfigureFromArguments(string[] args)
    {
        string? requested = Environment.GetEnvironmentVariable("MIGRATION_TEST_LAB_RUN_ID");
        if (args.Length == 0 && string.IsNullOrEmpty(requested)) return;
        if (args is not ["--test-lab", _, "--run-id", _] || !Guid.TryParseExact(args[3], "D", out Guid id))
            throw new IOException("测试实例参数不完整，禁止回退生产启动。");
        InitializeTestLab(args[1], id);
    }

    public static void InitializeTestLab(string manifestPath, Guid runId)
    {
        if (_volumes is not null) throw new InvalidOperationException("测试上下文不能重复配置。");
        if (!PathValidator.IsAdministrator() || runId == Guid.Empty
            || Environment.GetEnvironmentVariable("MIGRATION_TEST_LAB_RUN_ID") != runId.ToString("D"))
            throw new IOException("测试 RunId 或管理员身份不符。");
        string workRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MoveWithSymlinkWPF-TestLab", "runs", runId.ToString("D"));
        if (manifestPath != Path.Combine(workRoot, "lab.json")) throw new IOException("未登记的 lab.json。");
        var identity = new PathIdentityService();
        DirectoryIdentity manifestIdentity = identity.ReadEntry(manifestPath);
        if (manifestIdentity.IsDirectory || manifestIdentity.IsReparsePoint) throw new IOException("无效的测试登记文件。");
        string package = AppContext.BaseDirectory.TrimEnd('\\');
        string? category = Path.GetFileName(Path.GetDirectoryName(package));
        if (!Guid.TryParseExact(Path.GetFileName(package), "D", out _)
            || category is not ("App" or "Harness")
            || !Path.GetDirectoryName(package)!.Equals(Path.Combine(workRoot, category), StringComparison.OrdinalIgnoreCase))
            throw new IOException("测试程序不在本次登记的离线包中。");
        if (identity.ReadDirectory(package, false).IsReparsePoint) throw new IOException("测试包不能是重解析点。");
        bool locked = false;
        try { using var probe = new FileStream(Path.Combine(workRoot, "run.lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) { locked = true; }
        if (!locked) throw new IOException("必须由持有 run.lock 的测试 launcher 启动。");
        byte[] bytes = File.ReadAllBytes(manifestPath);
        if (bytes.Length is 0 or > 65536) throw new IOException("测试登记文件大小无效。");
        ReadOnlyMemory<byte> json = bytes;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement lab = document.RootElement;
        if (lab.GetProperty("schemaVersion").GetInt32() != 1 || lab.GetProperty("runId").GetGuid() != runId)
            throw new IOException("测试登记版本或 RunId 不符。");
        var volumes = new LabVolume[2];
        for (int i = 0; i < 2; i++)
        {
            string image = Path.Combine(workRoot, i == 0 ? "source.vhdx" : "target.vhdx");
            DirectoryIdentity imageIdentity = identity.ReadEntry(image);
            if (lab.GetProperty("imagePaths")[i].GetString() != image || imageIdentity.IsReparsePoint || imageIdentity.IsDirectory
                || imageIdentity.FileId.ToString("X16", CultureInfo.InvariantCulture) != lab.GetProperty("imageFileIds")[i].GetString())
                throw new IOException("测试镜像文件身份不符。");
            string letter = lab.GetProperty("driveLetters")[i].GetString() ?? "";
            if (letter.Length != 1 || letter[0] is < 'A' or > 'Z') throw new IOException("测试盘符无效。");
            string root = letter + @":\";
            DirectoryIdentity volume = identity.ReadDirectory(root, false);
            if (volume.IsReparsePoint || !volume.VolumeGuid.Equals(lab.GetProperty("volumeGuidPaths")[i].GetString(), StringComparison.OrdinalIgnoreCase)
                || volume.VolumeSerial != lab.GetProperty("volumeSerials")[i].GetUInt32()
                || !new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException("测试卷真实身份不符。");
            volumes[i] = new LabVolume(root, volume);
        }
        if (volumes[0].Identity.VolumeGuid == volumes[1].Identity.VolumeGuid
            || volumes.Any(v => v.Identity.VolumeGuid == identity.ReadDirectory(workRoot, false).VolumeGuid))
            throw new IOException("测试卷必须彼此不同且不是宿主工作卷。");
        string config = Path.Combine(package, "quick-migrate.json");
        ReadValidatedConfiguration(config, volumes);
        _stateDirectory = Path.Combine(volumes[0].Root, "LabState", "Transactions");
        TestConfigPath = config;
        _volumes = volumes;
    }

    public static void AssertDataPath(string path, bool allowLink = false)
    {
        if (_volumes is not null) RequireLabPath(path, _volumes, true, allowLink);
    }

    public static void AssertStatePath(string path)
    {
        if (_volumes is null) return;
        if (!PathIdentityService.IsWithin(path, _stateDirectory!)) throw new IOException("状态路径越出登记测试卷。");
        RequireLabPath(path, _volumes, true);
    }

    public static string ReadTestConfiguration()
    {
        if (_volumes is null || TestConfigPath is null) throw new IOException("未配置测试实例。");
        return ReadValidatedConfiguration(TestConfigPath, _volumes);
    }

    private static string ReadValidatedConfiguration(string path, LabVolume[] volumes)
    {
        DirectoryIdentity file = new PathIdentityService().ReadEntry(path);
        if (file.IsReparsePoint || file.IsDirectory) throw new IOException("缺少普通测试配置文件。");
        string text = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(text);
        CheckUniqueFields(document.RootElement);
        JsonElement settings = document.RootElement;
        if (settings.GetProperty("profiles").GetArrayLength() != 0) throw new IOException("测试配置不能扫描生产 profiles。");
        RequireLabPath(settings.GetProperty("defaults").GetProperty("unifiedTargetRoot").GetString()!, volumes, false);
        foreach (JsonElement source in settings.GetProperty("standaloneSources").EnumerateArray())
            RequireLabPath(source.GetProperty("absolutePath").GetString()!, volumes, false);
        return text;
    }

    private static void CheckUniqueFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("测试配置包含重复或大小写分身字段。");
                CheckUniqueFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) CheckUniqueFields(child);
    }

    private static void RequireLabPath(string path, LabVolume[] volumes, bool allowMissing, bool allowLink = false)
    {
        path = PathIdentityService.Normalize(path);
        var identity = new PathIdentityService();
        LabVolume? volume = volumes.SingleOrDefault(v => PathIdentityService.IsWithin(path, Path.Combine(v.Root, "Data"))
            || PathIdentityService.IsWithin(path, Path.Combine(v.Root, "LabState")));
        if (volume is null) throw new IOException($"路径越出测试数据区：{path}");
        identity.RequireSame(volume.Root, volume.Identity);
        string existing = path;
        DirectoryIdentity? objectIdentity = identity.ReadIfExists(existing);
        while (allowMissing && objectIdentity is null)
        {
            existing = Path.GetDirectoryName(existing) ?? throw new IOException("无法定位测试父路径。");
            objectIdentity = identity.ReadIfExists(existing);
        }
        if (objectIdentity is null || objectIdentity.VolumeGuid != volume.Identity.VolumeGuid)
            throw new IOException("测试路径缺失或卷身份不符。");
        if (objectIdentity.IsReparsePoint)
        {
            if (!allowLink || existing != path) throw new IOException("测试路径包含未知重解析点。");
            string target = new DirectoryInfo(existing).LinkTarget ?? throw new IOException("无法读取待核对的链接目标。");
            string resolved = Path.IsPathFullyQualified(target) ? target : Path.GetFullPath(target, Path.GetDirectoryName(existing)!);
            RequireLabPath(resolved, volumes, false);
        }
    }
}
