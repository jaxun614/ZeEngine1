#region Includes
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
#endregion

namespace ZeEngine.Networking;

/// <summary>What this machine is doing on the network.</summary>
public enum NetRole { None, Host, Client }

/// <summary>
/// Message-based TCP networking for one host and any number of clients.
///
/// Usage:
///   Network.Register&lt;PositionMessage&gt;(16);                  // once, at startup (ids 16-255)
///   Network.On&lt;PositionMessage&gt;((from, msg) => { ... });    // who sent it, and what
///   Network.Host();                    // or Network.Connect("192.168.1.20")
///   Network.Send(new PositionMessage { ... });                // host: to everyone; client: to the host
///
/// Threading: every socket operation runs on background tasks, but nothing reaches your game
/// code from those threads. Incoming messages and connection events are queued and delivered
/// from <see cref="Update"/>, which <see cref="Core"/> calls once per frame. Call the rest of
/// this API from the game thread only.
///
/// Peers: the host is always peer 0. Clients get ids 1, 2, 3... as they join, and an id is
/// never reused within one hosting session. A client's <see cref="LocalPeerId"/> is the id the
/// host gave it.
///
/// Delivery: messages are reliable and ordered per connection (it is TCP). Clients talk to the
/// host only; to let clients see each other, have the host relay with <see cref="SendToAllExcept{T}"/>.
/// </summary>
public sealed class NetworkManager : IDisposable
{
    public const int DefaultPort = 7777;

    /// <summary>The host's peer id.</summary>
    public const int HostPeerId = 0;

    /// <summary>Returned or accepted where "no peer" is meant.</summary>
    public const int NoPeer = -1;

    /// <summary>Largest message payload, in bytes.</summary>
    public const int MaxMessageSize = NetProtocol.MaxPayload;

    /// <summary>The first message id games may use. Lower ids are reserved for the engine.</summary>
    public const byte FirstMessageId = NetProtocol.FirstUserMessage;

    private const int OutboxCapacity = 1024;
    private static readonly TimeSpan FlushGrace = TimeSpan.FromMilliseconds(500);

    // ------------------------------------------------------------ Settings (set before Host/Connect)

    /// <summary>Hosts and clients with different game ids refuse to talk to each other.</summary>
    public string GameId { get; set; } = "ZeEngine";

    /// <summary>The name other players see for you.</summary>
    public string PlayerName { get; set; } = "Player";

    /// <summary>How many clients a host accepts at once (not counting the host).</summary>
    public int MaxPeers { get; set; } = 8;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a connection sends a ping, so silence can be told apart from quiet.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A peer we have not heard from for this long is dropped with <see cref="NetDisconnectReason.Timeout"/>.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // ------------------------------------------------------------ Events (raised from Update, on the game thread)

    /// <summary>
    /// A peer finished the handshake. On a host this is a client that joined (its id). On a
    /// client it fires once, with <see cref="HostPeerId"/>, when the connection is ready.
    /// </summary>
    public event Action<int> PeerConnected;

    /// <summary>
    /// A connected peer went away. On a client this is the host, which means the game is over.
    /// Not raised for peers you drop yourself by calling <see cref="Stop"/>.
    /// </summary>
    public event Action<int, NetDisconnectReason> PeerDisconnected;

    /// <summary>A <see cref="Connect"/> attempt did not get through. <see cref="Status"/> says why in words.</summary>
    public event Action<NetDisconnectReason> ConnectionFailed;

    // ------------------------------------------------------------ State

    private readonly object _lock = new();
    private readonly ConcurrentQueue<NetEvent> _events = new();

    private volatile Session _session;
    private int _generation;                        // bumped whenever a session starts or is stopped on purpose
    private volatile string _status = "Offline";

    // Message registry and send scratch space. Game thread only.
    private readonly Dictionary<byte, MessageType> _typesById = new();
    private readonly Dictionary<Type, MessageType> _typesByClrType = new();
    private readonly NetWriter _scratch = new(256);
    private readonly List<Peer> _targets = new();

    public NetRole Role => _session?.Role ?? NetRole.None;

    /// <summary>True when at least one peer is connected and ready.</summary>
    public bool Connected => PeerCount > 0;

    /// <summary>One line describing what the network is doing, for a debug overlay or menu.</summary>
    public string Status => _status;

