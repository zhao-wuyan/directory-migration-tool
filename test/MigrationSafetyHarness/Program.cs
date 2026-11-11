using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MigrationCore.Models;
using MigrationCore.Services;
using Identity = DiskLabPath.Identity;

internal static partial class Program
{
    private const string RunnerName = "MigrationSafetyHarness.exe";
    private const string PayloadName = "payload.txt";
    private const long MinimumHostBytes = 1L << 30;
    private const int MaximumManifestBytes = 64 * 1024;
    private const uint DirectoryFlag = (uint)FileAttributes.Directory;
    private const uint ReparseFlag = (uint)FileAttributes.ReparsePoint;

    private sealed class CaseEvidence(string name)
    {
        public string Name { get; } = name;
        public string Status { get; set; } = "NotRun";
        public string? Error { get; set; }
        public Dictionary<string, object?> Observations { get; } = new();
    }

    private sealed record Lab(Guid RunId, string WorkRoot, Identity HostIdentity, Volume Source, Volume Target)
    {
        public void CheckRoots()
        {
            Source.CheckRoot();
            Target.CheckRoot();
        }

        public long BeforeWrite()
        {
            CheckRoots();
            SameObject(HostIdentity, DiskLabPath.AssertOrdinaryPath(WorkRoot, false), "WorkRoot changed");
            var drive = new DriveInfo(Path.GetPathRoot(WorkRoot)!);
            Require(drive.DriveType == DriveType.Fixed, "The lab host must be a fixed drive.");
            long free = drive.AvailableFreeSpace;
            Require(free >= MinimumHostBytes, $"Host free space {free} bytes is below 1 GiB.");
            return free;
        }
    }

    private sealed record Volume(string Root, string GuidRoot, uint Serial)
    {
        public Identity CheckRoot() => DiskLabPath.Check(Root, Root, GuidRoot, Serial, false, false);
        public Identity Existing(string path, string scope, bool allowReparse = false) =>
            DiskLabPath.Check(path, scope, GuidRoot, Serial, false, allowReparse);

        public void Missing(string path, string scope) => Require(
            DiskLabPath.Check(path, scope, GuidRoot, Serial, true, false) is null,
            $"Expected no object at {path}.");
    }

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--lab", _, "--run-id", _, "--suite", "Identity", "--read-transaction", _, "--state-directory", _])
            return await ReadTransactionChild(args);
        string suite = args.Length == 6 ? args[5] : "Invalid";
        string[] names = suite == "Identity" ? IdentityCaseNames :
            ["roots", "unknown-drive", "wrong-serial", "wrong-guid", "component-boundary",
             "system-path-parser", "untrusted-reparse", "cross-volume-copy", "source-rename",
             "actual-link-target", "fixture-only-deletion"];
        var cases = names.Select(name => new CaseEvidence(name)).ToArray();
        var started = DateTimeOffset.UtcNow;
        Guid? runId = null;
        Guid? attemptId = null;
        string? failure = null;
        try
        {
            Lab lab = LoadLab(args);
            runId = lab.RunId;
            attemptId = Guid.NewGuid();
            if (suite == "Identity")
            {
                MigrationRuntime.InitializeTestLab(args[1], lab.RunId);
                await RunIdentityCases(lab, attemptId.Value, cases);
            }
            else await RunCases(lab, attemptId.Value, cases);
        }
        catch (Exception ex)
        {
            failure = Describe(ex);
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            suite,
            runId,
            attemptId,
            startedAtUtc = started,
            finishedAtUtc = DateTimeOffset.UtcNow,
            status = failure is null ? "Passed" : "Failed",
            error = failure,
            cases
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return failure is null ? 0 : 1;
    }

