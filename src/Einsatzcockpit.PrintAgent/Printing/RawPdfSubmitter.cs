using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Printing;
using Einsatzcockpit.PrintAgent.Printing.Native;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Einsatzcockpit.PrintAgent.Printing;

public sealed class RawPdfSubmitter : IPrintSubmitter
{
    public string Name => "pdf_direct";
    public async Task<SubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        SafePrinterHandle handle;
        try { handle = WindowsSpooler.Open(request.Route.LocalQueue); }
        catch (Exception ex) { throw new PrintRejectedException("Druckerwarteschlange konnte nicht geöffnet werden.", ex); }
        using (handle)
        {
            var doc = new Winspool.DOC_INFO_1 { pDocName = request.DocumentName, pOutputFile = null!, pDatatype = "RAW" };
            uint job;
            try { job = Winspool.StartDocPrinter(handle, 1, ref doc); if (job == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            catch (Exception ex) { throw new PrintRejectedException("RAW-Druckauftrag konnte nicht angelegt werden.", ex); }
            try
            {
                if (!Winspool.StartPagePrinter(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
                Write(handle, Pjl.Build(request.DocumentName, request.Options?.Copies ?? 1, request.Options?.Duplex, request.Options?.Media, PrintValueMappings.IsColor(request.Options?.Color)));
                await using var pdf = File.OpenRead(request.PdfPath); var bytes = new byte[64 * 1024]; int read; while ((read = await pdf.ReadAsync(bytes, cancellationToken)) > 0) Write(handle, bytes, read);
                Write(handle, Pjl.End()); if (!Winspool.EndPagePrinter(handle)) throw new Win32Exception(Marshal.GetLastWin32Error()); return new SubmitResult($"win:{job}");
            }
            finally { Winspool.EndDocPrinter(handle); }
        }
    }
    private static void Write(SafePrinterHandle handle, byte[] bytes, int? count = null) { var length = count ?? bytes.Length; if (!Winspool.WritePrinter(handle, bytes, length, out var written) || written != length) throw new Win32Exception(Marshal.GetLastWin32Error()); }
}
