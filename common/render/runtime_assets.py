"""Build runtime-assets/bundles/ from game/: the game's files the player loads besides the cards.

Every bundle keeps the game's object payloads byte for byte. Only the envelope changes:
platform Android to Windows, textures decoded by convert(), and for the objects that
come from the APK's player data, a small AssetBundle envelope around them.

Sources: game/Data (the APK's assets/bin/Data, fetched by the updater), the bundles of
game/, and two envelopes exported by Unity in runtime-assets/templates/.
"""
import copy
import hashlib
import json
import shutil
import warnings
from pathlib import Path
import UnityPy
from UnityPy.files import SerializedFile
from UnityPy.helpers.TypeTreeHelper import read_typetree
from UnityPy.streams import EndianBinaryReader
from .convert import WINDOWS_TARGET, convert
from .game import Game
from .paths import GAME, ROOT, RUNTIME_ASSETS, RUNTIME_SOURCES, UNITY_VERSION

UnityPy.config.FALLBACK_UNITY_VERSION = UNITY_VERSION
warnings.filterwarnings('ignore', message='No valid Unity version found.*')
BUNDLES = RUNTIME_ASSETS/'bundles'
TEMPLATES = RUNTIME_SOURCES/'templates'
PLAYER_DATA = GAME/'Data'
PRESENTATION_MATERIAL = 'Common/CardNew/System/Materials/UI_Default_From_RT.mat_bundles'
# What the detail background loads, in the order the player loads them in reverse.
DIALOG_BACKGROUND = (
    'Common/UI/Prefabs/System/LtUIDialogBackground.prefab_bundles',
    'ab_UI_bundles',
    'UnityMonoScripts',
    'Common/UI/Textures/Common_bundles',
    'Common/UI/Textures/_Others_bundles',
    'Common/Shader/Common/UI/LtUI-HolographicShinyEffect.shader_bundles',
    'ab_ExplicitLayoutShader_bundles',
    'Common/Shader/Common/UI/UI_Rainbow4.shader_bundles',
    'Common/CardNew/Common/Model/Textures/Card/SimpleCard_Side02.png_bundles',
    'Common/Shader/Common/UI/UI_Rainbow.shader_bundles',
    'Common/CardNew/Common/Model/Textures/OldHologram/Ramp/ALL_RMP_RareMark_Igaiga.png_bundles',
    'Common/Shader/Common/CardNew/Card_Side_And_Back.shader_bundles',
)
# The three compiled flip clips, onto the envelope's FlipTemplate0..2.
FLIP_CLIPS = ('CardFlip_RightReverb', 'CardFlip_Idle', 'CardFlip_LeftReverb')
BLOOM_PASSES = ['DownSampling', 'Image2SheetComponent', 'MakeTextureSheet', 'Blur', 'Blur', 'Sheet2Image', 'Composite']


def sha(data):
    return hashlib.sha256(data).hexdigest()


def relative(path):
    return Path(path).resolve().relative_to(ROOT).as_posix()


def layout(node):
    """A type tree's serialized layout: two objects share it before bytes are copied across."""
    return (node.m_Type, node.m_Name, node.m_ByteSize, node.m_Version, node.m_MetaFlag, tuple(layout(n) for n in node.m_Children))


def rename_serialized(bundle, digest):
    """Give the envelope its own CAB identity, so it never collides with a game bundle."""
    for name, file in list(bundle.file.files.items()):
        if isinstance(file, SerializedFile):
            if file.externals:
                raise ValueError(f'Envelope with external references: {name}')
            file._m_target_platform = WINDOWS_TARGET
            del bundle.file.files[name]
            file.name = 'CAB-'+digest[:32]
            bundle.file.files[file.name] = file