    private static Lab LoadLab(string[] args)
    {
        Require(args is ["--lab", _, "--run-id", _, "--suite", "Boundary" or "Identity"],
            "Expected --lab <registered lab.json> --run-id <GUID D> --suite Boundary|Identity.");
        Require(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && PathValidator.IsAdministrator(),
            "The Windows lab requires an elevated process on Windows 10 build 19041 or later.");
        Guid runId = ParseGuid(args[3], "launch run ID");
        Require(ParseGuid(Environment.GetEnvironmentVariable("MIGRATION_TEST_LAB_RUN_ID"), "launcher run ID") == runId,
            "The launcher run ID does not match the requested run.");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        Require(!string.IsNullOrWhiteSpace(local), "LocalApplicationData is unavailable.");
        string workRoot = Path.Combine(local, "MoveWithSymlinkWPF-TestLab", "runs", runId.ToString("D"));
        string manifestPath = Path.Combine(workRoot, "lab.json");
        Require(string.Equals(args[1], manifestPath, StringComparison.Ordinal), "Manifest is not the registered lab.json.");
        DiskLabPath.AssertOrdinaryPath(args[1], false);
        Identity host = DiskLabPath.AssertOrdinaryPath(workRoot, false);
        Require((host.Attributes & DirectoryFlag) != 0, "WorkRoot must be an ordinary directory.");
        CheckPackage(workRoot);
        CheckLaunchLock(workRoot);
        OrdinarySingleFile(manifestPath);

        using var file = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(file.Length is > 0 and <= MaximumManifestBytes, "Invalid lab.json size.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        // Windows PowerShell's UTF-8 writer may include a UTF-8 BOM.
        string json = reader.ReadToEnd();
        if (json.StartsWith('\uFEFF'))
            json = json[1..];
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        string[] expected = ["schemaVersion", "runId", "createdAtUtc", "imagePaths", "diskUniqueIds", "partitionGuids",
            "volumeUniqueIds", "volumeSerials", "driveLetters", "volumeGuidPaths", "vhdIdentifiers", "imageFileIds"];
        Require(document.RootElement.ValueKind == JsonValueKind.Object, "Manifest must be a JSON object.");
        foreach (JsonProperty field in document.RootElement.EnumerateObject())
            Require(expected.Contains(field.Name, StringComparer.Ordinal) && fields.TryAdd(field.Name, field.Value),
                $"Unexpected or duplicate manifest field: {field.Name}.");
        Require(fields.Count == expected.Length, "Manifest fields are incomplete.");
        Require(fields["schemaVersion"].ValueKind == JsonValueKind.Number && fields["schemaVersion"].TryGetInt32(out int schema) && schema == 1,
            "Unsupported lab.json schema version.");
        Require(ParseGuid(StringValue(fields["runId"]), "manifest run ID") == runId, "Manifest run ID mismatch.");
        Require(DateTime.TryParseExact(StringValue(fields["createdAtUtc"]), "O", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTime created) && created.Kind == DateTimeKind.Utc,
            "Manifest creation time must be in UTC round-trip format.");

        string[] imagePaths = Strings(fields["imagePaths"]);
        string[] imageIds = DistinctStrings(fields["imageFileIds"]);
        _ = DistinctStrings(fields["diskUniqueIds"]);
        _ = DistinctStrings(fields["volumeUniqueIds"]);
        ValidateDistinctGuids(Strings(fields["partitionGuids"]), "partition GUID");
        ValidateDistinctGuids(Strings(fields["vhdIdentifiers"]), "VHD identifier");
        string[] letters = DistinctStrings(fields["driveLetters"]);
        string[] guidRoots = DistinctStrings(fields["volumeGuidPaths"]);
        JsonElement[] serials = Pair(fields["volumeSerials"]);
        var volumes = new Volume[2];
        for (int i = 0; i < volumes.Length; i++)
        {
            string imagePath = Path.Combine(workRoot, i == 0 ? "source.vhdx" : "target.vhdx");
            Require(string.Equals(imagePath, imagePaths[i], StringComparison.Ordinal), "Unexpected manifest VHDX path.");
            Identity image = OrdinarySingleFile(imagePaths[i]);
            Require(string.Equals(image.FileId.ToString("X16", CultureInfo.InvariantCulture), imageIds[i], StringComparison.Ordinal),
                $"Image file identity changed: {imagePath}.");
            Require(letters[i] is [>= 'A' and <= 'Z'], "Manifest drive letter is not a single uppercase DOS letter.");
            DiskLabPath.RequireVolumeGuidRoot(guidRoots[i]);
            Require(!host.FinalPath.StartsWith(guidRoots[i], StringComparison.OrdinalIgnoreCase), "A lab volume is the host WorkRoot volume.");
            Require(serials[i].ValueKind == JsonValueKind.Number, "Volume serial must be an unsigned 32-bit JSON number.");
            Require(serials[i].TryGetUInt32(out uint serial), "Volume serial is outside the unsigned 32-bit range.");
            volumes[i] = new Volume(letters[i] + @":\", guidRoots[i], serial);
        }
        var lab = new Lab(runId, workRoot, host, volumes[0], volumes[1]);
        lab.CheckRoots();
        foreach (Volume volume in volumes)
            Require(new DriveInfo(volume.Root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase), "A lab volume is not NTFS.");
        return lab;
    }

    private static void CheckPackage(string workRoot)
    {
        string package = AppContext.BaseDirectory.TrimEnd('\\');
        string harness = Path.Combine(workRoot, "Harness");
        string? process = Environment.ProcessPath;
        Require(string.Equals(Path.GetDirectoryName(package), harness, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(process, Path.Combine(package, RunnerName), StringComparison.OrdinalIgnoreCase),
            "Run the registered WorkRoot Harness package, not an unpackaged runner.");
        _ = ParseGuid(Path.GetFileName(package), "package attempt ID");
        DiskLabPath.AssertOrdinaryPath(package, false);
        OrdinarySingleFile(process!);
    }

    private static void CheckLaunchLock(string workRoot)
    {
        string path = Path.Combine(workRoot, "run.lock");
        OrdinarySingleFile(path);
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)) // ERROR_SHARING_VIOLATION
        {
            return;
        }
        throw new InvalidOperationException("The launcher must hold the registered run lock.");
    }

    private static async Task RunCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        Volume source = lab.Source;
        Volume target = lab.Target;
        string sample = Path.Combine(source.Root, @"Data\Sources\Sample");
        string protectedRoot = Path.Combine(source.Root, @"Data\Protected");
        string labState = Path.Combine(source.Root, "LabState");
        string targets = Path.Combine(target.Root, @"Data\Targets");
        string prefix = $"Boundary-{attempt:D}";
        string original = Path.Combine(sample, prefix);
        string renamed = Path.Combine(sample, prefix + "-backup");
        string destination = Path.Combine(targets, prefix);
        string decoy = Path.Combine(sample, prefix + "-untrusted");
        string sentinel = Path.Combine(protectedRoot, prefix + ".txt");
        byte[] payload = Encoding.UTF8.GetBytes($"Boundary payload {attempt:D}\n");
        byte[] sentinelBytes = Encoding.UTF8.GetBytes($"Protected Boundary sentinel {attempt:D}\n");
        Identity sourceDirectory = null!, targetDirectory = null!, sourceFile = null!, targetFile = null!;
        Identity sentinelId = null!, decoyId = null!, linkId = null!;

        void Run(int index, Action<Dictionary<string, object?>> action) =>
            RunCase(lab, cases[index], observations => { action(observations); return Task.CompletedTask; }).GetAwaiter().GetResult();

        Run(0, o =>
        {
            o["source"] = source.CheckRoot();
            o["target"] = target.CheckRoot();
            foreach (string fixtureRoot in new[] { sample, protectedRoot, labState })
                Require((source.Existing(fixtureRoot, fixtureRoot).Attributes & DirectoryFlag) != 0, $"Not a fixture directory: {fixtureRoot}.");
            Require((target.Existing(targets, targets).Attributes & DirectoryFlag) != 0, "Targets is not a fixture directory.");
        });
        Run(1, o =>
        {
            char unknown = Enumerable.Range('A', 26).Select(value => (char)value)
                .First(letter => letter != source.Root[0] && letter != target.Root[0]);
            string path = unknown + @":\Data\Sources\Sample";
            o["path"] = path;
            o["rejection"] = Reject(() => source.Existing(path, sample), "Outside allowed root:");
        });
        Run(2, o =>
        {
            uint wrong = source.Serial ^ 1;
            o["registeredSerial"] = source.Serial;
            o["claimedSerial"] = wrong;
            o["rejection"] = Reject(() => DiskLabPath.Check(sample, sample, source.GuidRoot, wrong, false, false), "Volume serial mismatch:");
        });
        Run(3, o =>
        {
            o["registeredGuid"] = source.GuidRoot;
            o["claimedGuid"] = target.GuidRoot;
            o["rejection"] = Reject(() => DiskLabPath.Check(sample, sample, target.GuidRoot, source.Serial, false, false), "Volume GUID mismatch:");
        });
        Run(4, o =>
        {
            string sibling = sample + "Sibling\\fixture";
            o["path"] = sibling;
            o["rejection"] = Reject(() => source.Existing(sibling, sample), "Outside allowed root:");
            Require(DiskLabPath.IsWithin(original, sample), "Parser rejected a valid child component.");
        });
        Run(5, o =>
        {
            // Pure parser fixtures: do not open or probe any system directory.
            string[] systemPaths = [@"C:\Windows\System32", @"C:\Program Files", @"C:\ProgramData\Microsoft",
                Path.Combine(source.Root, "Windows", "System32")];
            foreach (string path in systemPaths)
                Require(!DiskLabPath.IsWithin(path, sample) && !DiskLabPath.IsWithin(path, targets), "System path fell inside a fixture scope.");
            o["outsideScopes"] = systemPaths;
            string[] malformed = [@"\\server\share\fixture", @"\\?\C:\Windows", @"\\.\C:\Windows", @"C:Windows",
                sample + @"\..\Protected", sample + @"\\payload", sample + @"\payload:stream",
                sample + @"\NUL.txt", sample + @"\payload.", sample + @"\payload ", sample + "/payload"];
            o["syntaxRejections"] = malformed.Select(path => Reject(() => DiskLabPath.IsWithin(path, sample),
                "Expected an absolute DOS path:", "Invalid DOS path component:", "Reserved DOS device component:")).ToArray();
        });
        Run(6, o =>
        {
            o["sentinel"] = sentinel;
            o["decoy"] = decoy;
            sentinelId = WriteFile(lab, source, protectedRoot, sentinel, sentinelBytes);
            o["hostFreeBytes"] = lab.BeforeWrite();
            source.Missing(decoy, sample);
            source.Existing(protectedRoot, protectedRoot);
            Require(SymbolicLinkHelper.CreateDirectorySymbolicLink(decoy, protectedRoot), "Could not create the lab-only untrusted reparse fixture.");
            decoyId = source.Existing(decoy, sample, allowReparse: true);
            Require((decoyId.Attributes & ReparseFlag) != 0, "Expected a real reparse point.");
            o["reparseIdentity"] = decoyId;
            o["leafRejection"] = Reject(() => source.Existing(decoy, sample), "Reparse point rejected:");
            o["ancestorRejection"] = Reject(() => source.Existing(Path.Combine(decoy, Path.GetFileName(sentinel)), sample), "Reparse point rejected:");
            o["sentinelSha256"] = VerifyFile(source, protectedRoot, sentinel, sentinelId, sentinelBytes);
        });
        await RunCase(lab, cases[7], async o =>
        {
            o["sourcePath"] = original;
            o["targetPath"] = destination;
            sourceDirectory = NewDirectory(lab, source, sample, original);
            targetDirectory = NewDirectory(lab, target, targets, destination);
            string input = Path.Combine(original, PayloadName);
            sourceFile = WriteFile(lab, source, original, input, payload);
            string previousDirectory = Directory.GetCurrentDirectory();
            string systemDirectory = Environment.SystemDirectory;
            Identity robocopy = DiskLabPath.AssertOrdinaryPath(Path.Combine(systemDirectory, "robocopy.exe"), false);
            Require((robocopy.Attributes & DirectoryFlag) == 0, "System robocopy.exe is not a file.");
            Require(DiskLabPath.AssertOrdinaryPath(Path.Combine(AppContext.BaseDirectory, "robocopy.exe"), true) is null,
                "Unexpected robocopy.exe in the runner package.");
            try
            {
                // The existing executor resolves robocopy by name. Keep untrusted launch
                // directories out of the executable search, including the current directory.
                Directory.SetCurrentDirectory(systemDirectory);
                o["hostFreeBytes"] = lab.BeforeWrite();
                SameObject(sourceDirectory, source.Existing(original, sample), "Copy source changed");
                SameObject(targetDirectory, target.Existing(destination, targets), "Copy target changed");
                VerifyFile(source, original, input, sourceFile, payload);
                Require(Directory.EnumerateFileSystemEntries(original).SequenceEqual(new[] { input }, StringComparer.OrdinalIgnoreCase) &&
                    !Directory.EnumerateFileSystemEntries(destination).Any(), "Unexpected copy fixture contents.");
                var executor = new CopyOperationExecutor(original, destination,
                    new FileStats { TotalBytes = payload.Length, TotalFiles = 1 }, robocopyThreads: 2, sampleMilliseconds: 250);
                await executor.ExecuteAsync(null, null, CancellationToken.None);
            }
            finally
            {
                Directory.SetCurrentDirectory(previousDirectory);
            }
            lab.CheckRoots();
            SameObject(targetDirectory, target.Existing(destination, targets), "Copy target changed");
            string output = Path.Combine(destination, PayloadName);
            targetFile = target.Existing(output, destination);
            o["sourceIdentity"] = sourceFile;
            o["targetIdentity"] = targetFile;
            o["bytes"] = payload.Length;
            o["sourceSha256"] = VerifyFile(source, original, input, sourceFile, payload);
            o["targetSha256"] = VerifyFile(target, destination, output, targetFile, payload);
        });
        Run(8, o =>
        {
            o["from"] = original;
            o["to"] = renamed;
            o["hostFreeBytes"] = lab.BeforeWrite();
            SameObject(sourceDirectory, source.Existing(original, sample), "Rename source changed");
            source.Missing(renamed, sample);
            Directory.Move(original, renamed);
            source.Missing(original, sample);
            Identity after = source.Existing(renamed, sample);
            SameObject(sourceDirectory, after, "Rename did not preserve the source directory identity", comparePath: false);
            o["before"] = sourceDirectory;
            o["after"] = after;
            o["sha256"] = VerifyFile(source, renamed, Path.Combine(renamed, PayloadName), sourceFile, payload, comparePath: false);
        });
        Run(9, o =>
        {
            o["linkPath"] = original;
            o["targetPath"] = destination;
            o["hostFreeBytes"] = lab.BeforeWrite();
            source.Missing(original, sample);
            SameObject(targetDirectory, target.Existing(destination, targets), "Link target changed");
            Require(SymbolicLinkHelper.CreateDirectorySymbolicLink(original, destination), "Could not create cross-volume fixture link.");
            linkId = source.Existing(original, sample, allowReparse: true);
            Require((linkId.Attributes & (DirectoryFlag | ReparseFlag)) == (DirectoryFlag | ReparseFlag), "Expected a directory reparse point.");
            o["linkIdentity"] = linkId;
            lab.CheckRoots();
            SameObject(linkId, source.Existing(original, sample, allowReparse: true), "Link object changed");
            SameObject(targetDirectory, target.Existing(destination, targets), "Link target changed");
            Identity followed = DiskLabPath.ReadIdentity(original, true);
            SameObject(targetDirectory, followed, "Followed handle does not identify the registered target directory");
            Require((followed.Attributes & (DirectoryFlag | ReparseFlag)) == DirectoryFlag, "Followed handle is not an ordinary target directory.");
            o["followedIdentity"] = followed;
        });
        Run(10, o =>
        {
            var deleted = new List<string>();
            o["deleted"] = deleted;
            o["hostFreeBytes"] = lab.BeforeWrite();
            DeleteDirectory(lab, source, sample, original, linkId, isLink: true);
            deleted.Add(original);
            o["targetSha256AfterUnlink"] = VerifyFile(target, destination, Path.Combine(destination, PayloadName), targetFile, payload);
            o["backupSha256AfterUnlink"] = VerifyFile(source, renamed, Path.Combine(renamed, PayloadName), sourceFile, payload, comparePath: false);
            DeleteDirectory(lab, source, sample, decoy, decoyId, isLink: true);
            deleted.Add(decoy);
            o["sentinelSha256AfterUnlink"] = VerifyFile(source, protectedRoot, sentinel, sentinelId, sentinelBytes);
            DeleteFile(lab, source, renamed, Path.Combine(renamed, PayloadName), sourceFile, payload, comparePath: false);
            deleted.Add(Path.Combine(renamed, PayloadName));
            DeleteDirectory(lab, source, sample, renamed, sourceDirectory, comparePath: false);
            deleted.Add(renamed);
            o["targetSha256AfterBackupDeletion"] = VerifyFile(target, destination, Path.Combine(destination, PayloadName), targetFile, payload);
            DeleteFile(lab, target, destination, Path.Combine(destination, PayloadName), targetFile, payload);
            deleted.Add(Path.Combine(destination, PayloadName));
            DeleteDirectory(lab, target, targets, destination, targetDirectory);
            deleted.Add(destination);
            DeleteFile(lab, source, protectedRoot, sentinel, sentinelId, sentinelBytes);
            deleted.Add(sentinel);
        });
        // On any failure the runner stops; no broad or speculative rollback is attempted.
    }

