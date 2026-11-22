# Common: game extraction and card compilation

The part both projects share. It reads the game from an Android emulator over ADB and
turns it into compiled cards that the Unity player and the web export both read.
Everything it writes goes to `data/` at the repository root.

## Commands

From the repository root:

```powershell
python -m common.render update              # data/game from the emulator, then data/runtime-assets and data/cards
python -m common.render runtime-assets      # rebuild data/runtime-assets from data/game
python -m common.render build               # recompile data/cards from data/game
python -m common.render build PK_20_024100_02
```

Any rooted emulator works: BlueStacks, MuMu, LDPlayer, Nox, MEmu, an Android Studio
image, or a rooted phone. The updater connects to the usual emulator ports when ADB
lists nothing, takes the device that has the game (or the one in `ANDROID_SERIAL`),
and finds how it runs a command as root (`su -c`, `su 0`, or ADB already root).

ADB and ffmpeg are not shipped. ADB is taken from the `ADB` environment variable,
then the `PATH`, then the Android SDK, then the one an emulator installs (BlueStacks'
`HD-Adb.exe`, LDPlayer's, MuMu's, Nox's `nox_adb.exe`, MEmu's); ffmpeg from `FFMPEG`,
then the `PATH` (`winget install ffmpeg`). An error says what is missing.

`update` transfers and converts only the files that changed. If a file is missing
on the device, it stops before touching `data/game`.

## What it produces

| Folder | Content |
| --- | --- |
| `data/game/` | The game's files under their real names (`Common/CardNew/...`, `MasterData/`), and `Data/`, the player data of the APK. The bundles the renderers load are converted for Windows. |
| `data/cards/` | One zip per card, `<id>.zip`: everything the player loads for it, its plans gzipped and stored as they are. An immersive card's movie sits beside it, `<id>.mp4`. `data/cards/index.json` lists the cards and the failures. |
| `data/runtime-assets/` | The game's files the player needs besides the cards (detail background, vignette, bloom, flip clips, text settings), repackaged for Windows with their payloads unchanged, and the plans next to them. |

## Organisation

| Path | Role |
| --- | --- |
| `render/updater.py` | Emulator → `data/game`: index reading, decryption, content checks. |
| `render/compiler.py` | `data/game` → `data/cards`. |
| `render/runtime_assets.py` | `data/game` → `data/runtime-assets`. |
| `render/effects.py` | Exports the fields of each card effect controller. |
| `render/paths.py` | Every shared location. |
| `runtime-assets/` | Versioned inputs of `runtime_assets`: the presentation plans and two Unity envelopes (`templates/`). |
| `tests/` | Unit tests and the updater check. |

A text tag that is not ported makes its card fail: it is listed under `failures`
in `data/cards/index.json`.
