#region Includes
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.Collections.Generic;
using ImGuiNET;
using ImGuiNET.SampleProgram.XNA;
using ZeEngine.Content;
using ZeEngine.Graphics;
using ZeEngine.Scenes;
using ZeEngine.Input;
using ZeEngine.Audio;
using ZeEngine.Networking;
#endregion

namespace ZeEngine;

public class Core : Game
{
    /// <summary>  
    /// The material that is used when changing scenes  
    /// </summary>  
    public static Material SceneTransitionMaterial { get; private set; }

    /// <summary>  
    /// Gets a runtime generated 1x1 pixel texture.  
    /// </summary>  
    public static Texture2D Pixel { get; private set; }

    internal static Core s_instance;

    /// <summary>
    /// Gets a reference to the Core instance.
    /// </summary>
    public static Core Instance => s_instance;

    // The scene that is currently active.
    private static Scene s_activeScene;

    // The next scene to switch to, if there is one.
    private static Scene s_nextScene;

    /// <summary>
    /// Gets the graphics device manager to control the presentation of graphics.
    /// </summary>
    public static GraphicsDeviceManager Graphics { get; private set; }

    /// <summary>
    /// Gets the graphics device used to create graphical resources and perform primitive rendering.
    /// </summary>
    public static new GraphicsDevice GraphicsDevice { get; private set; }

    /// <summary>
    /// Gets the sprite batch used for all 2D rendering.
    /// </summary>
    public static SpriteBatch SpriteBatch { get; private set; }

    /// <summary>
    /// Gets the content manager used to load global assets.
    /// </summary>
    public static new ContentManager Content { get; private set; }

    /// <summary>  
    /// Gets the content manager that can load global assets from the SharedContent folder.  
    /// </summary>  
    public static ContentManager SharedContent { get; private set; }


    /// <summary>
    /// Gets a reference to the input management system.
    /// </summary>
    public static InputManager Input { get; private set; }

    /// <summary>
    /// Gets or Sets a value that indicates if the game should exit when the esc key on the keyboard is pressed.
    /// </summary>
    public static bool ExitOnEscape { get; set; }

    /// <summary>
    /// Gets a reference to the audio control system.
    /// </summary>
    public static AudioController Audio { get; private set; }

    /// <summary>  
    /// Gets the ImGui renderer used for debug UIs.  
    /// </summary>  
    public static ImGuiRenderer ImGuiRenderer { get; private set; }

    /// <summary>
    /// Gets whether the ImGui debug UI can run on this platform. ImGui.NET only ships native
    /// libraries for desktop operating systems, so it is unavailable on Android and iOS.
    /// When false, <see cref="ImGuiRenderer"/> is null.
    /// </summary>
    public static bool IsImGuiSupported => !IsMobile;

    private static bool IsMobile => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    /// <summary>  
    /// A set of grayscale gradient textures to use as transition guides  
    /// </summary>  
    public static List<Texture2D> SceneTransitionTextures { get; private set; }

    /// <summary>  
    /// The current transition between scenes  
    /// </summary>  
    public static SceneTransition SceneTransition { get; protected set; } = SceneTransition.Open(1000);

    /// <summary>  
    /// The material that draws point lights  
    /// </summary>  
    public static Material PointLightMaterial { get; private set; }

    /// <summary>  
    /// The material that combines the various off screen textures  
    /// </summary>  
    public static Material DeferredCompositeMaterial { get; private set; }

    /// <summary>  
    /// The  material that draws shadow hulls  
    /// </summary>  
    public static Material ShadowHullMaterial { get; private set; }

    public static NetworkManager Network { get; private set; }
    public static LanDiscovery Discovery { get; private set; }

    // resize event to notify subscribers when the window is resized
    public static event Action<int, int> Resized;
    private bool _resizing;
    public static NineSlice PanelSlice { get; private set; }

    
    

