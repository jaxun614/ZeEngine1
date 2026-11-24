using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Xunit;
using ZeEngine.Networking;

namespace ZeEngine.Tests;

// These use the real discovery port (7778) on localhost. Close any running copy of the game first.
public sealed class LanDiscoveryTests : IDisposable
{
    private readonly List<LanDiscovery> _all = new();

    private LanDiscovery Create()
    {
        var discovery = new LanDiscovery();
        _all.Add(discovery);
        return discovery;
    }

    public void Dispose()
    {
        foreach (LanDiscovery discovery in _all) discovery.Dispose();
    }

    private static string UniqueName() => "test-" + Guid.NewGuid().ToString("N").Substring(0, 8);

    private static async Task<ServerInfo> WaitForServer(LanDiscovery listener, string name, int timeoutMs = 6000)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMs)
        {
            ServerInfo found = listener.GetServers().FirstOrDefault(s => s.Name == name);
            if (found != null) return found;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Never heard a server called {name}. {listener.Describe()}");
    }

    [Fact]
    public async Task Listener_HearsAnAnnouncer()
    {
        string name = UniqueName();
        LanDiscovery announcer = Create(), listener = Create();

        listener.StartListening();
        announcer.StartAnnouncing(name, "World", 4242, 4, () => 2);

        ServerInfo server = await WaitForServer(listener, name);
        Assert.Equal("World", server.WorldName);
        Assert.Equal(4242, server.Port);
        Assert.Equal(2, server.Players);
        Assert.Equal(4, server.MaxPlayers);
        Assert.False(server.IsFull);
    }

    [Fact]
    public async Task StopListening_ReturnsAtOnce_AndReleasesTheSocket()
    {
        LanDiscovery listener = Create();
        listener.StartListening();
        await Task.Delay(300);   // let it settle into waiting for a packet, which is the dangerous moment

        var timer = Stopwatch.StartNew();
        listener.StopListening();
        timer.Stop();

        Assert.True(timer.ElapsedMilliseconds < 500, $"StopListening took {timer.ElapsedMilliseconds} ms");
        Assert.False(listener.IsListening);

        // The listener closes its own socket shortly after. Prove it by taking the port exclusively.
        bool released = false;
        var wait = Stopwatch.StartNew();
        while (!released && wait.ElapsedMilliseconds < 3000)
        {
            try
            {
                using var exclusive = new UdpClient(new IPEndPoint(IPAddress.Any, LanDiscovery.DiscoveryPort));
                released = true;
            }
            catch (SocketException)
            {
                await Task.Delay(50);
            }
        }

        Assert.True(released, "the discovery port was never released after StopListening");
    }

    [Fact]
    public async Task CanListenAgain_AfterStopping()
    {
        string name = UniqueName();
        LanDiscovery announcer = Create(), listener = Create();

        // This is what happens when a scene that browses for games is left and entered again.
        listener.StartListening();
        listener.StopListening();
        listener.StartListening();
        Assert.True(listener.IsListening);

        announcer.StartAnnouncing(name, "World", 1111, 8, () => 1);
        ServerInfo server = await WaitForServer(listener, name);
        Assert.Equal(1111, server.Port);
    }

    [Fact]
    public async Task RapidStartStop_NeverThrowsOrHangs_AndStillWorksAfterwards()
    {
        string name = UniqueName();
        LanDiscovery announcer = Create(), listener = Create();

        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            listener.StartListening();
            listener.StopListening();
        }

        Assert.True(timer.ElapsedMilliseconds < 3000, $"100 start/stop cycles took {timer.ElapsedMilliseconds} ms");
        Assert.False(listener.IsListening);

        listener.StartListening();
        announcer.StartAnnouncing(name, "World", 2222, 8, () => 3);
        ServerInfo server = await WaitForServer(listener, name);
        Assert.Equal(2222, server.Port);
    }

    [Fact]
    public async Task Dispose_WhileAnnouncingAndListening_IsSafe()
    {
        LanDiscovery announcer = Create(), listener = Create();
        listener.StartListening();
        announcer.StartAnnouncing(UniqueName(), "World", 3333, 8, () => 1);
        await Task.Delay(300);

        var timer = Stopwatch.StartNew();
        announcer.Dispose();
        listener.Dispose();

        Assert.True(timer.ElapsedMilliseconds < 500, $"Dispose took {timer.ElapsedMilliseconds} ms");
        Assert.False(announcer.IsAnnouncing);
        Assert.False(listener.IsListening);

        await Task.Delay(300);   // let the background loops wind down; nothing should throw
    }
}
