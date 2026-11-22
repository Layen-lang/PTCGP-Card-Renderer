"""Particle scenes for the browser: the node tree, its Shuriken systems and the materials PropertyCustomizer builds at runtime."""
import numpy as np
from common.render.immersive import customizer_materials
from .shaders import translate_pass

# Serialized fields the simulator reads; the disabled modules go away.
MODULES = ('InitialModule', 'ShapeModule', 'EmissionModule', 'SizeModule', 'RotationModule', 'ColorModule', 'VelocityModule',
           'InheritVelocityModule', 'ClampVelocityModule', 'NoiseModule', 'CustomDataModule', 'SubModule', 'ForceModule', 'UVModule')
EMPTY_MATERIAL = dict(m_SavedProperties=dict(m_Floats=[], m_Colors=[], m_TexEnvs=[]))


def script_of(assets, tree):
    pointer = tree.get('m_Script', {})
    return assets.tree(pointer).get('m_ClassName', '') if pointer.get('m_PathID') else ''


class SceneBuilder:
    """Works on an Export: it shares its texture, mesh and program stores."""

    def __init__(self, export, material_values, render_state, local_matrix):
        self.export = export
        self.assets = export.source
        self.material_values = material_values
        self.render_state = render_state
        self.local_matrix = local_matrix
        self.gradients = []

    def location_of(self, roots):
        """Path of a GameObject below one of the named roots; None elsewhere."""
        assets = self.assets

        def location(go):
            if 'm_GameObject' in assets.tree(go):
                go = assets.tree(go)['m_GameObject']
            indices = []
            while True:
                node = assets.tree(go)
                transform = next(assets.tree(c['component']) for c in node['m_Component'] if 'm_Father' in assets.tree(c['component']))
                father = transform['m_Father']
                if node['m_Name'] in roots or not father['m_PathID']:
                    if node['m_Name'] not in roots:
                        return None
                    return dict(root=roots[node['m_Name']], indices=list(reversed(indices)))
                parent = assets.tree(father)
                indices.append(next(i for i, p in enumerate(parent['m_Children']) if p['m_PathID'] == next(c['component']['m_PathID'] for c in node['m_Component'] if 'm_Father' in assets.tree(c['component']))))
                go = parent['m_GameObject']
        return location

    def materials(self, location):
        return customizer_materials(self.assets, lambda tree: script_of(self.assets, tree), location)

    def material(self, plan):
        """A generated material as a draw recipe, as SourceImmersiveMaterials.Apply sets it."""
        export, assets = self.export, self.assets
        row = assets.named('Shader', plan['shader'])
        pointer = {'m_PathID': row['id']}
        shader = assets.tree(pointer)
        form = shader['m_ParsedForm']
        values = self.material_values(form, EMPTY_MATERIAL)
        numbers = values['numbers']
        for item in plan['floats']:
            numbers[item['name']] = item['value']
        for item in plan['vectors']:
            numbers[item['name']] = item['value']
        program = next(p for p in form['m_SubShaders'][0]['m_Passes'])
        program_id = export.program(pointer, shader, 0, program, set())
        textures = {}
        for item in plan['textures']:
            found = [r for r in assets.rows() if r['type'] in ('Texture2D', 'Cubemap') and r['name'] == item['name'] and assets.bundle_name(r['bundle']).lower() == item['bundleName'].lower()]
            textures[item['property']] = export.texture({'m_PathID': found[0]['id']}, item['property'])
        names = export.programs[program_id]['names']
        for name, index in textures.items():
            row = export.textures[index]
            numbers[f'{name}_TexelSize'] = [1/row['width'], 1/row['height'], row['width'], row['height']]
        gradients = {g['property']: dict(resolution=g['resolution'], filter=g['filter'], keys=g['keys']) for g in plan['gradients']}
        for name in export.programs[program_id]['samplers']:
            if name not in textures and name not in gradients:
                textures[name] = values['textures'].get(name, dict(default='gray'))['default']
        state = self.render_state(program['m_State'], numbers)
        if plan['shader'].endswith('/UI'):
            # This shader's output is not premultiplied: it keeps its own SrcAlpha, whatever the recipe says for Battle.
            state['blend'][0] = 5
        if plan.get('clipped'):
            # CardPartsEffectStencilModifier: only where the card's outer shape wrote its bit.
            test = dict(comp=3, pass_=0, fail=0, zFail=0)
            state.update(stencilRef=1, stencilReadMask=1, stencilWriteMask=0, stencilFront=test, stencilBack=test)
        return dict(program=program_id, queue=plan['queue'], uniforms={n: numbers[n] for n in names if n in numbers}, textures=textures,
                    gradients=gradients, state=state, streams=plan['streams'], uiScale=plan.get('uiScale', 1),
                    canvasGeometry=plan['shader'].endswith('/UI'))

    def mesh_of(self, pointer):
        if not pointer or not pointer['m_PathID']:
            return None
        try:
            self.assets.object(pointer)
        except KeyError:
            return None
        return self.export.mesh(pointer)

    def system(self, tree, renderer, material):
        config = {k: tree[k] for k in ('lengthInSec', 'simulationSpeed', 'looping', 'prewarm', 'startDelay', 'moveWithTransform', 'scalingMode', 'randomSeed', 'emitterVelocityMode')}
        for name in MODULES:
            if tree[name].get('enabled', True):
                config[name] = tree[name]
        shape = config.get('ShapeModule')
        if shape:
            shape = dict(shape)
            shape['mesh'] = self.mesh_of(shape.pop('m_Mesh', None))
            for key in ('m_MeshRenderer', 'm_SkinnedMeshRenderer', 'm_Sprite', 'm_SpriteRenderer', 'm_Texture'):
                shape.pop(key, None)
            config['ShapeModule'] = shape
        sub = config.get('SubModule')
        if sub:
            config['SubModule'] = dict(enabled=True, subEmitters=[dict(type=e['type'], properties=e['properties'], emitProbability=e['emitProbability'], emitter=e['emitter']['m_PathID']) for e in sub['subEmitters'] if e['emitter']['m_PathID']])
        return dict(config=config, material=material, renderMode=renderer['m_RenderMode'], alignment=renderer['m_RenderAlignment'], sortMode=renderer['m_SortMode'],
                    fudge=renderer['m_SortingFudge'], pivot=renderer['m_Pivot'], mesh=self.mesh_of(renderer.get('m_Mesh')), sourceId=str(tree.get('_id', 0)))

    def scene(self, root_pointer, root_name, plans):
        """A node tree below a Transform, with its systems. Materials are matched by root and path."""
        assets = self.assets
        recipes = {tuple(p['indices']): p for p in plans if p['root'] == root_name}
        materials = []
        built = {}

        def walk(pointer, path):
            transform = assets.tree(pointer)
            go = assets.tree(transform['m_GameObject'])
            components = [(c['component'], assets.object(c['component'])['type']) for c in go['m_Component']]
            system = next((p for p, kind in components if kind == 'ParticleSystem'), None)
            renderer = next((p for p, kind in components if kind == 'ParticleSystemRenderer'), None)
            node = dict(name=go['m_Name'], active=bool(go['m_IsActive']), matrix=self.local_matrix(transform).T.flatten().tolist(),
                        children=[walk(c, [*path, i]) for i, c in enumerate(transform['m_Children'])])
            if system and renderer:
                plan = recipes.get(tuple(path))
                material = None
                if plan:
                    materials.append(self.material(plan))
                    material = len(materials)-1
                node['system'] = self.system(assets.tree(system), assets.tree(renderer), material)
                node['system']['id'] = str(system['m_PathID'])
                built[system['m_PathID']] = node
            return node
        return dict(root=walk(root_pointer, []), materials=materials)


def plain_arrays(values):
    return np.asarray(values, dtype=np.float32).flatten().tolist()
