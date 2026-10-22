using System.Windows.Threading;

namespace PdfDesk.App.Viewer;

/// <summary>
/// A single background worker per document that runs render jobs lowest-priority-number first. A job with the same key
/// as a queued one replaces it, so scrolling quickly never builds a backlog of stale renders.
/// Results are delivered on the UI thread.
/// </summary>
public sealed class RenderScheduler : IDisposable
{
    private sealed record Job(string Key, double Priority, Func<object?> Work, Action<object?> Done);

    private readonly Dispatcher _dispatcher;
    private readonly Lock _lock = new();
    private readonly List<Job> _jobs = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();

    public RenderScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var worker = new Thread(Loop) { IsBackground = true, Name = "PdfDesk render", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    public void Enqueue(string key, double priority, Func<object?> work, Action<object?> done)
    {
        lock (_lock)
        {
            _jobs.RemoveAll(j => j.Key == key);
            _jobs.Add(new Job(key, priority, work, done));
        }
        _signal.Release();
    }

    public bool IsQueued(string key)
    {
        lock (_lock) return _jobs.Exists(j => j.Key == key);
    }

    /// <summary>Drops queued jobs (all, or those whose key matches).</summary>
    public void Clear(Func<string, bool>? match = null)
    {
        lock (_lock)
        {
            if (match is null) _jobs.Clear();
            else _jobs.RemoveAll(j => match(j.Key));
        }
    }

    private void Loop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Job? job;
            lock (_lock)
            {
                job = _jobs.Count == 0 ? null : _jobs.MinBy(j => j.Priority);
                if (job is not null) _jobs.Remove(job);
            }
            if (job is null) continue;

            object? result;
            try
            {
                result = job.Work();
            }
            catch (ObjectDisposedException)
            {
                continue; // the document was edited or closed mid-render
            }
            catch (Exception ex)
            {
                Log.Write($"Render job {job.Key} failed: {ex.Message}");
                continue;
            }

            if (token.IsCancellationRequested) return;
            _dispatcher.BeginInvoke(DispatcherPriority.Render, () => job.Done(result));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Clear();
    }
}
