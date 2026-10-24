using System.Printing;
using InkvoltPrint.Core.Probe;
using InkvoltPrint.Core.Suite;
using InkvoltPrint.Windows.Printing;
using InkvoltPrint.Windows.Suite;

namespace InkvoltPdf.App;

/// <summary>
/// The Inkvolt suite: Inkvolt Print (printers and scanners) is Inkvolt PDF's companion app. Inkvolt PDF uses Inkvolt Print's libraries for
/// scanning and printer status, reads the printers Inkvolt Print remembers, and can open Inkvolt Print when it is installed.
/// </summary>
internal static class Inkvolt
{
    /// <summary>InkvoltPrint.exe when installed (App Paths or the default install folders), else null.</summary>
    public static string? InkvoltPrintPath => InkvoltApps.Find(InkvoltApps.InkvoltPrint);

    /// <summary>Starts Inkvolt Print (or brings the running one forward). False when it isn't installed or won't start.</summary>
    public static bool OpenInkvoltPrint() => InkvoltApps.Launch(InkvoltApps.InkvoltPrint);
}

/// <summary>
/// Status of the printer behind a Windows print queue, when Inkvolt Print knows that printer: ready / sleeping / error /
/// offline, toner and paper problems. Read-only (status and supplies only), cached briefly, and bounded by
/// <see cref="Budget"/> so it never holds up printing for long; any failure just means "no status".
/// </summary>
internal static class PrinterCheck
{
    /// <summary>Longest a check may take, all lookups included.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QueuesFresh = TimeSpan.FromMinutes(5);
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, (DateTime At, Task<PrinterHealth> Health)> Health = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PrinterConnection> Connections = new(StringComparer.OrdinalIgnoreCase);
    private static (DateTime At, Task<IReadOnlyList<WindowsQueue>> List)? _queues;

    public sealed record Result(InkvoltPrinter Printer, PrinterHealth Health);

    /// <summary>Name and port of a queue (read where the queue object lives); null when the spooler won't say.</summary>
    public static QueueInfo? Describe(PrintQueue? queue)
    {
        if (queue is null) return null;
        try
        {
            string? port = null;
            try
            {
                port = queue.QueuePort?.Name;
            }
            catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
            {
                // Some queues (remote, virtual) don't expose their port: match by name and container instead.
            }
            return new QueueInfo(queue.FullName ?? queue.Name, port);
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Starts a check of the default printer in the background, so the answer is ready when the Print dialog closes.</summary>
    public static void Prewarm() => _ = Task.Run(async () =>
    {
        try
        {
            if (InkvoltPrinters.Load().Printers.Count == 0) return;
            QueueInfo? info;
            using (var queue = LocalPrintServer.GetDefaultPrintQueue())
                info = Describe(queue);
            if (info is not null) await CheckAsync(info);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No default printer, or the spooler isn't running: nothing to warm up.
            Log.Write($"Printer check: no default printer to check ({ex.GetType().Name})");
        }
    });

    /// <summary>
    /// The Inkvolt Print printer behind <paramref name="queue"/> and its health, or null (not a Inkvolt Print printer, or no answer
    /// in time from the PC's own lookups). A printer that doesn't answer within <see cref="Budget"/> reads as offline.
    /// </summary>
    public static async Task<Result?> CheckAsync(QueueInfo queue)
    {
        using var budget = new CancellationTokenSource(Budget);
        try
        {
            var known = await Task.Run(() => InkvoltPrinters.Load(), budget.Token);
            if (known.Printers.Count == 0) return null;
            string? container = null;
            try
            {
                var queues = await QueuesAsync().WaitAsync(budget.Token);
                container = queues.FirstOrDefault(q => string.Equals(q.Name, queue.Name, StringComparison.OrdinalIgnoreCase))?.ContainerId;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                Log.Write($"Printer check: queue list unavailable: {ex.Message}");
            }
            if (QueueMatch.Match(queue with { ContainerId = container ?? queue.ContainerId }, known) is not { } printer) return null;
            PrinterHealth health;
            try
            {
                health = await HealthAsync(printer).WaitAsync(budget.Token);
            }
            catch (OperationCanceledException)
            {
                health = PrinterHealth.Offline("Not responding");
            }
            return new Result(printer, health);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"Printer check failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static Task<IReadOnlyList<WindowsQueue>> QueuesAsync()
    {
        lock (Gate)
        {
            if (_queues is { } q && DateTime.UtcNow - q.At < QueuesFresh && !q.List.IsFaulted) return q.List;
            var list = Task.Run(QueueFinder.ListAsync);
            _queues = (DateTime.UtcNow, list);
            return list;
        }
    }

    /// <summary>The printer's health, shared by concurrent callers and reused for <see cref="Fresh"/>.</summary>
    private static Task<PrinterHealth> HealthAsync(InkvoltPrinter printer)
    {
        lock (Gate)
        {
            if (Health.TryGetValue(printer.Host, out var c) && DateTime.UtcNow - c.At < Fresh) return c.Health;
            var task = Task.Run(() => ProbeAsync(printer));
            Health[printer.Host] = (DateTime.UtcNow, task);
            return task;
        }
    }

    private static async Task<PrinterHealth> ProbeAsync(InkvoltPrinter printer)
    {
        // Its own deadline (a little over the caller's budget), so a slow printer can't leave a probe running for long.
        using var cts = new CancellationTokenSource(Budget + TimeSpan.FromSeconds(1));
        try
        {
            PrinterConnection? conn;
            lock (Gate) Connections.TryGetValue(printer.Host, out conn);
            if (conn is null)
            {
                conn = await PrinterConnection.OpenAsync(printer.Host, printer.EsclRoot, cts.Token);
                if (!conn.Probe.Reachable)
                {
                    conn.Dispose();
                    return PrinterHealth.Offline("Not responding");
                }
                lock (Gate)
                {
                    if (Connections.TryGetValue(printer.Host, out var other)) { conn.Dispose(); conn = other; }
                    else Connections[printer.Host] = conn;
                }
            }
            var status = conn.GetStatusAsync(cts.Token);
            var supplies = SuppliesAsync(conn, cts.Token);
            await Task.WhenAll(status, supplies);
            return PrinterHealth.From(status.Result, supplies.Result);
        }
        catch (OperationCanceledException)
        {
            return PrinterHealth.Offline("Not responding");
        }
        catch (Exception ex) when (PrinterConnection.IsOffline(ex))
        {
            return PrinterHealth.Offline("Not responding");
        }
    }

    private static async Task<IReadOnlyList<InkvoltPrint.Core.Model.Consumable>> SuppliesAsync(PrinterConnection conn, CancellationToken ct)
    {
        try
        {
            return await conn.GetSuppliesAsync(ct);
        }
        catch (Exception ex) when (PrinterConnection.IsOffline(ex) || PrinterConnection.IsTransient(ex))
        {
            return []; // status alone is still worth showing
        }
    }

    /// <summary>"is offline", "needs attention"…: the warning's title after the printer's name.</summary>
    public static string Problem(PrinterHealth h) => h.State switch
    {
        Readiness.Offline => "isn't responding",
        _ => "has a problem",
    };
}
