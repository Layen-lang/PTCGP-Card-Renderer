"""Compile a card into web-cards/<id>/: all a browser needs, without Unity.

card.json holds geometry, parameters, render states, effects and translated shaders.
Textures keep Unity's row order: the first stored row is v=0, nothing is flipped.
Identical files go once in web-cards/_shared/, so a browser caches them across cards.
"""
import concurrent.futures
import ctypes
import gzip
import hashlib
import io
import json
import os
import re
import atexit
import shutil
import subprocess
import tempfile
import time
from pathlib import Path
import numpy as np
from PIL import Image
from UnityPy.enums import TextureFormat
from UnityPy.helpers.MeshHelper import MeshHandler
from common.render import compiler
from common.render.assets import CardAssets
from common.render.gold import design_path
from .paths import CARDS, LANGUAGES, PLAYER, ROOT, RUNTIME_SOURCES, WEB_CARDS
from common.render.immersive import SOURCE_ROOTS
from .particles import SceneBuilder
from .shaders import translate_pass
from .shared import write_shared_assets

FORMAT_VERSION = 2
SHARED = '_shared'
# Results reused across cards and runs. Not part of the packages: it can be deleted.
CACHE = '_cache'
ENVIRONMENTS_KEPT = 16
BUILTIN_QUAD = 10210
QUEUES = dict(BACKGROUND=1000, GEOMETRY=2000, ALPHATEST=2450, TRANSPARENT=3000, OVERLAY=4000)
# Passes CustomDrawObjectsPass draws. A pass without LightMode is SRPDefaultUnlit.
DRAWN_PASSES = {None, 'SRPDefaultUnlit', 'UniversalForward', 'UniversalForwardOnly', 'MultiPass1', 'MultiPass2', 'MultiPass3', 'MultiPass4'}
ENGINE_GLOBALS = {'unity_ObjectToWorld', 'unity_WorldToObject', 'unity_MatrixV', 'unity_MatrixVP', 'glstate_matrix_projection', '_WorldSpaceCameraPos', '_ZBufferParams', '_ProjectionParams', '_ScreenParams', '_Time'}
# Read by CardRGBObject to derive its Oklab vectors.
RGB_COLORS = ('_AreaAColor', '_AreaBColor', '_UIColorR', '_UIColorG')
# Values read from the player: SourceCardMatchedPose camera, studio placement and UICardView sizing.
CAMERA = dict(position=[40, -100, -1.911506], fieldOfView=35, near=.3, far=1000)
STUDIO = [40, -100, 0]
VERTICAL_PERCENTAGE = (.88*.5)/.6026955842971802
CARD_ASPECT = 630/880
MAX_TILT = 30
CARD_HEIGHT_LOGICAL = 880
UI_ORTHOGRAPHIC = json.loads((RUNTIME_SOURCES/'plans/presentation-stack.json').read_text(encoding='utf-8'))['appCamera']['orthographicSize']
OUTLINE_RAMP_FIT = .025
TARGET_SIZE = 1403
# Illustration quality. Small textures and data read as numbers (phases, ramps, masks) stay lossless:
# lossy noise there moves the holograms, it is the largest gap with the game.
DATA_TEXTURE = re.compile(r'phase|rmp|ramp|mask|flow|noise|normal|lut|holo|glitter', re.I)
WEBP_QUALITY = 95
LOSSLESS_PIXELS = 128*128
# Sharpest image first: lossy WebP halves the colour resolution. Turn off to weigh about 2.4 times less.
EVERYTHING_LOSSLESS = True
# Encoding effort, measured: see the bench of build-web. Effort above these barely shrinks the files.
WEBP_METHOD = 2
LOSSLESS_EFFORT = 50
# Encoding is most of an export and runs outside the GIL: each worker encodes on spare cores while its player renders.
TEXT_ENCODERS = 3
# CreateDynamicUIRenderTexture at high quality: 734 x 1024, doubled for the text.
TEXT_SIZE = 1468, 2048
# SourceCardShaderState.ApplyHologramRotation: the detail loader supplies Vector3.zero.
RENDERER_OVERRIDES = dict(_Rotation=[0, 0, 0, 0])
CHANNEL_FIELDS = dict(position='m_Vertices', normal='m_Normals', tangent='m_Tangents', color='m_Colors', uv0='m_UV0', uv1='m_UV1', uv2='m_UV2', uv3='m_UV3')


