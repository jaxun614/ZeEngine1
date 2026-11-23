using System;
using System.IO;
using System.Text.Json;

namespace ZeEngine;

public class OptionsService
{
    private static readonly Lazy<OptionsService> _instance = new(() => new OptionsService()); // 
    public static OptionsService Instance => _instance.Value;

    private static readonly string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YourGame", "options.json");

    public GameOptions Current { get; private set; }
    public event Action<GameOptions> OptionsChanged;

    private OptionsService() => Current = Load();

    private GameOptions Load()
    {
        try
        {
            if (File.Exists(SavePath))
                return JsonSerializer.Deserialize<GameOptions>(File.ReadAllText(SavePath)) ?? new GameOptions();
        }
        catch { /* corrupt file, fall back to defaults */ }
        return new GameOptions();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
        File.WriteAllText(SavePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Called by the options screen when a value changes / on confirm
    public void Apply(GameOptions updated)
    {
        Current = updated;
        OptionsChanged?.Invoke(Current);
        Save(); // persist immediately, don't wait for game exit
    }
}