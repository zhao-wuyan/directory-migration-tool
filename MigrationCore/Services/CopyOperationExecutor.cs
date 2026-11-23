using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using MigrationCore.Models;

namespace MigrationCore.Services;

public sealed class CopyOperationExecutor(MigrationTransactionRecord transaction, FileStats stats,
    int robocopyThreads, int sampleMilliseconds, string actionName = "复制")
{
    public async Task<CopyExecutionResult> ExecuteAsync(IProgress<MigrationProgress>? progress,
        IProgress<string>? logProgress, CancellationToken cancellationToken)
    {
        MigrationTransactionRecord record = transaction;
        var identity = new PathIdentityService();
        var verification = new CopyVerificationService();
        MigrationTransactionStore? store = null;
        OwnedProcessJob? job = null;
        Task? stdout = null, stderr = null;
        CancellationTokenRegistration registration = default;
        using var ioCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string source = record.Mode == MigrationMode.Restore ? record.TargetPath : record.SourcePath;
        string staging = "";
        string actual = "";
        int? exitCode = null;
        bool processExited = true;
        bool recovery = false;
        bool saving = false;
        bool promoted = false;
        bool sourceAdmitted = false;
        FileManifest? before = null;
        VerificationSummary? summary = null;
        string? stopError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Mode is not (MigrationMode.Migrate or MigrationMode.Restore)) throw new IOException("此复制入口仅支持迁移或还原。");
            MigrationRuntime.AssertDataPath(record.SourcePath, allowLink: record.Mode == MigrationMode.Restore);
            MigrationRuntime.AssertDataPath(record.TargetPath);
            store = new MigrationTransactionStore();
            var saved = await store.LoadAsync(record.TransactionId, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("复制事务记录缺失。");
            if (saved.Revision != record.Revision || saved.Stage != MigrationStage.Prepared || saved.Mode != record.Mode
                || saved.SourcePath != record.SourcePath || saved.TargetPath != record.TargetPath
                || saved.SourceIdentity != record.SourceIdentity || saved.TargetIdentity != record.TargetIdentity)
                throw new IOException("复制事务阶段、revision 或身份已经变化。");
            record = saved;
            identity.RequireSame(source, record.Mode == MigrationMode.Restore ? record.TargetIdentity : record.SourceIdentity);
            sourceAdmitted = true;
            actual = source;
            identity.RequireSame(Path.GetDirectoryName(record.TargetPath)!, record.TargetParentIdentity);
            if (record.Mode == MigrationMode.Restore)
            {
                identity.RequireSame(record.SourcePath, record.LinkIdentity);
                identity.RequireSame(record.SourcePath, record.TargetIdentity, true);
            }
            else CheckFinalTarget();
            Report(CopyPhase.Preparing);
            before = await verification.CaptureAsync(source, record.VerificationMode, cancellationToken).ConfigureAwait(false);
            CopyVerificationService.ManifestHardLinkAnalysis hardLinkAnalysis = CopyVerificationService.AnalyzeManifestHardLinks(before);
            if (hardLinkAnalysis.HasExternalHardLinks)
            {
                string sampleList = string.Join("、", hardLinkAnalysis.ExternalHardLinkRelativePaths.Take(3));
                throw new IOException(
                    $"检测到源目录存在指向目录外部的硬链接文件（共 {hardLinkAnalysis.ExternalHardLinkRelativePaths.Length} 项，例如：{sampleList}）；跨卷复制会破坏共享语义且无法释放源盘空间，已安全中止。");
            }

            var secondaryHardLinkPaths = new HashSet<string>(
                hardLinkAnalysis.InternalGroups.SelectMany(g => g.SecondaryRelativePaths),
                StringComparer.OrdinalIgnoreCase);

            string final = record.Mode == MigrationMode.Restore ? record.SourcePath : record.TargetPath;
            string parent = Path.GetDirectoryName(final) ?? throw new IOException("复制父路径缺失。");
            DirectoryIdentity parentIdentity = identity.ReadDirectory(parent, false);
            if (parentIdentity.IsReparsePoint) throw new IOException("复制父目录不是普通实体目录。");
            long requiredBytes = 0;
            foreach (var item in before.Entries)
            {
                if (!item.Value.IsDirectory && secondaryHardLinkPaths.Contains(item.Key))
                {
                    continue;
                }
                requiredBytes = checked(requiredBytes + item.Value.Length);
                foreach (var stream in item.Value.AlternateStreams) requiredBytes = checked(requiredBytes + stream.Length);
            }
            if (!PathValidator.CheckDiskSpace(parent, requiredBytes).IsSufficient)
                throw new IOException("实际复制目的卷空间不足，尚未创建 staging。");
            staging = Path.Combine(parent, "." + Path.GetFileName(final)
                + (record.Mode == MigrationMode.Restore ? ".restore_" : ".copy_") + record.TransactionId.ToString("D"));
            MigrationRuntime.AssertDataPath(staging);
            identity.RequireMissing(staging);
            if (record.StagingPath is not null && record.StagingPath != staging) throw new IOException("事务 staging 路径不符。");
            saving = true;
            record = await store.SaveAsync(record with
            {
                StagingPath = staging, StagingParentIdentity = parentIdentity,
                Stage = MigrationStage.Copying
            }, record.Revision, cancellationToken).ConfigureAwait(false);
            saving = false;
            cancellationToken.ThrowIfCancellationRequested();
            identity.RequireSame(parent, parentIdentity);
            if (!CreateDirectoryW(staging, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"不能新建唯一 staging：{staging}");
            actual = staging;
            DirectoryIdentity stageIdentity = identity.ReadDirectory(staging, false);
            saving = true;
            record = await store.SaveAsync(record with { StagingIdentity = stageIdentity }, record.Revision,
                CancellationToken.None).ConfigureAwait(false);
            saving = false;
            Report(CopyPhase.Preparing);
            cancellationToken.ThrowIfCancellationRequested();
            identity.RequireSame(staging, stageIdentity);
            if (Directory.EnumerateFileSystemEntries(staging).Any()) throw new IOException("新建 staging 出现未知内容，拒绝复制。");
            // /E 不替换既有目的根目录 ACL；先冻结源根权限，避免继承目标父目录的规则。
            RootSecuritySnapshot.Apply(staging, before.Entries[""].AclSddl);
            identity.RequireSame(staging, stageIdentity);
            long previousBytes = 0;
            if (hardLinkAnalysis.HasInternalHardLinks)
            {
                logProgress?.Report(
                    $"[{actionName}] 检测到 {hardLinkAnalysis.InternalGroups.Length} 组目录内闭环硬链接（共 {secondaryHardLinkPaths.Count} 个共享链接项），启用无损硬链接重建模式，零空间膨胀。");
                previousBytes = await ExecuteHardLinkAwareCopyAsync(
                    before,
                    source,
                    staging,
                    hardLinkAnalysis,
                    secondaryHardLinkPaths,
                    Math.Clamp(robocopyThreads, 1, 16),
                    Math.Clamp(sampleMilliseconds, 10, 10000),
                    (bytes, speed) => Report(CopyPhase.Sampling, bytes, speed),
                    () => identity.RequireSame(staging, stageIdentity),
                    cancellationToken).ConfigureAwait(false);
                exitCode = 0;
            }
            else
            {
                var arguments = new[] { source, staging, "/E", "/COPYALL", "/DCOPY:DAT", "/R:0", "/W:0", "/XJ", "/SL",
                    "/XC", "/XN", "/XO", "/BYTES", "/FP", "/UNICODE", $"/MT:{Math.Clamp(robocopyThreads, 1, 128)}" };
                job = new OwnedProcessJob();
                job.Start(Path.Combine(Environment.SystemDirectory, "robocopy.exe"), arguments, parent, Encoding.Unicode);
                processExited = false;
                registration = cancellationToken.Register(() =>
                {
                    try { job.Stop(); }
                    catch (Exception ex) { Interlocked.CompareExchange(ref stopError, ex.Message, null); }
                });
                stdout = Drain(job.Output, false);
                stderr = Drain(job.Error, true);
                Task exit = job.WaitForExitAsync(cancellationToken);
                Task lifecycle = Task.WhenAll(stdout, stderr, exit);
                Report(CopyPhase.ProcessStarted);
                cancellationToken.ThrowIfCancellationRequested();
                identity.RequireSame(source, before.RootIdentity);
                identity.RequireSame(staging, stageIdentity);
                identity.RequireSame(parent, parentIdentity);
                // 两个读取任务与退出等待此时已经挂起；根进程仍受控挂起，取消可确定复现。
                Report(CopyPhase.ReadingOutput);
                cancellationToken.ThrowIfCancellationRequested();
                Report(CopyPhase.WaitingForExit);
                cancellationToken.ThrowIfCancellationRequested();
                job.Resume();
                var clock = Stopwatch.StartNew();
                double previousSeconds = 0;
                double speed = 0;
                do
                {
                    if (stdout.IsFaulted) await stdout.ConfigureAwait(false);
                    if (stderr.IsFaulted) await stderr.ConfigureAwait(false);
                    if (exit.IsFaulted) await exit.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    Report(CopyPhase.Sampling, previousBytes, speed);
                    await Task.WhenAny(lifecycle, Task.Delay(Math.Clamp(sampleMilliseconds, 10, 10000), cancellationToken)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    identity.RequireSame(staging, stageIdentity);
                    long bytes = FileStatsService.GetDirectorySize(staging);
                    double seconds = clock.Elapsed.TotalSeconds;
                    double instantaneous = seconds > previousSeconds ? Math.Max(0, bytes - previousBytes) / (seconds - previousSeconds) : 0;
                    speed = speed == 0 ? instantaneous : 0.3 * instantaneous + 0.7 * speed;
                    previousBytes = bytes;
                    previousSeconds = seconds;
                } while (!lifecycle.IsCompleted);
                await lifecycle.ConfigureAwait(false);
                processExited = job.HasExited && job.ActiveProcesses == 0;
                if (!processExited) throw new IOException("无法确认本任务进程集已经退出。");
                exitCode = job.ExitCode;
                if (exitCode is < 0 or >= 8) throw new IOException($"Robocopy {actionName}失败，退出码 {exitCode}；数据保留于 {staging}。");
            }
            Report(CopyPhase.Verifying, previousBytes);
            cancellationToken.ThrowIfCancellationRequested();
            identity.RequireSame(staging, stageIdentity);
            identity.RequireSame(parent, parentIdentity);
            summary = await verification.VerifyAsync(before, source, staging, record.VerificationMode, cancellationToken).ConfigureAwait(false);
            if (!summary.IsMatch) throw new IOException(string.Join(Environment.NewLine, summary.Mismatches));
            saving = true;
            record = await store.SaveAsync(record with { Stage = MigrationStage.CopyVerified, VerificationSummary = summary },
                record.Revision, cancellationToken).ConfigureAwait(false);
            saving = false;
            Report(CopyPhase.Promoting, previousBytes);
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Mode == MigrationMode.Migrate)
            {
                identity.RequireSame(parent, parentIdentity);
                identity.RequireSame(staging, stageIdentity);
                CheckFinalTarget();
                if (record.ReservedEmptyTargetIdentity is not null) Directory.Delete(record.TargetPath, recursive: false);
                identity.RequireMissing(record.TargetPath);
                Directory.Move(staging, record.TargetPath);
                promoted = true;
                actual = record.TargetPath;
                identity.RequireSame(record.TargetPath, stageIdentity);
                saving = true;
                record = await store.SaveAsync(record with { TargetIdentity = stageIdentity }, record.Revision,
                    CancellationToken.None).ConfigureAwait(false);
                saving = false;
            }
            Report(CopyPhase.Completed, stats.TotalBytes);
            return Result(null, false);

            async Task Drain(StreamReader reader, bool error)
            {
                while (await reader.ReadLineAsync(ioCancellation.Token).ConfigureAwait(false) is string line)
                {
                    if (!string.IsNullOrWhiteSpace(line)) logProgress?.Report($"[Robocopy-{actionName}{(error ? "-stderr" : "")}] {line}");
                }
            }
        }
        catch (Exception ex)
        {
            string error = ex.Message;
            if (job is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    job.Stop();
                    await job.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                    processExited = job.HasExited && job.ActiveProcesses == 0;
                    if (processExited && job.Id is not null) exitCode = job.ExitCode;
                }
                catch (Exception cleanupError)
                {
                    processExited = false;
                    error += $"；无法确认本任务进程退出：{cleanupError.Message}";
                }
                ioCancellation.Cancel();
                try { await Task.WhenAll(stdout ?? Task.CompletedTask, stderr ?? Task.CompletedTask).ConfigureAwait(false); }
                catch (Exception readError) { error += $"；日志读取已结束：{readError.Message}"; }
            }
            recovery = !processExited || promoted || (actual.Length != 0 && actual != source && (saving || record.StagingIdentity is null));
            if (sourceAdmitted)
            {
                try { identity.RequireSame(source, before?.RootIdentity ?? (record.Mode == MigrationMode.Restore ? record.TargetIdentity : record.SourceIdentity)); }
                catch (Exception sourceError)
                {
                    sourceAdmitted = false;
                    recovery = true;
                    error += $"；源实体身份无法确认：{sourceError.Message}";
                    if (actual == source) actual = "";
                }
            }
            if (actual.Length != 0 && actual != source)
            {
                try { identity.RequireSame(actual, record.StagingIdentity); }
                catch (Exception dataError)
                {
                    recovery = true;
                    error += $"；staging/提升实体的位置身份无法确认：{dataError.Message}";
                    actual = sourceAdmitted ? source : "";
                }
            }
            if (staging.Length != 0 && record.StagingParentIdentity is not null)
            {
                try { identity.RequireSame(Path.GetDirectoryName(staging)!, record.StagingParentIdentity); }
                catch (Exception parentError) { recovery = true; error += $"；复制父目录身份变化：{parentError.Message}"; }
            }
            if (stopError is not null) error += $"；取消请求：{stopError}";
            bool cancelled = cancellationToken.IsCancellationRequested;
            if (store is not null)
            {
                try
                {
                    record = await store.SaveAsync(record with
                    {
                        Stage = recovery ? MigrationStage.RecoveryRequired : cancelled ? MigrationStage.Cancelled : MigrationStage.Failed,
                        VerificationSummary = summary, LastError = error
                    }, record.Revision, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception saveError)
                {
                    recovery = true;
                    error += $"；状态保存失败，保留全部实体：{saveError.Message}";
                }
            }
            return Result(error, cancelled);
        }
        finally
        {
            registration.Dispose();
            job?.Dispose();
        }

        void CheckFinalTarget()
        {
            if (record.ReservedEmptyTargetIdentity is null) identity.RequireMissing(record.TargetPath);
            else
            {
                identity.RequireSame(record.TargetPath, record.ReservedEmptyTargetIdentity);
                if (Directory.EnumerateFileSystemEntries(record.TargetPath).Any()) throw new IOException("目标出现未知内容，禁止清空或覆盖。");
            }
        }

        void Report(CopyPhase phase, long bytes = 0, double speed = 0) => progress?.Report(new MigrationProgress
        {
            Stage = record.Stage, CopyPhase = phase, ProcessId = job?.Id, CurrentPhase = 3,
            PhaseDescription = $"{actionName}文件", CopiedBytes = bytes, TotalBytes = stats.TotalBytes,
            SpeedBytesPerSecond = speed,
            PercentComplete = phase == CopyPhase.Completed ? 90 : 10 + (stats.TotalBytes > 0 ? Math.Clamp(bytes * 80.0 / stats.TotalBytes, 0, 80) : 0),
            EstimatedTimeRemaining = speed > 0 ? TimeSpan.FromSeconds(Math.Max(0, stats.TotalBytes - bytes) / speed) : null,
            Message = $"{phase}：{actual}"
        });

        CopyExecutionResult Result(string? error, bool cancelled) => new()
        {
            ExitCode = exitCode, StagingPath = staging, ActualDataPath = actual,
            Error = error, ProcessExited = processExited, Cancelled = cancelled, RecoveryRequired = recovery,
            ProcessId = job?.Id, Transaction = record, SourceManifest = before, Verification = summary
        };
    }

    /// <summary>
    /// 面向闭环纯内部硬链接目录的无损重建复制核心：仅复制每组唯一主文件与 ADS，通过 Win32 CreateHardLinkW 在目标目录重建全部共享硬链接。
    /// </summary>
    public static async Task<long> ExecuteHardLinkAwareCopyAsync(
        FileManifest before,
        string sourceRoot,
        string stagingRoot,
        CopyVerificationService.ManifestHardLinkAnalysis hardLinkAnalysis,
        HashSet<string> secondaryRelativePaths,
        int maxDegreeOfParallelism,
        int sampleMilliseconds,
        Action<long, double> reportProgress,
        Action verifyStagingIdentity,
        CancellationToken cancellationToken)
    {
        // 1. 按深度自顶向下创建所有子目录骨架（暂不设只读属性，确保后续文件与硬链接可写入）
        FileManifestEntry[] subDirectories = before.Entries.Values
            .Where(e => e.IsDirectory && e.RelativePath.Length > 0)
            .OrderBy(e => CountSeparators(e.RelativePath))
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (FileManifestEntry dir in subDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetDir = Path.Combine(stagingRoot, dir.RelativePath);
            Directory.CreateDirectory(targetDir);
        }

        // 2. 并发复制全部唯一实体文件（普通文件 + 每组内部硬链接的 Primary 主文件）及其 ADS 备用流
        FileManifestEntry[] primaryFiles = before.Entries.Values
            .Where(e => !e.IsDirectory && !secondaryRelativePaths.Contains(e.RelativePath))
            .ToArray();

        long totalCopiedBytes = 0;
        var clock = Stopwatch.StartNew();
        long lastReportMs = 0;
        long lastReportedBytes = 0;
        double smoothedSpeed = 0;
        var reportLock = new object();

        void MaybeReport(bool force = false)
        {
            long nowMs = clock.ElapsedMilliseconds;
            lock (reportLock)
            {
                if (!force && nowMs - lastReportMs < sampleMilliseconds) return;
                long currentBytes = Interlocked.Read(ref totalCopiedBytes);
                double dt = (nowMs - lastReportMs) / 1000.0;
                double instant = dt > 0.001 ? Math.Max(0, currentBytes - lastReportedBytes) / dt : 0;
                smoothedSpeed = smoothedSpeed == 0 ? instant : 0.3 * instant + 0.7 * smoothedSpeed;
                lastReportMs = nowMs;
                lastReportedBytes = currentBytes;
                reportProgress(currentBytes, smoothedSpeed);
            }
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(primaryFiles, parallelOptions, async (entry, ct) =>
        {
            string srcPath = Path.Combine(sourceRoot, entry.RelativePath);
            string dstPath = Path.Combine(stagingRoot, entry.RelativePath);
            string? dstParent = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dstParent))
            {
                Directory.CreateDirectory(dstParent);
            }

            await CopySingleFileStreamAsync(srcPath, dstPath, read =>
            {
                Interlocked.Add(ref totalCopiedBytes, read);
                MaybeReport();
            }, ct).ConfigureAwait(false);

            // 复制该唯一主文件的全部非默认 ADS 数据流
            foreach (AlternateStreamManifest ads in entry.AlternateStreams)
            {
                string streamSuffix = ads.Name.EndsWith(":$DATA", StringComparison.Ordinal)
                    ? ads.Name[..^6]
                    : ads.Name;
                await CopySingleFileStreamAsync(srcPath + streamSuffix, dstPath + streamSuffix, read =>
                {
                    Interlocked.Add(ref totalCopiedBytes, read);
                    MaybeReport();
                }, ct).ConfigureAwait(false);
            }

            CopyFileAclFromSource(srcPath, dstPath);
            File.SetCreationTimeUtc(dstPath, File.GetCreationTimeUtc(srcPath));
            File.SetLastWriteTimeUtc(dstPath, entry.LastWriteTimeUtc);
        }).ConfigureAwait(false);

        verifyStagingIdentity();

        // 3. 在目标 staging 内对同组全部 Secondary 路径调用 Win32 CreateHardLinkW 无损重建硬链接（零额外磁盘空间）
        foreach (CopyVerificationService.ManifestHardLinkGroup group in hardLinkAnalysis.InternalGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dstPrimary = Path.Combine(stagingRoot, group.PrimaryRelativePath);
            foreach (string secRel in group.SecondaryRelativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string dstSecondary = Path.Combine(stagingRoot, secRel);
                string? secParent = Path.GetDirectoryName(dstSecondary);
                if (!string.IsNullOrEmpty(secParent))
                {
                    Directory.CreateDirectory(secParent);
                }

                if (!CreateHardLinkW(@"\\?\" + dstSecondary, @"\\?\" + dstPrimary, IntPtr.Zero))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"在目标目录重建内部硬链接失败：{dstSecondary} -> {dstPrimary}");
                }
            }
        }

        // 4. 恢复全部唯一主文件的最终文件属性（含 ReadOnly/Hidden/System）并重新确认写入时间（同组硬链接共享同一 MFT 实体自动生效）
        foreach (FileManifestEntry entry in primaryFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string dstPath = Path.Combine(stagingRoot, entry.RelativePath);
            File.SetLastWriteTimeUtc(dstPath, entry.LastWriteTimeUtc);
            File.SetAttributes(dstPath, entry.Attributes);
        }

        // 5. 自底向上恢复子目录的 ACL、时间戳与属性（避免写入子文件冲掉父目录 LastWriteTimeUtc）
        for (int i = subDirectories.Length - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileManifestEntry dir = subDirectories[i];
            string srcDir = Path.Combine(sourceRoot, dir.RelativePath);
            string dstDir = Path.Combine(stagingRoot, dir.RelativePath);
            CopyDirectoryAclFromSource(srcDir, dstDir);
            Directory.SetCreationTimeUtc(dstDir, Directory.GetCreationTimeUtc(srcDir));
            Directory.SetLastWriteTimeUtc(dstDir, dir.LastWriteTimeUtc);
            File.SetAttributes(dstDir, dir.Attributes);
        }

        // 6. 恢复根目录时间戳与属性（根目录 ACL 已在复制前由 RootSecuritySnapshot.Apply 冻结）
        FileManifestEntry rootEntry = before.Entries[""];
        Directory.SetLastWriteTimeUtc(stagingRoot, rootEntry.LastWriteTimeUtc);
        FileAttributes rootAttrs = rootEntry.Attributes & ~FileAttributes.ReadOnly;
        if (rootAttrs != 0)
        {
            File.SetAttributes(stagingRoot, rootAttrs);
        }
        if ((rootEntry.Attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(stagingRoot, rootEntry.Attributes);
        }

        MaybeReport(force: true);
        return Interlocked.Read(ref totalCopiedBytes);
    }

    private static async Task CopySingleFileStreamAsync(
        string srcPath,
        string dstPath,
        Action<int> onChunkCopied,
        CancellationToken ct)
    {
        const int bufferSize = 1024 * 1024;
        await using var input = new FileStream(
            srcPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            dstPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        byte[] buffer = new byte[bufferSize];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            onChunkCopied(read);
        }
    }

    private static void CopyFileAclFromSource(string srcPath, string dstPath)
    {
        var srcInfo = new FileInfo(srcPath);
        var dstInfo = new FileInfo(dstPath);
        try
        {
            FileSecurity srcSec = srcInfo.GetAccessControl(AccessControlSections.All);
            var dstSec = new FileSecurity();
            dstSec.SetSecurityDescriptorBinaryForm(srcSec.GetSecurityDescriptorBinaryForm());
            dstInfo.SetAccessControl(dstSec);
        }
        catch (Exception ex) when (ex is PrivilegeNotHeldException or UnauthorizedAccessException)
        {
            FileSecurity srcSec = srcInfo.GetAccessControl(AccessControlSections.Access);
            var dstSec = new FileSecurity();
            dstSec.SetSecurityDescriptorBinaryForm(srcSec.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            dstInfo.SetAccessControl(dstSec);
        }
    }

    private static void CopyDirectoryAclFromSource(string srcPath, string dstPath)
    {
        var srcInfo = new DirectoryInfo(srcPath);
        var dstInfo = new DirectoryInfo(dstPath);
        try
        {
            DirectorySecurity srcSec = srcInfo.GetAccessControl(AccessControlSections.All);
            var dstSec = new DirectorySecurity();
            dstSec.SetSecurityDescriptorBinaryForm(srcSec.GetSecurityDescriptorBinaryForm());
            dstInfo.SetAccessControl(dstSec);
        }
        catch (Exception ex) when (ex is PrivilegeNotHeldException or UnauthorizedAccessException)
        {
            DirectorySecurity srcSec = srcInfo.GetAccessControl(AccessControlSections.Access);
            var dstSec = new DirectorySecurity();
            dstSec.SetSecurityDescriptorBinaryForm(srcSec.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            dstInfo.SetAccessControl(dstSec);
        }
    }

    private static int CountSeparators(string path)
    {
        int count = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\') count++;
        }
        return count;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
