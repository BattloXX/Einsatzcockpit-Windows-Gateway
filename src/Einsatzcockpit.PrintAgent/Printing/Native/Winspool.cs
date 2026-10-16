using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Einsatzcockpit.PrintAgent.Printing.Native;

internal sealed class SafePrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafePrinterHandle() : base(true) { }
    protected override bool ReleaseHandle() => Winspool.ClosePrinter(handle);
}

internal static class Winspool
{
    internal const uint JOB_CONTROL_DELETE = 5;
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern bool OpenPrinter(string name, out SafePrinterHandle handle, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] internal static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern uint StartDocPrinter(SafePrinterHandle h, uint level, ref DOC_INFO_1 doc);
    [DllImport("winspool.drv", SetLastError = true)] internal static extern bool StartPagePrinter(SafePrinterHandle h);
    [DllImport("winspool.drv", SetLastError = true)] internal static extern bool EndPagePrinter(SafePrinterHandle h);
    [DllImport("winspool.drv", SetLastError = true)] internal static extern bool EndDocPrinter(SafePrinterHandle h);
    [DllImport("winspool.drv", SetLastError = true)] internal static extern bool WritePrinter(SafePrinterHandle h, byte[] bytes, int count, out int written);
    [DllImport("winspool.drv", SetLastError = true)] internal static extern bool EnumJobs(SafePrinterHandle h, uint first, uint count, uint level, IntPtr buffer, uint bytes, out uint needed, out uint returned);
    [DllImport("winspool.drv", SetLastError = true)] internal static extern bool SetJob(SafePrinterHandle h, uint job, uint level, IntPtr data, uint command);
    [DllImport("winspool.drv", SetLastError = true)] internal static extern bool GetPrinter(SafePrinterHandle h, uint level, IntPtr buffer, uint bytes, out uint needed);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DOC_INFO_1 { public string pDocName, pOutputFile, pDatatype; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct JOB_INFO_1 { public uint JobId; public string? pPrinterName, pMachineName, pUserName, pDocument, pDatatype, pStatus; public uint Status, Priority, Position, TotalPages, PagesPrinted; public SYSTEMTIME Submitted; }
    // JOB_INFO_1.Submitted ist ein SYSTEMTIME (16 Byte), kein FILETIME – sonst stimmt die Schrittweite beim Lesen mehrerer Jobs nicht.
    [StructLayout(LayoutKind.Sequential)] internal struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct PRINTER_INFO_2 { public string? pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation; public IntPtr pDevMode, pSepFile, pPrintProcessor, pDatatype, pParameters, pSecurityDescriptor; public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM; }
}
