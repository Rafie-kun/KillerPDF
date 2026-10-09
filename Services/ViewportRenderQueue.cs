namespace KillerPDF.Services;

// One raster worker per pane. Pending requests carry estimates, not pixel buffers.
internal sealed class ViewportRenderQueue(int capacity = 32, long byteBudget = 512L << 20)
{
    private readonly Lock _sync = new();
    private readonly List<Work> _pending = [];
    private Work? _active;
    private bool _running;
    private long _sequence;

    internal Task<T> Enqueue<T>(object key, int priority, long estimatedBytes,
        Func<CancellationToken, T> render, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(render);
        if (capacity <= 0 || byteBudget <= 0 || estimatedBytes <= 0 || estimatedBytes > byteBudget)
            throw new ArgumentOutOfRangeException(nameof(estimatedBytes));
        cancellationToken.ThrowIfCancellationRequested();
        var job = new Work<T>(key, priority, estimatedBytes, render, cancellationToken);
        lock (_sync)
        {
            foreach (var old in _pending.Where(w => w.Token.IsCancellationRequested || w.Key.Equals(key)).ToArray())
            {
                _pending.Remove(old);
                old.Drop();
            }
            job.Sequence = ++_sequence;
            _pending.Add(job);
            while (_pending.Count > capacity || _pending.Sum(w => w.EstimatedBytes) > byteBudget)
            {
                var worst = _pending.OrderByDescending(w => w.Priority).ThenBy(w => w.Sequence).First();
                _pending.Remove(worst);
                worst.Drop();
            }
            if (!_running)
            {
                _running = true;
                // The pump serves every queued request; cancel each Work through its own token.
                _ = Task.Run(Pump, CancellationToken.None);
            }
        }
        return job.Completion.Task;
    }

    internal void CancelAll()
    {
        lock (_sync)
        {
            _active?.Cancel();
            foreach (var job in _pending) job.Drop();
            _pending.Clear();
        }
    }

    private void Pump()
    {
        while (true)
        {
            Work job;
            lock (_sync)
            {
                if (_pending.Count == 0) { _running = false; return; }
                job = _pending.OrderBy(w => w.Priority).ThenBy(w => w.Sequence).First();
                _pending.Remove(job);
                _active = job;
            }
            job.Execute();
            lock (_sync) { _active = null; job.Dispose(); }
        }
    }

    private abstract class Work(object key, int priority, long bytes, CancellationToken token) : IDisposable
    {
        private readonly CancellationTokenSource _source = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal object Key { get; } = key;
        internal int Priority { get; } = priority;
        internal long EstimatedBytes { get; } = bytes;
        internal long Sequence { get; set; }
        internal CancellationToken Token => _source.Token;
        internal void Cancel() => _source.Cancel();
        internal abstract void Execute();
        internal abstract void Drop();
        public void Dispose() => _source.Dispose();
    }

    private sealed class Work<T>(object key, int priority, long bytes,
        Func<CancellationToken, T> render, CancellationToken token) : Work(key, priority, bytes, token)
    {
        internal TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal override void Execute()
        {
            try
            {
                Token.ThrowIfCancellationRequested();
                T result = render(Token);
                Token.ThrowIfCancellationRequested();
                Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) { Completion.TrySetCanceled(); }
            catch (Exception ex) { Completion.TrySetException(ex); }
        }
        internal override void Drop() { Completion.TrySetCanceled(); Dispose(); }
    }
}