class Export:
    def __init__(self, card_id, out):
        context = compiler.context
        self.card_id = card_id
        self.out = out
        self.catalog = context.catalog
        kind, card, illustration, prefab, settings, ui, extras = compiler.card_roots(card_id)
        self.kind = kind
        self.prefab_name = illustration+'_L'
        self.prefab = prefab
        # A card with a gold frame also reads its design, which brings the frame's template and materials.
        self.gold = compiler.read_card_json(card_id, 'gold.json.gz') if compiler.read_summary(card_id).get('hasGold') else None
        design = [context.blob(design_path(compiler.gold_design(kind, card_id)))] if self.gold else []
        self.source = CardAssets(context.catalog, [prefab['blob'], *design, *map(context.blob, extras)])
        self.extras = extras
        self.programs, self.program_ids = [], {}
        self.textures, self.texture_ids = [], {}
        self.meshes, self.mesh_ids = [], {}
        self.unset = set()
        self.no_controller = not compiler.read_card_json(card_id, 'effects.json.gz')['effects']

    def unity_object(self, pointer):
        row = self.source.object(pointer)
        return next(o for o in self.catalog.env(row['bundle']).objects if o.path_id == row['id'])

    def memo_path(self, kind, key):
        return WEB_CARDS/CACHE/kind/f'{key}.json'

    def write_image(self, image, lossless):
        return store_shared(webp_bytes(image, lossless), 'webp')

    def write_rgba(self, row, image, lossless):
        """Colour and coverage apart when some pixels are see-through: Safari premultiplies every
        image it decodes, and the RGB under alpha 0, which the shaders read, would be lost."""
        color, alpha = split_alpha(image)
        row['file'] = self.write_image(color, lossless)
        if alpha:
            row['alpha'] = self.write_image(alpha, True)
        row['split'] = True

    def texture(self, pointer, property_name):
        key = pointer['m_PathID']
        if key in self.texture_ids:
            return self.texture_ids[key]
        tree = self.source.tree(pointer)
        name, width, height = tree['m_Name'], tree['m_Width'], tree['m_Height']
        lossless = EVERYTHING_LOSSLESS or width*height <= LOSSLESS_PIXELS or bool(DATA_TEXTURE.search(name)) or bool(DATA_TEXTURE.search(property_name))
        # Frames, backgrounds and ramps come back on thousands of cards: each is encoded once.
        memo = self.memo_path('textures', f"{self.source.object(pointer)['bundle']}-{key}-{int(lossless)}")
        if memo.is_file():
            row = json.loads(retried(lambda: memo.read_text(encoding='utf-8')))
            if 'file' in row and not row.get('format') and not row.get('split'):
                with Image.open(WEB_CARDS/SHARED/row['file'].rsplit('/', 1)[1]) as stored:
                    image = stored.copy()
                if image.mode == 'RGBA':
                    self.write_rgba(row, image, lossless)
                row['split'] = True
                write_atomic(memo, json.dumps(row, ensure_ascii=False))
            self.textures.append(row)
            self.texture_ids[key] = len(self.textures)-1
            return self.texture_ids[key]
        settings = tree['m_TextureSettings']
        row = dict(name=name, width=width, height=height, filter=settings['m_FilterMode'], wrapU=settings['m_WrapU'], wrapV=settings['m_WrapV'])
        data = self.unity_object(pointer).read().get_image_data()
        fmt = TextureFormat(tree['m_TextureFormat'])
        if fmt == TextureFormat.RGBAHalf:
            # WebP cannot hold HDR values: the 16-bit floats are stored with their mips.
            row.update(format='rgba16f', file=store_shared(gzip.compress(data, mtime=0), 'bin.gz'), mips=tree['m_MipCount'])
        elif fmt == TextureFormat.R16:
            row.update(format='r16', file=store_shared(gzip.compress(data[:width*height*2], mtime=0), 'bin.gz'))
        elif fmt == TextureFormat.R8:
            # Stored as gray, sampled through .r: green and blue are not 0 as in Unity.
            row['file'] = self.write_image(Image.frombytes('L', (width, height), data[:width*height]).convert('RGB'), lossless)
        elif fmt not in (TextureFormat.RGBA32, TextureFormat.RGB24):
            raise ValueError(f'Texture format not ported: {fmt.name} ({name})')
        elif tree['m_ImageCount'] == 6:
            row['cube'] = True
            row['files'] = [self.write_image(Image.frombytes('RGBA', (width, height), data[face*width*height*4:(face+1)*width*height*4]), True) for face in range(6)]
        else:
            mode = 'RGBA' if fmt == TextureFormat.RGBA32 else 'RGB'
            image = Image.frombytes(mode, (width, height), data[:width*height*len(mode)])
            if mode == 'RGBA':
                self.write_rgba(row, image, lossless)
            else:
                row['file'] = self.write_image(image, lossless)
        row['split'] = True
        write_atomic(memo, json.dumps(row, ensure_ascii=False))
        self.textures.append(row)
        self.texture_ids[key] = len(self.textures)-1
        return self.texture_ids[key]

    def mesh(self, pointer):
        key = pointer['m_PathID']
        if key in self.mesh_ids:
            return self.mesh_ids[key]
        if key == BUILTIN_QUAD:
            # Unity's built-in Quad, referenced outside the bundles.
            arrays = dict(position=np.array([[-.5, -.5, 0], [.5, .5, 0], [.5, -.5, 0], [-.5, .5, 0]], np.float32), normal=np.tile(np.array([0, 0, -1, 0], np.float32), (4, 1)),
                          tangent=np.tile(np.array([1, 0, 0, -1], np.float32), (4, 1)), uv0=np.array([[0, 0], [1, 1], [1, 0], [0, 1]], np.float32))
            self.meshes.append(dict(name='Quad', arrays=arrays, indices=[0, 1, 2, 1, 0, 3], submeshes=[[0, 6]], center=[0, 0, 0]))
            self.mesh_ids[key] = len(self.meshes)-1
            return self.mesh_ids[key]
        handler = MeshHandler(self.unity_object(pointer).read())
        handler.process()
        arrays = {}
        for channel, field in CHANNEL_FIELDS.items():
            values = getattr(handler, field, None)
            if values:
                arrays[channel] = np.asarray(values, dtype=np.float32)
        # UnityPy gives UNorm8 colors as bytes; the shaders read them from 0 to 1.
        if 'color' in arrays and arrays['color'].max() > 1:
            arrays['color'] /= 255
        tree = self.source.tree(pointer)
        submeshes = []
        indices = np.asarray(handler.m_IndexBuffer, dtype=np.uint32)
        for sub in tree['m_SubMeshes']:
            if sub['topology'] != 0:
                raise ValueError('Mesh topology not ported')
            first = sub['firstByte']//(2 if tree['m_IndexFormat'] == 0 else 4)
            submeshes.append([first, sub['indexCount']])
        aabb = tree['m_LocalAABB']['m_Center']
        self.meshes.append(dict(name=tree['m_Name'], arrays=arrays, indices=indices.tolist(), submeshes=submeshes, center=[aabb['x'], aabb['y'], aabb['z']]))
        self.mesh_ids[key] = len(self.meshes)-1
        return self.mesh_ids[key]

    def program(self, shader_pointer, shader, pass_index, program, keywords):
        key = shader_pointer['m_PathID'], pass_index, tuple(sorted(keywords))
        if key not in self.program_ids:
            memo = self.memo_path('programs', f"{self.source.object(shader_pointer)['bundle']}-{shader_pointer['m_PathID']}-{pass_index}-{'_'.join(map(str, sorted(keywords)))}")
            if memo.is_file():
                translated = json.loads(retried(lambda: memo.read_text(encoding='utf-8')))
            else:
                translated = translate_pass(shader, program, keywords)
                translated['name'] = shader['m_ParsedForm']['m_Name']
                write_atomic(memo, json.dumps(translated, ensure_ascii=False))
            self.programs.append(translated)
            self.program_ids[key] = len(self.programs)-1
        return self.program_ids[key]

    def material_draws(self, material, node_matrix, mesh, submesh, tree_order, surface, renderer, slot):
        """One draw per drawn pass of a material, in the pass order of the shader."""
        shader_pointer = material['m_Shader']
        shader = self.source.tree(shader_pointer)
        form = shader['m_ParsedForm']
        sub_shader = form['m_SubShaders'][0]
        queue = material['m_CustomRenderQueue']
        if queue < 0:
            queue = shader_queue(sub_shader)
        values = material_values(form, material)
        # SourceCardEffects.Attach, fitted on the real game: cards without a controller read this ramp further.
        if self.no_controller and form['m_Name'].endswith('ShadowBox/Opaque-Hologram_Tuning'):
            values['numbers']['_RampOffset'] += OUTLINE_RAMP_FIT
        keyword_names = form['m_KeywordNames']
        # A material's "invalid" keywords resolve as soon as the shader declares them.
        active = set(material['m_ValidKeywords']) | set(material['m_InvalidKeywords'])
        keywords = {i for i, name in enumerate(keyword_names) if name in active}
        disabled = {name.lower() for name in material.get('disabledShaderPasses', [])}
        draws = []
        for pass_index, program in enumerate(sub_shader['m_Passes']):
            light_mode = pass_tags(program).get('LIGHTMODE')
            if light_mode not in DRAWN_PASSES or (light_mode or '').lower() in disabled:
                continue
            program_id = self.program(shader_pointer, shader, pass_index, program, keywords)
            textures = {}
            uniforms = {}
            for name in self.programs[program_id]['names']:
                if name in values['textures']:
                    binding = values['textures'][name]
                    if surface and name in surface:
                        textures[name] = f'dynamic{surface[name]}'
                    else:
                        textures[name] = self.texture(binding['pointer'], name) if binding['pointer'] else binding['default']
                elif name in RENDERER_OVERRIDES:
                    uniforms[name] = RENDERER_OVERRIDES[name]
                elif name in values['numbers']:
                    uniforms[name] = values['numbers'][name]
                elif name in self.programs[program_id]['samplers']:
                    # A controller may bind it through its renderer; Unity's unset texture is gray.
                    textures[name] = 'gray'
                elif name not in ENGINE_GLOBALS:
                    # Neither a property nor a known global: zero in Unity too, unless a controller sets it.
                    self.unset.add(f"{form['m_Name']}:{name}")
            draws.append(dict(queue=queue, order=tree_order, mesh=mesh, submesh=submesh, program=program_id, model=node_matrix, renderer=renderer, slot=slot,
                              uniforms=uniforms, textures=textures, state=render_state(program['m_State'], values['numbers'])))
        return draws

    def build(self, languages):
        rows = self.source.rows_of(self.prefab['blob'])
        gold = self.gold
        nodes = rows + (self.source.rows_of(gold['template']['bundle']) if gold else [])
        objects = {row['id']: row for row in nodes}
        trees = {row['id']: self.source.tree({'m_PathID': row['id']}) for row in nodes if row['type'] in ('Transform', 'GameObject', 'MeshRenderer', 'MeshFilter')}
        root = next(row['id'] for row in rows if row['type'] == 'Transform' and not trees[row['id']]['m_Father']['m_PathID'])
        binding_rows = compiler.read_card_json(self.card_id, 'bindings.json.gz')['bindings']
        # A renderer can hold several dynamic textures, each under its own property.
        surfaces = {}
        for row in binding_rows:
            surfaces.setdefault(int(row['rendererId']), {})[row['property']] = row['type']
        effects = compiler.read_card_json(self.card_id, 'effects.json.gz')
        for effect in effects['effects']:
            for curve in effect.get('curves', ()):
                curve.pop('nativeJson', None)
        # CircularKiraObject plays its trail on B's mesh instance, shared with A (original playing path).
        trail_meshes = {}
        for effect in effects['effects']:
            if effect['kind'] == 'CircularKiraObject' and effect.get('trailKiraAMeshFilterPath') and effect.get('trailKiraBMeshFilterPath'):
                trail_meshes[tuple(effect['trailKiraAMeshFilterPath']['indices'])] = tuple(effect['trailKiraBMeshFilterPath']['indices'])
        draws = []
        order = [0]

        renderers = []
        node_filters = {}
        node_matrices = {}
        root_matrix = []
        order_rows = []
        particle_systems = sum(row['type'] == 'ParticleSystem' for row in rows)

        def walk(i, parent, active, path):
            transform = trees[i]
            node = trees[transform['m_GameObject']['m_PathID']]
            # LoadFace replaces the prefab root's rotation (SourceCardTouch).
            matrix = parent @ local_matrix(dict(transform, m_LocalRotation=dict(x=0, y=0, z=0, w=1)) if not path else transform)
            node_matrices[tuple(path)] = matrix.T.flatten().tolist()
            if not path:
                root_matrix.append(matrix)
            active = active and bool(node['m_IsActive'])
            components = [objects.get(c['component']['m_PathID']) for c in node['m_Component']]
            filter_row = next((c for c in components if c and c['type'] == 'MeshFilter'), None)
            renderer = next((c for c in components if c and c['type'] == 'MeshRenderer'), None)
            if filter_row:
                node_filters[tuple(path)] = trees[filter_row['id']]['m_Mesh']
            if renderer and filter_row and active and trees[renderer['id']]['m_Enabled']:
                order_rows.append((renderer, filter_row, matrix.T.flatten().tolist(), path))
            for number, child in enumerate(transform['m_Children']):
                walk(child['m_PathID'], matrix, active, [*path, number])
        walk(root, np.identity(4), True, [])
        # SourceAdditionalGoldFrame: the template becomes the card's last child, and hides the
        # first direct child that bears each of its own direct children's names.
        hidden, gold_index, gold_surfaces, panel = set(), None, {}, None
        if gold:
            names = [trees[trees[c['m_PathID']]['m_GameObject']['m_PathID']]['m_Name'] for c in trees[root]['m_Children']]
            gold_index = len(names)
            template = trees[int(gold['template']['sourceId'])]
            template_transform = next(c['component']['m_PathID'] for c in template['m_Component'] if objects.get(c['component']['m_PathID'], {}).get('type') == 'Transform')
            for child in trees[template_transform]['m_Children']:
                name = trees[trees[child['m_PathID']]['m_GameObject']['m_PathID']]['m_Name']
                if name in names:
                    hidden.add(names.index(name))
            walk(template_transform, root_matrix[0], True, [gold_index])
            for row in gold['bindings']:
                gold_surfaces.setdefault((gold_index, *row['indices']), {})[row['property']] = row['type']
            # Renderer.material replaces the first slot, only when the design overrides it.
            if gold['panelMaterialOverride']:
                panel = (gold_index, *gold['panelIndices']), {'m_PathID': int(gold['panelMaterial']['sourceId'])}
        for effect in effects['effects']:
            effect['node'] = node_matrices[tuple(effect['indices'])]
        for renderer, filter_row, model, path in order_rows:
            pointer_mesh = trees[filter_row['id']]['m_Mesh']
            if tuple(path) in trail_meshes:
                pointer_mesh = node_filters[trail_meshes[tuple(path)]]
            mesh = self.mesh(pointer_mesh)
            index = len(renderers)
            row = dict(path=path, model=model, mesh=mesh, tags=set(), colors=[])
            top = path[0] if path else None
            if gold and top == gold_index:
                row['variant'] = 'gold'
            elif top in hidden:
                row['variant'] = 'base'
            surface = gold_surfaces.get(tuple(path)) if row.get('variant') == 'gold' else surfaces.get(renderer['id'])
            for slot, pointer in enumerate(trees[renderer['id']]['m_Materials']):
                if panel and slot == 0 and tuple(path) == panel[0]:
                    pointer = panel[1]
                if not pointer['m_PathID']:
                    continue
                material = self.source.tree(pointer)
                shader = self.source.tree(material['m_Shader'])
                for program in shader['m_ParsedForm']['m_SubShaders'][0]['m_Passes']:
                    tag = pass_tags(program).get('SEARCHTAG')
                    if tag:
                        row['tags'].add(tag)
                numbers = material_values(shader['m_ParsedForm'], material)['numbers']
                row['colors'].append({name: numbers[name] for name in RGB_COLORS if name in numbers})
                order[0] += 1
                draws.extend(self.material_draws(material, model, mesh, min(slot, len(self.meshes[mesh]['submeshes'])-1), order[0], surface, index, slot))
            row['tags'] = sorted(row['tags'])
            renderers.append(row)
        # The queue first; the engine sorts the transparent queues back to front each frame (CommonTransparent).
        draws.sort(key=lambda draw: (draw['queue'], draw['order']))
        for draw in draws:
            del draw['order']
        for program in self.programs:
            del program['names'], program['samplers']
        # The back and the edge cannot be seen before a flip: they load after the first frame.
        front_textures = {v for draw in draws if not self.programs[draw['program']]['name'].endswith('Side&Back') for v in draw['textures'].values()}
        for index, row in enumerate(self.textures):
            row['lazy'] = index not in front_textures
        used = {}
        for draw in draws:
            used.setdefault(draw['mesh'], set()).update(self.programs[draw['program']]['attributes'].values())
        meshes = []
        for index, mesh in enumerate(self.meshes):
            meshes.append(mesh_row(mesh, used.get(index, ())))
        text = export_text(self.card_id, self.out, languages, sorted({row['type'] for row in binding_rows}))
        names = dict(zip(LANGUAGES, compiler.read_summary(self.card_id)['displayNames']))
        extra = {}
        if gold:
            # The whole card UI is composed again with the gold design.
            types = sorted({row['type'] for row in binding_rows} | {row['type'] for row in gold['bindings']})
            extra['goldText'] = export_text(self.card_id, self.out, languages, types, gold=True)
        return dict(version=FORMAT_VERSION, id=self.card_id, names={k: names[k] for k in languages}, **extra,
                    camera=CAMERA, studio=STUDIO, verticalPercentage=VERTICAL_PERCENTAGE, cardAspect=CARD_ASPECT,
                    maxTilt=MAX_TILT, targetSize=TARGET_SIZE, cardHeightLogical=CARD_HEIGHT_LOGICAL, uiOrthographic=UI_ORTHOGRAPHIC, dynamicText=text, unset=sorted(self.unset),
                    effects=effects['effects'], emissive=bool(effects['emissive']), unportedParticleSystems=particle_systems, renderers=renderers,
                    programs=[program_row(program) for program in self.programs], textures=self.textures, meshes=meshes, draws=draws)


