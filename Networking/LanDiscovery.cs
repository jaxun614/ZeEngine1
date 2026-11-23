using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace ZeEngine.Networking;

/// <summary>A game found on the local network.</summary>
public sealed class ServerInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string WorldName { get; set; }
    public string Address { get; set; }
    public int Port { get; set; }
    public int Players { get; set; }
    public int MaxPlayers { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public bool IsFull => Players >= MaxPlayers;
}

/// <summary>
/// LAN server discovery over UDP broadcast.
///  - A host calls StartAnnouncing(): it shouts "I'm here" once a second.
///  - The server browser calls StartListening(): it collects those shouts.
///    GetServers() returns hosts heard from in the last few seconds.
/// Beacon packet: magic, server id, name, world, TCP port, players, max players.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    public const int DiscoveryPort = 7778;

    private const string Magic = "ZEG1";
    private const int MaxTextLength = 32;
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, ServerInfo> _servers = new();
    private readonly object _lock = new();

    private CancellationTokenSource _announceCts;
    private UdpClient _listenClient;

    private long _packetsSent;
    private long _packetsReceived;

    public bool IsListening { get { lock (_lock) return _listenClient != null; } }
    public bool IsAnnouncing { get { lock (_lock) return _announceCts != null; } }
    public long PacketsSent => Interlocked.Read(ref _packetsSent);
    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    /// <summary>Most recent send/receive problem, for diagnostics.</summary>
    public string LastError { get; private set; }

    /// <summary>One-line status for debugging: is it listening/announcing and how many packets moved.</summary>
    public string Describe() =>
        $"listen={(IsListening ? "on" : "off")} heard={PacketsReceived} | announce={(IsAnnouncing ? "on" : "off")} sent={PacketsSent}"
        + (LastError != null ? $" | {LastError}" : "");

    /// <summary>Set if the listener could not start (for example the port is blocked).</summary>
    public string ListenError { get; private set; }

    // ------------------------------------------------------------ Hosting side
    public void StartAnnouncing(string name, string worldName, int tcpPort, int maxPlayers, Func<int> getPlayers)
    {
        StopAnnouncing();

        var cts = new CancellationTokenSource();
        lock (_lock) _announceCts = cts;

        string id = Guid.NewGuid().ToString("N");
        var thread = new Thread(() => AnnounceLoop(id, name, worldName, tcpPort, maxPlayers, getPlayers, cts.Token))
        {
            IsBackground = true
        };
        thread.Start();
    }

    public void StopAnnouncing()
    {
        lock (_lock)
        {
            _announceCts?.Cancel();
            _announceCts = null;
        }
    }

    private void AnnounceLoop(string id, string name, string worldName, int tcpPort, int maxPlayers,
                                     Func<int> getPlayers, CancellationToken token)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };

            while (!token.IsCancellationRequested)
            {
                byte[] packet = BuildPacket(id, name, worldName, tcpPort, getPlayers(), maxPlayers);

                foreach (IPAddress target in GetBroadcastTargets())
                {
                    try
                    {
                        udp.Send(packet, packet.Length, new IPEndPoint(target, DiscoveryPort));
                        Interlocked.Increment(ref _packetsSent);
                    }
                    catch (Exception ex)
                    {
                        // That network may be unreachable right now; keep trying the others.
                        LastError = $"send to {target}: {ex.Message}";
                    }
                }

                token.WaitHandle.WaitOne(AnnounceInterval);
            }
        }
        catch (Exception ex)
        {
            LastError = $"announce stopped: {ex.Message}";
            Debug.WriteLine($"LAN announce stopped: {ex.Message}");
        }
    }

    private static byte[] BuildPacket(string id, string name, string worldName, int tcpPort, int players, int maxPlayers)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Magic);
        writer.Write(id);
        writer.Write(Truncate(name));
        writer.Write(Truncate(worldName));
        writer.Write(tcpPort);
        writer.Write((byte)Math.Clamp(players, 0, 255));
        writer.Write((byte)Math.Clamp(maxPlayers, 0, 255));
        writer.Flush();
        return stream.ToArray();
    }

    private static string Truncate(string text)
    {
        text ??= string.Empty;
        return text.Length <= MaxTextLength ? text : text.Substring(0, MaxTextLength);
    }

    /// <summary>
    /// Where to send the beacon: the global broadcast address, loopback (so two
    /// instances on one PC can see each other), and each network card's own
    /// subnet broadcast address (more reliable on PCs with several adapters).
    /// </summary>
    private static List<IPAddress> GetBroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork || info.IPv4Mask == null) continue;

                    byte[] ip = info.Address.GetAddressBytes();
                    byte[] mask = info.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++)
                        broadcast[i] = (byte)(ip[i] | ~mask[i]);

                    targets.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not enumerate network adapters: {ex.Message}");
        }

        return targets.Distinct().ToList();
    }

    // ------------------------------------------------------------ Browsing side
    public void StartListening()
    {
        lock (_lock)
        {
            if (_listenClient != null) return;

            try
            {
                var udp = new UdpClient(AddressFamily.InterNetwork);
                // Lets several game instances on one PC all listen on the same port.
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

                _listenClient = udp;
                ListenError = null;

                new Thread(() => ListenLoop(udp)) { IsBackground = true }.Start();
            }
            catch (Exception ex)
            {
                ListenError = $"LAN search unavailable: {ex.Message}";
                LastError = ListenError;
                Debug.WriteLine(ListenError);
            }
        }
    }

    public void StopListening()
    {
        lock (_lock)
        {
            try { _listenClient?.Dispose(); } catch { }
            _listenClient = null;
        }

        _servers.Clear();
    }

    private void ListenLoop(UdpClient udp)
    {
        try
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                byte[] data = udp.Receive(ref remote);
                HandlePacket(data, remote.Address);
            }
        }
        catch (ObjectDisposedException) { }   // StopListening() closed the socket
        catch (SocketException) { }           // same, on some platforms
        catch (Exception ex)
        {
            LastError = $"listener stopped: {ex.Message}";
            Debug.WriteLine($"LAN listener stopped: {ex.Message}");
        }
    }

    private void HandlePacket(byte[] data, IPAddress sender)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(data));

            if (reader.ReadString() != Magic) return;

            var info = new ServerInfo
            {
                Id = reader.ReadString(),
                Name = Truncate(reader.ReadString()),
                WorldName = Truncate(reader.ReadString()),
                Port = reader.ReadInt32(),
                Players = reader.ReadByte(),
                MaxPlayers = reader.ReadByte(),
                Address = sender.ToString(),
                LastSeenUtc = DateTime.UtcNow
            };

            if (info.Port < 1 || info.Port > 65535) return;

            Interlocked.Increment(ref _packetsReceived);

            // A host with several network cards can reach us via more than one address.
            // Keep the first one we saw so the entry doesn't flip between addresses.
            _servers.AddOrUpdate(info.Id, info, (_, old) =>
            {
                if (DateTime.UtcNow - old.LastSeenUtc < ServerTimeout)
                    info.Address = old.Address;
                return info;
            });
        }
        catch
        {
            // Not one of our packets, or malformed. Ignore.
        }
    }

    /// <summary>Servers heard from recently, sorted by name.</summary>
    public List<ServerInfo> GetServers()
    {
        DateTime now = DateTime.UtcNow;

        foreach (var pair in _servers)
        {
            if (now - pair.Value.LastSeenUtc > ServerTimeout)
                _servers.TryRemove(pair.Key, out _);
        }

        return _servers.Values.OrderBy(s => s.Name).ThenBy(s => s.WorldName).ToList();
    }

    public void Dispose()
    {
        StopAnnouncing();
        StopListening();
    }
}
