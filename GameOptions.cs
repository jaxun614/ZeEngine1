using MonoGame.Extended;
namespace ZeEngine;

public enum WindowMode { Windowed, Fullscreen, Borderless }
public enum Difficulty { Easy, Normal, Hard }
public enum PlayerMode { God, Softcore, Mediumcore, Hardcore }

public class GameOptions
{
    // Video
    public int ResolutionWidth { get; set; } = 1280;
    public int ResolutionHeight { get; set; } = 720;
    public WindowMode WindowMode { get; set; } = WindowMode.Windowed;
    public bool VSync { get; set; } = true;
    public int FrameRateCap { get; set; } = 60; // 0 = uncapped
    public float Gamma { get; set; } = 1f;

    // Audio
    public float MasterVolume { get; set; } = 1f;
    public float MusicVolume { get; set; } = 1f;
    public float SfxVolume { get; set; } = 1f;
    public bool MuteOnFocusLost { get; set; } = true;

    // Controls
    public float MouseSensitivity { get; set; } = 1f;
    public bool InvertY { get; set; } = false;
    public bool ControllerVibration { get; set; } = true;

    // ------ Gameplay ------
    // world
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;
    public float PlayRate {get; set; } = 1f;

    // player
    public bool AutoPickup { get; set; } = true;
    public bool AutoJump { get; set; } = false;

    // visual
    public bool ScreenShake { get; set; } = true;
    public float ScreenShakeIntensity { get; set; } = 1f;
    public bool ShowDamageNumbers { get; set; } = true;
    public bool ShowHitBoxes { get; set; } = false; 
    public bool ShowEntityShadow { get; set; } = true;

    // accessability
    public bool Subtitles { get; set; } = false;

    // multiplayer
    public bool Connected { get; set; } = false;
    public bool SplitScreen { get; set; } = false;


    public GameOptions Clone() => (GameOptions)MemberwiseClone();
}