def gzip_shared(data, extension):
    return store_shared(gzip.compress(data, 9, mtime=0), extension)


def program_row(program):
    """Identical programs come back on thousands of cards: one cached file each."""
    source = {key: program[key] for key in ('name', 'vertex', 'fragment', 'attributes')}
    return dict(file=gzip_shared(json.dumps(source, ensure_ascii=False, separators=(',', ':')).encode(), 'program.gz'), name=program['name'], attributes=program['attributes'])


def mesh_row(mesh, channels):
    """Vertices as little-endian floats one channel after the other, then 32-bit indices."""
    arrays = {channel: values for channel, values in mesh['arrays'].items() if channel in channels}
    data = b''.join(values.astype('<f4').tobytes() for values in arrays.values())+np.asarray(mesh['indices'], '<u4').tobytes()
    return dict(name=mesh['name'], file=gzip_shared(data, 'mesh.gz'), vertices={channel: values.shape[1] for channel, values in arrays.items()},
                vertexCount=len(next(iter(arrays.values()))) if arrays else 0, indexCount=len(mesh['indices']), submeshes=mesh['submeshes'], center=mesh['center'])


def split_alpha(image):
    """RGB, and the alpha as gray when it is not opaque everywhere."""
    alpha = image.getchannel('A')
    if alpha.getextrema()[0] == 255:
        return image.convert('RGB'), None
    return image.convert('RGB'), alpha.convert('RGB')


