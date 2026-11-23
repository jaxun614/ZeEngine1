namespace ZeEngine.Saves;

/// <summary>
/// The player and world chosen in the menus. Gameplay screens read these instead
/// of needing them passed through constructors.
/// </summary>
public static class GameSession
{
    public static PlayerData Player { get; set; }
    public static WorldData World { get; set; }   // null when joining someone else's world

    /// <summary>Call when leaving gameplay to persist both files.</summary>
    public static void Save()
    {
        if (Player != null) SaveManager.SavePlayer(Player);
        if (World != null) SaveManager.SaveWorld(World);
    }

    public static void Clear()
    {
        Player = null;
        World = null;
    }
}
