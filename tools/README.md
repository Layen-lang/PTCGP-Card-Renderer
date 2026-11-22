# Third-party tools

Binaries the project calls, shipped with their licenses so a fresh clone works.
Each one can be replaced through the environment variable listed.

ADB and ffmpeg are not here: Google's ADB builds may not be redistributed, every
emulator already installs its own, and Windows builds of ffmpeg are GPL and about 100 MB. The
project finds them by itself (`common/render/tools.py`).

| Folder | Tool | Used by | License | Override |
| --- | --- | --- | --- | --- |
| `astcenc/` | [astc-encoder](https://github.com/ARM-software/astc-encoder) by Arm: decodes the game's HDR ASTC textures | `common/render` (update, build) | Apache 2.0, `astcenc/LICENSE` | — |
| `spirv-cross/` | [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) by Khronos, C API as a DLL: SPIR-V to GLSL | `web/export` (shader translation) | Apache 2.0, `spirv-cross/LICENSE` | `SPIRV_CROSS` |
| `smolv/` | [SMOL-V](https://github.com/aras-p/smol-v) by Aras Pranckevičius, plus `smolv_decode.cpp`, a small command-line decoder written for this project; `build.cmd` rebuilds it | `web/export` (shader translation) | MIT or public domain, `smolv/license.txt` | `SMOLV_DECODE` |
