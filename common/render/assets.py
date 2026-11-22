"""Dependency closures and serialized objects, read directly from game/ bundles."""
import json
import os
import threading
import time
import warnings
from collections import OrderedDict
import UnityPy
from .paths import UNITY_VERSION

UnityPy.config.FALLBACK_UNITY_VERSION = UNITY_VERSION
warnings.filterwarnings('ignore', message='No valid Unity version found.*')
_lock = threading.Lock()


def plain(value):
    """Same plain structure as a JSON export of the type tree."""
    return json.loads(json.dumps(value, default=lambda x: list(x) if isinstance(x, bytes) else str(x)))


class BundleCatalog:
    """Dependencies and object lists per bundle, cached on disk across runs."""

    def __init__(self, game, cache_dir, open_limit=64):
        self.game = game
        self.cache_dir = cache_dir
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        self.info = {}
        self.envs = OrderedDict()
        self.open_limit = open_limit
        self.trees = {}

    def env(self, blob):
        env = self.envs.get(blob)
        if env is None:
            env = UnityPy.load(str(self.game.path(self.game.by_blob[blob])))
            self.envs[blob] = env
            while len(self.envs) > self.open_limit:
                self.envs.popitem(last=False)
        else:
            self.envs.move_to_end(blob)
        return env

    def describe(self, blob):
        info = self.info.get(blob)
        if info is not None:
            return info
        path = self.cache_dir/(blob+'.json')
        info = self._cached(path)
        if info is None:
            info = self._scan(blob)
            # The compiler's processes describe the same shared bundles at once on a first run.
            # Windows refuses to replace a file another process has open: the description written
            # by the other process is the same, so this one is kept in memory only.
            temporary = path.with_suffix(f'.{os.getpid()}.{threading.get_ident()}.tmp')
            temporary.write_text(json.dumps(info), encoding='utf-8')
            try:
                temporary.replace(path)
            except PermissionError:
                temporary.unlink(missing_ok=True)
        self.info[blob] = info
        return info

    @staticmethod
    def _cached(path):
        """A description on disk, or None: absent, being replaced or half written by another process."""
        for attempt in range(5):
            try:
                return json.loads(path.read_text(encoding='utf-8'))
            except FileNotFoundError:
                return None
            except (PermissionError, ValueError):
                time.sleep(.05*(attempt+1))
        return None

    def _scan(self, blob):
        env = self.env(blob)
        objects, name, dependencies = [], None, None
        for obj in env.objects:
            kind = obj.type.name
            if kind == 'AssetBundle':
                tree = obj.read_typetree()
                name, dependencies = tree['m_Name'], tree['m_Dependencies']
                continue
            if kind == 'Shader':
                label = obj.read().m_ParsedForm.m_Name
            else:
                try:
                    label = obj.peek_name() or ''
                except Exception:
                    label = obj.read_typetree().get('m_Name', '')
            objects.append((obj.path_id, kind, label))
        return dict(name=name, dependencies=dependencies, objects=objects)

    def resolve(self, cab):
        rows = self.game.by_cab.get(cab.lower())
        if not rows:
            raise ValueError(f'Missing dependency {cab}')
        return rows[0]['blob']

    def closure(self, roots):
        """Breadth-first bundle order from the original m_Dependencies lists."""
        order, seen, queue = [], set(), list(roots)
        while queue:
            blob = queue.pop(0)
            if blob in seen:
                continue
            seen.add(blob)
            order.append(blob)
            queue.extend(self.resolve(cab) for cab in self.describe(blob)['dependencies'])
        return order

    def tree(self, blob, path_id):
        key = (blob, path_id)
        tree = self.trees.get(key)
        if tree is None:
            for obj in self.env(blob).objects:
                if obj.path_id == path_id:
                    tree = plain(obj.read_typetree())
                    break
            else:
                raise KeyError(key)
            self.trees[key] = tree
        return tree


class CardAssets:
    """Serialized objects of one closure, addressed by original PathID."""

    def __init__(self, catalog, roots):
        self.catalog = catalog
        self.roots = list(roots)
        self.blobs = catalog.closure(self.roots)
        self.objects = {}
        for blob in self.blobs:
            for path_id, kind, name in catalog.describe(blob)['objects']:
                self.objects.setdefault(path_id, []).append(dict(bundle=blob, id=path_id, type=kind, name=name))

    def rows_of(self, blob):
        return [dict(bundle=blob, id=i, type=kind, name=name) for i, kind, name in self.catalog.describe(blob)['objects']]

    def object(self, pointer):
        rows = self.objects[pointer['m_PathID']]
        if len(rows) != 1:
            raise ValueError(f'Ambiguous serialized identity: {pointer}')
        return rows[0]

    def tree(self, pointer):
        obj = self.object(pointer)
        return self.catalog.tree(obj['bundle'], obj['id'])

    def asset(self, pointer):
        obj = self.object(pointer)
        return dict(bundle=obj['bundle'], name=obj['name'], sourceId=str(obj['id']))

    def named(self, kind, name):
        found = [row for rows in self.objects.values() for row in rows if row['type'] == kind and row['name'] == name]
        if len(found) != 1:
            raise ValueError(f'Expected one {kind} named {name}; got {len(found)}')
        return found[0]

    def rows(self):
        return [row for rows in self.objects.values() for row in rows]

    def find(self, kind, predicate, skip=('Common/Font_bundles',)):
        """Scan one kind of object; the font bundle's huge assets are never candidates."""
        found = [row for row in self.rows() if row['type'] == kind and self.bundle_name(row['bundle']) not in skip
                 and predicate(self.tree({'m_PathID': row['id']}))]
        if len(found) != 1:
            raise ValueError(f'Expected one {kind} matching source fields; got {len(found)}')
        return found[0]

    def bundle_name(self, blob):
        return self.catalog.describe(blob)['name']

    def card_settings(self, card_id, blob):
        found = [row for row in self.rows_of(blob) if row['type'] == 'MonoBehaviour']
        found = [row for row in found if (lambda t: '_cardDesignSettings' in t and t.get('_id') == card_id)(self.catalog.tree(blob, row['id']))]
        if len(found) != 1:
            raise ValueError(f'Settings of {card_id} are not unique')
        return self.catalog.tree(blob, found[0]['id'])
