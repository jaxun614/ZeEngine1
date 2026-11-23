#region Includes
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Xna.Framework;
#endregion

namespace ZeEngine.Networking;

// enumerate the roles a user can hold 
public enum NetRole { None, Host, Client }

/// <summary>
/// Minimal 1-vs-1 TCP networking. One instance hosts, the other connects.
/// Each packet is 8 bytes: float X, float Y. All socket work happens on a
/// background thread; the game thread only calls Send() and TryGetRemote().
/// </summary>
public sealed class NetworkManager : IDisposable
{
    public const int DefaultPort = 7777;

    private readonly object _stateLock = new();
    private readonly object _sendLock = new();
    private readonly byte[] _sendBuffer = new byte[8];

    private TcpListener _listener;
    private TcpClient _client;
    private NetworkStream _stream;

    private int _generation;                       // invalidates stale background threads
    private volatile bool _connected;
    private volatile string _status = "Offline";
    private volatile NetRole _role = NetRole.None;

    private Vector2 _remote;
    private bool _hasRemote;

    public NetRole Role => _role;
    public bool Connected => _connected;
    public string Status => _status;

    // ---------------------------------------------------------------- Host
    public void Host(int port)
    {
        Stop();
        int gen = _generation;
        _role = NetRole.Host;
        _status = $"Hosting on port {port} - waiting for a player...";

        new Thread(() => HostThread(port, gen)) { IsBackground = true }.Start();
    }

    private void HostThread(int port, int gen)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            lock (_stateLock)
            {
                if (gen != _generation) return;
                _listener = listener;
            }

            listener.Start();
            TcpClient accepted = listener.AcceptTcpClient(); // blocks until someone joins
            listener.Stop();

            RunConnection(accepted, gen, "Player joined!");
        }
        catch (Exception ex)
        {
            Fail(gen, ex);
        }
    }

    // ---------------------------------------------------------------- Client
    public void Connect(string ip, int port)
    {
        Stop();
        int gen = _generation;
        _role = NetRole.Client;
        _status = $"Connecting to {ip}:{port}...";

        new Thread(() => ClientThread(ip, port, gen)) { IsBackground = true }.Start();
    }

    private void ClientThread(string ip, int port, int gen)
    {
        try
        {
            var client = new TcpClient();
            client.Connect(ip, port); // blocks until connected or fails
            RunConnection(client, gen, $"Connected to {ip}:{port}");
        }
        catch (Exception ex)
        {
            Fail(gen, ex);
        }
    }

    // ---------------------------------------------------------------- Shared receive loop
    private void RunConnection(TcpClient client, int gen, string message)
    {
        client.NoDelay = true;

        lock (_stateLock)
        {
            if (gen != _generation) { client.Dispose(); return; }
            _client = client;
            _stream = client.GetStream();
        }

        _connected = true;
        _status = message;

        var buffer = new byte[8];
        while (true)
        {
            _stream.ReadExactly(buffer, 0, 8); // throws when the other side disconnects
            float x = BitConverter.ToSingle(buffer, 0);
            float y = BitConverter.ToSingle(buffer, 4);

            lock (_stateLock)
            {
                _remote = new Vector2(x, y);
                _hasRemote = true;
            }
        }
    }

    private void Fail(int gen, Exception ex)
    {
        if (gen != _generation) return; // we stopped on purpose, ignore

        string reason = ex is EndOfStreamException || ex is IOException
            ? "Other player disconnected"
            : ex.Message;

        Stop();
        _status = $"{reason}. Press H to host or J to join.";
    }

    // ---------------------------------------------------------------- Game-thread API
    public void Send(Vector2 position)
    {
        if (!_connected) return;

        try
        {
            lock (_sendLock)
            {
                BitConverter.TryWriteBytes(_sendBuffer.AsSpan(0, 4), position.X);
                BitConverter.TryWriteBytes(_sendBuffer.AsSpan(4, 4), position.Y);
                _stream.Write(_sendBuffer, 0, 8);
            }
        }
        catch
        {
            // The receive thread will notice the broken connection and report it.
        }
    }

    public bool TryGetRemote(out Vector2 position)
    {
        lock (_stateLock)
        {
            position = _remote;
            return _hasRemote;
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            _generation++;
            _connected = false;
            _role = NetRole.None;
            _status = "Offline";
            _hasRemote = false;

            try { _listener?.Stop(); } catch { }
            try { _client?.Dispose(); } catch { }
            _listener = null;
            _client = null;
            _stream = null;
        }
    }

    public void Dispose() => Stop();
}
