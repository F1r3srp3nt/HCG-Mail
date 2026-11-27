using System.Net;
using System.Net.Sockets;

namespace HCG.Mail.App.Tests;

/// <summary>
/// Minimal TCP relay used to simulate networks between HCG Mail and a server:
/// <see cref="Cut"/> drops every connection and refuses new ones; <see cref="Restore"/> accepts again;
/// <see cref="BlackHole"/> keeps accepting connections but silently drops all traffic (a firewall that filters mail, or an
/// inspecting proxy that never completes the handshake); <see cref="KillSilently"/> makes the existing connections dead
/// without either end being told (a NAT mapping dropped during sleep or a Wi-Fi switch), while new ones work.
/// </summary>
internal sealed class TcpRelay : IDisposable
{
    private readonly IPEndPoint _target;
    private readonly List<TcpClient> _open = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private volatile bool _blackHole;
    private int _generation;

    /// <summary>Connections accepted so far (each reconnect is one more).</summary>
    public int Accepted;

    public void BlackHole() => _blackHole = true;

    public void Open() => _blackHole = false;

    /// <summary>Existing connections stop carrying data (silently); new connections are relayed normally.</summary>
    public void KillSilently() => Interlocked.Increment(ref _generation);

    public TcpRelay(IPEndPoint target, int port)
    {
        _target = target;
        Port = port;
    }

    public int Port { get; }

    public void Restore()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start();
        var token = _cts.Token;
        var listener = _listener;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token);
                }
                catch
                {
                    return;
                }
                _ = PumpAsync(client, token);
            }
        });
    }

    public void Cut()
    {
        _cts?.Cancel();
        _listener?.Stop();
        lock (_open)
        {
            foreach (var c in _open)
                c.Dispose();
            _open.Clear();
        }
    }

    private async Task PumpAsync(TcpClient client, CancellationToken token)
    {
        Interlocked.Increment(ref Accepted);
        var generation = Volatile.Read(ref _generation);
        using var upstream = new TcpClient();
        lock (_open)
            _open.Add(client);
        try
        {
            if (_blackHole)
            {
                // Accept, then say nothing: the client waits for a greeting or handshake that never comes.
                await Task.Delay(Timeout.Infinite, token);
                return;
            }
            await upstream.ConnectAsync(_target, token);
            lock (_open)
                _open.Add(upstream);
            var a = CopyAsync(client.GetStream(), upstream.GetStream(), generation, token);
            var b = CopyAsync(upstream.GetStream(), client.GetStream(), generation, token);
            await Task.WhenAny(a, b);
        }
        catch
        {
            // Connection cut.
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>Copies data, or swallows it once the connection is black-holed or silently killed.</summary>
    private async Task CopyAsync(Stream from, Stream to, int generation, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await from.ReadAsync(buffer, token)) > 0)
        {
            if (_blackHole || generation != Volatile.Read(ref _generation))
                continue; // dropped on the floor: nobody is told
            await to.WriteAsync(buffer.AsMemory(0, n), token);
        }
    }

    public void Dispose() => Cut();
}