def webp_bytes(image, lossless):
    # exact: the RGB of fully transparent pixels matters, the shaders read colour and alpha apart.
    options = dict(lossless=True, quality=LOSSLESS_EFFORT) if lossless or EVERYTHING_LOSSLESS else dict(quality=WEBP_QUALITY, alpha_quality=100)
    stream = io.BytesIO()
    image.save(stream, 'WEBP', method=WEBP_METHOD, exact=True, **options)
    return stream.getvalue()


def retried(action):
    """Windows refuses a file another worker or the antivirus has open for a moment."""
    for attempt in range(40):
        try:
            return action()
        except PermissionError:
            if attempt == 39:
                raise
            time.sleep(.05)


def move_once(temporary, path):
    """Same content from every writer: if another worker got there first, its file stays."""
    try:
        retried(lambda: temporary.replace(path))
    except PermissionError:
        if not path.exists():
            raise
        temporary.unlink(missing_ok=True)


def write_atomic(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f'{path.name}.{os.getpid()}.tmp')
    temporary.write_text(text, encoding='utf-8')
    move_once(temporary, path)


def store_shared(data, extension):
    """Write a file once under its content hash; the path is relative to a card's folder."""
    name = f'{hashlib.sha256(data).hexdigest()[:24]}.{extension}'
    folder = WEB_CARDS/SHARED
    folder.mkdir(parents=True, exist_ok=True)
    path = folder/name
    if not path.exists():
        temporary = folder/f'{name}.{os.getpid()}.tmp'
        temporary.write_bytes(data)
        move_once(temporary, path)
    return f'../{SHARED}/{name}'


