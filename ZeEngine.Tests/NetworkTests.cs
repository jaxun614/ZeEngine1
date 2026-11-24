using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;
using ZeEngine.Networking;

namespace ZeEngine.Tests;

// Two small game messages used by the tests.
public sealed class ChatMessage : INetMessage
{
    public string Text;
    public void Write(NetWriter writer) => writer.Write(Text);
    public void Read(NetReader reader) => Text = reader.ReadString();
}

public sealed class NumberMessage : INetMessage
{
    public int Value;
    public void Write(NetWriter writer) => writer.Write(Value);
    public void Read(NetReader reader) => Value = reader.ReadInt32();
}

// Never registered, to test the "forgot to register" error.
public sealed class UnregisteredMessage : INetMessage
{
    public void Write(NetWriter writer) { }
    public void Read(NetReader reader) { }
}

public sealed class NetworkTests : IDisposable
{
    private const string TestGameId = "zeengine-tests";

    /// <summary>A NetworkManager plus a record of everything that happened to it.</summary>
    private sealed class Node
    {
        public NetworkManager Net;
        public readonly List<int> Connected = new();
        public readonly List<(int Peer, NetDisconnectReason Reason)> Disconnected = new();
        public readonly List<NetDisconnectReason> Failed = new();
        public readonly List<(int From, string Text)> Chats = new();
        public readonly List<(int From, int Value)> Numbers = new();
    }

    private readonly List<Node> _nodes = new();

    public void Dispose()
    {
        foreach (Node node in _nodes) node.Net.Dispose();
    }

    private Node Create(string name = "Player", Action<NetworkManager> configure = null)
    {
        var net = new NetworkManager
        {
            GameId = TestGameId,
            PlayerName = name,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            KeepAliveInterval = TimeSpan.FromMilliseconds(100),
            IdleTimeout = TimeSpan.FromSeconds(5),
        };
        configure?.Invoke(net);

        net.Register<ChatMessage>(16);
        net.Register<NumberMessage>(17);

        var node = new Node { Net = net };
        net.PeerConnected += id => node.Connected.Add(id);
        net.PeerDisconnected += (id, reason) => node.Disconnected.Add((id, reason));
        net.ConnectionFailed += reason => node.Failed.Add(reason);
        net.On<ChatMessage>((from, m) => node.Chats.Add((from, m.Text)));
        net.On<NumberMessage>((from, m) => node.Numbers.Add((from, m.Value)));

        _nodes.Add(node);
        return node;
    }

    private Node StartHost(Action<NetworkManager> configure = null)
    {
        Node host = Create("Host", configure);
        Assert.True(host.Net.Host(0), host.Net.Status);
        return host;
    }

    private async Task<Node> JoinAsync(Node host, string name)
    {
        Node client = Create(name);
        client.Net.Connect("127.0.0.1", host.Net.LocalPort);
        await WaitFor(() => client.Connected.Count == 1, $"{name} to connect");
        return client;
    }

    /// <summary>Pumps Update() on every node until the condition holds.</summary>
    private async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            foreach (Node node in _nodes) node.Net.Update();
            if (condition()) return;

            if (timer.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"Timed out waiting for {what}.");

