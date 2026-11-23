using System;

namespace ZeEngine.Saves;

/// <summary>Anything stored as its own file on disk. Id is also the file name.</summary>
public interface ISaveData
{
    string Id { get; set; }
}

/// <summary>
/// Everything that belongs to the character and travels between worlds
/// (like a Terraria .plr). Add inventory, stats, appearance, etc. here.
/// </summary>
public sealed class PlayerData : ISaveData
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Player";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastPlayedUtc { get; set; } = DateTime.UtcNow;

    public int MaxHealth { get; set; } = 100;

    public override string ToString() => $"{Name}   -   {LastPlayedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
}

/// <summary>
/// Everything that belongs to the world and is shared by whoever plays in it
/// (like a Terraria .wld). Add tiles, chests, NPCs, etc. here (or split large
/// data into a binary file next to this one later).
/// </summary>
public sealed class WorldData : ISaveData
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "World";
    public int Seed { get; set; }
    public int Width { get; set; } = 400;
    public int Height { get; set; } = 200;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastPlayedUtc { get; set; } = DateTime.UtcNow;

    public override string ToString() => $"{Name}   -   {LastPlayedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
}

/// <summary>A remote server the player added by hand (stored in servers.json).</summary>
public sealed class SavedServer
{
    public string Name { get; set; } = "Server";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 7777;
}
