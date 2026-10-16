using System.Diagnostics;
using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Abstractions;
using Einsatzcockpit.PrintAgent.Core.Printing;
using Einsatzcockpit.PrintAgent.Core.Protocol;

namespace Einsatzcockpit.PrintAgent.Printing;

public sealed class CommandSubmitter(IReadOnlyCollection<string> allowedCommands) : IPrintSubmitter
{
    private readonly HashSet<string> _allowed = allowedCommands.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public string Name => "command";
    public async Task<SubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        var command = Option(request.Route, "command"); if (command.ValueKind != JsonValueKind.Array || command.GetArrayLength() == 0) throw new PrintRejectedException("Kein Druckkommando konfiguriert.");
        var parts = command.EnumerateArray().Select(x => x.GetString() ?? "").ToArray(); var program = Path.GetFullPath(parts[0]);
        if (!_allowed.Contains(program) || !File.Exists(program)) throw new PrintRejectedException("Druckkommando ist nicht erlaubt.");
        var values = new Dictionary<string, string> { ["pdf"] = request.PdfPath, ["queue"] = request.Route.LocalQueue, ["copies"] = (request.Options?.Copies ?? 1).ToString(), ["duplex"] = PrintValueMappings.Duplex(request.Options?.Duplex), ["media"] = PrintValueMappings.Media(request.Options?.Media), ["document"] = request.DocumentName };
        using var process = new Process { StartInfo = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true } }; foreach (var argument in PrintPathHelpers.ExpandCommandArguments(parts.Skip(1), values)) process.StartInfo.ArgumentList.Add(argument);
        process.Start(); var stderr = process.StandardError.ReadToEndAsync(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)); using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try { await process.WaitForExitAsync(linked.Token); } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { process.Kill(true); throw new TimeoutException("Druckkommando hat das Zeitlimit überschritten."); }
        if (process.ExitCode != 0) throw new PrintRejectedException($"Druckkommando beendet mit {process.ExitCode}: {(await stderr)[..Math.Min(500, (await stderr).Length)]}");
        return new SubmitResult($"cmd:{process.Id}");
    }
    internal static JsonElement Option(RouteConfig route, string name) => route.Options is { } options && options.TryGetProperty(name, out var value) ? value : default;
}
