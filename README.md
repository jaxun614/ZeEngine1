# ZeEngine

A small engine layer on top of [MonoGame](https://monogame.net). It is meant to
be consumed by a game project (see [MGLite](https://github.com/jaxun614/MGLite)).

## What's in it

- `Core` — a singleton `Game` subclass that owns the graphics device, sprite
  batch, content managers, input, audio and networking
- Scenes with animated transitions (`Scene`, `SceneTransition`)
- Hot-reloadable shaders (`Material`, `WatchedAsset<T>`) — debug builds only
- 2D deferred lighting: normal buffer, point lights, stencil shadow hulls
- Sprites, animation, texture atlas, tilemap, nine-slice
- Input (keyboard, mouse, gamepad), audio controller
- JSON save/load for players and worlds
- LAN multiplayer: TCP transport and UDP broadcast server discovery
- ImGui debug UI (desktop only; skipped on Android and iOS, see `Core.IsImGuiSupported`)

## Using it

Add it to your game repo as a submodule:

    git submodule add https://github.com/jaxun614/ZeEngine1.git ZeEngine

Reference the project, and build the shared content (shaders and transition
textures) from each platform head project:

    <ProjectReference Include="../ZeEngine/ZeEngine.csproj" />

    <MonoGameContentReference Include="..\ZeEngine\SharedContent\SharedContent.mgcb"
                              Link="SharedContent\SharedContent.mgcb" />

Then derive from `Core`:

    public class MyGame : ZeEngine.Core
    {
        public MyGame() : base("My Game", 1080, 720, false) { }

        protected override void LoadContent()
        {
            base.LoadContent();
            ChangeScene(new MyFirstScene());
        }
    }

The `SharedContent` folder is built by the game project, not by this library,
so the `.mgcb` must be referenced from every platform head.

## Layout

    ZeEngine.csproj
    Core.cs                 game singleton
    Audio/ Content/ Graphics/ ImGui/ Input/ Networking/ Saves/ Scenes/
    SharedContent/          engine shaders + transition textures (.mgcb)