class PlayerData:
    """The APK's assets/bin/Data, uncompressed: plain Unity serialized files."""

    def __init__(self, root=PLAYER_DATA):
        if not (root/'globalgamemanagers').is_file():
            raise ValueError(f'{root} is missing: run python -m common.render update.')
        self.root = root
        self.env = UnityPy.load(str(root))
        self.files = {f.name: f for f in self.env.files.values() if isinstance(f, SerializedFile)}
        chunks = sorted(root.glob('globalgamemanagers.assets.split*'), key=lambda p: int(p.suffix.removeprefix('.split')))
        self.managers = UnityPy.load(b''.join(p.read_bytes() for p in chunks)) if chunks else None

    def shader(self, name, env=None):
        found = [o for o in (env or self.managers).objects if o.type.name == 'Shader' and o.read_typetree()['m_ParsedForm']['m_Name'] == name]
        if len(found) != 1:
            raise ValueError(f'Shader {name}: {len(found)} found')
        return found[0]

    def builtin_shader(self, name):
        return self.shader(name, UnityPy.load(str(self.root/'Resources/unity_builtin_extra')))

    def objects(self, kind):
        for file in self.files.values():
            for obj in file.objects.values():
                if obj.type.name == kind:
                    yield obj


class Builder:
    def __init__(self, game=None, data=None, log=print):
        self.game = game or Game()
        self.data = data or PlayerData()
        self.log = log
        self.presentation = self.closure(PRESENTATION_MATERIAL)
        self.envelope = next(row for row in self.presentation if self.kinds(row) == ['AssetBundle', 'Shader'])

    def kinds(self, row):
        return sorted(o.type.name for o in UnityPy.load(self.game.path(row).read_bytes()).objects)

    def closure(self, name):
        """A bundle and everything it depends on, through the CAB names of game/index.json."""
        rows, queue = [], [self.game.by_name[name]]
        while queue:
            row = queue.pop(0)
            if row in rows:
                continue
            rows.append(row)
            env = UnityPy.load(self.game.path(row).read_bytes())
            bundle = next(o.read_typetree() for o in env.objects if o.type.name == 'AssetBundle')
            for dependency in bundle['m_Dependencies']:
                found = self.game.by_cab.get(dependency.lower(), [])
                if len(found) != 1:
                    raise ValueError(f'Dependency {dependency}: {len(found)} bundles')
                queue.append(found[0])
        return rows

    def copy_converted(self, row, folder):
        """A game bundle for the Windows player: platform and textures only, payloads checked."""
        destination = folder/(row['blob']+'.bundle')
        shutil.copyfile(self.game.path(row), destination)
        convert(destination)
        return destination

    def wrap_shader(self, shader, destination, name, container, set_bundle_name=False):
        """An original Shader object in a two-object AssetBundle envelope taken from the game."""
        tree = shader.read_typetree()
        if tree['m_Dependencies'] or tree['m_NonModifiableTextures']:
            raise ValueError(f'Shader with dependencies: {name}')
        pack = UnityPy.load(self.game.path(self.envelope).read_bytes())
        target = next(o for o in pack.objects if o.type.name == 'Shader')
        if layout(target._get_typetree_node()) != layout(shader._get_typetree_node()):
            raise ValueError('Shader serialized schemas differ')
        raw = shader.get_raw_data()
        target.set_raw_data(raw)
        asset = next(o for o in pack.objects if o.type.name == 'AssetBundle')
        bundle = asset.read_typetree()
        bundle['m_Name'] = name
        if set_bundle_name:
            bundle['m_AssetBundleName'] = name
        bundle['m_Dependencies'] = []
        if len(bundle['m_Container']) != 1 or bundle['m_Container'][0][1]['asset']['m_PathID'] != target.path_id:
            raise ValueError('Unexpected envelope container')
        bundle['m_Container'][0] = (container, bundle['m_Container'][0][1])
        asset.save_typetree(bundle)
        rename_serialized(pack, sha(raw))
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(pack.file.save(packer='lz4'))
        check = next(o for o in UnityPy.load(str(destination)).objects if o.type.name == 'Shader')
        if check.get_raw_data() != raw or check.read_typetree() != tree:
            raise ValueError(f'Shader payload changed: {name}')
        return dict(path=relative(destination), runtimeName=name, shaderName=tree['m_ParsedForm']['m_Name'],
                    sourceShaderSha256=sha(raw), bundleSha256=sha(destination.read_bytes()), platforms=tree['platforms'],
                    originalShaderPayloadUnchanged=True, originalShaderTreeUnchanged=True, gameVersion=self.game.version)

    def bloom(self):
        shader = self.data.shader('CustomPostEffect/Bloom')
        passes = [p['m_State']['m_Name'] for p in shader.read_typetree()['m_ParsedForm']['m_SubShaders'][0]['m_Passes']]
        if passes != BLOOM_PASSES:
            raise ValueError(f'Bloom passes changed: {passes}')
        return self.wrap_shader(shader, BUNDLES/'source-bloom.bundle', 'Source/Builtin/SheetBloom',
                                'Source/Builtin/CustomPostEffect/Bloom', set_bundle_name=True)

    def default_ui(self):
        report = self.wrap_shader(self.data.builtin_shader('UI/Default'), BUNDLES/'default-ui-player/original-ui-default.bundle',
                                  'pocket-original-ui-default-175', 'assets/pocket-original/ui-default.shader')
        (BUNDLES/'default-ui-player/manifest.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
        return report

    def vignette(self):
        report = self.wrap_shader(self.data.shader('LtUI/Vignette'), BUNDLES/'vignette-player/screen-vignette.bundle',
                                  'pocket-original-screen-vignette-175', 'assets/pocket-original/screen-vignette.shader')
        (BUNDLES/'vignette-player/manifest.json').write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
        return report

    def flip_clips(self):
        clips = {}
        for obj in self.data.objects('AnimationClip'):
            name = obj.read_typetree()['m_Name']
            if name in FLIP_CLIPS:
                if name in clips:
                    raise ValueError(f'Duplicate clip: {name}')
                clips[name] = obj
        bundle = UnityPy.load((TEMPLATES/'flip-clips-schema.bundle').read_bytes())
        targets = sorted((o for o in bundle.objects if o.type.name == 'AnimationClip'), key=lambda o: o.read_typetree()['m_Name'])
        if len(targets) != len(FLIP_CLIPS) or set(clips) != set(FLIP_CLIPS):
            raise ValueError(f'Flip clips: {sorted(clips)}')
        payloads = []
        for name, target in zip(FLIP_CLIPS, targets):
            source = clips[name].read_typetree()
            binding = source['m_ClipBindingConstant']
            if binding['pptrCurveMapping'] or source['m_Events'] or binding['genericBindings'][0]['path'] != 3490380455:  # CRC32 of Rotation
                raise ValueError(f'Unexpected clip: {name}')
            raw = clips[name].get_raw_data()
            target.set_raw_data(raw)
            payloads.append(raw)
        envelope = next(o for o in bundle.objects if o.type.name == 'AssetBundle')
        tree = envelope.read_typetree()
        tree['m_Name'] = tree['m_AssetBundleName'] = 'Source/Builtin/CardFlip'
        envelope.save_typetree(tree)
        rename_serialized(bundle, sha(b''.join(payloads)))
        destination = BUNDLES/'source-flip-clips.bundle'
        destination.write_bytes(bundle.file.save(packer='lz4'))
        check = {o.read_typetree()['m_Name']: o.get_raw_data() for o in UnityPy.load(str(destination)).objects if o.type.name == 'AnimationClip'}
        if sorted(check.values()) != sorted(payloads):
            raise ValueError('Flip clip payload changed')
        return dict(path=relative(destination), clips=list(FLIP_CLIPS))

    def tmp_settings(self):
        template = TEMPLATES/'tmp-settings-schema.bundle'
        bundle = UnityPy.load(template.read_bytes())
        target = next(o for o in bundle.objects if o.type.name == 'MonoBehaviour')
        node = target.serialized_type.node
        found = []
        for obj in self.data.objects('MonoBehaviour'):
            raw = obj.get_raw_data()
            try:
                tree = read_typetree(node, EndianBinaryReader(raw, endian='<'), byte_size=len(raw), check_read=True)
            except Exception:
                continue
            if 'm_leadingCharacters' in tree and 'm_followingCharacters' in tree:
                found.append((obj, raw, tree))
        if len(found) != 1:
            raise ValueError(f'TMP settings: {len(found)} candidates')
        original, raw, tree = found[0]
        if target.save_typetree(tree) != raw:
            raise ValueError('TMP settings do not round-trip')
        expected = copy.deepcopy(tree)
        expected['m_Script'] = target.read_typetree()['m_Script']
        for key, name in (('m_leadingCharacters', 'Leading'), ('m_followingCharacters', 'Following')):
            pointer = tree[key]
            source_file = original.assets_file.externals[pointer['m_FileID']-1].path
            text = self.data.files[source_file].objects[pointer['m_PathID']]
            destination_text = next(o for o in bundle.objects if o.type.name == 'TextAsset' and o.read_typetree()['m_Name'] == name)
            destination_text.set_raw_data(text.get_raw_data())
            expected[key] = dict(m_FileID=0, m_PathID=destination_text.path_id)
        target.set_raw_data(target.save_typetree(expected))
        envelope = next(o for o in bundle.objects if o.type.name == 'AssetBundle')
        envelope_tree = envelope.read_typetree()
        envelope_tree['m_Name'] = envelope_tree['m_AssetBundleName'] = 'Source/Builtin/TMPSettings'
        envelope.save_typetree(envelope_tree)
        rename_serialized(bundle, sha(raw))
        destination = BUNDLES/'source-tmp-settings.bundle'
        destination.write_bytes(bundle.file.save(packer='lz4'))
        if next(o for o in UnityPy.load(str(destination)).objects if o.type.name == 'MonoBehaviour').read_typetree() != expected:
            raise ValueError('TMP settings changed')
        return dict(path=relative(destination), sourcePayloadSha256=sha(raw))

    def presentation_bundles(self):
        folder = BUNDLES/'presentation-player'
        rows = []
        for row in self.presentation:
            path = self.copy_converted(row, folder)
            rows.append(dict(blob=row['blob'], name=row['name'], path=relative(path), bundleSha256=sha(path.read_bytes())))
        manifest = dict(asset=dict(name=PRESENTATION_MATERIAL, blob=self.game.by_name[PRESENTATION_MATERIAL]['blob']),
                        bundles=rows, gameVersion=self.game.version)
        (folder/'manifest.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
        return manifest

    def dialog_background(self):
        folder = BUNDLES/'dialog-background-player'
        rows = []
        for name in DIALOG_BACKGROUND:
            row = self.game.by_name[name]
            path = self.copy_converted(row, folder)
            rows.append(dict(path=relative(path), blob=row['blob'], runtimeName=name, bundleSha256=sha(path.read_bytes())))
        manifest = dict(bundles=rows, gameVersion=self.game.version)
        (folder/'manifest.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
        return manifest

    def build(self):
        # The player reads plans and bundles from one root: data/.
        shutil.copytree(RUNTIME_SOURCES/'plans', RUNTIME_ASSETS/'plans', dirs_exist_ok=True)
        for folder in ('presentation-player', 'dialog-background-player', 'default-ui-player', 'vignette-player'):
            shutil.rmtree(BUNDLES/folder, ignore_errors=True)
            (BUNDLES/folder).mkdir(parents=True)
        steps = (self.presentation_bundles, self.dialog_background, self.default_ui, self.vignette,
                 self.bloom, self.flip_clips, self.tmp_settings)
        for step in steps:
            step()
            self.log(f'runtime assets: {step.__name__}')


def build_runtime_assets(data_root=PLAYER_DATA, log=print):
    Builder(data=PlayerData(Path(data_root)), log=log).build()
