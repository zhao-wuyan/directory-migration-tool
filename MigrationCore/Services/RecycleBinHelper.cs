using System.Runtime.InteropServices;
using MigrationCore.Models;
using static MigrationCore.Services.ShellRecycleInterop;

namespace MigrationCore.Services;

/// <summary>由 Shell 决定能否回收；拒绝永久删除，并以实际回收回调和源路径状态报告结果。</summary>
public static class RecycleBinHelper
{
    public static BackupDeletionResult MoveDirectoryToRecycleBin(Guid requestId, Guid transactionId,
        string directoryPath, FileManifest manifest)
    {
        BackupDeletionResult? result = null;
        var sink = new RecycleProgressSink(directoryPath, manifest);
        // IFileOperation 及所有 RCW 只在这个 STA 中创建、调用和释放。
        var thread = new Thread(() =>
        {
            bool initialized = false;
            try
            {
                Check(CoInitializeEx(IntPtr.Zero, 2)); // COINIT_APARTMENTTHREADED
                initialized = true;
                sink.ValidateInitial();
                RunShell(sink);
            }
            catch (Exception ex) { sink.RecordError(ex.Message); }
            finally
            {
                try { result = sink.CreateResult(requestId, transactionId); }
                finally { if (initialized) CoUninitialize(); }
            }
        });
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }
        catch (Exception ex)
        {
            sink.RecordError(ex.Message);
            return sink.CreateResult(requestId, transactionId);
        }
        return result!;
    }

    private static void RunShell(RecycleProgressSink sink)
    {
        IFileOperation? operation = null;
        IShellItem? item = null;
        uint cookie = 0;
        bool advised = false;
        try
        {
            Type type = Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), true)!;
            operation = (IFileOperation)Activator.CreateInstance(type)!;
            // 前台已双确认；关闭 Shell 交互，由 PreDeleteItem 否决不可回收项，不能仅依赖自动确认保护数据。
            const uint noUi = 0x00000004 | 0x00000010 | 0x00000200 | 0x00000400; // FOF_NO_UI
            Check(operation.SetOperationFlags(0x00080000 | 0x00100000 | noUi));
            Check(operation.Advise(sink, out cookie));
            advised = true;
            Guid iid = typeof(IShellItem).GUID;
            Check(SHCreateItemFromParsingName(sink.Root, IntPtr.Zero, in iid, out item));
            Check(operation.DeleteItem(item, IntPtr.Zero));
            Check(operation.PerformOperations());
            Check(operation.GetAnyOperationsAborted(out bool aborted));
            if (aborted) sink.RecordError("Shell 已取消回收；未授权永久删除。");
        }
        finally
        {
            if (operation is not null && advised)
            {
                try { Check(operation.Unadvise(cookie)); }
                catch (Exception ex) { sink.RecordError(ex.Message); }
            }
            if (item is not null) Marshal.ReleaseComObject(item);
            if (operation is not null) Marshal.ReleaseComObject(operation);
            GC.KeepAlive(sink);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleProgressSink : IFileOperationProgressSink
    {
        private readonly string _requestedRoot;
        private readonly FileManifest _manifest;
        private readonly PathIdentityService _identity = new();
        private readonly CopyVerificationService _verification = new();
        private readonly HashSet<string> _approved = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _recycled = new(StringComparer.OrdinalIgnoreCase);
        private string? _error;
        public string Root { get; private set; }

        public RecycleProgressSink(string root, FileManifest manifest)
        {
            _requestedRoot = root;
            Root = root;
            _manifest = manifest;
        }

        public void RecordError(string error) => _error ??= error;

        public void ValidateInitial()
        {
            MigrationRuntime.AssertDataPath(_requestedRoot);
            Root = PathIdentityService.Normalize(_requestedRoot);
            if (!Root.Equals(PathIdentityService.Normalize(_manifest.RootPath), StringComparison.OrdinalIgnoreCase)
                || !_manifest.Entries.TryGetValue("", out FileManifestEntry? root)
                || root.Identity != _manifest.RootIdentity || !root.IsDirectory || root.Identity.IsReparsePoint)
                throw new IOException("回收路径与冻结清单根身份不符。");
            foreach (var pair in _manifest.Entries)
            {
                string path = EntryPath(pair.Key);
                if (pair.Key != pair.Value.RelativePath || pair.Value.Identity.IsReparsePoint
                    || (pair.Value.Attributes & FileAttributes.ReparsePoint) != 0
                    || pair.Value.IsDirectory != pair.Value.Identity.IsDirectory
                    || (pair.Key.Length != 0 && (Path.IsPathRooted(pair.Key)
                        || !Path.GetRelativePath(Root, path).Equals(pair.Key, StringComparison.OrdinalIgnoreCase))))
                    throw new IOException($"冻结清单路径或类型无效：{pair.Key}");
                MigrationRuntime.AssertDataPath(path);
            }
            ValidateRemaining();
        }

        private string EntryPath(string relative)
        {
            string path = relative.Length == 0 ? Root : PathIdentityService.Normalize(Path.Combine(Root, relative));
            if (!PathIdentityService.IsWithin(path, Root)) throw new IOException("冻结清单路径越界。");
            return path;
        }

        // 同 Mode 重新捕获，不将当前集合认领为新的删除清单。
        private void ValidateRemaining()
        {
            _identity.RequireSame(Root, _manifest.RootIdentity);
            FileManifest current = _verification.CaptureAsync(Root, _manifest.Mode, CancellationToken.None).GetAwaiter().GetResult();
            foreach (var pair in current.Entries)
            {
                if (!_manifest.Entries.TryGetValue(pair.Key, out FileManifestEntry? expected)
                    || !CopyVerificationService.SameEntry(expected, pair.Value, true,
                        writeTime: !expected.IsDirectory || _recycled.Count == 0))
                    throw new IOException($"回收前发现新增、替换或变化的项目：{pair.Key}");
            }
            foreach (string relative in _manifest.Entries.Keys)
                if (!current.Entries.ContainsKey(relative) && !HasRecycleEvidence(EntryPath(relative)))
                    throw new IOException($"冻结清单项目缺失且无回收证据：{relative}");
        }

        private bool HasRecycleEvidence(string path) => _recycled.Any(root => PathIdentityService.IsWithin(path, root));

        public int PreDeleteItem(uint flags, IntPtr item)
        {
            try
            {
                if ((flags & RecycleIfPossible) == 0) throw new IOException("Shell 判定项目无法回收，已拒绝永久删除。");
                string path = GetPath(item);
                if (!PathIdentityService.IsWithin(path, Root)) throw new IOException("Shell 删除项目超出冻结清单。");
                string relative = path.Equals(Root, StringComparison.OrdinalIgnoreCase) ? "" : Path.GetRelativePath(Root, path);
                if (!_manifest.Entries.TryGetValue(relative, out FileManifestEntry? expected))
                    throw new IOException($"Shell 删除项目不在冻结清单：{path}");
                ValidateRemaining();
                _identity.RequireSame(path, expected.Identity);
                _approved.Add(path);
                return 0;
            }
            catch (Exception ex) { RecordError(ex.Message); return Abort; }
        }

        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created)
        {
            try
            {
                string path = GetPath(item);
                // 文档约定 created 非空指向回收站项目；永久删除时为空。S_OK 本身不构成证据。
                if (!_approved.Contains(path) || (flags & RecycleIfPossible) == 0 || result < 0 || created == IntPtr.Zero)
                    throw new IOException($"Shell 未提供实际回收证据：{path} (0x{result:X8})");
                _identity.RequireMissing(path);
                _recycled.Add(path);
                return 0;
            }
            catch (Exception ex) { RecordError(ex.Message); return Abort; }
        }

        public BackupDeletionResult CreateResult(Guid requestId, Guid transactionId)
        {
            bool completeManifestRetained = false;
            try
            {
                MigrationRuntime.AssertDataPath(Root);
                FileManifest current = _verification.CaptureAsync(Root, _manifest.Mode, CancellationToken.None).GetAwaiter().GetResult();
                completeManifestRetained = current.RootIdentity == _manifest.RootIdentity
                    && CopyVerificationService.Fingerprint(current, true) == CopyVerificationService.Fingerprint(_manifest, true);
            }
            catch (Exception ex)
            {
                // 根已实际回收时，无法再捕获源树是预期结果，不把它误记为回收失败。
                if (!_recycled.Contains(Root)) RecordError(ex.Message);
            }
            var items = new List<BackupDeletionItem>();
            foreach (var pair in _manifest.Entries)
            {
                string path = Root;
                BackupItemOutcome outcome = BackupItemOutcome.Failed;
                string? error = _error;
                try
                {
                    path = EntryPath(pair.Key);
                    MigrationRuntime.AssertDataPath(path);
                    DirectoryIdentity? current = _identity.ReadIfExists(path);
                    if (current == pair.Value.Identity) outcome = BackupItemOutcome.Retained;
                    else if (current is null && HasRecycleEvidence(path)) { outcome = BackupItemOutcome.Recycled; error = null; }
                    else error = current is null ? "源项目已不在且无法证明实际回收。" : "源路径身份已变化，不能认领原项目结果。";
                }
                catch (Exception ex) { error = ex.Message; }
                items.Add(new BackupDeletionItem(path, outcome, error));
            }
            bool recycled = items.Count != 0 && items.All(item => item.Outcome == BackupItemOutcome.Recycled);
            BackupDeletionStatus status = recycled && _error is null ? BackupDeletionStatus.Recycled
                : completeManifestRetained ? BackupDeletionStatus.Failed : BackupDeletionStatus.Partial;
            return new BackupDeletionResult
            {
                RequestId = requestId, TransactionId = transactionId, Status = status, Items = items.ToArray(),
                Error = status == BackupDeletionStatus.Recycled ? null : _error ?? "回收未完成，保留未回收项目；未执行永久删除。",
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) { if (result < 0) RecordError($"Shell 回收失败：0x{result:X8}"); return 0; }
        // 这个 sink 只授权删除流程，其他破坏性操作一律拒绝。
        public int PreRenameItem(uint flags, IntPtr item, IntPtr name) => Abort;
        public int PostRenameItem(uint flags, IntPtr item, IntPtr name, int result, IntPtr created) => Abort;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name) => Abort;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created) => Abort;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name) => Abort;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created) => Abort;
        public int PreNewItem(uint flags, IntPtr destination, IntPtr name) => Abort;
        public int PostNewItem(uint flags, IntPtr destination, IntPtr name, IntPtr template, uint attributes, int result, IntPtr created) => Abort;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
