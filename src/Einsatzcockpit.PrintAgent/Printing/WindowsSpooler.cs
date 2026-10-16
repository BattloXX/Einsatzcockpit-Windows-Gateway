using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Printing.Native;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Einsatzcockpit.PrintAgent.Printing;

public sealed class WindowsSpooler : IPrintSpooler
{
    // JOB_STATUS_*: PAUSED 0x1, ERROR 0x2, DELETING 0x4, PRINTING 0x10, OFFLINE 0x20,
    // PAPEROUT 0x40, DELETED 0x100, BLOCKED_DEVQ 0x200, USER_INTERVENTION 0x400.
    private const uint ErrorFlags = 0x0001 | 0x0002 | 0x0020 | 0x0040 | 0x0200 | 0x0400;
    private const uint DeletingFlags = 0x0004 | 0x0100;
    private const string GonePrefix = "win:gone:";
    // Nur explizit beim Löschen beobachtete Jobs gelten nach dem Verschwinden als abgebrochen.
    // Ein Job, der zwischendurch einen Fehler (z. B. offline, Papier) hatte und danach normal
    // aus der Warteschlange verschwindet, wurde gedruckt – ihn als Fehlschlag zu werten,
    // würde einen Ersatzdruck und damit einen Doppeldruck auslösen.
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);

    public static string GoneRef(string documentName) => GonePrefix + documentName;
    public Task<SpoolJobInfo?> FindJobByDocumentNameAsync(string queue, string documentName, CancellationToken cancellationToken) { SpoolJobInfo? result = Jobs(queue).FirstOrDefault(j => j.pDocument == documentName) is { } job ? new SpoolJobInfo($"win:{job.JobId}") : null; return Task.FromResult<SpoolJobInfo?>(result); }
    public Task<SpoolJobState> GetJobStateAsync(string queue, string spoolRef, CancellationToken cancellationToken)
    {
        if (spoolRef.StartsWith(GonePrefix, StringComparison.Ordinal)) return Task.FromResult(SpoolJobState.Gone);
        if (!uint.TryParse(spoolRef.AsSpan(4), out var id)) return Task.FromResult(SpoolJobState.Gone);
        var job = Jobs(queue).FirstOrDefault(x => x.JobId == id);
        if (job.JobId == 0) return Task.FromResult(_deleted.Remove(spoolRef) ? SpoolJobState.Deleted : SpoolJobState.Gone);
        if ((job.Status & DeletingFlags) != 0) { _deleted.Add(spoolRef); return Task.FromResult(SpoolJobState.InQueue); }
        if ((job.Status & ErrorFlags) != 0) return Task.FromResult(SpoolJobState.Error((int)job.Status, ErrorText(job.Status)));
        return Task.FromResult((job.Status & 0x0010) != 0 ? SpoolJobState.Printing : SpoolJobState.InQueue);
    }
    public Task CancelJobAsync(string queue, string spoolRef, CancellationToken cancellationToken)
    {
        if (spoolRef.StartsWith(GonePrefix, StringComparison.Ordinal) || !uint.TryParse(spoolRef.AsSpan(4), out var job)) return Task.CompletedTask;
        using var handle = Open(queue); if (!Winspool.SetJob(handle, job, 0, IntPtr.Zero, Winspool.JOB_CONTROL_DELETE)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Task.CompletedTask;
    }
    internal static bool TryGetPrinter(string queue, out Winspool.PRINTER_INFO_2 printer)
    {
        using var handle = Open(queue); Winspool.GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed); var memory = Marshal.AllocHGlobal((int)needed);
        try { if (!Winspool.GetPrinter(handle, 2, memory, needed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error()); printer = Marshal.PtrToStructure<Winspool.PRINTER_INFO_2>(memory); return true; } finally { Marshal.FreeHGlobal(memory); }
    }
    private static List<Winspool.JOB_INFO_1> Jobs(string queue)
    { using var handle = Open(queue); Winspool.EnumJobs(handle, 0, 999, 1, IntPtr.Zero, 0, out var needed, out var count); if (needed == 0) return []; var memory = Marshal.AllocHGlobal((int)needed); try { if (!Winspool.EnumJobs(handle, 0, 999, 1, memory, needed, out _, out count)) throw new Win32Exception(Marshal.GetLastWin32Error()); var size = Marshal.SizeOf<Winspool.JOB_INFO_1>(); return Enumerable.Range(0, (int)count).Select(i => Marshal.PtrToStructure<Winspool.JOB_INFO_1>(memory + i * size)).ToList(); } finally { Marshal.FreeHGlobal(memory); } }
    internal static SafePrinterHandle Open(string queue) { if (!Winspool.OpenPrinter(queue, out var handle, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error()); return handle; }
    private static string ErrorText(uint s) => string.Join(", ", new[] { (s & 0x0001) != 0 ? "angehalten" : null, (s & 0x0002) != 0 ? "Fehler" : null, (s & 0x0020) != 0 ? "offline" : null, (s & 0x0040) != 0 ? "Papier fehlt" : null, (s & 0x0200) != 0 ? "Druckerfehler" : null, (s & 0x0400) != 0 ? "Benutzereingriff erforderlich" : null }.Where(x => x is not null)!);
}
