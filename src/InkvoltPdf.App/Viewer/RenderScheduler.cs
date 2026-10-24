using System.Windows.Threading;

namespace InkvoltPdf.App.Viewer;

/// <summary>
/// Background workers per document that run jobs lowest-priority-number first, on two lanes: the view lane (priority
/// below <see cref="BackgroundPriority"/>: visible pages, detail tiles, snapshots) and the background lane (text layers,
/// thumbnails, organizer cards), so a long thumbnail or text job never holds up the page the user is looking at.
/// A job with the same key as a queued one replaces it, so scrolling quickly never builds a backlog of stale renders.
/// Running jobs get a <see cref="CancellationToken"/>: they are cancelled when cleared, when replaced by a job with the
/// same key and a different tag, and (background lane only) pre-empted and re-queued when view work arrives.
/// Results and failures are delivered on the UI thread.
/// </summary>
public sealed class RenderScheduler : IDisposable
{
    public const double BackgroundPriority = 100;

    private sealed class Job(string key, double priority, object? tag, Func<CancellationToken, object?> work, Action<object?> done, Action<Exception>? failed)
    {
        public string Key { get; } = key;
        public double Priority { get; set; } = priority;
        public object? Tag { get; } = tag;
        public Func<CancellationToken, object?> Work { get; } = work;
        public Action<object?> Done { get; } = done;
        public Action<Exception>? Failed { get; } = failed;
        public int Lane => Priority < BackgroundPriority ? 0 : 1;
        public CancellationTokenSource? Cts;
        public bool Stale, Preempted;
    }

    private readonly Dispatcher _dispatcher;
    private readonly Lock _lock = new();
    private readonly List<Job> _jobs = [];
    private readonly Job?[] _running = new Job?[2];
    private readonly SemaphoreSlim[] _signals = [new(0), new(0)];
    private readonly CancellationTokenSource _cts = new();

    public RenderScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        for (var lane = 0; lane < 2; lane++)
        {
            var l = lane;
            var worker = new Thread(() => Loop(l))
            {
                IsBackground = true,
                Name = l == 0 ? "Inkvolt PDF render" : "Inkvolt PDF background",
                Priority = l == 0 ? ThreadPriority.Normal : ThreadPriority.BelowNormal,
            };
            worker.Start();
        }
    }

    /// <summary>Queues work that can't be cancelled once it runs.</summary>
    public void Enqueue(string key, double priority, Func<object?> work, Action<object?> done) =>
        Enqueue(key, priority, _ => work(), done);

    /// <summary>
    /// Queues cancellable work. With a <paramref name="tag"/> (e.g. the render scale), an equal pending job (queued or
    /// running) is kept instead of restarted, and a running job with the same key but another tag is cancelled.
    /// <paramref name="done"/> and <paramref name="failed"/> both run on the UI (dispatcher) thread. <paramref name="failed"/>
    /// receives exceptions (including <see cref="ObjectDisposedException"/> when the document changed mid-job); without it
    /// failures are only logged. Neither runs for a job that was cancelled or replaced.
    /// </summary>
    public void Enqueue(string key, double priority, Func<CancellationToken, object?> work, Action<object?> done,
        Action<Exception>? failed = null, object? tag = null)
    {
        int lane;
        lock (_lock)
        {
            if (tag is not null)
            {
                if (_jobs.Find(j => j.Key == key) is { } queued && Equals(queued.Tag, tag))
                {
                    var before = queued.Lane;
                    queued.Priority = priority;
                    // Moved to the other lane: wake that lane's worker, or the job would wait for unrelated work there.
                    if (queued.Lane != before) _signals[queued.Lane].Release();
                    return;
                }
                foreach (var r in _running)
                {
                    if (r is null || r.Key != key || r.Stale) continue;
                    if (Equals(r.Tag, tag) && _jobs.TrueForAll(j => j.Key != key)) return; // already being made
                    if (!Equals(r.Tag, tag)) Cancel(r);
                }
            }
            _jobs.RemoveAll(j => j.Key == key);
            var job = new Job(key, priority, tag, work, done, failed);
            _jobs.Add(job);
            lane = job.Lane;
            // View work pre-empts a running background job (it is re-queued and runs again later).
            if (lane == 0 && _running[1] is { Stale: false, Preempted: false } bg)
            {
                bg.Preempted = true;
                bg.Cts?.Cancel();
            }
        }
        _signals[lane].Release();
    }

    public bool IsQueued(string key)
    {
        lock (_lock) return _jobs.Exists(j => j.Key == key) || Array.Exists(_running, r => r is { Stale: false } && r.Key == key);
    }

    /// <summary>Drops queued jobs and cancels running ones (all, or those whose key matches).</summary>
    public void Clear(Func<string, bool>? match = null)
    {
        lock (_lock)
        {
            if (match is null) _jobs.Clear();
            else _jobs.RemoveAll(j => match(j.Key));
            foreach (var r in _running)
                if (r is not null && (match is null || match(r.Key))) Cancel(r);
        }
    }

    private static void Cancel(Job job)
    {
        job.Stale = true;
        try
        {
            job.Cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Loop(int lane)
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _signals[lane].Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Job? job = null;
            CancellationTokenSource? cts = null;
            lock (_lock)
            {
                foreach (var j in _jobs)
                    if (j.Lane == lane && (job is null || j.Priority < job.Priority)) job = j;
                if (job is not null)
                {
                    _jobs.Remove(job);
                    job.Cts = cts = new CancellationTokenSource();
                    _running[lane] = job;
                }
            }
            if (job is null || cts is null) continue;

            object? result = null;
            Exception? error = null;
            var cancelled = false;
            try
            {
                result = job.Work(cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }

            bool stale;
            lock (_lock)
            {
                _running[lane] = null;
                stale = job.Stale;
                cts.Dispose();
                job.Cts = null;
                if (cancelled && !stale && job.Preempted && !_jobs.Exists(j => j.Key == job.Key))
                {
                    job.Preempted = false;
                    _jobs.Add(job);
                    _signals[lane].Release();
                }
            }

            if (token.IsCancellationRequested) return;
            if (stale || cancelled) continue;
            if (error is not null)
            {
                if (error is not ObjectDisposedException) Log.Write($"Render job {job.Key} failed: {error.Message}");
                if (job.Failed is { } failed) _dispatcher.BeginInvoke(DispatcherPriority.Render, () => failed(error));
                continue; // ObjectDisposed: the document was edited or closed mid-job
            }
            _dispatcher.BeginInvoke(DispatcherPriority.Render, () => job.Done(result));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Clear();
    }
}