def shortest(values):
    """Float32 values in their shortest exact decimal form."""
    return [float(np.format_float_positional(v, unique=True, trim='0', precision=None)) for v in values.astype(np.float32).flatten()]


def pass_tags(program):
    """A pass's tags live in its state; names are kept as authored or uppercased, depending on the tag."""
    return {name.upper(): value for name, value in [*program['m_Tags']['tags'], *program['m_State']['m_Tags']['tags']]}


def local_matrix(transform):
    """Unity's TRS, same formula as Matrix4x4.TRS."""
    p, r, s = transform['m_LocalPosition'], transform['m_LocalRotation'], transform['m_LocalScale']
    x, y, z, w = r['x'], r['y'], r['z'], r['w']
    rotation = np.array([
        [1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w), 0],
        [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w), 0],
        [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y), 0],
        [0, 0, 0, 1]])
    scale = np.diag([s['x'], s['y'], s['z'], 1])
    translate = np.identity(4)
    translate[:3, 3] = [p['x'], p['y'], p['z']]
    return translate @ rotation @ scale


def shader_queue(sub_shader):
    tag = dict(sub_shader['m_Tags']['tags']).get('QUEUE', 'Geometry').upper()
    for name, base in QUEUES.items():
        if tag.startswith(name):
            return base+int(tag[len(name):] or 0)
    raise ValueError(f'Unknown render queue: {tag}')


def material_values(form, material):
    """The material's properties over the shader's defaults, as the engine will set them."""
    numbers, textures = {}, {}
    for prop in form['m_PropInfo']['m_Props']:
        name, kind = prop['m_Name'], prop['m_Type']
        if kind == 4:
            textures[name] = dict(pointer=None, default=prop['m_DefTexture']['m_DefaultName'] or 'gray')
            numbers[name+'_ST'] = [1, 1, 0, 0]
        elif kind in (2, 3):
            numbers[name] = prop['m_DefValue[0]']
        else:
            numbers[name] = [prop[f'm_DefValue[{i}]'] for i in range(4)]
    saved = material['m_SavedProperties']
    for name, value in saved['m_Floats']:
        numbers[name] = value
    for name, value in saved.get('m_Ints', []):
        numbers[name] = value
    for name, value in saved['m_Colors']:
        numbers[name] = [value['r'], value['g'], value['b'], value['a']]
    for name, value in saved['m_TexEnvs']:
        pointer = value['m_Texture'] if value['m_Texture']['m_PathID'] else None
        textures[name] = dict(pointer=pointer, default=textures.get(name, {}).get('default', 'gray'))
        numbers[name+'_ST'] = [value['m_Scale']['x'], value['m_Scale']['y'], value['m_Offset']['x'], value['m_Offset']['y']]
    return dict(numbers=numbers, textures=textures)


