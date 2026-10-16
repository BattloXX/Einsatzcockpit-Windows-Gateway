using System.Text.Json;
using Einsatzcockpit.PrintAgent.Core.Jobs;
using Einsatzcockpit.PrintAgent.Core.Protocol;

namespace Einsatzcockpit.PrintAgent.Core.Cloud;

/// <summary>Sendet Status ausschließlich aus der persistierten Outbox.</summary>
public sealed class OutboxPump
{
    private readonly JobStore _store;
    private readonly Func<string, CancellationToken, Task> _send;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OutboxPump(JobStore store, Func<string, CancellationToken, Task> send)
    {
        _store = store;
        _send = send;
    }

    public async Task PumpAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var item in _store.PendingOutbox())
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(item.PayloadJson);
                var message = JsonSerializer.Serialize(new Envelope(item.Type, item.Id, null, payload), ProtocolJson.Options);
                _store.IncrementOutboxAttempts(item.Id);
                await _send(message, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Ack(string id) => _store.RemoveOutbox(id);
}
