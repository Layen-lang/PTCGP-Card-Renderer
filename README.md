# Pokémon TCG Pocket card renderer

Renders the 3D cards of Pokémon TCG Pocket outside the game: the effect of every
rarity, the card text in the game's 9 languages, tilt, flip and the immersive
sequences.

**Immersive (IM) cards are implemented** in both projects: a long press starts the
full sequence, as in the game, with the light under the finger, the transition
particles, the original movie inside the card, its Pause and Skip buttons, and the
closing transition. The sequence is checked frame by frame against the game.

> **No game file is included.** This repository holds code only. Cards, textures,
> shaders and bundles are extracted from **your own** installation of the game.
> This is a fan project, not affiliated with Nintendo, The Pokémon Company,
> Creatures, GAME FREAK or DeNA. Pokémon and the related names are their
> trademarks.

## Two projects on one base

| Folder | What it is |
| --- | --- |
| [`common/`](common/) | Shared by both projects: extracts the game and compiles the cards. |
| [`unity/`](unity/) | **Project 1**, the Windows player: the game's own shaders in Unity 6. The fidelity reference. |
| [`web/`](web/) | **Project 2**, the browser engine: a `<pokemon-card>` element and a gallery, in WebGL2 with no dependency. |
| [`tools/`](tools/) | Third-party programs the projects call, with their licenses. |
| `data/` | Everything extracted or generated, never published. Delete it and rebuild it at will. |

Each folder has its own README.

## Getting started

You need Windows and the game installed on a rooted Android emulator with ADB enabled:
BlueStacks, MuMu, LDPlayer, Nox, MEmu or an Android Studio image (a rooted phone works too).

1. Clone with long paths enabled, as the embedded URP package has some:
   `git config --global core.longpaths true`.
2. Install Python 3.12 or newer, then `pip install -r common/requirements.txt`.
3. Install ffmpeg: `winget install ffmpeg`. It remuxes the movies of the immersive
   cards. ADB comes with the emulator and is found by itself.
4. Open the game once in the emulator so that it downloads its data.
5. From the repository root:

   ```powershell
   python -m common.render update
   ```

   This copies and decrypts the game files into `data/game`, rebuilds
   `data/runtime-assets`, then compiles every card into `data/cards`.

   **How long it takes.** These figures were measured on an 8-core, 16-thread
   PC (Ryzen 7 7800X3D) with 16 GB of RAM:

   | Step | First run | Limited by |
   | --- | --- | --- |
   | Copy and decryption, 73,000 files | about 2 min | ADB's throughput (4 parallel streams) |
   | Checking and storing the files | about 1.5 min | Disk |
   | Player assets, analysis and conversion | about 3.5 min | CPU and disk |
   | Card compilation, 4,126 cards | about 2.5 min | CPU and memory: one process per 1.25 GB of free RAM |

   A first run takes about 10 minutes in all. The log can stay still for a
   minute or more: it is still working. Later runs copy, decrypt and compile
   only what the game changed. Plan for
   about 10 GB of disk: 8.3 GB for `data/game` and 1.1 GB for `data/cards`.
   To compile only some cards, run `python -m common.render build <card ids>`.
6. Go on with the project you want: [unity](unity/README.md), then
   [web](web/README.md), which needs the compiled player. Exporting every card
   for the browser is the longest step, about 1 h 20: its README gives the
   figures and how to shorten it.

Every command runs from the repository root.

## How the 3D rendering works

A card in the game is not an image. It is a small 3D scene, drawn again on every
frame with the game's own shaders. The two projects replay that scene outside
the game.

1. **What the game stores.** Each card is a Unity prefab inside the game's asset
   bundles. It holds the card's layers as meshes (frame, illustration, foil and
   relief), the materials that draw them with the game's compiled shaders, their
   textures, and the effect controllers. The controllers are scripts that move
   the shader parameters with the tilt and with time. That movement is what makes
   foils shimmer and reliefs catch the light.
2. **Extraction** (`update`). The files are copied from the emulator and
   decrypted, as the game itself does when it loads them.
3. **Compilation** (`build`). For each card, `common/` reads the prefab and its
   dependencies and writes a plan to `data/cards/<id>.zip`. The plan lists every
   draw in the game's order (mesh, material, render states, queue), the settings
   of each effect controller, and the immersive timeline for an IM card. Nothing
   is redrawn by hand: the plan only describes what the game draws.
4. **Drawing.**
   - The Unity player loads the plan with the game's original shaders, in a
     render pipeline adapted to reproduce the game's renderer.
   - The browser engine uses the same plan, exported with its shaders translated
     to WebGL2.

   In both, the card is drawn by a camera into a square target. What its
   materials emit then goes through the game's bloom passes, and the result is
   shown on screen with the tilt.
5. **Effects.** Each effect controller of the game has a port: one C# file per
   controller in the player, `effects.js` in the browser. On every frame it turns
   the tilt and the elapsed time into the same shader values as in the game.
6. **Text.** The game composes the name, attacks and abilities with its own
   interface. The player composes them the same way, in the 9 languages, and the
   web export stores the result as images.
7. **Immersive sequences.** A long press starts the game's timeline:
   - the light under the finger;
   - the particle systems of the transition, simulated as the game's Shuriken
     systems do;
   - the zoom curves of the card;
   - the original movie, inside the card's rounded shape;
   - the closing transition.

## License

Copyright (C) 2026 Layen. Made by **Layen**.

[GNU Affero General Public License v3.0](LICENSE): you may use, modify and
redistribute this code, provided you keep the copyright notice and publish your
changes under the same license, including when you only run a modified version
on a server, such as the web engine on a public site.

Third-party components keep their own licenses: see [tools/README.md](tools/README.md)
and `unity/project/Packages/com.unity.render-pipelines.universal/LICENSE.md`.