    /// <summary>
    /// Creates a new Core instance.
    /// </summary>
    /// <param name="title">The title to display in the title bar of the game window.</param>
    /// <param name="width">The initial width, in pixels, of the game window.</param>
    /// <param name="height">The initial height, in pixels, of the game window.</param>
    /// <param name="fullScreen">Indicates if the game should start in fullscreen mode.</param>
    public Core(string title, int width, int height, bool fullScreen)
    {
        // Ensure that multiple cores are not created.
        if (s_instance != null)
        {
            throw new InvalidOperationException($"Only a single Core instance can be created");
        }

        // Store reference to engine for global member access.
        s_instance = this;

        // Create a new graphics device manager.
        Graphics = new GraphicsDeviceManager(this);

        // Set the graphics defaults.
        Graphics.PreferredBackBufferWidth = width;
        Graphics.PreferredBackBufferHeight = height;
        Graphics.IsFullScreen = fullScreen;

        // Apply the graphic presentation changes.
        Graphics.ApplyChanges();

        // Set the window title.
        Window.Title = title;

        // Set the core's content manager to a reference of the base Game's
        // content manager.
        Content = base.Content;

        // Set the root directory for content.
        Content.RootDirectory = "Content";

        // Set the core's shared content manager, pointing to the SharedContent folder.
        SharedContent = new ContentManager(Services, "SharedContent");

        // Mouse is visible by default.
        IsMouseVisible = true;

        ExitOnEscape = true;

        // Resizable windows only exist on desktop.
        if (!IsMobile)
        {
            Window.AllowUserResizing = true;
            Window.ClientSizeChanged += OnClientSizeChanged;
        }
    }

    protected override void Initialize()
    {
        base.Initialize();

        Input = new InputManager();
        Audio = new AudioController();
        Network = new NetworkManager();
        // in Initialize(), after Network = new NetworkManager();
        Discovery = new LanDiscovery();

    }

    protected override void LoadContent()  
    {  

        // Set the core's graphics device to a reference of the base Game's
        // graphics device.
        GraphicsDevice = base.GraphicsDevice;

        // in LoadContent(), after GraphicsDevice is assigned
        PanelSlice = NineSlice.CreatePanel(GraphicsDevice); 

        // Create the sprite batch instance.
        SpriteBatch = new SpriteBatch(GraphicsDevice);

        // Create the ImGui renderer (desktop only, see IsImGuiSupported).
        if (IsImGuiSupported)
        {
            ImGuiRenderer = new ImGuiRenderer(this);
            ImGuiRenderer.RebuildFontAtlas();
        }

        Pixel = new Texture2D(GraphicsDevice, 1, 1);
        Pixel.SetData(new Color[]{ Color.White });

          

        SceneTransitionMaterial = SharedContent.WatchMaterial("effects/sceneTransitionEffect");  
        SceneTransitionMaterial.SetParameter("EdgeWidth", .05f);
        SceneTransitionMaterial.IsDebugVisible = true;

        SceneTransitionTextures = new List<Texture2D>();
        SceneTransitionTextures.Add(SharedContent.Load<Texture2D>("images/angled"));
        SceneTransitionTextures.Add(SharedContent.Load<Texture2D>("images/concave"));
        SceneTransitionTextures.Add(SharedContent.Load<Texture2D>("images/radial"));
        SceneTransitionTextures.Add(SharedContent.Load<Texture2D>("images/ripple"));

        PointLightMaterial = SharedContent.WatchMaterial("effects/pointLightEffect");
        PointLightMaterial.IsDebugVisible = true;
        PointLightMaterial.SetParameter("LightBrightness", .25f);
        PointLightMaterial.SetParameter("LightSharpness", .1f);

        DeferredCompositeMaterial = SharedContent.WatchMaterial("effects/deferredCompositeEffect");
        DeferredCompositeMaterial.IsDebugVisible = true;

        ShadowHullMaterial = SharedContent.WatchMaterial("effects/shadowHullEffect");
        ShadowHullMaterial.IsDebugVisible = true;

        base.LoadContent();
    }


