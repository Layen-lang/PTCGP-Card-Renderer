"""Locations of the web project, on top of the common ones of common/render/paths.py.

The two shader tools come from tools/, unless an environment variable points elsewhere.
"""
import os
from pathlib import Path
from common.render.paths import CARDS, DATA, LANGUAGES, PLAYER, REPO, ROOT, RUNTIME_ASSETS, RUNTIME_SOURCES, TOOLS, UNITY_VERSION  # noqa: F401

WEB = REPO/'web'
ENGINE = WEB/'engine'
WEB_CARDS = DATA/'web-cards'
SPIRV_CROSS = Path(os.environ.get('SPIRV_CROSS', TOOLS/'spirv-cross/spirv-cross.dll'))
SMOLV_DECODE = Path(os.environ.get('SMOLV_DECODE', TOOLS/'smolv/smolv_decode.exe'))