def render_state(state, numbers):
    """Fixed values and material properties resolved: every value is the Unity enum."""
    def value(field):
        return int(numbers.get(field['name'], field['val'])) if field['name'] != '<noninit>' else int(field['val'])

    def stencil(side):
        return dict(comp=value(side['comp']), pass_=value(side['pass']), fail=value(side['fail']), zFail=value(side['zFail']))
    blend = state['rtBlend0']
    sided = any(int(state['stencilOpFront'][k]['val']) != d or int(state['stencilOpBack'][k]['val']) != d for k, d in (('pass', 0), ('fail', 0), ('zFail', 0), ('comp', 8)))
    return dict(cull=value(state['culling']), zTest=value(state['zTest']), zWrite=value(state['zWrite']),
                blend=[value(blend[k]) for k in ('srcBlend', 'destBlend', 'srcBlendAlpha', 'destBlendAlpha')],
                blendOp=[value(blend['blendOp']), value(blend['blendOpAlpha'])], colorMask=value(blend['colMask']),
                stencilRef=value(state['stencilRef']), stencilReadMask=value(state['stencilReadMask']), stencilWriteMask=value(state['stencilWriteMask']),
                stencilFront=stencil(state['stencilOpFront'] if sided else state['stencilOp']),
                stencilBack=stencil(state['stencilOpBack'] if sided else state['stencilOp']))


class TextService:
    """One player per worker process, kept running: starting Unity per card took 5 s."""
    # Unity's dynamic font atlases and open bundles grow with each job.
    RESTART_AFTER = 300
    JOB_TIMEOUT = 120

    def __init__(self):
        self.folder = Path(tempfile.mkdtemp(prefix='web-text-'))
        self.process = None
        self.jobs = 0
        atexit.register(self.close)

    def start(self):
        if os.name == 'nt':
            # A crashing player would wait on Windows' error box, and the export on it for two minutes:
            # without it, the player ends at once and the next job starts another.
            ctypes.windll.kernel32.SetErrorMode(0x0001 | 0x0002 | 0x8000)
        self.process = subprocess.Popen([str(PLAYER), '-batchmode', '-force-vulkan', '--serve-dynamic-ui', str(self.folder), '--quality', 'High',
                                         '--project-root', str(ROOT), '-logFile', str(self.folder/'player.log')])
        self.jobs = 0

    def close(self):
        if self.process and self.process.poll() is None:
            (self.folder/'job.txt.tmp').write_text('quit\n', encoding='utf-8')
            (self.folder/'job.txt.tmp').replace(self.folder/'job.txt')
            try:
                self.process.wait(10)
            except subprocess.TimeoutExpired:
                self.process.kill()
        shutil.rmtree(self.folder, ignore_errors=True)

    def render(self, card_id, language, types, gold=False):
        if self.process is None or self.process.poll() is not None or self.jobs >= self.RESTART_AFTER:
            if self.process and self.process.poll() is None:
                self.process.kill()
                self.process.wait()
            self.start()
        self.jobs += 1
        done = self.folder/'done.txt'
        retried(lambda: done.unlink(missing_ok=True))
        job = self.folder/'job.txt.tmp'
        job.write_text('\n'.join((card_id, language, str(self.folder), ','.join(map(str, types)), *(['gold'] if gold else []))), encoding='utf-8')
        retried(lambda: job.replace(self.folder/'job.txt'))
        limit = time.time()+self.JOB_TIMEOUT
        while not done.exists():
            if time.time() > limit or self.process.poll() is not None:
                self.process.kill()
                self.process = None
                raise RuntimeError(f'The player stopped answering: {card_id} {language}')
            time.sleep(.005)
        result = retried(lambda: done.read_text(encoding='utf-8'))
        if result != 'ok':
            raise RuntimeError(result[:300])
        return {kind: self.folder/f'dynamic-ui-{kind}.rgba' for kind in types}


_service = None
_encoders = None


def text_is_current(path, card_id, language, gold=False):
    """Rendered text is kept while it is lossless and newer than what it depends on."""
    try:
        with path.open('rb') as stream:
            if stream.read(16)[12:] != b'VP8L':
                return False
        made = path.stat().st_mtime
        # The card's zip holds its UI plans and bindings: the text is current while newer than it.
        return all(made >= source.stat().st_mtime for source in (PLAYER, compiler.card_archive(card_id)))
    except OSError:
        return False


def alpha_path(path):
    return path.with_name(path.name.replace('.webp', '.alpha.webp'))


def encode_text(image, path):
    color, alpha = split_alpha(image)
    path.write_bytes(webp_bytes(color, EVERYTHING_LOSSLESS))
    alpha_path(path).write_bytes(webp_bytes(alpha or Image.new('RGB', image.size, (255, 255, 255)), True))


def split_text(source, path):
    with Image.open(source) as image:
        encode_text(image.convert('RGBA'), path)


def export_text(card_id, out, languages, types, gold=False):
    """The composed card UI, per language and dynamic UI type, from the Unity player; gold: with the gold frame's design."""
    global _service, _encoders
    _encoders = _encoders or concurrent.futures.ThreadPoolExecutor(TEXT_ENCODERS)
    folder = out/'text'
    folder.mkdir(exist_ok=True)
    previous = WEB_CARDS/card_id/'text'
    prefix = 'gold-' if gold else ''
    rows, jobs = {}, []
    for language in languages:
        names = {kind: f'{prefix}{language}-{kind}.webp' for kind in types}
        rows[language] = {str(kind): dict(file=f'text/{name}', alpha=f"text/{alpha_path(Path(name)).name}", width=TEXT_SIZE[0], height=TEXT_SIZE[1]) for kind, name in names.items()}
        if all(text_is_current(previous/name, card_id, language, gold) for name in names.values()):
            for name in names.values():
                if alpha_path(previous/name).is_file():
                    os.link(previous/name, folder/name)
                    os.link(alpha_path(previous/name), alpha_path(folder/name))
                else:
                    jobs.append(_encoders.submit(split_text, previous/name, folder/name))
            continue
        _service = _service or TextService()
        try:
            paths = _service.render(card_id, language, types, gold)
        except RuntimeError:
            # A player can die after hundreds of cards: a new one finishes the job.
            paths = _service.render(card_id, language, types, gold)
        for kind, path in paths.items():
            # The raw texture's first row is v=0, as the engine samples it.
            image = Image.frombytes('RGBA', TEXT_SIZE, path.read_bytes())
            jobs.append(_encoders.submit(encode_text, image, folder/names[kind]))
    for job in jobs:
        job.result()
    return rows


