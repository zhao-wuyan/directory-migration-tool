using System.Security.AccessControl;
using System.Security.Principal;
using MigrationCore.Models.Safety;
using MigrationCore.Services;
using MigrationCore.Services.Safety;

internal static partial class Program
{
    private static readonly string[] PolicyCaseNames =
    [
        "P01-L0-hard-denied-system-and-reserved",
        "P02-L3-lab-volume-and-junction-ancestor-rejected",
        "P03-HasUserContent-ACL-fail-closed"
    ];

    private static async Task RunPolicyCases(Lab lab, Guid attempt, CaseEvidence[] cases)
    {
        async Task Case(string name, Func<Dictionary<string, object?>, Task> action) =>
            await RunCase(lab, cases.Single(c => c.Name == name), action);

        await Case("P01-L0-hard-denied-system-and-reserved", o =>
        {
            string[] mustReject =
            [
                lab.Source.Root,
                Path.Combine(lab.Source.Root, "System Volume Information"),
                Path.Combine(lab.Source.Root, "$Recycle.Bin"),
                Path.Combine(lab.Source.Root, "Boot"),
                Path.Combine(lab.Source.Root, ".MoveFolderQuarantine"),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            ];

            foreach (string path in mustReject)
            {
                var decision = MigrationSafetyPolicy.EvaluateL0HardDeny(path, checkFilesystem: false);
                Require(decision.IsHardDenied, $"Expected L0 HardDenied for {path}");
            }
            o["verifiedBlockedCount"] = mustReject.Length;
            return Task.CompletedTask;
        });

        await Case("P02-L3-lab-volume-and-junction-ancestor-rejected", o =>
        {
            string scope = Path.Combine(lab.Source.Root, @"Data\Sources\Sample");
            string normalDir = Path.Combine(scope, $"PolicyNormal-{attempt:D}");
            string decoyLink = Path.Combine(scope, $"PolicyLink-{attempt:D}");
            string protectedRoot = Path.Combine(lab.Source.Root, @"Data\Protected");

            NewDirectory(lab, lab.Source, scope, normalDir);
            var normalDecision = MigrationSafetyPolicy.EvaluateSource(normalDir, checkFilesystem: true);
            Require(!normalDecision.IsHardDenied && normalDecision.Level == MigrationAdmissionLevel.ManualRiskConfirm,
                $"Normal lab directory should be L3 ManualRiskConfirm, got {normalDecision.Level}");

            lab.BeforeWrite();
            Directory.CreateSymbolicLink(decoyLink, protectedRoot);
            try
            {
                var linkDecision = MigrationSafetyPolicy.EvaluateSource(decoyLink, checkFilesystem: true);
                Require(linkDecision.IsHardDenied, "Reparse point leaf must be L0 HardDenied");

                var ancestorDecision = MigrationSafetyPolicy.EvaluateTarget(Path.Combine(decoyLink, "Child"), checkFilesystem: true);
                Require(ancestorDecision.IsHardDenied, "Reparse point ancestor on target must be L0 HardDenied");
                o["normalLevel"] = normalDecision.Level.ToString();
                o["linkReason"] = linkDecision.Summary;
            }
            finally
            {
                Directory.Delete(decoyLink, false);
                Directory.Delete(normalDir, false);
            }
            return Task.CompletedTask;
        });

        await Case("P03-HasUserContent-ACL-fail-closed", o =>
        {
            string scope = Path.Combine(lab.Target.Root, @"Data\Targets");
            string dir = Path.Combine(scope, $"PolicyAcl-{attempt:D}");
            NewDirectory(lab, lab.Target, scope, dir);
            string originalSddl = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                var denied = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
                denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
                new DirectoryInfo(dir).SetAccessControl(denied);

                Require(PathValidator.HasUserContent(dir), "HasUserContent must return true on ACL deny.");
                var empty = PathValidator.IsTargetDirectoryEmpty(dir);
                Require(!empty.IsEmpty, "IsTargetDirectoryEmpty must return false on ACL deny.");
                o["error"] = empty.Error;
            }
            finally
            {
                var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorSddlForm(originalSddl, AccessControlSections.Access);
                new DirectoryInfo(dir).SetAccessControl(restored);
                Directory.Delete(dir, false);
            }
            return Task.CompletedTask;
        });
    }
}