            await Task.Delay(5);
        }
    }

    /// <summary>Pumps for a fixed time, for asserting that something does NOT happen.</summary>
    private async Task Settle(int ms = 300)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < ms)
        {
            foreach (Node node in _nodes) node.Net.Update();
            await Task.Delay(5);
        }
    }

    // ------------------------------------------------------------ Writer and reader

    [Fact]
    public void WriterAndReader_RoundTripEveryType()
    {
        var w = new NetWriter(4);   // tiny on purpose, to force it to grow
        w.Write((byte)200);
        w.Write(true);
        w.Write(false);
        w.Write((short)-1234);
        w.Write((ushort)54321);
        w.Write(-123456789);
        w.Write(4000000000u);
        w.Write(long.MinValue + 7);
        w.Write(3.25f);
        w.Write("héllo wörld ✓");
        w.Write("");
        w.Write((string)null);

        var r = new NetReader(w.ToArray());
        Assert.Equal((byte)200, r.ReadByte());
        Assert.True(r.ReadBoolean());
        Assert.False(r.ReadBoolean());
        Assert.Equal((short)-1234, r.ReadInt16());
        Assert.Equal((ushort)54321, r.ReadUInt16());
        Assert.Equal(-123456789, r.ReadInt32());
        Assert.Equal(4000000000u, r.ReadUInt32());
        Assert.Equal(long.MinValue + 7, r.ReadInt64());
        Assert.Equal(3.25f, r.ReadSingle());
        Assert.Equal("héllo wörld ✓", r.ReadString());
        Assert.Equal("", r.ReadString());
        Assert.Equal("", r.ReadString());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void Reader_ThrowsOnTruncatedOrLyingData()
    {
        Assert.Throws<InvalidDataException>(() => new NetReader(new byte[] { 1, 2 }).ReadInt32());

        // A string that claims 500 bytes but only has 3.
        var w = new NetWriter();
        w.Write((ushort)500);
        w.Write((byte)1);
        w.Write((byte)2);
        w.Write((byte)3);
        Assert.Throws<InvalidDataException>(() => new NetReader(w.ToArray()).ReadString());
    }

    [Fact]
    public void Writer_RejectsStringsTooLongForTheLengthPrefix()
    {
        Assert.Throws<ArgumentException>(() => new NetWriter().Write(new string('a', NetWriter.MaxStringBytes + 1)));
    }

    // ------------------------------------------------------------ Registry

    [Fact]
    public void Register_RejectsReservedAndDuplicateIds()
    {
        var net = new NetworkManager();
        Assert.Throws<ArgumentOutOfRangeException>(() => net.Register<ChatMessage>(3));
        net.Register<ChatMessage>(16);
        Assert.Throws<InvalidOperationException>(() => net.Register<NumberMessage>(16));   // id taken
        Assert.Throws<InvalidOperationException>(() => net.Register<ChatMessage>(20));     // type taken
    }

    [Fact]
    public void Send_UnregisteredMessage_Throws_AndOfflineSendReturnsFalse()
    {
        Node node = Create();
        Assert.Throws<InvalidOperationException>(() => node.Net.Send(new UnregisteredMessage()));
        Assert.False(node.Net.Send(new ChatMessage { Text = "nobody home" }));
    }

    // ------------------------------------------------------------ Connecting

    [Fact]
    public async Task HostAndClient_Connect_AndKnowEachOther()
    {
        Node host = StartHost();
        Assert.Equal(NetRole.Host, host.Net.Role);
        Assert.Equal(0, host.Net.LocalPeerId);
        Assert.False(host.Net.Connected);

        Node alice = await JoinAsync(host, "Alice");
        await WaitFor(() => host.Connected.Count == 1, "host to see Alice");

        Assert.Equal(NetRole.Client, alice.Net.Role);
        Assert.Equal(1, alice.Net.LocalPeerId);
        Assert.Equal(NetworkManager.HostPeerId, alice.Connected[0]);
        Assert.Equal(1, host.Connected[0]);

        Assert.True(host.Net.Connected);
        Assert.Equal(1, host.Net.PeerCount);
        Assert.Equal("Alice", host.Net.GetPeerName(1));
        Assert.Equal("Host", alice.Net.GetPeerName(NetworkManager.HostPeerId));
    }

    [Fact]
    public async Task SeveralClients_GetDistinctIds()
    {
        Node host = StartHost();
        Node a = await JoinAsync(host, "A");
        Node b = await JoinAsync(host, "B");
        Node c = await JoinAsync(host, "C");
        await WaitFor(() => host.Connected.Count == 3, "host to see all three");

        Assert.Equal(new[] { 1, 2, 3 }, host.Connected.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, new[] { a.Net.LocalPeerId, b.Net.LocalPeerId, c.Net.LocalPeerId }.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, host.Net.GetPeerIds());
    }

    [Fact]
    public async Task ServerFull_RejectsTheExtraClient()
    {
        Node host = StartHost(n => n.MaxPeers = 1);
        await JoinAsync(host, "First");

        Node second = Create("Second");
        second.Net.Connect("127.0.0.1", host.Net.LocalPort);
        await WaitFor(() => second.Failed.Count == 1, "second client to be rejected");

        Assert.Equal(NetDisconnectReason.ServerFull, second.Failed[0]);
        Assert.Equal(NetRole.None, second.Net.Role);
        Assert.Equal(1, host.Net.PeerCount);
    }

    [Fact]
    public async Task DifferentGameId_IsRejected()
    {
        Node host = StartHost();

        Node stranger = Create("Stranger", n => n.GameId = "some-other-game");
        stranger.Net.Connect("127.0.0.1", host.Net.LocalPort);
        await WaitFor(() => stranger.Failed.Count == 1, "mismatched client to be rejected");

        Assert.Equal(NetDisconnectReason.VersionMismatch, stranger.Failed[0]);
        Assert.Equal(0, host.Net.PeerCount);
    }

    [Fact]
    public async Task ConnectingToNothing_FailsWithAReadableStatus()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Node client = Create();
        client.Net.Connect("127.0.0.1", closedPort);
        await WaitFor(() => client.Failed.Count == 1, "connection attempt to fail");

        Assert.Equal(NetDisconnectReason.ConnectFailed, client.Failed[0]);
        Assert.Contains("Could not connect", client.Net.Status);
        Assert.Equal(NetRole.None, client.Net.Role);
    }

    [Fact]
    public void Host_OnABusyPort_ReturnsFalse()
    {
        var squatter = new TcpListener(IPAddress.Any, 0);
        squatter.Start();
        try
        {
            int port = ((IPEndPoint)squatter.LocalEndpoint).Port;

            Node host = Create();
            Assert.False(host.Net.Host(port));
            Assert.Contains("Could not host", host.Net.Status);
            Assert.Equal(NetRole.None, host.Net.Role);
        }
        finally
        {
            squatter.Stop();
        }
    }

    // ------------------------------------------------------------ Messages

    [Fact]
    public async Task Messages_FlowBothWays_WithTheSendersId()
    {
        Node host = StartHost();
        Node alice = await JoinAsync(host, "Alice");
        await WaitFor(() => host.Connected.Count == 1, "host to see Alice");

        Assert.True(alice.Net.Send(new ChatMessage { Text = "hi host" }));
        await WaitFor(() => host.Chats.Count == 1, "host to get the chat");
        Assert.Equal((1, "hi host"), host.Chats[0]);

        Assert.True(host.Net.SendTo(1, new NumberMessage { Value = 42 }));
        await WaitFor(() => alice.Numbers.Count == 1, "Alice to get the number");
        Assert.Equal((NetworkManager.HostPeerId, 42), alice.Numbers[0]);
    }

    [Fact]
    public async Task Messages_ArriveInOrder_EvenInABurst()
    {
        Node host = StartHost();
        Node alice = await JoinAsync(host, "Alice");
        await WaitFor(() => host.Connected.Count == 1, "host to see Alice");

        for (int i = 0; i < 500; i++) alice.Net.Send(new NumberMessage { Value = i });

        await WaitFor(() => host.Numbers.Count == 500, "all 500 numbers");
        Assert.True(host.Numbers.Select(n => n.Value).SequenceEqual(Enumerable.Range(0, 500)));
    }

    [Fact]
    public async Task HostBroadcast_ReachesEveryone_AndRelayExcludesTheOriginalSender()
    {
        Node host = StartHost();
        Node a = await JoinAsync(host, "A");
        Node b = await JoinAsync(host, "B");
        await WaitFor(() => host.Connected.Count == 2, "host to see both");

        host.Net.Send(new ChatMessage { Text = "to all" });
        await WaitFor(() => a.Chats.Count == 1 && b.Chats.Count == 1, "both clients to get the broadcast");

        // A speaks; the host relays to everyone but A.
        int aId = a.Net.LocalPeerId;
        host.Net.On<NumberMessage>((from, m) => host.Net.SendToAllExcept(from, m));
        a.Net.Send(new NumberMessage { Value = 7 });

        await WaitFor(() => b.Numbers.Count == 1, "B to get the relayed number");
        await Settle();

        Assert.Equal((NetworkManager.HostPeerId, 7), b.Numbers[0]);
        Assert.Equal(0, a.Numbers.Count);                 // A did not get its own message back
        Assert.Equal((aId, 7), host.Numbers[0]);
    }

    [Fact]
    public async Task Off_StopsADelivery()
    {
        Node host = StartHost();
        Node alice = await JoinAsync(host, "Alice");
        await WaitFor(() => host.Connected.Count == 1, "host to see Alice");

        int extraCalls = 0;
        Action<int, ChatMessage> counter = (from, m) => extraCalls++;
        host.Net.On(counter);

        alice.Net.Send(new ChatMessage { Text = "one" });
        await WaitFor(() => host.Chats.Count == 1, "first chat");
        Assert.Equal(1, extraCalls);

        host.Net.Off(counter);
        alice.Net.Send(new ChatMessage { Text = "two" });
        await WaitFor(() => host.Chats.Count == 2, "second chat");
        Assert.Equal(1, extraCalls);
    }

    [Fact]
    public async Task LargeMessages_Work_AndOversizedOnesAreRefusedBeforeSending()
    {
        Node host = StartHost();
        Node alice = await JoinAsync(host, "Alice");
        await WaitFor(() => host.Connected.Count == 1, "host to see Alice");

        string big = new string('x', 10_000);
        alice.Net.Send(new ChatMessage { Text = big });
        await WaitFor(() => host.Chats.Count == 1, "the big chat");
        Assert.Equal(big, host.Chats[0].Text);

        Assert.Throws<ArgumentException>(() => alice.Net.Send(new ChatMessage { Text = new string('x', 20_000) }));
        Assert.True(alice.Net.Connected);   // the failed send did not hurt the connection
    }

    // ------------------------------------------------------------ Leaving

    [Fact]
    public async Task HostStopping_TellsTheClientWhy()
    {
        Node host = StartHost();
        Node alice = await JoinAsync(host, "Alice");
        await WaitFor(() => host.Connected.Count == 1, "host to see Alice");

        host.Net.Stop();
        await WaitFor(() => alice.Disconnected.Count == 1, "Alice to notice");

        Assert.Equal((NetworkManager.HostPeerId, NetDisconnectReason.Shutdown), alice.Disconnected[0]);
        Assert.Equal(NetRole.None, alice.Net.Role);
        Assert.False(alice.Net.Connected);
        Assert.Equal(0, host.Disconnected.Count);   // Stop() raises no events for what you closed yourself
    }

    [Fact]
    public async Task ClientLeaving_IsReportedToTheHost_AndFreesTheSlot()
    {
        Node host = StartHost(n => n.MaxPeers = 1);
        Node alice = await JoinAsync(host, "Alice");
        await WaitFor(() => host.Connected.Count == 1, "host to see Alice");

        alice.Net.Stop();
        await WaitFor(() => host.Disconnected.Count == 1, "host to notice");

        Assert.Equal((1, NetDisconnectReason.Shutdown), host.Disconnected[0]);
        Assert.Equal(0, host.Net.PeerCount);

        // The slot is free again, and the new player gets a fresh id (ids are never reused).
        Node bob = await JoinAsync(host, "Bob");
        Assert.Equal(2, bob.Net.LocalPeerId);
    }

    [Fact]
    public async Task Kick_RemovesAClient_AndTellsIt()
    {
        Node host = StartHost();
        Node alice = await JoinAsync(host, "Alice");
        Node bob = await JoinAsync(host, "Bob");
        await WaitFor(() => host.Connected.Count == 2, "host to see both");

        int aliceId = alice.Net.LocalPeerId;
        host.Net.Kick(aliceId);
        await WaitFor(() => alice.Disconnected.Count == 1 && host.Disconnected.Count == 1, "the kick to land");

        Assert.Equal((NetworkManager.HostPeerId, NetDisconnectReason.Kicked), alice.Disconnected[0]);
        Assert.Equal((aliceId, NetDisconnectReason.Kicked), host.Disconnected[0]);
        Assert.Equal(1, host.Net.PeerCount);
        Assert.True(bob.Net.Connected);
    }

    [Fact]
    public async Task HostCanStartAgain_AfterStopping()
    {
        Node host = StartHost();
        await JoinAsync(host, "Alice");
        host.Net.Stop();
        Assert.Equal(NetRole.None, host.Net.Role);
        Assert.Equal("Offline", host.Net.Status);

        Assert.True(host.Net.Host(0));
        Node bob = await JoinAsync(host, "Bob");
        Assert.Equal(1, bob.Net.LocalPeerId);   // a fresh session starts counting again
    }

    [Fact]
    public async Task EventsQueuedBeforeStop_AreNeverDelivered()
    {
        Node host = StartHost();
        Node alice = Create("Alice");
        alice.Net.Connect("127.0.0.1", host.Net.LocalPort);

        // Wait for the connection WITHOUT calling Update, so everything piles up in the queue.
        var timer = Stopwatch.StartNew();
        while (host.Net.PeerCount != 1 || alice.Net.PeerCount != 1)
        {
            if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException("Timed out waiting for the connection.");
            await Task.Delay(5);
        }

        alice.Net.Send(new ChatMessage { Text = "too late" });
        await Task.Delay(200);   // let it arrive and sit in the host's queue

        host.Net.Stop();
        host.Net.Update();       // must not deliver the join or the chat from the stopped session

        Assert.Equal(0, host.Connected.Count);
        Assert.Equal(0, host.Chats.Count);
    }

    // ------------------------------------------------------------ Misbehaving peers (raw sockets)

    private static byte[] Frame(byte type, byte[] payload)
    {
        var frame = new byte[3 + payload.Length];
        frame[0] = (byte)(payload.Length & 0xFF);
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = type;
        Array.Copy(payload, 0, frame, 3, payload.Length);
        return frame;
    }

    private static async Task<(byte Type, byte[] Payload)> ReadRawFrameAsync(NetworkStream stream)
    {
        var header = new byte[3];
        await stream.ReadExactlyAsync(header, 0, 3);
        var payload = new byte[header[0] | (header[1] << 8)];
        await stream.ReadExactlyAsync(payload, 0, payload.Length);
        return (header[2], payload);
    }

    /// <summary>Connects without NetworkManager and completes a correct handshake.</summary>
    private static async Task<(TcpClient Tcp, NetworkStream Stream)> RawJoinAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = tcp.GetStream();

        var hello = new NetWriter();
        hello.Write((ushort)1);       // protocol version
        hello.Write(TestGameId);
        hello.Write("raw");
        await stream.WriteAsync(Frame(0, hello.ToArray()));

        (byte type, _) = await ReadRawFrameAsync(stream);
        Assert.Equal((byte)1, type);  // Welcome
        return (tcp, stream);
    }

    [Fact]
    public async Task SilentPeer_IsDroppedAfterTheIdleTimeout()
    {
        Node host = StartHost(n => n.IdleTimeout = TimeSpan.FromMilliseconds(600));

        (TcpClient tcp, NetworkStream stream) = await RawJoinAsync(host.Net.LocalPort);
        using (tcp)
        {
            await WaitFor(() => host.Connected.Count == 1, "host to see the raw client");
            await WaitFor(() => host.Disconnected.Count == 1, "the idle timeout");

            Assert.Equal((1, NetDisconnectReason.Timeout), host.Disconnected[0]);
        }
    }

    [Fact]
    public async Task OversizedFrame_DropsThePeerAsAProtocolError()
    {
        Node host = StartHost();

        (TcpClient tcp, NetworkStream stream) = await RawJoinAsync(host.Net.LocalPort);
        using (tcp)
        {
            await WaitFor(() => host.Connected.Count == 1, "host to see the raw client");

            // Header claims 65535 payload bytes, far over the 16 KB limit.
            await stream.WriteAsync(new byte[] { 0xFF, 0xFF, 20 });
            await WaitFor(() => host.Disconnected.Count == 1, "the protocol error");

            Assert.Equal((1, NetDisconnectReason.ProtocolError), host.Disconnected[0]);
        }
    }

    [Fact]
    public async Task UnknownMessageId_IsIgnored_AndTheConnectionSurvives()
    {
        Node host = StartHost();

        (TcpClient tcp, NetworkStream stream) = await RawJoinAsync(host.Net.LocalPort);
        using (tcp)
        {
            await WaitFor(() => host.Connected.Count == 1, "host to see the raw client");

            await stream.WriteAsync(Frame(200, new byte[] { 1, 2, 3 }));   // nobody registered 200

            var chat = new NetWriter();
            chat.Write("still here");
            await stream.WriteAsync(Frame(16, chat.ToArray()));

            await WaitFor(() => host.Chats.Count == 1, "the chat after the unknown message");
            Assert.Equal((1, "still here"), host.Chats[0]);
            Assert.Equal(0, host.Disconnected.Count);
        }
    }

    [Fact]
    public async Task MalformedPayload_DropsThePeer_AndTellsItWhy()
    {
        Node host = StartHost();

        (TcpClient tcp, NetworkStream stream) = await RawJoinAsync(host.Net.LocalPort);
        using (tcp)
        {
            await WaitFor(() => host.Connected.Count == 1, "host to see the raw client");

            // NumberMessage needs 4 bytes; send 1.
            await stream.WriteAsync(Frame(17, new byte[] { 9 }));
            await WaitFor(() => host.Disconnected.Count == 1, "the malformed message to be rejected");

            Assert.Equal((1, NetDisconnectReason.ProtocolError), host.Disconnected[0]);
            Assert.Equal(0, host.Numbers.Count);

            // The host said goodbye with the reason before closing.
            byte type;
            byte[] payload;
            do { (type, payload) = await ReadRawFrameAsync(stream); } while (type == 3);   // skip pings
            Assert.Equal((byte)4, type);
            Assert.Equal((byte)NetDisconnectReason.ProtocolError, payload[0]);
        }
    }

    [Fact]
    public async Task HalfFinishedHandshake_DoesNotBlockOrCountAgainstTheLimit()
    {
        Node host = StartHost(n =>
        {
            n.MaxPeers = 1;
            n.HandshakeTimeout = TimeSpan.FromMilliseconds(300);
        });

        // Connect and say nothing at all.
        using var lurker = new TcpClient();
        await lurker.ConnectAsync(IPAddress.Loopback, host.Net.LocalPort);

        // A real client can still join the single slot.
        Node alice = await JoinAsync(host, "Alice");
        await WaitFor(() => host.Connected.Count == 1, "host to see Alice");
        Assert.Equal(1, host.Net.PeerCount);
    }
}
