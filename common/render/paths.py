"""Every location shared by the two projects.

Source files live in common/, unity/ and web/. Everything generated or extracted from
the game lives in data/, which is also the root the Windows player reads and writes.
"""
import os
from pathlib import Path

PACKAGE = Path(__file__).resolve().parent
COMMON = PACKAGE.parent
REPO = COMMON.parent
TOOLS = REPO/'tools'
# Fixed reference data shipped with the package.
PACKAGE_DATA = PACKAGE/'data'
# Plans and Unity envelopes, versioned; runtime_assets copies the plans next to the bundles.
RUNTIME_SOURCES = COMMON/'runtime-assets'

DATA = REPO/'data'
# The player's --project-root: it resolves cards/, runtime-assets/ and logs/ from here.
ROOT = DATA
GAME = DATA/'game'
CARDS = DATA/'cards'
LOGS = DATA/'logs'
RUNTIME_ASSETS = DATA/'runtime-assets'
PLAYER = DATA/'build/windows-native/CardViewer.exe'

UNITY_PROJECT = REPO/'unity/project'
UNITY_VERSION = '6000.0.78f1'
UNITY_EDITOR = Path(os.environ.get('UNITY_EDITOR', rf'C:\Program Files\Unity\Hub\Editor\{UNITY_VERSION}\Editor\Unity.exe'))
LANGUAGES = ('fr_FR', 'en_US', 'ja_JP', 'it_IT', 'de_DE', 'es_ES', 'pt_BR', 'ko_KR', 'zh_TW')
RARITIES = ('RR', 'R', 'C', 'U', 'AR', 'S', 'SR', 'SSR', 'SAR', 'IM', 'UR', 'AN')
