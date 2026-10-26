using Einsatzcockpit.PrintAgent.Core.Abstractions;

namespace Einsatzcockpit.PrintAgent.Printing;

public sealed class CompositeSpooler(WindowsSpooler windows, IppDirectSubmitter ipp) : IPrintSpooler
{
    public Task<SpoolJobInfo?> FindJobByDocumentNameAsync(string queue, string documentName, CancellationToken cancellationToken) => windows.FindJobByDocumentNameAsync(queue, documentName, cancellationToken);
    public Task<SpoolJobState> GetJobStateAsync(string queue, string spoolRef, CancellationToken cancellationToken) => spoolRef.StartsWith("win:", StringComparison.Ordinal) ? windows.GetJobStateAsync(queue, spoolRef, cancellationToken) : spoolRef.StartsWith("ipp:", StringComparison.Ordinal) ? ipp.GetJobStateAsync(spoolRef, cancellationToken) : Task.FromResult(spoolRef.StartsWith("cmd:", StringComparison.Ordinal) || spoolRef.StartsWith("file:", StringComparison.Ordinal) ? SpoolJobState.Gone : SpoolJobState.Error(0, "Unbekannte Spoolerreferenz"));
    public Task CancelJobAsync(string queue, string spoolRef, CancellationToken cancellationToken) => spoolRef.StartsWith("win:", StringComparison.Ordinal) ? windows.CancelJobAsync(queue, spoolRef, cancellationToken) : Task.CompletedTask;
}
