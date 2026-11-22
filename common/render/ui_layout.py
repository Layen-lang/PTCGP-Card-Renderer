"""Original UI prefab hierarchy and component bindings, and card DynamicUI bindings."""


def build_layout(source, blob):
    objects = {row['id']: row for row in source.rows_of(blob) if row['type'] in ('GameObject', 'Transform', 'RectTransform', 'MonoBehaviour')}
    trees = {key: source.tree({'m_PathID': key}) for key in objects}
    scripts = {}
    for row in source.rows():
        if row['type'] == 'MonoScript':
            if row['id'] in scripts and scripts[row['id']] != row['name']:
                raise ValueError('Conflicting MonoScript identity')
            scripts[row['id']] = row['name']
    transforms = {key: t for key, t in trees.items() if objects[key]['type'] in ('Transform', 'RectTransform')}
    by_gameobject = {t['m_GameObject']['m_PathID']: key for key, t in transforms.items()}
    roots = [key for key, t in transforms.items() if t['m_Father']['m_PathID'] == 0]
    if len(roots) != 1:
        raise ValueError(f'Expected one UI root: {roots}')
    nodes = {}

    def walk(key, indices, names):
        t = transforms[key]
        go = trees[t['m_GameObject']['m_PathID']]
        names = names + [go['m_Name']]
        nodes[key] = dict(transform_id=str(key), gameobject_id=str(t['m_GameObject']['m_PathID']),
                          indices=indices, path='/'.join(names), active=go['m_IsActive'], transform=t)
        for i, child in enumerate(t['m_Children']):
            if child['m_FileID'] != 0:
                raise ValueError('External transform in UI hierarchy')
            walk(child['m_PathID'], indices + [i], names)
    walk(roots[0], [], [])

    def location(component_id):
        t = trees[component_id]
        go = component_id if objects[component_id]['type'] == 'GameObject' else t['m_GameObject']['m_PathID']
        n = nodes[by_gameobject[go]]
        return dict(indices=n['indices'], path=n['path'])
    components = []
    for key, tree in trees.items():
        if objects[key]['type'] != 'MonoBehaviour':
            continue
        cls = scripts.get(tree['m_Script']['m_PathID'])
        if cls is None:
            raise ValueError(f'Missing script for {key}')
        row = dict(id=str(key), type=cls, **location(key), fields=tree)
        if '_text' in tree and tree['_text'].get('m_PathID'):
            row['target_text'] = location(tree['_text']['m_PathID'])
        components.append(row)
    return dict(nodes=list(nodes.values()), components=components)


def build_bindings(source, prefab_blob):
    objects = {row['id']: row for row in source.rows_of(prefab_blob) if row['type'] in ('Transform', 'MonoBehaviour', 'MeshRenderer', 'SkinnedMeshRenderer')}
    trees = {i: source.tree({'m_PathID': i}) for i in objects}
    transforms = {i: t for i, t in trees.items() if objects[i]['type'] == 'Transform'}
    locations = {}

    def walk(i, indices):
        t = transforms[i]
        locations[t['m_GameObject']['m_PathID']] = indices
        for n, c in enumerate(t['m_Children']):
            assert c['m_FileID'] == 0
            walk(c['m_PathID'], indices + [n])
    roots = [i for i, t in transforms.items() if t['m_Father']['m_PathID'] == 0]
    assert len(roots) == 1
    walk(roots[0], [])
    bindings = []
    for i, t in trees.items():
        if '_dynamicUIType' not in t:
            continue
        properties = {0: '_DynamicUITex', 3: '_AdditionalDynamicUITex'}
        if t['_dynamicPropertyType'] not in properties:
            raise ValueError(f"Unported dynamic shader property: {t['_dynamicPropertyType']}")
        renderer = trees[t['_renderer']['m_PathID']]
        bindings.append(dict(indices=locations[renderer['m_GameObject']['m_PathID']],
                             type=t['_dynamicUIType'], property=properties[t['_dynamicPropertyType']], sourceId=str(i), rendererId=str(t['_renderer']['m_PathID'])))
    return dict(bindings=bindings)
