namespace MigrationCore.Services;

/// <summary>
/// 应用级单执行排他门控的当前状态快照。
/// </summary>
public sealed record OperationGateStatus(
    bool IsBusy,
    string? OperationKind,
    string? OperationTitle,
    string[] ActivePaths,
    DateTimeOffset? StartedAtUtc)
{
    public static OperationGateStatus Idle { get; } = new(false, null, null, [], null);
}

/// <summary>
/// 跨模式（手动迁移、一键迁移、扫描批量迁移、还原、修复、备份处理）共享的应用级单执行排他门控。
/// 确保任一时刻最多仅有一个变更型任务操作文件系统与事务目录，消除分散 bool 标志竞态。
/// </summary>
public sealed class OperationExecutionGate
{
    public static OperationExecutionGate Shared { get; } = new();

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Lock _sync = new();
    private OperationGateStatus _status = OperationGateStatus.Idle;

    public event EventHandler? StateChanged;
    public event EventHandler<OperationGateStatus>? OperationCompleted;

    public OperationGateStatus CurrentStatus
    {
        get
        {
            lock (_sync) return _status;
        }
    }

    public bool IsBusy => CurrentStatus.IsBusy;

    /// <summary>
    /// 非阻塞尝试获取排他执行租约；若已有其他变更操作在运行，立即返回 null。
    /// </summary>
    public OperationGateLease? TryAcquire(string operationKind, string title, IReadOnlyList<string>? activePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (!_semaphore.Wait(0))
        {
            return null;
        }

        return EnterLease(operationKind, title, activePaths);
    }

    /// <summary>
    /// 异步尝试获取排他执行租约；支持等待极短窗口或立即返回 null（timeout == TimeSpan.Zero）。
    /// </summary>
    public async Task<OperationGateLease?> TryAcquireAsync(
        string operationKind,
        string title,
        IReadOnlyList<string>? activePaths = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        TimeSpan waitTime = timeout ?? TimeSpan.Zero;
        if (!await _semaphore.WaitAsync(waitTime, ct).ConfigureAwait(false))
        {
            return null;
        }

        return EnterLease(operationKind, title, activePaths);
    }

    /// <summary>
    /// 获取排他执行租约，若已被占用则直接抛出带当前活动操作描述的 IOException。
    /// </summary>
    public async Task<OperationGateLease> AcquireOrThrowAsync(
        string operationKind,
        string title,
        IReadOnlyList<string>? activePaths = null,
        CancellationToken ct = default)
    {
        var lease = await TryAcquireAsync(operationKind, title, activePaths, TimeSpan.Zero, ct).ConfigureAwait(false);
        if (lease is null)
        {
            OperationGateStatus current = CurrentStatus;
            throw new IOException($"当前已有操作正在执行（{current.OperationTitle ?? current.OperationKind ?? "未知任务"}），请等待其完成或取消后再启动「{title}」。");
        }
        return lease;
    }

    private OperationGateLease EnterLease(string operationKind, string title, IReadOnlyList<string>? activePaths)
    {
        lock (_sync)
        {
            _status = new OperationGateStatus(
                IsBusy: true,
                OperationKind: operationKind,
                OperationTitle: title,
                ActivePaths: activePaths is null ? [] : activePaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray(),
                StartedAtUtc: DateTimeOffset.UtcNow);
        }

        RaiseStateChanged();
        return new OperationGateLease(this);
    }

    private void Release()
    {
        OperationGateStatus completed;
        lock (_sync)
        {
            completed = _status;
            _status = OperationGateStatus.Idle;
        }

        _semaphore.Release();
        RaiseStateChanged();
        if (completed.IsBusy)
        {
            try
            {
                OperationCompleted?.Invoke(this, completed);
            }
            catch
            {
                // 订阅方异常不得破坏锁释放
            }
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // UI 订阅方异常不得破坏锁释放
        }
    }

    public sealed class OperationGateLease : IDisposable, IAsyncDisposable
    {
        private OperationExecutionGate? _owner;

        internal OperationGateLease(OperationExecutionGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            OperationExecutionGate? owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
