"""The two outside programs the project runs but does not ship: ADB and ffmpeg.

Each is looked for in its environment variable, then on the PATH. ADB is also taken from the
Android SDK or from an emulator, which installs its own: any of them talks to any device.
"""
import functools
import os
import shutil
from pathlib import Path

# Registry keys holding an emulator's install folder, under HKEY_LOCAL_MACHINE or HKEY_CURRENT_USER.
EMULATOR_KEYS = (r'SOFTWARE\BlueStacks_nxt', r'SOFTWARE\BlueStacks', r'Software\XuanZhi\LDPlayer9', r'Software\XuanZhi\LDPlayer')
# The ADB an emulator ships, relative to its install folder.
EMULATOR_ADB = ('HD-Adb.exe', 'adb.exe', r'shell\adb.exe', r'nx_main\adb.exe', r'bin\nox_adb.exe')
# Usual install folders, when the registry does not say.
EMULATOR_FOLDERS = (
    r'C:\Program Files\BlueStacks_nxt', r'C:\Program Files\BlueStacks',
    r'C:\LDPlayer\LDPlayer9', r'C:\LDPlayer\LDPlayer4.0',
    r'C:\Program Files\Netease\MuMuPlayer', r'C:\Program Files\Netease\MuMuPlayer-12.0', r'C:\Program Files\Netease\MuMu Player 12',
    r'C:\Program Files (x86)\Nox', r'C:\Program Files\Nox',
    r'C:\Program Files\Microvirt\MEmu',
)


def registry_folders():
    folders = []
    try:
        import winreg
    except ImportError:
        return folders
    for hive in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        for key in EMULATOR_KEYS:
            try:
                with winreg.OpenKey(hive, key) as handle:
                    folders.append(winreg.QueryValueEx(handle, 'InstallDir')[0])
            except OSError:
                pass
    return folders


def sdk_folders():
    """Android Studio's SDK, where its emulator's ADB lives."""
    roots = [os.environ.get('ANDROID_HOME'), os.environ.get('ANDROID_SDK_ROOT'), os.path.join(os.environ.get('LOCALAPPDATA', ''), 'Android', 'Sdk')]
    return [Path(root)/'platform-tools' for root in roots if root]


def emulator_folders():
    return [Path(folder) for folder in (*registry_folders(), *EMULATOR_FOLDERS)]


def from_environment(variable):
    value = os.environ.get(variable)
    if value and not Path(value).is_file():
        raise ValueError(f'{variable} points to a missing file: {value}')
    return value


@functools.cache
def adb():
    found = from_environment('ADB') or shutil.which('adb')
    if not found:
        candidates = [folder/'adb.exe' for folder in sdk_folders()]+[folder/name for folder in emulator_folders() for name in EMULATOR_ADB]
        found = next((str(path) for path in candidates if path.is_file()), None)
    if not found:
        raise ValueError('ADB not found: install Android platform-tools or an emulator, put adb on the PATH, or set the ADB environment variable.')
    return found


@functools.cache
def ffmpeg():
    found = from_environment('FFMPEG') or shutil.which('ffmpeg')
    if not found:
        raise ValueError('ffmpeg not found: install it with "winget install ffmpeg", or set the FFMPEG environment variable.')
    return found