    private static async Task RunCase(Lab lab, CaseEvidence result, Func<Dictionary<string, object?>, Task> action)
    {
        try
        {
            lab.CheckRoots();
            await action(result.Observations);
            result.Status = "Passed";
        }
        catch (Exception ex)
        {
            result.Status = "Failed";
            result.Error = Describe(ex);
            throw;
        }
    }

    private static Identity NewDirectory(Lab lab, Volume volume, string scope, string path)
    {
        lab.BeforeWrite();
        volume.Missing(path, scope);
        if (!CreateDirectoryW(@"\\?\" + path, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not create fresh fixture: {path}.");
        Identity identity = volume.Existing(path, scope);
        Require((identity.Attributes & DirectoryFlag) != 0, $"Not a fixture directory: {path}.");
        return identity;
    }

    private static Identity WriteFile(Lab lab, Volume volume, string scope, string path, byte[] bytes)
    {
        lab.BeforeWrite();
        volume.Missing(path, scope);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.Write(bytes);
        Identity identity = volume.Existing(path, scope);
        VerifyFile(volume, scope, path, identity, bytes);
        return identity;
    }

    private static string VerifyFile(Volume volume, string scope, string path, Identity expected, byte[] bytes, bool comparePath = true)
    {
        Identity actual = volume.Existing(path, scope);
        SameObject(expected, actual, $"File identity changed: {path}", comparePath);
        Require((actual.Attributes & DirectoryFlag) == 0 && actual.LinkCount == 1, $"Not a single-link fixture file: {path}.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(file.Length == bytes.Length, $"Fixture file length differs: {path}.");
        Span<byte> actualBytes = stackalloc byte[bytes.Length];
        file.ReadExactly(actualBytes);
        Require(actualBytes.SequenceEqual(bytes), $"Fixture file contents differ: {path}.");
        return Convert.ToHexString(SHA256.HashData(actualBytes));
    }

    private static void DeleteFile(Lab lab, Volume volume, string scope, string path, Identity expected, byte[] bytes, bool comparePath = true)
    {
        lab.BeforeWrite();
        VerifyFile(volume, scope, path, expected, bytes, comparePath);
        File.Delete(path);
        volume.Missing(path, scope);
    }

    private static void DeleteDirectory(Lab lab, Volume volume, string scope, string path, Identity expected,
        bool isLink = false, bool comparePath = true)
    {
        lab.BeforeWrite();
        Identity actual = volume.Existing(path, scope, allowReparse: isLink);
        SameObject(expected, actual, $"Directory identity changed: {path}", comparePath);
        Require((actual.Attributes & (DirectoryFlag | ReparseFlag)) == (DirectoryFlag | (isLink ? ReparseFlag : 0U)),
            $"Directory fixture type changed: {path}.");
        Directory.Delete(path, recursive: false);
        volume.Missing(path, scope);
    }

    private static Identity OrdinarySingleFile(string path)
    {
        Identity identity = DiskLabPath.AssertOrdinaryPath(path, false);
        Require((identity.Attributes & DirectoryFlag) == 0 && identity.LinkCount == 1 && identity.FileId != 0,
            $"Not an ordinary single-link file: {path}.");
        return identity;
    }

    private static void SameObject(Identity expected, Identity actual, string message, bool comparePath = true)
    {
        Require(expected.Serial == actual.Serial && expected.FileId == actual.FileId && expected.FileId != 0 &&
            (expected.Attributes & (DirectoryFlag | ReparseFlag)) == (actual.Attributes & (DirectoryFlag | ReparseFlag)) &&
            (!comparePath || string.Equals(expected.FinalPath, actual.FinalPath, StringComparison.OrdinalIgnoreCase)), message);
    }

    private static string Reject(Action action, params string[] expectedPrefixes)
    {
        try { action(); }
        catch (IOException ex) when (expectedPrefixes.Any(prefix => ex.Message.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return ex.Message;
        }
        throw new InvalidOperationException("The guard accepted an input that had to be rejected.");
    }

    private static Guid ParseGuid(string? value, string field)
    {
        Require(Guid.TryParseExact(value, "D", out Guid guid) && guid != Guid.Empty &&
            string.Equals(guid.ToString("D"), value, StringComparison.OrdinalIgnoreCase), $"Invalid {field}.");
        return guid;
    }

    private static void ValidateDistinctGuids(string[] values, string field) => Require(
        ParseGuid(values[0], field) != ParseGuid(values[1], field), $"Duplicate {field}.");

    private static string StringValue(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.String, "Expected a manifest string.");
        string text = value.GetString()!;
        Require(!string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl), "Invalid manifest string.");
        return text;
    }

    private static JsonElement[] Pair(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2, "Expected exactly two manifest values (source then target).");
        return [value[0], value[1]];
    }

    private static string[] Strings(JsonElement value) => Pair(value).Select(StringValue).ToArray();

    private static string[] DistinctStrings(JsonElement value)
    {
        string[] values = Strings(value);
        Require(!values[0].Equals(values[1], StringComparison.OrdinalIgnoreCase), "Expected distinct source and target values.");
        return values;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}
