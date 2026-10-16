using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Printing;

namespace Einsatzcockpit.PrintAgent.Printing;

public sealed class FolderSubmitter : IPrintSubmitter
{
    public string Name => "folder";
    public async Task<SubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        var directory = CommandSubmitter.Option(request.Route, "target_dir").GetString(); if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) throw new PrintRejectedException("Zielverzeichnis fehlt.");
        var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}_{PrintPathHelpers.SafeFileName(request.DocumentName)}.pdf"); var temporary = path + ".tmp";
        try { await using (var input = File.OpenRead(request.PdfPath)) await using (var output = File.Create(temporary)) await input.CopyToAsync(output, cancellationToken); File.Move(temporary, path); }
        catch (Exception ex) { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } throw new PrintRejectedException("Zielverzeichnis ist nicht beschreibbar.", ex); }
        return new SubmitResult("file:" + path);
    }
}
