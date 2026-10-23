using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Einsatzcockpit.PrintAgent.Core.Cloud;

public interface IWebSocketTransport
{
    int? CloseCode => null;
    int? ConnectStatusCode => null;
    Task ConnectAsync(Uri uri, string bearerToken, CancellationToken cancellationToken);
    Task SendAsync(string message, CancellationToken cancellationToken = default);
    Task<string?> ReceiveAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}

public sealed record WebSocketTransportOptions(IWebProxy? Proxy = null);

public sealed class ClientWebSocketTransport : IWebSocketTransport, IDisposable
{
    private readonly WebSocketTransportOptions _options;
    private ClientWebSocket? _socket;
    public int? CloseCode { get; private set; }
    public int? ConnectStatusCode => _socket is null ? null : (int)_socket.HttpStatusCode;

    public ClientWebSocketTransport(WebSocketTransportOptions? options = null)
    {
        _options = options ?? new WebSocketTransportOptions();
    }

    public async Task ConnectAsync(Uri uri, string bearerToken, CancellationToken cancellationToken)
    {
        CloseCode = null;
        _socket?.Dispose();
        _socket = new ClientWebSocket();
        _socket.Options.CollectHttpResponseDetails = true;
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + bearerToken);
        if (_options.Proxy is not null)
        {
            _socket.Options.Proxy = _options.Proxy;
        }
        await _socket.ConnectAsync(uri, cancellationToken);
    }

    public Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            throw new WebSocketException("WebSocket ist nicht verbunden.");
        }
        return _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken);
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is null)
        {
            return null;
        }
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                CloseCode = (int?)result.CloseStatus;
                return null;
            }
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            }
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_socket is { State: WebSocketState.Open })
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken);
        }
    }

    public void Dispose() => _socket?.Dispose();
}
