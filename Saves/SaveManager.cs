using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ZeEngine.Saves;

/// <summary>
/// Reads and writes player and world files. Players and worlds are completely
/// separate files in separate folders:
///   &lt;AppData&gt;/&lt;GameName&gt;/Players/&lt;id&gt;.plr
///   &lt;AppData&gt;/&lt;GameName&gt;/Worlds/&lt;id&gt;.wld
/// Files are JSON for now, which is easy to debug. Files are named by a GUID,
/// so two saves can share a name and renaming never touches the file.
/// </summary>
public static class SaveManager
{
    public const string PlayerExtension = ".plr";
    public const string WorldExtension = ".wld";
    public const int MaxNameLength = 20;

    /// <summary>Folder name under the user's app-data directory. Set this once at startup.</summary>
    public static string GameName { get; set; } = "ZeEngine";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), GameName);
    public static string PlayersDirectory => Path.Combine(RootDirectory, "Players");
    public static string WorldsDirectory => Path.Combine(RootDirectory, "Worlds");

    // ------------------------------------------------------------ Players
    public static List<PlayerData> LoadPlayers() =>
        LoadAll<PlayerData>(PlayersDirectory, PlayerExtension).OrderByDescending(p => p.LastPlayedUtc).ToList();

    public static void SavePlayer(PlayerData player) => Save(PlayersDirectory, player, PlayerExtension);

    public static void DeletePlayer(string id) => Delete(PlayersDirectory, id, PlayerExtension);

    public static PlayerData CreatePlayer(string name)
    {
        var player = new PlayerData { Name = name };
        SavePlayer(player);
        return player;
    }

    // ------------------------------------------------------------ Worlds
    public static List<WorldData> LoadWorlds() =>
        LoadAll<WorldData>(WorldsDirectory, WorldExtension).OrderByDescending(w => w.LastPlayedUtc).ToList();

    public static void SaveWorld(WorldData world) => Save(WorldsDirectory, world, WorldExtension);

    public static void DeleteWorld(string id) => Delete(WorldsDirectory, id, WorldExtension);

    public static WorldData CreateWorld(string name, int seed)
    {
        var world = new WorldData { Name = name, Seed = seed };
        SaveWorld(world);
        return world;
    }

    // ------------------------------------------------------------ Saved (remote) servers
    public static string ServersFile => Path.Combine(RootDirectory, "servers.json");

    public static List<SavedServer> LoadServers()
    {
        try
        {
            if (!File.Exists(ServersFile)) return new List<SavedServer>();
            return JsonSerializer.Deserialize<List<SavedServer>>(File.ReadAllText(ServersFile), JsonOptions)
                   ?? new List<SavedServer>();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not read servers file: {ex.Message}");
            return new List<SavedServer>();
        }
    }

    public static void SaveServers(List<SavedServer> servers)
    {
        Directory.CreateDirectory(RootDirectory);

        string temp = ServersFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(servers, JsonOptions));
        File.Move(temp, ServersFile, overwrite: true);
    }

    // ------------------------------------------------------------ Helpers
    /// <summary>Trims the name and checks it. Returns false and sets error if it's not usable.</summary>
    public static bool TryValidateName(string input, out string cleaned, out string error)
    {
        cleaned = (input ?? string.Empty).Trim();
        error = null;

        if (cleaned.Length == 0) { error = "Please enter a name."; return false; }
        if (cleaned.Length > MaxNameLength) { error = $"Name can be at most {MaxNameLength} characters."; return false; }
        return true;
    }

    /// <summary>Blank = random seed, a number = that seed, any other text = a stable hash of the text.</summary>
    public static int ParseSeed(string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return Random.Shared.Next();
        if (int.TryParse(text, out int number)) return number;

        unchecked
        {
            uint hash = 2166136261;           // FNV-1a
            foreach (char c in text) { hash ^= c; hash *= 16777619; }
            return (int)hash;
        }
    }

    private static List<T> LoadAll<T>(string directory, string extension) where T : class, ISaveData
    {
        var results = new List<T>();
        if (!Directory.Exists(directory)) return results;

        foreach (string file in Directory.EnumerateFiles(directory, "*" + extension))
        {
            // Windows patterns like *.plr can also match longer extensions, so double-check.
            if (!string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var data = JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonOptions);
                if (data == null) continue;

                data.Id = Path.GetFileNameWithoutExtension(file); // the file name is the source of truth
                results.Add(data);
            }
            catch (Exception ex)
            {
                // A corrupt file shouldn't stop the rest of the list from loading.
                Debug.WriteLine($"Skipping unreadable save '{file}': {ex.Message}");
            }
        }

        return results;
    }

    private static void Save<T>(string directory, T data, string extension) where T : class, ISaveData
    {
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, Path.GetFileName(data.Id) + extension);
        string temp = path + ".tmp";

        // Write to a temp file first so a crash mid-save can't destroy the old save.
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static void Delete(string directory, string id, string extension)
    {
        string path = Path.Combine(directory, Path.GetFileName(id) + extension);
        if (File.Exists(path)) File.Delete(path);
    }
}
