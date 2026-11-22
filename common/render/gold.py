"""Original gold-frame selection for an eligible L card.

Frame switching/restoration and full rendering fidelity remain unvalidated.
"""
import hashlib
import json
from pathlib import Path
import UnityPy
from .paths import CARDS, UNITY_VERSION

UnityPy.config.FALLBACK_UNITY_VERSION = UNITY_VERSION


def frame_id(game):
    frame = game.table('CardFrame')
    assert len(frame) == 1 and frame[0]['CardFrameID'] == 'GOLD_FRAME_100010'
    return frame[0]['CardFrameID']


def eligible(game, card_id):
    return any(r['CardFrameID'] == frame_id(game) for r in game.table('CardFrameDropSetting').rows('CardID', card_id))


def design_for(kind, categories):
    assert categories <= {1, 2, 3}, 'Unknown original categories'
    if kind == 'Pokemon':
        return 'Pokemon_CUR_GoldFrame_Paradox' if categories & {2, 3} else 'Pokemon_CUR_GoldFrame'
    return 'Trainers_CU_GoldFrame_Paradox_Ancient' if 2 in categories else 'Trainers_CU_GoldFrame_Paradox_Future' if 3 in categories else 'Trainers_CU_GoldFrame'


def design_path(design):
    return f'Common/CardNew/Template/L/Settings/DesignSettings/{design}.asset_bundles'


def build_gold_plan(game, source, card_id, kind, design, settings_blob):
    settings = source.card_settings(card_id, settings_blob)
    gold_obj = source.named('MonoBehaviour', design)
    gold = source.tree({'m_PathID': gold_obj['id']})
    template = source.asset(gold['_templatePrefab'])
    template_go = source.tree(gold['_templatePrefab'])
    assert template_go['m_Name'].startswith('Template_L_') and 'GoldFrame' in template_go['m_Name']
    transforms = {}
    locations = {}

    def walk(go_pointer, indices):
        go = source.tree(go_pointer)
        tr = [c['component'] for c in go['m_Component'] if source.object(c['component'])['type'] == 'Transform']
        assert len(tr) == 1
        transforms[go_pointer['m_PathID']] = source.tree(tr[0])
        locations[go_pointer['m_PathID']] = indices
        for i, c in enumerate(transforms[go_pointer['m_PathID']]['m_Children']):
            walk(source.tree(c)['m_GameObject'], indices+[i])
    walk(gold['_templatePrefab'], [])
    root_transform = transforms[gold['_templatePrefab']['m_PathID']]
    swapped_names = [source.tree(source.tree(c)['m_GameObject'])['m_Name'] for c in root_transform['m_Children']]
    parts = [source.tree(c['component']) for c in template_go['m_Component'] if source.object(c['component'])['type'] == 'MonoBehaviour']
    parts = [t for t in parts if 'panelRenderer' in t]
    assert len(parts) == 1
    panel_pointer = parts[0]['panelRenderer']
    panel = source.tree(panel_pointer)
    panel_name = source.tree(panel['m_GameObject'])['m_Name']
    condition = next((r for r in gold['_renderers'] if source.tree(source.tree(r['_renderer'])['m_GameObject'])['m_Name'] == panel_name), None)
    assert condition is not None
    material = None
    for c in condition['_materialConditions']:
        if not c['_override']:
            continue
        assert c['_overrideType'] == 1, 'Unproved gold panel override type'
        default = None
        selected = None
        for r in source.tree(c['_overrideMaterialConditions'])['_conditions']:
            f = r['_conditionType']
            assert f & ~7 == 0, 'Unknown original material condition flags'
            if f == 0:
                default = r['_material']
                continue
            # Original GetMaterial gates energy on PokemonCardSettings and
            # card type on TrainersCardSettings, then stage on CardSettings.
            matches = (kind != 'Pokemon' or not f & 2 or r['_energyType'] == settings['_energyType']) and (kind != 'Trainers' or not f & 1 or r['_cardType'] == settings['_cardType']) and (not f & 4 or r['_stage'] == settings['_stageType'])
            if matches:
                selected = r['_material']
                break
        chosen = selected or default
        assert chosen and chosen['m_PathID'], 'No original panel material matches'
        material = source.asset(chosen)
    # DynamicCardParts.Apply writes panelRenderer.material only when an
    # original condition has _override enabled. Trainer gold deliberately
    # keeps its serialized material (_override=0); its dormant conditions
    # are not a reason to synthesize or demand a replacement.
    panel_materials = [source.asset(p) for p in panel['m_Materials']]
    bindings = []
    for go_id, indices in locations.items():
        for c in source.tree({'m_PathID': go_id})['m_Component']:
            if source.object(c['component'])['type'] != 'MonoBehaviour':
                continue
            t = source.tree(c['component'])
            if '_dynamicUIType' not in t:
                continue
            assert t['_dynamicPropertyType'] in (0, 3)
            r = source.tree(t['_renderer'])
            bindings.append(dict(indices=locations[r['m_GameObject']['m_PathID']], type=t['_dynamicUIType'], property={0: '_DynamicUITex', 3: '_AdditionalDynamicUITex'}[t['_dynamicPropertyType']], sourceId=str(c['component']['m_PathID'])))
    return dict(scope='L', frameId=frame_id(game), template=template, swappedNames=swapped_names, panelIndices=locations[panel['m_GameObject']['m_PathID']],
                panelMaterial=material, panelMaterialOverride=material is not None, serializedPanelMaterials=panel_materials, bindings=bindings,
                originalDesign=design, originalSettings=settings, fidelityValidated=False, restorationUiValidated=False)


def expose_template(path, source_id, blob):
    """Expose an existing template in the converted bundle's index.

    Only AssetBundle.m_Container changes in cards/_gold; every other serialized
    payload must remain byte-identical.
    """
    destination = CARDS/'_gold'/f'{blob}-{source_id}.bundle'
    if destination.is_file():
        return destination
    destination.parent.mkdir(parents=True, exist_ok=True)
    env = UnityPy.load(path.read_bytes())
    bundle = next(o for o in env.objects if o.type.name == 'AssetBundle')
    templates = [o for o in env.objects if o.type.name == 'GameObject' and o.path_id == int(source_id)]
    assert len(templates) == 1
    name = templates[0].read_typetree()['m_Name']
    assert name.startswith('Template_L_') and 'GoldFrame' in name
    before = {(o.assets_file.name, o.path_id): hashlib.sha256(o.get_raw_data()).hexdigest() for o in env.objects}
    tree = bundle.read_typetree()
    assert len(tree['m_Container']) == 1
    label, entry = tree['m_Container'][0]
    assert '/Template/L/' in label
    pointer = {'m_FileID': 0, 'm_PathID': int(source_id)}
    assert pointer in tree['m_PreloadTable']
    # Preserve the original dependency preload range, including the template.
    tree['m_Container'].append((name, dict(entry, asset=pointer)))
    bundle.save_typetree(tree)
    temporary = destination.with_name(destination.name+f'.{id(env)}.tmp')
    temporary.write_bytes(env.file.save(packer='lz4'))
    check = UnityPy.load(temporary.read_bytes())
    after = {(o.assets_file.name, o.path_id): hashlib.sha256(o.get_raw_data()).hexdigest() for o in check.objects}
    changed = [k for k in before if before[k] != after[k]]
    assert changed == [(bundle.assets_file.name, bundle.path_id)]
    del check
    temporary.replace(destination)
    return destination
