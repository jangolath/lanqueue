using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sample.Core;

/// <summary>A line server on 127.0.0.1 with a random port: answers each line upper-cased. Loopback only; for integration tests.</summary>
public sealed class EchoServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(() => AcceptAsync(_stop.Token));
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private static async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    await writer.WriteLineAsync(line.ToUpperInvariant().AsMemory(), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The client left or the server stopped.
            }
        }
    }

    public static async Task<string> AskAsync(int port, string line, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
        var stream = client.GetStream();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        return await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? "";
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _stop.Dispose();
    }
}
