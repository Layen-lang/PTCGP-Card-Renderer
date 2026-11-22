"""Read access to game/: the mirror of the game's files under their own names."""
import json
from collections import defaultdict
from .paths import GAME


class Table(list):
    """A master table with cached lookups by field."""

    def __init__(self, rows):
        super().__init__(rows)
        self._indexes = {}

    def rows(self, field, value):
        index = self._indexes.get(field)
        if index is None:
            index = defaultdict(list)
            for row in self:
                index[row[field]].append(row)
            self._indexes[field] = index
        return index.get(value, [])

    def one(self, field, value):
        found = self.rows(field, value)
        if len(found) != 1:
            raise ValueError(f'Expected unique {field}={value}, got {len(found)}')
        return found[0]


class Game:
    def __init__(self, root=GAME):
        self.root = root
        index_path = root/'index.json'
        if not index_path.is_file():
            raise ValueError('data/game/index.json is missing: run python -m common.render update.')
        index = json.loads(index_path.read_text(encoding='utf-8'))
        self.version = index['version']
        self.version_code = index['versionCode']
        self.files = index['files']
        self.by_name = {row['name']: row for row in self.files}
        self.by_blob = {row['blob']: row for row in self.files}
        self.by_cab = defaultdict(list)
        for row in self.files:
            for cab in row['cabs']:
                self.by_cab[cab].append(row)
        self._tables = {}
        self._locales = {}

    def path(self, row):
        return self.root/row['name']

    def table(self, name):
        table = self._tables.get(name)
        if table is None:
            rows = json.loads((self.root/'MasterData'/(name+'.json')).read_text(encoding='utf-8'))
            table = self._tables[name] = Table(rows)
        return table

    def locale(self, language):
        strings = self._locales.get(language)
        if strings is None:
            path = self.root/'MasterData/Locale'/(language+'.json')
            if not path.is_file():
                raise ValueError(f'Missing language: {language}')
            strings = self._locales[language] = json.loads(path.read_text(encoding='utf-8'))
        return strings