    protected override void UnloadContent()
    {
        // Dispose of the audio controller.
        Audio.Dispose();
        Network?.Dispose();
        // in UnloadContent()
        Discovery?.Dispose();
        
        base.UnloadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        // Update the input manager.
        Input.Update(gameTime);

        // Deliver queued network events and messages, on the game thread.
        Network.Update();

        // Update the audio controller.
        Audio.Update();

        // if (ExitOnEscape && Input.Keyboard.WasKeyJustPressed(Keys.Escape))
        // {
        //     Exit();
        // }
        // if there is a next scene waiting to be switch to, then transition
        // to that scene.
        if (s_nextScene != null && SceneTransition.IsComplete)
        {
            TransitionScene();
        }

        // If there is an active scene, update it.
        if (s_activeScene != null)
        {
            s_activeScene.Update(gameTime);
        }

        SceneTransitionMaterial.SetParameter("Progress", SceneTransition.DirectionalRatio);
        SceneTransitionMaterial.Update();
        PointLightMaterial.Update();

        DeferredCompositeMaterial.SetParameter("ScreenSize", new Vector2(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height));
        DeferredCompositeMaterial.Update();

        ShadowHullMaterial.SetParameter("ShadowFadeStartDistance", .013f);  
        ShadowHullMaterial.SetParameter("ShadowFadeEndDistance", .13f);
        ShadowHullMaterial.SetParameter("ShadowIntensity", .85f);
        ShadowHullMaterial.Update();


        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        // If there is an active scene, draw it.
        if (s_activeScene != null)
        {
            s_activeScene.Draw(gameTime);
        }

        SpriteBatch.Begin(effect: SceneTransitionMaterial.Effect);  
        SpriteBatch.Draw(SceneTransitionTextures[SceneTransition.TextureIndex % SceneTransitionTextures.Count], GraphicsDevice.Viewport.Bounds, Color.White);
        SpriteBatch.End();
        
        // Draw debug UI
        Material.DrawVisibleDebugUi(gameTime);

        base.Draw(gameTime);
    }

    public static void ChangeScene(Scene next)
    {
        // Only set the next scene value if it is not the same
        // instance as the currently active scene.
        if (s_activeScene != next)
        {
            s_nextScene = next;
            SceneTransition = SceneTransition.Close(250);
        }
    }

    public static int MinWidth { get; set; } = 540;   // 2x the 270x180 design space
    public static int MinHeight { get; set; } = 360;

    private void OnClientSizeChanged(object sender, EventArgs e)
    {
        if (_resizing) return;

        int w = Window.ClientBounds.Width;
        int h = Window.ClientBounds.Height;
        if (w == 0 || h == 0) return; // minimized

        w = Math.Max(w, MinWidth);    // MonoGame has no minimum window size, so enforce one
        h = Math.Max(h, MinHeight);

        _resizing = true;
        try
        {
            if (Graphics.PreferredBackBufferWidth != w || Graphics.PreferredBackBufferHeight != h)
            {
                Graphics.PreferredBackBufferWidth = w;
                Graphics.PreferredBackBufferHeight = h;
                Graphics.ApplyChanges();
            }
        }
        finally { _resizing = false; }   // reset even if ApplyChanges throws

        Resized?.Invoke(w, h);
    }

    private static void TransitionScene()
    {
        SceneTransition = SceneTransition.Open(500);

        // If there is an active scene, dispose of it.
        if (s_activeScene != null)
        {
            s_activeScene.Dispose();
        }

        // Force the garbage collector to collect to ensure memory is cleared.
        GC.Collect();

        // Change the currently active scene to the new scene.
        s_activeScene = s_nextScene;

        // Null out the next scene value so it does not trigger a change over and over.
        s_nextScene = null;


        // If the active scene now is not null, initialize it.
        // Remember, just like with Game, the Initialize call also calls the
        // Scene.LoadContent
        if (s_activeScene != null)
        {
            s_activeScene.Initialize();
        }
    }
}
