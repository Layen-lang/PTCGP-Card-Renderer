"""Assets every package uses, once in web-cards/_shared/: the bloom passes and the flip animations."""
import json
import math
import struct
import UnityPy
from .paths import RUNTIME_ASSETS, UNITY_VERSION, WEB_CARDS
from .shaders import translate_pass

UnityPy.config.FALLBACK_UNITY_VERSION = UNITY_VERSION
BUNDLES = RUNTIME_ASSETS/'bundles'
FLIP_CLIPS = ('CardFlip_LeftReverb', 'CardFlip_RightReverb')
FLIP_RATE = 60


def state_of(state):
    blend = state['rtBlend0']
    return dict(blend=[int(blend[k]['val']) for k in ('srcBlend', 'destBlend', 'srcBlendAlpha', 'destBlendAlpha')])


def bloom_passes():
    env = UnityPy.load(str(BUNDLES/'source-bloom.bundle'))
    tree = next(o.read_typetree() for o in env.objects if o.type.name == 'Shader')
    passes = []
    for program in tree['m_ParsedForm']['m_SubShaders'][0]['m_Passes']:
        translated = translate_pass(tree, program, set())
        del translated['names'], translated['samplers']
        passes.append(dict(translated, name=program['m_State']['m_Name'], **state_of(program['m_State'])))
    return passes


def streamed_channels(stream):
    """Hermite pieces per curve of a streamed clip; the pre-frame at -infinity gives the first value."""
    raw = struct.pack('<'+'I'*len(stream['data']), *stream['data'])
    channels = [[] for _ in range(stream['curveCount'])]
    offset = 0
    while offset < len(raw):
        time, count = struct.unpack_from('<fI', raw, offset)
        offset += 8
        for _ in range(count):
            index, a, b, c, d = struct.unpack_from('<I4f', raw, offset)
            offset += 20
            if math.isfinite(time):
                channels[index].append((max(0, time), a, b, c, d))
    return channels


def sample(keys, time):
    current = keys[0]
    for key in keys:
        if key[0] > time:
            break
        current = key
    dt = max(0, time-current[0])
    return ((current[1]*dt+current[2])*dt+current[3])*dt+current[4]


def flip_clips():
    """The original flip clips: a single Euler curve, the yaw of the Rotation node, sampled."""
    env = UnityPy.load(str(BUNDLES/'source-flip-clips.bundle'))
    clips = {}
    for o in env.objects:
        if o.type.name != 'AnimationClip':
            continue
        tree = o.read_typetree()
        if tree['m_Name'] not in FLIP_CLIPS:
            continue
        channels = streamed_channels(tree['m_MuscleClip']['m_Clip']['data']['m_StreamedClip'])
        if len(channels) != 3 or any(sample(channels[i], t) for i in (0, 2) for t in (0, .2, .5)):
            raise ValueError(f"Unexpected flip animation: {tree['m_Name']}")
        duration = tree['m_MuscleClip']['m_StopTime']
        clips[tree['m_Name']] = dict(duration=duration, rate=FLIP_RATE, yaw=[round(sample(channels[1], min(i/FLIP_RATE, duration)), 3) for i in range(int(duration*FLIP_RATE)+2)])
    return clips


def write_shared_assets():
    folder = WEB_CARDS/'_shared'
    folder.mkdir(parents=True, exist_ok=True)
    (folder/'bloom.json').write_text(json.dumps(bloom_passes(), separators=(',', ':')), encoding='utf-8')
    (folder/'flip.json').write_text(json.dumps(flip_clips(), separators=(',', ':')), encoding='utf-8')