def export_movie(card_id, out):
    """The IM movie and its hold timing. The browser plays the original H.264 stream as it is."""
    movie = compiler.card_movie(card_id)
    if not movie.is_file():
        return None
    plan = compiler.read_card_json(card_id, 'immersive.json.gz')
    # A hard link: the film already exists on this disk, 18 MB per IM card.
    os.link(movie, out/'movie.mp4')
    write_immersive_scene(card_id)
    return dict(file='movie.mp4', holdStart=plan['holdStart'], holdDuration=plan['holdDuration'], rectSize=plan['movieRectSize'], uv=plan['movieUv'], endFramesRemaining=plan['endFramesRemaining'],
                cancelDuration=plan['cancelDuration'], movieStart=plan['movieStart'], start=plan['start'], end=plan['end'], startParticles=plan['startParticles'],
                endParticles=plan['endParticles'], effectAnimations=plan['effectAnimations'])


def scene_package(export, scenes):
    """Scenes with the programs, textures and meshes they share; textures load from a card's folder."""
    for program in export.programs:
        program.pop('names', None)
        program.pop('samplers', None)
    meshes = [dict(name=m['name'], vertices={c: dict(size=v.shape[1], data=shortest(v)) for c, v in m['arrays'].items() if c in ('position', 'uv0')}, indices=m['indices'], submeshes=m['submeshes']) for m in export.meshes]
    return dict(programs=export.programs, textures=export.textures, meshes=meshes, **scenes)


def transform_of(export, name):
    row = export.source.named('GameObject', name)
    tree = export.source.tree({'m_PathID': row['id']})
    return next(c['component'] for c in tree['m_Component'] if export.source.object(c['component'])['type'] in ('Transform', 'RectTransform'))


# Raised when the IM scene's content changes, so an existing data folder rebuilds it.
# 2: the Battle particle materials carry _IsTransparent.
IMMERSIVE_SCENE_VERSION = 2


def write_immersive_scene(card_id):
    """The timeline prefabs are the same for all IM cards: one file, once."""
    path = WEB_CARDS/SHARED/'immersive-scene.json'
    if path.is_file():
        try:
            if json.loads(path.read_text(encoding='utf-8')).get('version') == IMMERSIVE_SCENE_VERSION:
                return
        except ValueError:
            pass
    export = Export(card_id, None)
    export.source = CardAssets(export.catalog, [export.prefab['blob'], *map(compiler.context.blob, SOURCE_ROOTS)])
    builder = SceneBuilder(export, material_values, render_state, local_matrix)
    plans = compiler.read_card_json(card_id, 'immersive.json.gz')['materials']
    scenes = {root: builder.scene(transform_of(export, name), root, plans) for root, name in (('ui', 'ImmersiveUITimelines'), ('model', 'ImmersiveModelTimelines'))}
    write_atomic(path, json.dumps(dict(version=IMMERSIVE_SCENE_VERSION, **scene_package(export, scenes)), ensure_ascii=False, separators=(',', ':')))


def card_particles(card_id, out):
    """Systems inside the card's own prefab, the AN sand."""
    export = Export(card_id, None)
    export.source = CardAssets(export.catalog, [export.prefab['blob'], *map(compiler.context.blob, export.extras), compiler.context.blob('Common/Shader/Common/Vfx/CardPartsEffect.shader_bundles')])
    builder = SceneBuilder(export, material_values, render_state, local_matrix)
    name = export.prefab_name
    plans = builder.materials(builder.location_of({name: 'model'}))
    return scene_package(export, dict(model=builder.scene(transform_of(export, name), 'model', plans)))