    /// <summary>Our own id: 0 on a host, the id the host assigned on a client, <see cref="NoPeer"/> when offline.</summary>
    public int LocalPeerId => _session?.LocalPeerId ?? NoPeer;

    /// <summary>The TCP port a host is listening on (useful when hosting on port 0), or 0.</summary>
    public int LocalPort => _session?.Port ?? 0;

    /// <summary>Connected peers: the number of joined clients on a host, 1 or 0 on a client.</summary>
    public int PeerCount
    {
        get
        {
            lock (_lock) return _session?.Peers.Count ?? 0;
        }
    }

    /// <summary>The ids of the connected peers right now.</summary>
    public int[] GetPeerIds()
    {
        lock (_lock) return _session?.Peers.Keys.OrderBy(id => id).ToArray() ?? Array.Empty<int>();
    }

    /// <summary>The name a peer gave when it joined (the host's name for peer 0), or null if unknown.</summary>
    public string GetPeerName(int peerId)
    {
        lock (_lock)
            return _session != null && _session.Peers.TryGetValue(peerId, out Peer peer) ? peer.Name : null;
    }

    // ------------------------------------------------------------ Message registry

    /// <summary>
    /// Registers a message type under an id. Both sides must use the same id for the same type.
    /// Do this once at startup, before connecting.
    /// </summary>
    public void Register<T>(byte id) where T : INetMessage, new()
    {
        if (id < FirstMessageId)
            throw new ArgumentOutOfRangeException(nameof(id), $"Message ids below {FirstMessageId} are reserved for the engine.");
        if (_typesById.ContainsKey(id))
            throw new InvalidOperationException($"Message id {id} is already registered to {_typesById[id].Type.Name}.");
        if (_typesByClrType.ContainsKey(typeof(T)))
            throw new InvalidOperationException($"{typeof(T).Name} is already registered.");

        var type = new MessageType { Id = id, Type = typeof(T), Factory = () => new T() };
        _typesById[id] = type;
        _typesByClrType[typeof(T)] = type;
    }

