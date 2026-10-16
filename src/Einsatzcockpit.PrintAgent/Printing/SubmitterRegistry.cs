using Einsatzcockpit.PrintAgent.Core.Abstractions;

namespace Einsatzcockpit.PrintAgent.Printing;

public sealed class SubmitterRegistry(IEnumerable<IPrintSubmitter> submitters) : IPrintSubmitter
{
    private readonly Dictionary<string, IPrintSubmitter> _submitters = submitters.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
    public string Name => "registry";
    public Task<SubmitResult> SubmitAsync(PrintRequest request, CancellationToken cancellationToken) =>
        _submitters.TryGetValue(request.Route.RenderMode, out var submitter) ? submitter.SubmitAsync(request, cancellationToken) : Task.FromException<SubmitResult>(new PrintRejectedException($"Unbekannter Druckmodus: {request.Route.RenderMode}"));
}