def export_card(card_id, languages):
    out = WEB_CARDS/card_id
    temporary = out.with_name(out.name+'.tmp')
    shutil.rmtree(temporary, ignore_errors=True)
    temporary.mkdir(parents=True)
    try:
        package = Export(card_id, temporary).build(languages)
        package['immersive'] = export_movie(card_id, temporary)
        if package['unportedParticleSystems']:
            (temporary/'particles.json').write_text(json.dumps(card_particles(card_id, temporary), ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
            package['particles'] = True
        del package['unportedParticleSystems']
        (temporary/'card.json').write_text(json.dumps(package, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
        shutil.rmtree(out, ignore_errors=True)
        compiler.move_into_place(temporary, out)
    except Exception:
        shutil.rmtree(temporary, ignore_errors=True)
        raise
    return package_size(out)


def variant_view(package, gold):
    """What the engine loads for one look of the card (variantView in engine.js)."""
    variant = 'gold' if gold and 'goldText' in package else 'base'
    draws = [draw for draw in package['draws'] if package['renderers'][draw['renderer']].get('variant', variant) == variant]
    return dict(text=package['goldText' if variant == 'gold' else 'dynamicText'], programs={d['program'] for d in draws}, meshes={d['mesh'] for d in draws},
                textures={v for d in draws for v in d['textures'].values() if isinstance(v, int)})


def package_size(folder):
    """Bytes before the card shows, once one language is chosen, then the back that a flip needs.
    The gold frame's look is measured apart, when the card has one."""
    manifest = (folder/'card.json').read_bytes()
    package = json.loads(manifest)
    size = lambda name: (folder/name).stat().st_size

    def measure(gold):
        view = variant_view(package, gold)
        first = max(sum(size(row['file'])+size(row['alpha']) for row in rows.values()) for rows in view['text'].values())
        first += sum(size(row['file']) for key in ('programs', 'meshes') for i, row in enumerate(package[key]) if i in view[key])
        if package.get('particles'):
            first += size('particles.json')
        lazy = 0
        for i, row in enumerate(package['textures']):
            if i not in view['textures']:
                continue
            total = sum(size(name) for name in row.get('files', [row.get('file')]))+(size(row['alpha']) if row.get('alpha') else 0)
            if row.get('lazy'):
                lazy += total
            else:
                first += total
        return first, lazy
    first, lazy = measure(False)
    result = dict(firstFrameBytes=first+len(manifest), firstFrameGzipBytes=first+len(gzip.compress(manifest)), lazyBytes=lazy, languages=len(package['dynamicText']))
    if 'goldText' in package:
        result['goldFirstFrameBytes'] = measure(True)[0]+len(manifest)
    return result


def export_worker(card_id, languages, force):
    try:
        if not force and (WEB_CARDS/card_id/'card.json').is_file():
            return card_id, None, None
        return card_id, export_card(card_id, languages), None
    except Exception as error:
        shutil.rmtree(WEB_CARDS/(card_id+'.tmp'), ignore_errors=True)
        return card_id, None, f'{type(error).__name__}: {error}'
    finally:
        # Shader trees hold their whole blob: a worker keeps nothing between cards but the disk caches.
        compiler.context.catalog.trees.clear()
        while len(compiler.context.catalog.envs) > ENVIRONMENTS_KEPT:
            compiler.context.catalog.envs.popitem(last=False)


def write_catalog():
    """What web/gallery.html filters on: extensions, evolution families, types and rarities."""
    compiler.init_worker()
    game = compiler.context.game
    master = game.locale('fr_FR')['Master.bytes']
    pokemon = {row['PokemonID']: row for row in game.table('Pokemon')}
    previous = {row['CharacterID']: row['PreevolvedCharacterID'] or row['MegaEvolutionPreevolvedCharacterID'] for row in pokemon.values()}
    names = {row['CharacterID']: row['DisplayNameMSID'] for row in game.table('Character')}

    def family(character):
        seen = set()
        while previous.get(character) and character not in seen:
            seen.add(character)
            character = previous[character]
        return re.sub(r'\[.*?\]', '', master.get(names.get(character), character)).replace('\n', '').strip()
    extensions = [dict(id=row['ExpansionID'], name=master.get(row['LongNameMSID'], row['ExpansionID'])) for row in game.table('Expansion')]
    order = {row['id']: i for i, row in enumerate(extensions)}
    numbers = {}
    for row in game.table('ExpansionCollectionNumber'):
        numbers.setdefault(row['CardID'], []).append(dict(extension=row['ExpansionID'], number=row['CollectionNumber']))
    source = {row['CardID']: pokemon.get(row['PokemonID']) for row in game.table('PokemonCard')}
    cards = []
    for card_id, row in json.loads((CARDS/'index.json').read_text(encoding='utf-8'))['cards'].items():
        if not (WEB_CARDS/card_id/'card.json').is_file():
            continue
        owner = source.get(card_id)
        places = sorted(numbers.get(card_id, []), key=lambda place: order.get(place['extension'], len(order)))
        cards.append(dict(id=card_id, rarity=row['rarity'], kind=row['kind'], names=dict(zip(row['languages'], row['displayNames'])), places=places,
                          types=owner['PokemonTypes'] if owner else [], family=family(owner['CharacterID']) if owner else None, immersive=row['hasImmersive'],
                          **({'gold': True} if b'"goldText"' in (WEB_CARDS/card_id/'card.json').read_bytes() else {})))
    cards.sort(key=lambda c: (order.get(c['places'][0]['extension'], len(order)), c['places'][0]['number']) if c['places'] else (len(order), 0))
    write_atomic(WEB_CARDS/'catalog.json', json.dumps(dict(extensions=extensions, cards=cards), ensure_ascii=False, separators=(',', ':')))
    return len(cards)


# A worker holds up to about 0.9 GB and its player up to 0.6 GB. Short of memory, players crashed.
EXPORT_WORKER_MEMORY = 1.5*2**30


def export_workers():
    """As many processes as free memory allows, at most 8: encoding the text goes no faster past that."""
    cores = max(1, (os.cpu_count() or 2)-1)
    free = compiler.available_memory()
    return max(1, min(8, cores, int(free // EXPORT_WORKER_MEMORY))) if free else min(cores, 4)


def build_web(card_ids=None, languages=LANGUAGES, log=print, workers=None, force=True):
    """Parallel and resumable: without force, cards already exported are left alone."""
    if not card_ids:
        compiler.init_worker()
        card_ids = compiler.all_cards(compiler.context.game)
    workers = workers or export_workers()
    log(f'{workers} worker processes')
    failures = {}
    with concurrent.futures.ProcessPoolExecutor(workers, initializer=compiler.init_worker) as pool:
        jobs = [pool.submit(export_worker, card_id, tuple(languages), force) for card_id in card_ids]
        for done, job in enumerate(concurrent.futures.as_completed(jobs), 1):
            card_id, size, error = job.result()
            if error:
                failures[card_id] = error
            elif size:
                log(done, len(jobs), card_id, size)
    # A player can still stop on a card when memory runs short: one more try, with fewer processes.
    if failures:
        log(f'{len(failures)} cards failed, trying them again')
        with concurrent.futures.ProcessPoolExecutor(max(1, workers//2), initializer=compiler.init_worker) as pool:
            for card_id, size, error in pool.map(export_worker, list(failures), [tuple(languages)]*len(failures), [force]*len(failures)):
                if error:
                    failures[card_id] = error
                else:
                    del failures[card_id]
    write_shared_assets()
    write_catalog()
    (WEB_CARDS/'failures.json').write_text(json.dumps(failures, ensure_ascii=False, indent=1), encoding='utf-8')
    return failures
