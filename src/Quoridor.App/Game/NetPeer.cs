using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Quoridor.App.Game;

public enum NetState
{
    Idle,
    Listening,
    Connecting,
    Connected,
    Failed,
}

/// <summary>
/// A direct link between two copies of the game: one listens, the other dials it.
///
/// No signalling service and no server of ours — on a local network this needs nothing
/// at all, which is the case it is built for. Over the internet the host has to forward
/// a port, and that is a fair trade for having no third party in the path. The browser
/// build solves the same problem differently, with WebRTC, and the two do not
/// interoperate.
///
/// The protocol is one UTF-8 line per message.
/// </summary>
public sealed class NetPeer : IDisposable
{
    public const int DefaultPort = 25123;

    private readonly CancellationTokenSource _life = new();

    private TcpListener? _listener;
    private TcpClient? _client;
    private StreamWriter? _writer;

    /// <summary>Raised on a background thread for each line the other side sends.</summary>
    public event Action<string>? Received;

    /// <summary>Raised on a background thread whenever the connection state changes.</summary>
    public event Action? Changed;

    public NetState State { get; private set; } = NetState.Idle;

    public string Trouble { get; private set; } = string.Empty;

    public bool IsConnected => State == NetState.Connected;

    /// <summary>Which seat this instance plays: the host moves first.</summary>
    public int LocalSeat { get; private set; }

    public async Task HostAsync(int port)
    {
        Close();
        LocalSeat = 0;
        Set(NetState.Listening);

        try
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            TcpClient client = await _listener.AcceptTcpClientAsync(_life.Token);

            _listener.Stop();
            _listener = null;

            Attach(client);
        }
        catch (OperationCanceledException)
        {
            // Cancelled from Close; nothing to report.
        }
        catch (Exception ex)
        {
            Fail($"Could not listen on port {port}. {ex.Message}");
        }
    }

    public async Task JoinAsync(string address, int port)
    {
        Close();
        LocalSeat = 1;
        Set(NetState.Connecting);

        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(address, port, _life.Token);
            Attach(client);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fail($"Could not reach {address}:{port}. {ex.Message}");
        }
    }

    public async Task SendAsync(string line)
    {
        StreamWriter? writer = _writer;
        if (writer is null || !IsConnected) return;

        try
        {
            await writer.WriteLineAsync(line);
        }
        catch (Exception ex)
        {
            Fail($"The connection dropped. {ex.Message}");
        }
    }

    /// <summary>
    /// Addresses another machine on this network could dial. Loopback is filtered out —
    /// it is never the one to read out to someone across the room.
    /// </summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private void Attach(TcpClient client)
    {
        _client = client;
        client.NoDelay = true;

        NetworkStream stream = client.GetStream();
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        Set(NetState.Connected);

        _ = Task.Run(() => ReadLoopAsync(stream), _life.Token);
    }

    private async Task ReadLoopAsync(NetworkStream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false));

        try
        {
            while (!_life.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(_life.Token);

                if (line is null)
                {
                    Fail("The other player disconnected.");
                    return;
                }

                Received?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fail($"The connection dropped. {ex.Message}");
        }
    }

    private void Set(NetState state)
    {
        State = state;
        Changed?.Invoke();
    }

    private void Fail(string trouble)
    {
        Trouble = trouble;
        Set(NetState.Failed);
    }

    private void Close()
    {
        try { _listener?.Stop(); } catch (Exception) { /* already down */ }
        try { _client?.Close(); } catch (Exception) { /* already down */ }

        _listener = null;
        _client = null;
        _writer = null;
    }

    public void Dispose()
    {
        _life.Cancel();
        Close();
        _life.Dispose();
    }
}