    /// <summary>
    /// Calls <paramref name="handler"/> with the sender's peer id and the message whenever one
    /// arrives. Handlers run from <see cref="Update"/>. Add as many as you like.
    /// </summary>
    public void On<T>(Action<int, T> handler) where T : INetMessage
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        MessageType type = Lookup(typeof(T));
        var entry = new Handler(handler, (from, message) => handler(from, (T)message));
        type.Handlers = type.Handlers.Append(entry).ToArray();
    }

    /// <summary>Removes a handler added with <see cref="On{T}"/>.</summary>
    public void Off<T>(Action<int, T> handler) where T : INetMessage
    {
        MessageType type = Lookup(typeof(T));
        type.Handlers = type.Handlers.Where(h => !h.Original.Equals(handler)).ToArray();
    }

    private MessageType Lookup(Type clrType)
    {
        if (_typesByClrType.TryGetValue(clrType, out MessageType type)) return type;
        throw new InvalidOperationException($"{clrType.Name} is not registered. Call Register<{clrType.Name}>(id) first.");
    }

    // ------------------------------------------------------------ Hosting and connecting

    /// <summary>
    /// Starts hosting. Returns false (and explains in <see cref="Status"/>) if the port could not
    /// be opened, for example because another program is using it. Pass 0 for any free port.
    /// </summary>
    public bool Host(int port = DefaultPort)
    {
        Session s = BeginSession(NetRole.Host);
        s.LocalPeerId = HostPeerId;

        try
        {
            s.Listener = new TcpListener(IPAddress.Any, port);
            s.Listener.Start();
            s.Port = ((IPEndPoint)s.Listener.LocalEndpoint).Port;
        }
        catch (Exception ex)
        {
            EndSession(s);
            SetStatus(s, $"Could not host on port {port}: {ex.Message}");
            return false;
        }

        RefreshHostStatus(s);
        _ = Task.Run(() => AcceptLoopAsync(s));
        return true;
    }

    /// <summary>
    /// Connects to a host. This returns immediately; watch <see cref="PeerConnected"/> for success
    /// and <see cref="ConnectionFailed"/> for failure.
    /// </summary>
    public void Connect(string host, int port = DefaultPort)
    {
        Session s = BeginSession(NetRole.Client);
        SetStatus(s, $"Connecting to {host}:{port}...");
        _ = Task.Run(() => ConnectAsync(s, host, port));
    }

    /// <summary>
    /// Disconnects everyone and goes offline. Peers are told why. No events are raised for the
    /// connections you close this way, and events already queued are discarded.
    /// </summary>
    public void Stop()
    {
        Session s;
        lock (_lock)
        {
            s = _session;
            _session = null;
            _generation++;
            _status = "Offline";
        }

        if (s != null) Teardown(s, NetDisconnectReason.Shutdown);
    }

    /// <summary>Host only: removes a client. It is told it was kicked.</summary>
    public void Kick(int peerId)
    {
        Session s = _session;
        if (s == null || s.Role != NetRole.Host || peerId == HostPeerId) return;

        Peer peer = GetPeer(s, peerId);
        if (peer != null) Drop(s, peer, NetDisconnectReason.Kicked, raiseEvent: true, notifyRemote: true);
    }

    public void Dispose()
    {
        Session s;
        lock (_lock)
        {
            s = _session;
            _session = null;
            _generation++;
            _status = "Offline";
        }

        if (s == null) return;

        // Give the goodbye messages a moment to leave before the process exits.
        Task[] flushes = Teardown(s, NetDisconnectReason.Shutdown);
        try { Task.WaitAll(flushes, FlushGrace + FlushGrace); } catch { }
    }

    // ------------------------------------------------------------ Sending

    /// <summary>
    /// Sends to everyone we are connected to: all clients when hosting, the host when a client.
    /// Returns false if there was nobody to send to.
    /// </summary>
    public bool Send<T>(T message) where T : INetMessage => SendCore(message, NoPeer, NoPeer);

    /// <summary>Host only: sends to one client.</summary>
    public bool SendTo<T>(int peerId, T message) where T : INetMessage
    {
        if (peerId < 0) throw new ArgumentOutOfRangeException(nameof(peerId));
        return SendCore(message, peerId, NoPeer);
    }

    /// <summary>Host only: sends to every client except one. Use it to relay a client's message to the others.</summary>
    public bool SendToAllExcept<T>(int peerId, T message) where T : INetMessage => SendCore(message, NoPeer, peerId);

    private bool SendCore(INetMessage message, int onlyPeer, int exceptPeer)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));

        MessageType type = Lookup(message.GetType());

        _scratch.Reset();
        message.Write(_scratch);
        if (_scratch.Length > MaxMessageSize)
            throw new ArgumentException($"{type.Type.Name} is {_scratch.Length} bytes; the maximum is {MaxMessageSize}.");

        Session s;
        _targets.Clear();
        lock (_lock)
        {
            s = _session;
            if (s == null) return false;

            foreach (Peer peer in s.Peers.Values)
            {
                if (!peer.Ready) continue;
                if (onlyPeer != NoPeer && peer.Id != onlyPeer) continue;
                if (peer.Id == exceptPeer) continue;
                _targets.Add(peer);
            }
        }

        if (_targets.Count == 0) return false;

        // One frame, shared by every recipient.
        byte[] frame = NetProtocol.BuildFrame(type.Id, _scratch.AsSpan());

        bool queued = false;
        foreach (Peer peer in _targets)
        {
            if (peer.Outbox.Writer.TryWrite(frame))
                queued = true;
            else
                Drop(s, peer, NetDisconnectReason.SendOverflow, raiseEvent: true, notifyRemote: false);
        }

        _targets.Clear();
        return queued;
    }

    // ------------------------------------------------------------ Game-thread pump

    /// <summary>
    /// Delivers queued connection events and messages to your handlers. <see cref="Core"/> calls
    /// this once per frame; call it yourself only if you use this class without Core.
    /// </summary>
    public void Update()
    {
        while (_events.TryDequeue(out NetEvent e))
        {
            // Events from a session that has since been stopped or replaced are stale.
            if (e.SessionId != Volatile.Read(ref _generation)) continue;

            switch (e.Kind)
            {
                case NetEventKind.Connected:
                    PeerConnected?.Invoke(e.PeerId);
                    break;

                case NetEventKind.Disconnected:
                    PeerDisconnected?.Invoke(e.PeerId, e.Reason);
                    break;

                case NetEventKind.ConnectFailed:
                    ConnectionFailed?.Invoke(e.Reason);
                    break;

                case NetEventKind.Message:
                    Dispatch(e);
                    break;
            }
        }
    }

    private void Dispatch(NetEvent e)
    {
        if (!_typesById.TryGetValue(e.MessageId, out MessageType type))
        {
            // Probably a newer build of the game on the other side. Not worth disconnecting over.
            Debug.WriteLine($"Network: ignoring message with unregistered id {e.MessageId} from peer {e.PeerId}.");
            return;
        }

        INetMessage message;
        try
        {
            message = type.Factory();
            message.Read(new NetReader(e.Payload));
        }
        catch (Exception ex)
        {
            // A message we cannot read means the peer is broken or hostile. Drop it, keep playing.
            Debug.WriteLine($"Network: could not read {type.Type.Name} from peer {e.PeerId}: {ex.Message}");

            Session s = _session;
            Peer peer = s == null ? null : GetPeer(s, e.PeerId);
            if (peer != null) Drop(s, peer, NetDisconnectReason.ProtocolError, raiseEvent: true, notifyRemote: true);
            return;
        }

        foreach (Handler handler in type.Handlers)
            handler.Invoke(e.PeerId, message);
    }

    // ------------------------------------------------------------ Sessions

    private Session BeginSession(NetRole role)
    {
        Stop();

        Session s;
        lock (_lock)
        {
            s = new Session
            {
                Id = ++_generation,
                Role = role,
                GameId = GameId,
                PlayerName = PlayerName,
                MaxPeers = MaxPeers,
                ConnectTimeout = ConnectTimeout,
                HandshakeTimeout = HandshakeTimeout,
                KeepAliveInterval = KeepAliveInterval,
                IdleTimeout = IdleTimeout,
            };
            _session = s;
        }

        _ = Task.Run(() => KeepAliveLoopAsync(s));
        return s;
    }

    /// <summary>Ends a session that stopped by itself (lost connection, failed to connect).</summary>
    private void EndSession(Session s)
    {
        lock (_lock)
        {
            if (_session == s) _session = null;
        }

        Teardown(s, NetDisconnectReason.Shutdown);
    }

    private Task[] Teardown(Session s, NetDisconnectReason reason)
    {
        try { s.Cts.Cancel(); } catch { }
        try { s.Listener?.Stop(); } catch { }

        Peer[] peers;
        lock (_lock) peers = s.Peers.Values.ToArray();

        var flushes = new Task[peers.Length];
        for (int i = 0; i < peers.Length; i++)
            flushes[i] = Drop(s, peers[i], reason, raiseEvent: false, notifyRemote: true);

        return flushes;
    }

    private void SetStatus(Session s, string text)
    {
        if (Volatile.Read(ref _generation) == s.Id) _status = text;
    }

    private void RefreshHostStatus(Session s)
    {
        int count;
        lock (_lock) count = s.Peers.Count;

        SetStatus(s, count == 0
            ? $"Hosting on port {s.Port} - waiting for players..."
            : $"Hosting on port {s.Port} - {count} player{(count == 1 ? "" : "s")} connected");
    }

    private Peer GetPeer(Session s, int peerId)
    {
        lock (_lock) return s.Peers.TryGetValue(peerId, out Peer peer) ? peer : null;
    }

    private void Post(Session s, NetEvent e)
    {
        e.SessionId = s.Id;
        _events.Enqueue(e);
    }

    // ------------------------------------------------------------ Host: accepting clients

    private async Task AcceptLoopAsync(Session s)
    {
        try
        {
            while (!s.Cts.IsCancellationRequested)
            {
                TcpClient tcp = await s.Listener.AcceptTcpClientAsync(s.Cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleIncomingAsync(s, tcp));
            }
        }
        catch (Exception ex)
        {
            if (!s.Cts.IsCancellationRequested)
            {
                Debug.WriteLine($"Network: accept loop stopped: {ex.Message}");
                SetStatus(s, $"Hosting stopped: {ex.Message}");
                EndSession(s);
            }
        }
    }

    /// <summary>Runs the handshake for one new connection, then reads from it until it ends.</summary>
    private async Task HandleIncomingAsync(Session s, TcpClient tcp)
    {
        bool handedOff = false;
        try
        {
            tcp.NoDelay = true;
            NetworkStream stream = tcp.GetStream();

            (byte Type, byte[] Payload) hello;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token))
            {
                timeout.CancelAfter(s.HandshakeTimeout);
                hello = await NetProtocol.ReadFrameAsync(stream, new byte[NetProtocol.HeaderSize], timeout.Token)
                                         .ConfigureAwait(false);
            }

            if (hello.Type != NetProtocol.MsgHello) return;

            var reader = new NetReader(hello.Payload);
            ushort version = reader.ReadUInt16();
            string gameId = reader.ReadString();
            string name = reader.ReadString();

            if (version != NetProtocol.Version || gameId != s.GameId)
            {
                await RejectAsync(stream, NetDisconnectReason.VersionMismatch).ConfigureAwait(false);
                return;
            }

            Peer peer = null;
            bool full = false;
            lock (_lock)
            {
                if (_session != s) return;

                if (s.Peers.Count >= s.MaxPeers)
                {
                    full = true;
                }
                else
                {
                    peer = new Peer(++s.NextPeerId, tcp, stream) { Name = name };

                    // The welcome goes into the outbox before the peer becomes visible to
                    // Send(), so no game message can ever overtake it.
                    var welcome = new NetWriter();
                    welcome.Write(peer.Id);
                    welcome.Write(s.PlayerName);
                    peer.Outbox.Writer.TryWrite(NetProtocol.BuildFrame(NetProtocol.MsgWelcome, welcome.AsSpan()));

                    peer.Ready = true;
                    s.Peers[peer.Id] = peer;
                }
            }

            if (full)
            {
                await RejectAsync(stream, NetDisconnectReason.ServerFull).ConfigureAwait(false);
                return;
            }

            handedOff = true;
            peer.Touch();
            StartWriter(s, peer);

            Post(s, new NetEvent { Kind = NetEventKind.Connected, PeerId = peer.Id });
            RefreshHostStatus(s);

            await ReadLoopAsync(s, peer).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed or garbled handshake. Nothing to tell the game; just close the socket.
            Debug.WriteLine($"Network: incoming connection dropped during handshake: {ex.Message}");
        }
        finally
        {
            if (!handedOff) CloseQuietly(tcp);
        }
    }

    private static async Task RejectAsync(NetworkStream stream, NetDisconnectReason reason)
    {
        try
        {
            using var timeout = new CancellationTokenSource(FlushGrace);
            byte[] frame = NetProtocol.BuildFrame(NetProtocol.MsgReject, new[] { (byte)reason });
            await stream.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // The other side is gone or too slow; either way we are closing.
        }
    }

    // ------------------------------------------------------------ Client: connecting to a host

    private async Task ConnectAsync(Session s, string host, int port)
    {
        var tcp = new TcpClient { NoDelay = true };
        bool handedOff = false;

        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token))
            {
                timeout.CancelAfter(s.ConnectTimeout);
                await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }

            NetworkStream stream = tcp.GetStream();

            var hello = new NetWriter();
            hello.Write(NetProtocol.Version);
            hello.Write(s.GameId);
            hello.Write(s.PlayerName);
            await stream.WriteAsync(NetProtocol.BuildFrame(NetProtocol.MsgHello, hello.AsSpan()), s.Cts.Token)
                        .ConfigureAwait(false);

            (byte Type, byte[] Payload) reply;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token))
            {
                timeout.CancelAfter(s.HandshakeTimeout);
                reply = await NetProtocol.ReadFrameAsync(stream, new byte[NetProtocol.HeaderSize], timeout.Token)
                                         .ConfigureAwait(false);
            }

            if (reply.Type == NetProtocol.MsgReject)
            {
                var reason = (NetDisconnectReason)(reply.Payload.Length > 0 ? reply.Payload[0] : (byte)0);
                FailConnect(s, reason, DescribeRejection(reason));
                return;
            }

            if (reply.Type != NetProtocol.MsgWelcome)
            {
                FailConnect(s, NetDisconnectReason.ProtocolError, "The host sent an unexpected reply.");
                return;
            }

            var reader = new NetReader(reply.Payload);
            int myId = reader.ReadInt32();
            string hostName = reader.ReadString();

            var peer = new Peer(HostPeerId, tcp, stream) { Name = hostName };
            lock (_lock)
            {
                if (_session != s) return;   // Stop() was called while we were connecting.

                s.LocalPeerId = myId;
                peer.Ready = true;
                s.Peers[HostPeerId] = peer;
            }

            handedOff = true;
            peer.Touch();
            StartWriter(s, peer);

            SetStatus(s, $"Connected to {host}:{port}");
            Post(s, new NetEvent { Kind = NetEventKind.Connected, PeerId = HostPeerId });

            await ReadLoopAsync(s, peer).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (s.Cts.IsCancellationRequested)
        {
            // Stop() was called; nothing to report.
        }
        catch (OperationCanceledException)
        {
            FailConnect(s, NetDisconnectReason.ConnectFailed, $"Connecting to {host}:{port} timed out.");
        }
        catch (Exception ex) when (!s.Cts.IsCancellationRequested)
        {
            string why = ex is EndOfStreamException || ex is IOException
                ? "The host closed the connection."
                : ex.Message;

            FailConnect(s, NetDisconnectReason.ConnectFailed, $"Could not connect to {host}:{port}: {why}");
        }
        finally
        {
            if (!handedOff) CloseQuietly(tcp);
        }
    }

    private void FailConnect(Session s, NetDisconnectReason reason, string message)
    {
        SetStatus(s, message);
        Post(s, new NetEvent { Kind = NetEventKind.ConnectFailed, Reason = reason });
        EndSession(s);
    }

    private static string DescribeRejection(NetDisconnectReason reason) => reason switch
    {
        NetDisconnectReason.ServerFull => "The game is full.",
        NetDisconnectReason.VersionMismatch => "That host is running a different game or version.",
        _ => "The host refused the connection.",
    };

    // ------------------------------------------------------------ Per-peer I/O

    /// <summary>Reads frames from a peer until it disconnects, queueing game messages for Update.</summary>
    private async Task ReadLoopAsync(Session s, Peer p)
    {
        var header = new byte[NetProtocol.HeaderSize];
        NetDisconnectReason reason = NetDisconnectReason.ConnectionLost;

        try
        {
            while (true)
            {
                (byte type, byte[] payload) = await NetProtocol.ReadFrameAsync(p.Stream, header, CancellationToken.None)
                                                               .ConfigureAwait(false);
                p.Touch();

                if (type == NetProtocol.MsgPing) continue;

                if (type == NetProtocol.MsgDisconnect)
                {
                    reason = payload.Length > 0 && Enum.IsDefined((NetDisconnectReason)payload[0])
                        ? (NetDisconnectReason)payload[0]
                        : NetDisconnectReason.Shutdown;
                    break;
                }

                if (type < NetProtocol.FirstUserMessage)
                {
                    // A handshake message after the handshake: the peer is confused.
                    reason = NetDisconnectReason.ProtocolError;
                    break;
                }

                Post(s, new NetEvent { Kind = NetEventKind.Message, PeerId = p.Id, MessageId = type, Payload = payload });
            }
        }
        catch (InvalidDataException)
        {
            reason = NetDisconnectReason.ProtocolError;
        }
        catch
        {
            // Closed, reset, or disposed by Drop: the default reason (ConnectionLost) stands.
        }

        // No-op if we already dropped this peer ourselves.
        _ = Drop(s, p, reason, raiseEvent: true, notifyRemote: reason == NetDisconnectReason.ProtocolError);

        if (s.Role == NetRole.Client)
        {
            SetStatus(s, DescribeLoss(reason));
            EndSession(s);
        }
    }

    private static string DescribeLoss(NetDisconnectReason reason) => reason switch
    {
        NetDisconnectReason.Shutdown => "The host closed the game.",
        NetDisconnectReason.Kicked => "You were removed from the game.",
        NetDisconnectReason.Timeout => "The connection timed out.",
        NetDisconnectReason.ProtocolError => "Received invalid data from the host.",
        _ => "The connection was lost.",
    };

    /// <summary>Starts the task that sends a peer's queued frames in order.</summary>
    private void StartWriter(Session s, Peer p)
    {
        p.WriteTask = Task.Run(async () =>
        {
            try
            {
                await foreach (byte[] frame in p.Outbox.Reader.ReadAllAsync().ConfigureAwait(false))
                    await p.Stream.WriteAsync(frame).ConfigureAwait(false);
            }
            catch
            {
                _ = Drop(s, p, NetDisconnectReason.ConnectionLost, raiseEvent: true, notifyRemote: false);
            }
        });
    }

    /// <summary>
    /// Removes a peer and closes its connection. Safe to call more than once and from any thread;
    /// only the first call does anything. Returns a task that completes when the socket is closed.
    /// </summary>
    private Task Drop(Session s, Peer p, NetDisconnectReason reason, bool raiseEvent, bool notifyRemote)
    {
        if (Interlocked.Exchange(ref p.Dropped, 1) == 1) return Task.CompletedTask;

        bool wasReady;
        lock (_lock)
        {
            wasReady = p.Ready;
            p.Ready = false;

            if (s.Peers.TryGetValue(p.Id, out Peer current) && current == p)
                s.Peers.Remove(p.Id);
        }

        if (notifyRemote && wasReady)
            p.Outbox.Writer.TryWrite(NetProtocol.BuildFrame(NetProtocol.MsgDisconnect, new[] { (byte)reason }));

        // Whatever is already queued (including the goodbye) is still sent before closing.
        p.Outbox.Writer.TryComplete();

        if (wasReady)
        {
            if (raiseEvent) Post(s, new NetEvent { Kind = NetEventKind.Disconnected, PeerId = p.Id, Reason = reason });
            if (s.Role == NetRole.Host) RefreshHostStatus(s);
        }

        return CloseAfterFlushAsync(p);
    }

    private static async Task CloseAfterFlushAsync(Peer p)
    {
        try { await Task.WhenAny(p.WriteTask, Task.Delay(FlushGrace)).ConfigureAwait(false); } catch { }
        CloseQuietly(p.Tcp);
    }

    private static void CloseQuietly(TcpClient tcp)
    {
        try { tcp.Client.Shutdown(SocketShutdown.Send); } catch { }
        try { tcp.Dispose(); } catch { }
    }

    /// <summary>Pings every peer and drops any that have gone quiet. Runs for the life of a session.</summary>
    private async Task KeepAliveLoopAsync(Session s)
    {
        byte[] ping = NetProtocol.BuildFrame(NetProtocol.MsgPing, ReadOnlySpan<byte>.Empty);

        try
        {
            while (!s.Cts.IsCancellationRequested)
            {
                await Task.Delay(s.KeepAliveInterval, s.Cts.Token).ConfigureAwait(false);

                long now = Environment.TickCount64;
                Peer[] peers;
                lock (_lock) peers = s.Peers.Values.ToArray();

                foreach (Peer p in peers)
                {
                    if (now - Volatile.Read(ref p.LastReceiveTicks) > s.IdleTimeout.TotalMilliseconds)
                        _ = Drop(s, p, NetDisconnectReason.Timeout, raiseEvent: true, notifyRemote: true);
                    else if (!p.Outbox.Writer.TryWrite(ping))
                        _ = Drop(s, p, NetDisconnectReason.SendOverflow, raiseEvent: true, notifyRemote: false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Network: keep-alive loop stopped: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ Internal types

    private sealed class Session
    {
        public int Id;
        public NetRole Role;
        public readonly CancellationTokenSource Cts = new();

        // Settings captured when the session began.
        public string GameId;
        public string PlayerName;
        public int MaxPeers;
        public TimeSpan ConnectTimeout, HandshakeTimeout, KeepAliveInterval, IdleTimeout;

        public TcpListener Listener;
        public volatile int Port;
        public volatile int LocalPeerId = NoPeer;
        public int NextPeerId;                                  // host only; guarded by the manager's lock

        public readonly Dictionary<int, Peer> Peers = new();    // guarded by the manager's lock
    }

    private sealed class Peer
    {
        public readonly int Id;
        public readonly TcpClient Tcp;
        public readonly NetworkStream Stream;
        public string Name;
        public volatile bool Ready;
        public int Dropped;
        public long LastReceiveTicks;
        public Task WriteTask = Task.CompletedTask;

        public readonly Channel<byte[]> Outbox = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(OutboxCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

        public Peer(int id, TcpClient tcp, NetworkStream stream)
        {
            Id = id;
            Tcp = tcp;
            Stream = stream;
            Touch();
        }

        public void Touch() => Volatile.Write(ref LastReceiveTicks, Environment.TickCount64);
    }

    private enum NetEventKind { Connected, Disconnected, ConnectFailed, Message }

    private struct NetEvent
    {
        public NetEventKind Kind;
        public int SessionId;
        public int PeerId;
        public NetDisconnectReason Reason;
        public byte MessageId;
        public byte[] Payload;
    }

    private sealed class MessageType
    {
        public byte Id;
        public Type Type;
        public Func<INetMessage> Factory;
        public Handler[] Handlers = Array.Empty<Handler>();   // replaced, never mutated, so dispatch can iterate safely
    }

    private readonly record struct Handler(Delegate Original, Action<int, INetMessage> Invoke);
}
