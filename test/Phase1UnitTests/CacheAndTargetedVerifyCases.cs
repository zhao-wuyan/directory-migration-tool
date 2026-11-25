using System.Diagnostics;
using System.IO;
using System.Windows;
using MigrationCore.Models;
using MigrationCore.Models.Scanning;
using MigrationCore.Services;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;
using MoveWithSymlinkWPF;
using MoveWithSymlinkWPF.ViewModels;

internal static class CacheAndTargetedVerifyCases
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("VolumeCache-two-tier-TTL-hard-expiry-and-pin-protection", () =>
        {
            DateTimeOffset clock = new(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
            var options = new VolumeSnapshotCacheOptions
            {
                FreshTtl = TimeSpan.FromMinutes(15),
                HardExpiryTtl = TimeSpan.FromMinutes(60),
                EvictionSweepInterval = TimeSpan.Zero,
                MaxCachedVolumes = 3
            };

            using var cache = new VolumeSnapshotCache(
                options: options,
                utcNowProvider: () => clock,
                liveProbeProvider: root => new VolumeLiveProbe(root, 50L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024, @"\\?\Volume{test-c}\"));

            ScanSnapshot snapC = BuildDummySnapshot(@"C:\", clock, 50L * 1024 * 1024 * 1024, @"\\?\Volume{test-c}\");
            cache.Put(snapC, "系统盘 (C:)", 1.5);

            // 1. 0~15 min -> Fresh
            clock = clock.AddMinutes(5);
            VolumeSnapshotLookup? fresh = cache.TryGet(@"C:\");
            Require(fresh is not null && fresh.IsFresh, "Snapshot at 5min must be Fresh");

            // 2. 15~60 min -> Stale (still browsable)
            clock = clock.AddMinutes(15); // t = 20min
            VolumeSnapshotLookup? stale = cache.TryGet(@"C:\");
            Require(stale is not null && stale.IsStale && !string.IsNullOrWhiteSpace(stale.StaleReason),
                "Snapshot at 20min must be Stale with reason");

            // 3. Pin active snapshot past 60min -> NOT evicted while pinned
            cache.SetPinnedScanId(snapC.ScanId);
            clock = clock.AddMinutes(55); // t = 75min
            Require(cache.SweepExpired() == 0 && cache.Count == 1, "Pinned snapshot must not be evicted past HardExpiryTtl");
            VolumeSnapshotLookup? pinnedLookup = cache.TryGet(@"C:\");
            Require(pinnedLookup is not null && pinnedLookup.IsStale, "Pinned snapshot past HardExpiryTtl must remain readable as Stale");

            // 4. Unpin -> immediately evicted
            cache.SetPinnedScanId(null);
            Require(cache.Count == 0 && cache.TryGet(@"C:\") is null, "Unpinned snapshot past HardExpiryTtl must be evicted");
            return Task.CompletedTask;
        });

        await test("VolumeCache-LRU-cap-free-space-drift-and-gate-invalidation", () =>
        {
            DateTimeOffset clock = new(2026, 11, 25, 11, 0, 0, TimeSpan.Zero);
            long liveFreeC = 50L * 1024 * 1024 * 1024;
            var gate = new OperationExecutionGate();

            var options = new VolumeSnapshotCacheOptions
            {
                FreshTtl = TimeSpan.FromMinutes(15),
                HardExpiryTtl = TimeSpan.FromMinutes(60),
                EvictionSweepInterval = TimeSpan.Zero,
                MaxCachedVolumes = 2,
                FreeSpaceDriftThresholdBytes = 512L * 1024 * 1024
            };

            using var cache = new VolumeSnapshotCache(
                options: options,
                utcNowProvider: () => clock,
                liveProbeProvider: root => new VolumeLiveProbe(root, root.StartsWith("C", StringComparison.OrdinalIgnoreCase) ? liveFreeC : 50L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024),
                executionGate: gate);

            cache.Put(BuildDummySnapshot(@"C:\", clock, 50L * 1024 * 1024 * 1024), "C:", 1.0);
            clock = clock.AddSeconds(10);
            cache.Put(BuildDummySnapshot(@"D:\", clock, 50L * 1024 * 1024 * 1024), "D:", 1.0);

            // Touch C so D becomes least recently used
            clock = clock.AddSeconds(10);
            Require(cache.TryGet(@"C:\") is not null, "C should hit cache");

            // Put E -> D should be evicted, C and E retained
            clock = clock.AddSeconds(10);
            cache.Put(BuildDummySnapshot(@"E:\", clock, 50L * 1024 * 1024 * 1024), "E:", 1.0);
            Require(cache.Count == 2, $"LRU cap should keep 2 entries, got {cache.Count}");
            Require(cache.TryGet(@"D:\") is null, "Least recently used drive D should have been evicted");
            Require(cache.TryGet(@"C:\") is not null && cache.TryGet(@"E:\") is not null, "C and E should remain in cache");

            // External free space drift > 512 MiB on C -> immediately marked Stale
            liveFreeC -= 700L * 1024 * 1024;
            VolumeSnapshotLookup? driftedC = cache.TryGet(@"C:\", runLiveProbe: true);
            Require(driftedC is not null && driftedC.IsStale && driftedC.StaleReason!.Contains("剩余空间"),
                $"Free space drift must mark C as Stale, got {driftedC?.Freshness} / {driftedC?.StaleReason}");

            // OperationExecutionGate completion invalidates affected drives automatically
            using (var lease = gate.TryAcquire("BatchMigration", "测试迁移", [@"E:\Games\Demo", @"F:\Migrated\Demo"]))
            {
                Require(lease is not null, "Gate acquire should succeed");
            }
            VolumeSnapshotLookup? invalidatedE = cache.TryGet(@"E:\", runLiveProbe: false);
            Require(invalidatedE is not null && invalidatedE.IsStale && invalidatedE.StaleReason!.Contains("测试迁移"),
                $"Gate completion must invalidate affected drive E, got {invalidatedE?.Freshness} / {invalidatedE?.StaleReason}");

            return Task.CompletedTask;
        });

        await test("DirectoryWalk-captures-real-64bit-FileId-matching-PathIdentityService", async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "MoveFolder_FileIdWalk_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string filePath = Path.Combine(root, "sample.bin");
            File.WriteAllBytes(filePath, new byte[4096]);

            try
            {
                var identity = new PathIdentityService();
                DirectoryIdentity expectedFileId = identity.ReadEntry(filePath, followLink: false);

                var vol = new ScanVolumeInfo(Path.GetPathRoot(root)!, "Temp", expectedFileId.VolumeGuid, expectedFileId.VolumeSerial, "NTFS", 100_000_000, 50_000_000, false, "UnitTest");
                var walker = new DirectoryWalkScanner();
                ScanSnapshot snap = await walker.ScanAsync(vol, rootDirectoryOverride: root, fallbackReason: "fileid-test");

                int fileNodeIdx = Array.FindIndex(snap.Nodes, n => !n.IsDirectory && snap.GetNodeName(n.NodeId) == "sample.bin");
                Require(fileNodeIdx > 0, "Expected sample.bin node in walk snapshot");

                ulong walkFileId = TargetedPathVerifier.ComposeFileId(snap.Nodes[fileNodeIdx]);
                Require(walkFileId != 0 && walkFileId == expectedFileId.FileId,
                    $"Walk FileId 0x{walkFileId:X16} must match PathIdentityService FileId 0x{expectedFileId.FileId:X16}");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        });

        await test("TargetedMigrationVerify-usn-fast-path-and-size-drift-and-reparse-and-identity-stop", async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "MoveFolder_TargetMig_" + Guid.NewGuid().ToString("N"));
            string srcDir = Path.Combine(root, "AppData");
            string outsideDir = Path.Combine(root, "Outside");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(outsideDir);
            File.WriteAllBytes(Path.Combine(srcDir, "base.dat"), new byte[1024 * 1024]); // 1 MiB

            try
            {
                var identity = new PathIdentityService();
                DirectoryIdentity srcIdentity = identity.ReadDirectory(srcDir, followLink: false);

                // 1. USN zero-IO fast path when journalId & nextUsn unchanged
                ScanSnapshot stableMftSnap = BuildDummySnapshot(
                    Path.GetPathRoot(srcDir)!,
                    DateTimeOffset.UtcNow,
                    50L * 1024 * 1024 * 1024,
                    srcIdentity.VolumeGuid,
                    backend: ScanBackendKind.RawNtfsMft,
                    usnJournalId: 12345,
                    usnEndNextUsn: 99999,
                    requiresLiveReverify: false);

                var fastVerifier = new TargetedPathVerifier(
                    usnQuery: (string _, out long jid, out long usn) =>
                    {
                        jid = 12345;
                        usn = 99999;
                        return true;
                    });

                TargetedMigrationVerificationResult fastRes = await fastVerifier.VerifyDirectoryForMigrationAsync(
                    srcDir, srcIdentity, 1024 * 1024, 1024 * 1024, cachedSnapshot: stableMftSnap);
                Require(fastRes.IsAllowed && fastRes.UsedUsnFastPath && fastRes.ReasonCode == TargetedVerificationReasonCode.UsnUnchangedFastPath,
                    $"Expected USN fast-path pass, got {fastRes.Outcome}/{fastRes.ReasonCode}");

                // 2. USN moved -> targeted walk runs and passes when unchanged
                var liveVerifier = new TargetedPathVerifier(
                    usnQuery: (string _, out long jid, out long usn) =>
                    {
                        jid = 12345;
                        usn = 100500; // moved!
                        return true;
                    });
                var tightOptions = new TargetedMigrationVerifyOptions
                {
                    MaxRelativeSizeGrowth = 0.10,
                    MinSignificantGrowthBytes = 512 * 1024 // 512 KiB for fast unit test
                };

                TargetedMigrationVerificationResult cleanRes = await liveVerifier.VerifyDirectoryForMigrationAsync(
                    srcDir, srcIdentity, 1024 * 1024, 1024 * 1024, cachedSnapshot: stableMftSnap, options: tightOptions);
                Require(cleanRes.IsAllowed && !cleanRes.UsedUsnFastPath && cleanRes.LiveLogicalBytes == 1024 * 1024,
                    $"Unchanged targeted walk must pass, got {cleanRes.Outcome}: {cleanRes.Message}");

                // 3. Significant size growth -> DriftStop
                string extraFile = Path.Combine(srcDir, "new_big.dat");
                File.WriteAllBytes(extraFile, new byte[1024 * 1024]); // +1 MiB (+100%, >= 512 KiB)
                TargetedMigrationVerificationResult driftRes = await liveVerifier.VerifyDirectoryForMigrationAsync(
                    srcDir, srcIdentity, 1024 * 1024, 1024 * 1024, options: tightOptions);
                Require(driftRes.Outcome == TargetedVerificationOutcome.DriftStop
                        && driftRes.ReasonCode == TargetedVerificationReasonCode.SizeGrowthExceededThreshold,
                    $"Significant size growth must trigger DriftStop, got {driftRes.Outcome}/{driftRes.ReasonCode}");
                File.Delete(extraFile);

                // 4. Newly created Junction inside source dir -> HardStop
                string juncPath = Path.Combine(srcDir, "NewLink");
                using (var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{juncPath}\" \"{outsideDir}\"")
                {
                    CreateNoWindow = true, UseShellExecute = false
                })!)
                {
                    await proc.WaitForExitAsync();
                }

                TargetedMigrationVerificationResult reparseRes = await liveVerifier.VerifyDirectoryForMigrationAsync(
                    srcDir, srcIdentity, 1024 * 1024, 1024 * 1024, expectedHasReparseDescendants: false, options: tightOptions);
                Require(reparseRes.Outcome == TargetedVerificationOutcome.HardStop
                        && reparseRes.ReasonCode == TargetedVerificationReasonCode.NewReparsePointDetected,
                    $"New junction in subtree must trigger HardStop, got {reparseRes.Outcome}/{reparseRes.ReasonCode}");
                Directory.Delete(juncPath, false);

                // 5. Recreated same-name directory (FileId changed) -> HardStop
                Directory.Delete(srcDir, true);
                Directory.CreateDirectory(srcDir);
                TargetedMigrationVerificationResult idRes = await liveVerifier.VerifyDirectoryForMigrationAsync(
                    srcDir, srcIdentity, 1024 * 1024, 1024 * 1024, options: tightOptions);
                Require(idRes.Outcome == TargetedVerificationOutcome.HardStop
                        && idRes.ReasonCode == TargetedVerificationReasonCode.IdentityChanged,
                    $"Replaced same-name dir must trigger IdentityChanged HardStop, got {idRes.Outcome}/{idRes.ReasonCode}");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });

        await test("TargetedCleanupVerify-strict-drift-stop-and-missing-file-safe-skip", async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "MoveFolder_TargetClean_" + Guid.NewGuid().ToString("N"));
            string cacheDir = Path.Combine(root, "AppCache");
            string outsideDir = Path.Combine(root, "KeepSafe");
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(outsideDir);

            string fileA = Path.Combine(cacheDir, "a.tmp");
            string fileB = Path.Combine(cacheDir, "b.tmp");
            File.WriteAllBytes(fileA, new byte[2048]);
            File.WriteAllBytes(fileB, new byte[4096]);

            try
            {
                var identity = new PathIdentityService();
                DirectoryIdentity rootId = identity.ReadDirectory(cacheDir, followLink: false);
                DirectoryIdentity idA = identity.ReadEntry(fileA, followLink: false);
                DirectoryIdentity idB = identity.ReadEntry(fileB, followLink: false);
                DateTimeOffset snapTime = DateTimeOffset.UtcNow;

                var expectation = new CleanupDirectoryExpectation(
                    RootDirectoryPath: cacheDir,
                    ExpectedRootIdentity: rootId,
                    SnapshotCompletedAtUtc: snapTime,
                    ExpectedFiles:
                    [
                        new CleanupFileExpectation(fileA, idA.FileId, 2048, File.GetLastWriteTimeUtc(fileA).Ticks),
                        new CleanupFileExpectation(fileB, idB.FileId, 4096, File.GetLastWriteTimeUtc(fileB).Ticks)
                    ]);

                var verifier = new TargetedPathVerifier();

                // 1. b.tmp vanished on its own -> Passed with safe skip
                File.Delete(fileB);
                TargetedCleanupVerificationResult skipRes = await verifier.VerifyDirectoryForCleanupAsync(expectation);
                Require(skipRes.IsAllowed && skipRes.VerifiedReadyPaths.Length == 1 && skipRes.SkippedMissingPaths.Length == 1,
                    $"Missing cleanup file should safely skip, got {skipRes.Outcome}: {skipRes.Message}");

                // 2. Unexpected new file appeared -> DriftStop (never delete more than scanned)
                string newFile = Path.Combine(cacheDir, "c_new.tmp");
                File.WriteAllBytes(newFile, new byte[512]);
                TargetedCleanupVerificationResult newRes = await verifier.VerifyDirectoryForCleanupAsync(expectation);
                Require(newRes.Outcome == TargetedVerificationOutcome.DriftStop
                        && newRes.ReasonCode == TargetedVerificationReasonCode.UnexpectedNewFileDetected,
                    $"Unexpected new file in cleanup dir must trigger DriftStop, got {newRes.Outcome}/{newRes.ReasonCode}");
                File.Delete(newFile);

                // 3. Existing file modified after snapshot -> DriftStop
                File.SetLastWriteTimeUtc(fileA, snapTime.UtcDateTime.AddMinutes(2));
                TargetedCleanupVerificationResult modRes = await verifier.VerifyDirectoryForCleanupAsync(expectation);
                Require(modRes.Outcome == TargetedVerificationOutcome.DriftStop
                        && modRes.ReasonCode == TargetedVerificationReasonCode.FileModifiedSinceSnapshot,
                    $"Modified file after snapshot must trigger DriftStop, got {modRes.Outcome}/{modRes.ReasonCode}");

                // 4. Junction created in cleanup dir -> HardStop
                string junc = Path.Combine(cacheDir, "DangerLink");
                using (var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junc}\" \"{outsideDir}\"")
                {
                    CreateNoWindow = true, UseShellExecute = false
                })!)
                {
                    await proc.WaitForExitAsync();
                }
                TargetedCleanupVerificationResult linkRes = await verifier.VerifyDirectoryForCleanupAsync(expectation);
                Require(linkRes.Outcome == TargetedVerificationOutcome.HardStop
                        && linkRes.ReasonCode == TargetedVerificationReasonCode.NewReparsePointDetected,
                    $"Reparse point in cleanup dir must trigger HardStop, got {linkRes.Outcome}/{linkRes.ReasonCode}");
                Directory.Delete(junc, false);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });

        await test("DiskScanViewModel-card-opens-cached-snapshot-without-rescan-and-surfaces-stale-banner", () =>
        {
            Exception? captured = null;
            var sta = new Thread(() =>
            {
                try
                {
                    DateTimeOffset clock = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
                    using var cache = new VolumeSnapshotCache(
                        options: new VolumeSnapshotCacheOptions
                        {
                            FreshTtl = TimeSpan.FromMinutes(15),
                            HardExpiryTtl = TimeSpan.FromMinutes(60),
                            EvictionSweepInterval = TimeSpan.Zero
                        },
                        utcNowProvider: () => clock,
                        liveProbeProvider: root => new VolumeLiveProbe(root, 50L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024));

                    var vm = new DiskScanViewModel(cache);
                    Require(vm.VolumeCards.Count > 0, "Expected at least one local volume card");
                    VolumeCardItem firstCard = vm.VolumeCards[0];

                    // Initially no cache
                    Require(!firstCard.HasCachedSnapshot && !firstCard.ShowRescanButton, "Card should start uncached");

                    // Populate cache for firstCard's drive
                    ScanSnapshot snap = BuildDummySnapshot(firstCard.DriveRoot, clock, 50L * 1024 * 1024 * 1024);
                    cache.Put(snap, firstCard.DisplayTitle, 1.2);
                    Require(firstCard.HasCachedSnapshot && firstCard.ShowRescanButton && firstCard.ActionText == "查看结果",
                        $"Cached card should display '查看结果' + Rescan button, got HasCache={firstCard.HasCachedSnapshot}, Action={firstCard.ActionText}");

                    // Clicking OpenOrScanVolumeCommand opens DirectoryTree immediately from cache
                    vm.OpenOrScanVolumeCommand.ExecuteAsync(firstCard).GetAwaiter().GetResult();
                    Require(vm.ActiveSubView == DiskScanSubView.DirectoryTree && vm.CurrentSnapshot?.ScanId == snap.ScanId && !vm.IsSnapshotStale,
                        "OpenOrScanVolumeCommand should open cached snapshot directly without rescanning");

                    // Invalidating cache immediately raises Stale banner on active view
                    cache.InvalidateDrive(firstCard.DriveRoot, "刚完成测试迁移");
                    Require(vm.IsSnapshotStale && vm.SnapshotStaleMessage.Contains("刚完成测试迁移"),
                        $"Invalidated cache should show stale banner, got IsStale={vm.IsSnapshotStale}, Msg='{vm.SnapshotStaleMessage}'");
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join();
            if (captured is not null) throw captured;
            return Task.CompletedTask;
        });
    }

    private static ScanSnapshot BuildDummySnapshot(
        string driveRoot,
        DateTimeOffset completedUtc,
        long freeBytes,
        string volumeGuid = "",
        ScanBackendKind backend = ScanBackendKind.DirectoryWalkFallback,
        long? usnJournalId = null,
        long? usnEndNextUsn = null,
        bool requiresLiveReverify = true)
    {
        var pool = new Utf8NamePool();
        var rootName = pool.AddName(driveRoot.AsSpan());
        var nodes = new[]
        {
            new ScanNodeRecord
            {
                NodeId = 0,
                ParentNodeId = -1,
                NamePageIndex = rootName.PageIndex,
                NameOffset = rootName.Offset,
                NameLength = rootName.Length,
                Flags = ScanNodeFlags.IsDirectory,
                LogicalBytes = 1024,
                AllocatedBytes = 1024,
                UniqueAllocatedBytes = 1024,
                LinkCount = 1
            }
        };

        return new ScanSnapshot
        {
            Volume = new ScanVolumeInfo(driveRoot, "Test", volumeGuid, 1, "NTFS", 100L * 1024 * 1024 * 1024, freeBytes, backend == ScanBackendKind.RawNtfsMft, "Test"),
            Backend = backend,
            Coverage = ScanCoverageStatus.Complete,
            StartedAtUtc = completedUtc.AddSeconds(-1),
            CompletedAtUtc = completedUtc,
            PolicyVersion = MigrationSafetyPolicy.CurrentPolicyVersion,
            UsnStartJournalId = usnJournalId,
            UsnStartNextUsn = usnEndNextUsn,
            UsnEndNextUsn = usnEndNextUsn,
            RequiresLiveReverification = requiresLiveReverify,
            RootNodeId = 0,
            Nodes = nodes,
            CsrOffsets = [0, 0],
            CsrChildren = [],
            NamePool = pool
        };
    }
}
