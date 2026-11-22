# Project 2: browser engine (WebGL2)

The cards in a web page, with neither Unity nor any dependency. The engine reads
packages exported from the compiled cards (`data/cards`, see the
[main README](../README.md)) and is compared frame by frame with the Windows
player ([unity/](../unity/README.md)).

## Build the packages

From the repository root, once `python -m common.render update` has run and the
player is built (the export asks it to compose each card's text):

```powershell
python -m web.export build                                   # every card, 9 languages
python -m web.export build PK_10_000010_00 --languages en_US # one card
python -m web.export catalog                                 # data/web-cards/catalog.json, for the gallery
python -m web.export posters                                 # the images shown while a card loads (optional)
python -m web.export serve                                   # http://127.0.0.1:8765/
```

**How long it takes.** These figures were measured on an 8-core, 16-thread PC with
16 GB of RAM:

- **Card export.** The 4,126 cards in 9 languages took 1 h 20 with 4 processes
  (about 5 s per card for each process). Most of that time goes into encoding
  the text images losslessly, 4 per language.
- **Processes and memory.** Each process runs a player to compose the text, and
  the pair holds up to about 1.5 GB. The export starts as many as free memory
  allows, at most 8: 4 with 7 GB free. Closing the emulator and other heavy
  programs gives it more. `--workers` sets the number. A card that fails, as a
  player can when memory runs short, is tried again at the end.
- **Speeding it up.** `--languages en_US` exports a single language and cuts most
  of that work. `--resume` keeps the packages already built after an
  interruption. Later builds re-encode only what changed.
- **Posters.** About 2 s per card for its 9 languages, for each process, and
  about 6 GB for every card. They are optional.
- **Packages.** About 2 to 2.5 MB per card. Each of the 39 IM cards adds its
  movie, 15 to 21 MB.

`serve` lays the site out as a host would: `web/engine/` at the root and
`data/web-cards/` under `/web-cards/`. Then open
`http://127.0.0.1:8765/index.html?card=PK_10_000010_00&lang=en` or
`http://127.0.0.1:8765/gallery.html`.

Translating the game's shaders to GLSL uses SPIRV-Cross and SMOL-V from
[`tools/`](../tools/README.md). The posters need Chromium:
`playwright install chromium`.

## The component

The engine (`engine/src/`) has no dependency and only knows the package format:
it can leave the repository as it is.

```html
<script type="module" src="src/pokemon-card.js"></script>
<pokemon-card card="PK_10_000010_00" lang="en" base="/web-cards/"></pokemon-card>
```

The element's box is the card, its CSS size the card's size; the canvas overflows
around it to leave room for the tilt.

| Attribute | Effect |
| --- | --- |
| `card` | Card id. |
| `lang` | Text language: `en fr ja it de es pt ko zh`, `en` by default. |
| `base` | Package folder, `/web-cards/` by default. |
| `quality` | `game`: the game's 1403 px target without antialiasing, scaled to the screen; `max`: a screen-sized target with 4x antialiasing. `game` by default on touch screens, `max` elsewhere. |
| `samples` | Antialiasing (MSAA), 1 to 4; overrides `quality`. |
| `target` | Side of the render target in pixels; overrides `quality`. |
| `loading` | `lazy`: the card loads only once 300 px from the screen. |
| `side` | `back`: the card shows its back. |
| `tilt` | Rest pose in degrees, horizontal then vertical: `tilt="10 -5"`. |
| `max-tilt` | Finger tilt limit in degrees (30 in the game). |
| `background` | Colour behind the card, transparent by default. |
| `no-touch` | Decorative card: it ignores touch and lets the page scroll. |
| `no-flip` | A swipe does not flip the card. |
| `autoplay` | An IM card plays its sequence by itself, the movie muted. |
| `idle` | Slow sway at rest. |
| `motion` | `reduce`: still except under the finger; `full`: animated. By default the system's “reduce motion” setting. |
| `label` | Text read by screen readers; the card's name by default. |
| `fps` | Frame rate cap, to save battery. |
| `paused` | No animation at rest before the first touch. |
| `preview` | Without the IM sequence, in the shared context: for a grid. |
| `gyro` | Tilt from the phone's gyroscope. |
| `gold` | The gold frame, as the game's `AdditionalFrameLoader` puts it on: the frame's own parts replace the card's, and the whole card text is composed with the gold design. Ignored on a card without one. |
| `poster` | Image shown while loading: the prepared poster by default (`_posters/<id>/<language>.webp`, or `<language>-gold.webp` with `gold` when drawn with `--gold`), another address, or `none`. |

Events: `cardready` (`detail.firstFrame` in milliseconds), `cardfailed`. Method
`release()`: gives back what the card holds, when a page removes it.

Preloading: `customElements.get('pokemon-card').preload(id, { lang, base })` puts
in the browser cache everything a card loads before its first frame, poster
included, and returns a promise. A page calls it for the cards it will show next,
which then appear without waiting for the network. Each file is requested once.
The gallery preloads the cards up to three screens below.

While loading, the card shows its poster: its first frame, drawn in advance by the
engine itself (1403 px target, 4x antialiasing), cropped to the card at
733 x 1024, WebP at 90 %. It goes as soon as the real card is drawn. A poster older
than its package is redrawn; `--part 1/2` shares the work between processes, about
2 s per card for its 9 languages, about 160 KB each. Posters are optional: without one, a card
shows nothing until its first frame. `--languages en_US fr_FR` draws only some languages, and
`--gold` also draws the gold frame's look on the cards that have one, which is not done by
default (about 3 GB more for all of them). The gallery also shows them for the cards it does
not draw yet.

Gestures: drag to tilt (30 degrees at most), quick horizontal swipe to flip, still
long press on an IM card to start its sequence: preparation, transition with
particles, movie, Pause and Skip buttons, closing transition.

The gallery scrolls: cards near the screen are live, the others give their memory
back. They all share one WebGL context (`src/gpu.js`). It filters by expansion,
evolution family, rarity, type, category, name and language, can show the gold frame
on the cards that have one, keeps the filters in the address, and reads
`data/web-cards/catalog.json`.

## Files

| File | Role |
| --- | --- |
| `engine/src/engine.js` | Loading, render states, draw order, targets. |
| `engine/src/gpu.js` | The WebGL context shared by every card of the page, and what it keeps once. |
| `engine/src/effects.js` | Port of the card controllers and animation curves. |
| `engine/src/bloom.js` | The original seven bloom passes. |
| `engine/src/touch.js` | Tilt, swipe and gyroscope. |
| `engine/src/particles.js` | Shuriken simulation and particle geometry. |
| `engine/src/scene.js` | Drawing of the particle scenes. |
| `engine/src/immersive.js` | Sequence of an IM card. |
| `engine/src/pokemon-card.js` | The component. |
| `engine/gallery.html`, `engine/gallery.js` | The gallery, a page using the component. The engine does not depend on it. |
| `engine/diag.html`, `engine/diag.js` | Device diagnostics: a dozen measurements on one card, and a report to copy. |
| `export/` | The Python export of the packages (`python -m web.export`). |

## Package format

`data/web-cards/<id>/card.json`, next to `text/<language>-<type>.webp` and
`movie.mp4` for an IM card. Textures live in `data/web-cards/_shared/` under their
hash: an identical file exists once and is cached from one card to the next.
`_shared/bloom.json` and `_shared/flip.json` serve every card,
`_shared/immersive-scene.json` every IM card.

| Field | Content |
| --- | --- |
| `version` | Format version. |
| `camera`, `studio` | Camera position, field of view and planes; card position. Same values as in the game. |
| `verticalPercentage`, `cardAspect` | Share of the square target the front card covers, card proportions. |
| `maxTilt`, `targetSize` | Tilt limit in degrees, side of the game's target. |
| `programs` | `file`: translated GLSL ES 3.0 (`vertex`, `fragment`), gzipped, shared between cards; `attributes`: location to vertex channel. |
| `textures` | `file`, or `files` for a cube; `format` `rgba16f` or `r16` for a `.bin.gz`; size, filter, Unity wrap mode. `lazy`: loaded after the first frame. |
| `meshes` | `file`: the floats of each channel then 32-bit indices, gzipped. `vertices` (channel to size), `vertexCount`, `indexCount`, `submeshes` as `[first, count]`, `center`. |
| `renderers` | `path` in the hierarchy, `tags` (pass SearchTag), `model`, `colors`. The controllers write on them. `variant`: `gold` for the gold frame's own parts, `base` for the parts it replaces; drawn with either look when absent. |
| `draws` | In queue order: mesh, submesh, program, `renderer`, `model` matrix, `uniforms`, `textures`, `state`. |
| `effects`, `emissive` | Controller settings as exported for the player, bloom presence. |
| `dynamicText` | Per language and type, the composed UI image. |
| `goldText` | The same, composed with the gold design, on a card with a gold frame. |
| `immersive` | Movie, long press timing and transitions, else `null`. |
| `particles` | True when `particles.json` exists next to it: the prefab's Shuriken systems (AN sand). |
| `uiOrthographic`, `cardHeightLogical` | Scale of the UI layer where the IM particles live. |
| `unset` | Shader constants with no known value at export, zero unless a controller writes them. |

`uniforms` and `textures` names are Unity's. A texture is an index,
`dynamic<type>` or the name of a default texture (`white`, `black`, `gray`, `bump`).
`state` values are Unity enumerations: `CompareFunction`, `BlendMode`, `BlendOp`,
`StencilOp`, `CullMode`, `ColorWriteMask`.

Images keep Unity's row order: the first stored row is `v = 0`. They look upside
down when opened, and nothing is flipped on load.

## Loading

A card appears only once complete, and the image is the same from the first
instant to the last. To keep that fast, everything downloads at once, programs
compile as soon as they arrive while images are still coming, and only what is not
visible waits (the card's back, the IM sequence). The `cardready` event marks the
display.

Programs, meshes and textures live under their hash in `_shared/`, cached from one
card to the next. A host can serve them as `Cache-Control: immutable`, and HTTP/2
avoids waiting between files.

## Known limits

Everything has a first version. Some gaps remain or are unchecked:

- Particle randomness is not Unity's: same laws, other draws. Shuriken's
  `NoiseModule` is approximated.
- The gyroscope comes from the game's native code, but its settings are the
  constructor's: neither the activation nor the values of the game's instance are
  known. Hence the attribute.
- `Mathf.PerlinNoise` is exact for positive coordinates, the only ones the cards
  read. Negative ones differ.
- IM sequences follow the game's timing, card zoom, movie and closing
  transition, compared frame by frame with the game on an emulator. Their glow is
  still slightly weaker than the game's in the first second. Where the large
  bokeh spots fall, and their colour, depends on the particle draws.
- The sand has no reference and is checked by eye.
