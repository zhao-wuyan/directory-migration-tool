using System.Runtime.InteropServices;
using System.Text;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Scanning;

internal static partial class Program
{
    private static readonly string[] MftScanCaseNames =
    [
        "M01-raw-mft-scan-real-ntfs-vhdx",
        "M02-mft-vs-directory-walk-hardlink-ads-reparse"
    ];

    private static async Task RunMftScanCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        async Task Case(string name, Func<Dictionary<string, object?>, Task> action) =>
            await RunCase(lab, cases.Single(c => c.Name == name), action);

        string scope = Path.Combine(lab.Source.Root, @"Data\Sources\Sample");
        string fixtureRoot = Path.Combine(scope, $"MftFixture-{attempt:D}");
        string subA = Path.Combine(fixtureRoot, "SubA");
        string subB = Path.Combine(fixtureRoot, "SubB");
        string linkDir = Path.Combine(fixtureRoot, "DirLink");
        string protectedRoot = Path.Combine(lab.Source.Root, @"Data\Protected");

        await Case("M01-raw-mft-scan-real-ntfs-vhdx", async o =>
        {
            NewDirectory(lab, lab.Source, scope, fixtureRoot);
            NewDirectory(lab, lab.Source, fixtureRoot, subA);
            NewDirectory(lab, lab.Source, fixtureRoot, subB);

            // 常驻小文件 + 非驻留大文件 + 命名 ADS + 硬链接 + 目录符号链接
            WriteFile(lab, lab.Source, subA, Path.Combine(subA, "resident.txt"), Encoding.UTF8.GetBytes("hello resident"));
            byte[] largeBytes = new byte[65536];
            Array.Fill(largeBytes, (byte)0x5A);
            string mainFile = Path.Combine(subA, "shared.bin");
            WriteFile(lab, lab.Source, subA, mainFile, largeBytes);
            File.WriteAllText(mainFile + ":custom_ads", "alternate stream payload");

            string hardLinkPath = Path.Combine(subB, "shared_hardlink.bin");
            Require(CreateHardLinkW(hardLinkPath, mainFile, IntPtr.Zero), "CreateHardLinkW failed in MFT fixture");
            Directory.CreateSymbolicLink(linkDir, protectedRoot);

            var scanService = new DiskScanService();
            ScanSnapshot mftSnap = await scanService.ScanVolumeAsync(lab.Source.Root, allowFallbackToDirectoryWalk: false);
            Require(mftSnap.Backend == ScanBackendKind.RawNtfsMft, "M01 must execute real RawNtfsMft backend without fallback");
            Require(mftSnap.Nodes.Length > 5, "MFT snapshot must contain real NTFS nodes");

            int fixtureNodeId = mftSnap.Nodes.First(n => mftSnap.GetNodeName(n.NodeId).Equals(Path.GetFileName(fixtureRoot), StringComparison.OrdinalIgnoreCase)).NodeId;
            var hazards = mftSnap.InspectSubtreeHazards(fixtureNodeId);
            Require(hazards.HasHardLinks, "MFT scan must detect hard link inside fixture subtree");
            Require(hazards.HasReparseDescendants, "MFT scan must detect directory symlink without following");

            o["backend"] = mftSnap.Backend.ToString();
            o["nodeCount"] = mftSnap.Nodes.Length;
            o["fixtureUniqueAllocated"] = mftSnap.Nodes[fixtureNodeId].UniqueAllocatedBytes;
            o["fixtureLogical"] = mftSnap.Nodes[fixtureNodeId].LogicalBytes;
        });

        await Case("M02-mft-vs-directory-walk-hardlink-ads-reparse", async o =>
        {
            try
            {
                var vol = new DiskScanService().ListVolumes().Single(v => v.DriveRoot.Equals(lab.Source.Root, StringComparison.OrdinalIgnoreCase));
                var walker = new DirectoryWalkScanner();
                ScanSnapshot walkSnap = await walker.ScanAsync(vol, rootDirectoryOverride: fixtureRoot, fallbackReason: "harness-compare");
                Require(walkSnap.Backend == ScanBackendKind.DirectoryWalkFallback, "DirectoryWalk must declare fallback backend");

                int linkNode = walkSnap.Nodes.First(n => walkSnap.GetNodeName(n.NodeId) == "DirLink").NodeId;
                Require(walkSnap.Nodes[linkNode].IsReparsePoint && walkSnap.GetChildrenSpan(linkNode).Length == 0,
                    "DirectoryWalk must not follow directory symlink");

                o["walkNodeCount"] = walkSnap.Nodes.Length;
                o["walkLogical"] = walkSnap.Nodes[0].LogicalBytes;
            }
            finally
            {
                if (Directory.Exists(linkDir)) Directory.Delete(linkDir, false);
                if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, true);
            }
        });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
