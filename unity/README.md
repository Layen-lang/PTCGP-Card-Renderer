# Project 1: Windows player (Unity)

The player shows a compiled card with the game's own shaders, in a URP pipeline
adapted to reproduce the game's renderer. It is the project's reference: the
browser engine is compared with it frame by frame.

It reads `data/cards` and `data/runtime-assets`, made by the common commands: see
the [main README](../README.md).

## Open a card

Double-click `tools/Open-Card.cmd`, or from the repository root:

```powershell
python unity/tools/app.py
```

The window offers the card, its language and the gold frame. Its **Update the
game** button runs `python -m common.render update`, about 10 minutes the first time: see the
durations in the [main README](../README.md#getting-started). The
rendering is not validated 1:1 against the game.

IM cards play their full sequence: hold the card still for about 2 seconds. Space
pauses the movie and Escape skips it.

## Build the player

It needs Unity 6000.0.78f1, the game's version.

```powershell
powershell -ExecutionPolicy Bypass -File unity/tools/build_player.ps1
```

The player goes to `data/build/windows-native/CardViewer.exe`, its log to
`data/logs/player-build.log`. If Unity is not at its default location, set the
`UNITY_EDITOR` environment variable.

A rebuild takes under a minute. The first build also imports the whole project,
including the embedded render pipeline, and takes noticeably longer. Rebuild only
after changing the C# code: new or updated cards need a new compilation
(`python -m common.render build`), not a new player.

## Organisation

| Path | Role |
| --- | --- |
| `project/` | The Unity project: open this folder in Unity. |
| `project/Assets/Runtime/` | The renderer: card loading, one `Source*State.cs` per effect controller, presentation, gestures. |
| `project/Assets/Editor/` | The player build (`BuildNativePreview`). |
| `project/Packages/com.unity.render-pipelines.universal/` | URP 17.0.4, embedded and modified for the native renderer; see `PROVENANCE.md`. |
| `tools/` | The window (`app.py`), its shortcut and the build script. |

## Add a rarity or an effect

1. In `common/render/effects.py`, export the fields of the new controller.
2. In `project/Assets/Runtime/`, add its `Source*State.cs` file and declare it in
   `SourceCardEffects.cs`.
3. Add the rarity to `RARITIES` in `common/render/paths.py` if it is new.
4. Rebuild the player, then run `python -m common.render build <card>`.

