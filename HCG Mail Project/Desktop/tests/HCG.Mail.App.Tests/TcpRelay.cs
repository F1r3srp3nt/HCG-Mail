using System.Net;
using System.Net.Sockets;

namespace HCG.Mail.App.Tests;

/// <summary>
/// Minimal TCP relay used to simulate a network interruption between HCG Mail and a server:
/// <see cref="Cut"/> drops every connection and refuses new ones; <see cref="Restore"/> accepts again.
/// </summary>
internal sealed class TcpRelay : IDisposable
{
    private readonly IPEndPoint _target;
    private readonly List<TcpClient> _open = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

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
        using var upstream = new TcpClient();
        lock (_open)
            _open.Add(client);
        try
        {
            await upstream.ConnectAsync(_target, token);
            lock (_open)
                _open.Add(upstream);
            var a = client.GetStream().CopyToAsync(upstream.GetStream(), token);
            var b = upstream.GetStream().CopyToAsync(client.GetStream(), token);
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

    public void Dispose() => Cut();
}
