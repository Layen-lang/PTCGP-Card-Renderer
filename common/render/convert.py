"""Convert Android bundles in place for the Windows player.

ASTC/ETC textures are decoded with all their mips; every other payload is kept
and checked unchanged. The converted file replaces the one in game/.
"""
import hashlib
from pathlib import Path
import UnityPy
from UnityPy.enums import TextureFormat
from UnityPy.export.Texture2DConverter import parse_image_data
from UnityPy.files import SerializedFile
from .astc_hdr import decode as decode_hdr
from .paths import UNITY_VERSION

UnityPy.config.FALLBACK_UNITY_VERSION = UNITY_VERSION
WINDOWS_TARGET = 19
ANDROID_TARGET = 13


def digest(payload):
    return hashlib.sha256(payload).hexdigest()


def target_platforms(env):
    return [int(file._m_target_platform) for file in env.file.files.values() if isinstance(file, SerializedFile)]


def convert(path):
    """Return False if the file was already converted."""
    path = Path(path)
    # Loaded from memory: a file UnityPy maps stays locked on Windows.
    env = UnityPy.load(path.read_bytes())
    platforms = target_platforms(env)
    if platforms and all(p == WINDOWS_TARGET for p in platforms):
        return False
    if any(p != ANDROID_TARGET for p in platforms):
        raise ValueError(f'Unexpected source target {platforms}: {path}')
    original = {(o.assets_file.name, o.path_id): digest(o.get_raw_data()) for o in env.objects}
    decoded = {}
    for o in env.objects:
        if o.type.name not in ('Texture2D', 'Cubemap'):
            continue
        tex = o.read()
        fmt = TextureFormat(tex.m_TextureFormat)
        if 'ASTC' not in fmt.name and fmt.name not in ('ETC_RGB4', 'ETC2_RGBA8'):
            continue
        is_hdr = 'HDR' in fmt.name
        block = int(fmt.name.rsplit('_', 1)[1].split('x')[0]) if 'ASTC' in fmt.name else 4
        block_bytes = 8 if fmt.name == 'ETC_RGB4' else 16
        raw = tex.get_image_data()
        offset = 0
        levels = []
        for face in range(tex.m_ImageCount):
            for mip in range(tex.m_MipCount):
                w, h = max(1, tex.m_Width >> mip), max(1, tex.m_Height >> mip)
                size = ((w+block-1)//block)*((h+block-1)//block)*block_bytes
                levels.append(decode_hdr(raw[offset:offset+size], w, h, block) if is_hdr else
                              parse_image_data(raw[offset:offset+size], w, h, fmt, o.version, o.platform, tex.m_PlatformBlob, False).convert('RGBA').tobytes())
                offset += size
        if offset != len(raw):
            raise ValueError(f'Unaccounted texture bytes {tex.m_Name}: {offset}/{len(raw)}')
        tex.image_data = b''.join(levels)
        tex.m_TextureFormat = int(TextureFormat.RGBAHalf if is_hdr else TextureFormat.RGBA32)
        tex.m_CompleteImageSize = len(tex.image_data)//tex.m_ImageCount
        tex.m_StreamData.path = ''
        tex.m_StreamData.offset = tex.m_StreamData.size = 0
        tex.save()
        decoded[(o.assets_file.name, o.path_id)] = digest(tex.image_data)
    for file in env.file.files.values():
        if isinstance(file, SerializedFile):
            file._m_target_platform = WINDOWS_TARGET
    payload = env.file.save(packer='lz4')
    temporary = path.with_name(path.name+'.converting')
    temporary.write_bytes(payload)
    check = UnityPy.load(payload)
    for o in check.objects:
        key = (o.assets_file.name, o.path_id)
        if key in decoded:
            if digest(o.read().get_image_data()) != decoded[key]:
                raise ValueError(f'Decoded mip payload did not round-trip: {path}')
        elif digest(o.get_raw_data()) != original[key]:
            raise ValueError(f'Object payload changed: {path}')
    del env, check
    # The source may be a hard link to another copy: replace, never write through.
    temporary.replace(path)
    return True
