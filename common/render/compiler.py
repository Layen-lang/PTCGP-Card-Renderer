"""Compile game/ into cards/: everything the player needs for each card."""
import concurrent.futures
import ctypes
import gzip
import json
import os
import shutil
import time
import traceback
import zipfile
from .assets import BundleCatalog, CardAssets
from .card_data import CardData
from .convert import convert
from .effects import build_effects
from .game import Game
from .gold import build_gold_plan, design_for, design_path, eligible, expose_template
from .immersive import SOURCE_ROOTS, build_immersive
from .paths import CARDS, GAME, LANGUAGES, RARITIES
from .ui_layout import build_bindings, build_layout
from .ui_plan import build_ui_plan

# A worker peaks at about 1 GB (measured on 128 cards): as many as free memory allows, one core left.
WORKER_MEMORY = 1.25*2**30
context = None


def available_memory():
    """Free physical memory in bytes (Windows), or None where it cannot be read."""
    class Status(ctypes.Structure):
        _fields_ = [('length', ctypes.c_ulong), ('load', ctypes.c_ulong), ('total', ctypes.c_ulonglong), ('available', ctypes.c_ulonglong),
                    ('pageTotal', ctypes.c_ulonglong), ('pageAvailable', ctypes.c_ulonglong), ('virtualTotal', ctypes.c_ulonglong),
                    ('virtualAvailable', ctypes.c_ulonglong), ('extended', ctypes.c_ulonglong)]
    status = Status(length=ctypes.sizeof(Status))
    try:
        return status.available if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)) else None
    except AttributeError:
        return None


def worker_count():
    cores = max(1, (os.cpu_count() or 2)-1)
    free = available_memory()
    return max(2, min(cores, int(free // WORKER_MEMORY))) if free else min(cores, 4)


class Context:
    def __init__(self):
        self.game = Game()
        self.catalog = BundleCatalog(self.game, CARDS/'_index')
        self.data = CardData(self.game)
        self.layouts = {}
        self.roots = {}
        for row in self.game.files:
            name = row['name']
            if name.endswith('.prefab_bundles') and '/L/Prefabs/' in name and name.startswith('Common/CardNew/Face/'):
                self.roots[('prefab', name.rsplit('/', 1)[1].removesuffix('_L.prefab_bundles'))] = row
            elif name.endswith('/CardSettings.asset_bundles'):
                self.roots[('settings', name.split('/')[-2])] = row
            elif name.endswith('.bytes_bundles') and '/L/Movies/' in name:
                self.roots[('movie', name.rsplit('/', 1)[1].removesuffix('.bytes_bundles'))] = row

    def blob(self, name):
        return self.game.by_name[name]['blob']


def init_worker():
    global context
    context = Context()


def all_cards(game):
    return [r['CardID'] for table in ('PokemonCard', 'TrainerCard') for r in game.table(table)]


def find_card(game, card_id):
    for kind, table in (('Pokemon', 'PokemonCard'), ('Trainers', 'TrainerCard')):
        rows = game.table(table).rows('CardID', card_id)
        if rows:
            return kind, rows[0]
    raise ValueError(f'Identifiant inconnu : {card_id}')


def card_roots(card_id):
    """Original bundle names rooting the card's dependency closure."""
    game = context.game
    kind, card = find_card(game, card_id)
    illustration = card['IllustrationID']
    if illustration.rsplit('_', 1)[-1] not in RARITIES:
        raise ValueError('Rarity outside the L loading scope.')
    prefab = context.roots[('prefab', illustration)]
    settings = context.roots[('settings', illustration)]
    ui = f'Common/CardNew/System/Prefabs/{kind}CardUI.prefab_bundles'
    extras = [ui, settings['name']]
    if kind == 'Pokemon':
        pre = game.table('Pokemon').one('PokemonID', card['PokemonID'])['PreevolvedCharacterID']
        if pre:
            extras.append(f'Common/CardNew/Pokemon/{pre}/{pre}.png_bundles')
    return kind, card, illustration, prefab, settings, ui, extras


def gold_design(kind, card_id):
    categories = set(context.data.load(card_id, LANGUAGES[0])['pokemon' if kind == 'Pokemon' else 'trainer']['AdditionalCategories'])
    return design_for(kind, categories)


def required_blobs(card_id):
    """Every bundle the player can load for this card."""
    try:
        kind, card, illustration, prefab, settings, ui, extras = card_roots(card_id)
        blobs = set(context.catalog.closure([prefab['blob'], *map(context.blob, extras)]))
        if eligible(context.game, card_id):
            design = design_path(gold_design(kind, card_id))
            blobs |= set(context.catalog.closure([prefab['blob'], context.blob(design), *map(context.blob, extras)]))
        if illustration.endswith('_IM'):
            blobs |= set(context.catalog.closure([prefab['blob'], *map(context.blob, SOURCE_ROOTS)]))
        return card_id, sorted(blobs), None
    except Exception as error:
        return card_id, [], f'{type(error).__name__}: {error}'


# Files already converted, by name and content hash: checking one means loading the whole bundle.
CONVERTED = GAME/'converted.json'


def convert_blob(blob):
    row = context.game.by_blob[blob]
    return convert(context.game.path(row))


def write_json(path, value):
    text = json.dumps(value, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    if path.suffix == '.gz':
        with open(path, 'wb') as raw, gzip.GzipFile(fileobj=raw, mode='wb', mtime=0, filename='') as stream:
            stream.write(text)
    else:
        path.write_bytes(text)


def card_archive(card_id):
    """A compiled card is one zip: its plans, already gzipped, stored as they are."""
    return CARDS/f'{card_id}.zip'


def card_movie(card_id):
    """An IM card's movie stays beside its zip: the player's video needs a file of its own."""
    return CARDS/f'{card_id}.mp4'


def read_card_file(card_id, name):
    with zipfile.ZipFile(card_archive(card_id)) as archive:
        return archive.read(name)


def read_card_json(card_id, name):
    return json.loads(gzip.decompress(read_card_file(card_id, name)))


def read_summary(card_id):
    return json.loads(read_card_file(card_id, 'card.json'))


def pack_card(temporary, card_id):
    """Store the compiled plans in one zip, without compressing them again: reading one stays as fast.
    A fixed date keeps a recompiled card byte-identical."""
    archive = card_archive(card_id)
    partial = archive.with_name(archive.name+'.tmp')
    with zipfile.ZipFile(partial, 'w', zipfile.ZIP_STORED) as packed:
        for path in sorted(temporary.iterdir()):
            if path.name != 'movie.mp4':
                packed.writestr(zipfile.ZipInfo(path.name, date_time=(1980, 1, 1, 0, 0, 0)), path.read_bytes())
    movie = temporary/'movie.mp4'
    if movie.is_file():
        move_into_place(movie, card_movie(card_id))
    else:
        card_movie(card_id).unlink(missing_ok=True)
    move_into_place(partial, archive)
    shutil.rmtree(temporary, ignore_errors=True)
    # Cards compiled before this layout were one folder each.
    shutil.rmtree(CARDS/card_id, ignore_errors=True)


def bundle_rows(game, source):
    return dict(bundles=[dict(blob=blob, path='game/'+game.by_blob[blob]['name']) for blob in source.blobs])


def move_into_place(temporary, out):
    # Windows may briefly hold a new folder open (indexing, antivirus).
    for attempt in range(10):
        try:
            temporary.replace(out)
            return
        except PermissionError:
            if attempt == 9:
                raise
            time.sleep(.5)


def compile_card(card_id, out):
    game, catalog, data = context.game, context.catalog, context.data
    kind, card, illustration, prefab, settings, ui, extras = card_roots(card_id)
    temporary = out.with_name(out.name+'.tmp')
    shutil.rmtree(temporary, ignore_errors=True)
    temporary.mkdir(parents=True)
    source = CardAssets(catalog, [prefab['blob'], *map(context.blob, extras)])
    ui_blob = context.blob(ui)
    layout = context.layouts.get(kind)
    if layout is None:
        layout = context.layouts[kind] = build_layout(source, ui_blob)
    records = {language: data.load(card_id, language) for language in LANGUAGES}
    for language, record in records.items():
        write_json(temporary/f'ui.{language}.json.gz', build_ui_plan(source, layout, record, settings['blob']))
    write_json(temporary/'bindings.json.gz', build_bindings(source, prefab['blob']))
    write_json(temporary/'effects.json.gz', build_effects(source, prefab['blob']))
    write_json(temporary/'bundles.json.gz', bundle_rows(game, source))
    has_gold = eligible(game, card_id)
    if has_gold:
        design = gold_design(kind, card_id)
        gold_source = CardAssets(catalog, [prefab['blob'], context.blob(design_path(design)), *map(context.blob, extras)])
        for language, record in records.items():
            write_json(temporary/f'gold-ui.{language}.json.gz', build_ui_plan(gold_source, layout, record, settings['blob'], design))
        plan = build_gold_plan(game, gold_source, card_id, kind, design, settings['blob'])
        template = plan['template']
        exposed = expose_template(game.path(game.by_blob[template['bundle']]), template['sourceId'], template['bundle'])
        rows = bundle_rows(game, gold_source)
        next(r for r in rows['bundles'] if r['blob'] == template['bundle'])['path'] = 'cards/_gold/'+exposed.name
        write_json(temporary/'gold.json.gz', plan)
        write_json(temporary/'gold-bundles.json.gz', rows)
    has_immersive = illustration.endswith('_IM')
    if has_immersive:
        movie = context.roots[('movie', illustration)]
        immersive_source = CardAssets(catalog, [prefab['blob'], *map(context.blob, SOURCE_ROOTS)])
        plan = build_immersive(game, immersive_source, illustration, movie['name'], temporary)
        existing = set(source.blobs)
        extra = [b for b in immersive_source.blobs if b not in existing]
        write_json(temporary/'immersive.json.gz', plan)
        write_json(temporary/'immersive-bundles.json.gz', dict(bundles=[dict(blob=b, path='game/'+game.by_blob[b]['name']) for b in extra]))
    summary = dict(cardId=card_id, rarity=illustration.rsplit('_', 1)[-1], kind=kind, uiPrefabName=kind+'CardUI',
                   languages=list(records), displayNames=[record['name'] for record in records.values()],
                   prefabBundle=prefab['blob'], prefabName=illustration+'_L', uiBundle=ui_blob,
                   hasGold=has_gold, hasImmersive=has_immersive, fidelityValidated=False)
    write_json(temporary/'card.json', summary)
    pack_card(temporary, card_id)
    return summary


def compile_worker(card_id):
    try:
        summary = compile_card(card_id, CARDS/card_id)
    except Exception as error:
        shutil.rmtree(CARDS/(card_id+'.tmp'), ignore_errors=True)
        return card_id, None, f'{type(error).__name__}: {error}', traceback.format_exc()
    finally:
        if len(context.catalog.trees) > 60000:
            context.catalog.trees.clear()
    return card_id, summary, None, None


def build(card_ids=None, log=print):
    """Convert what the cards need, then compile them. Already converted files are skipped."""
    started = time.time()
    CARDS.mkdir(parents=True, exist_ok=True)
    init_worker()
    game = context.game
    selected = card_ids or all_cards(game)
    failures = {}
    workers = worker_count()
    log(f'{workers} worker processes')
    with concurrent.futures.ProcessPoolExecutor(workers, initializer=init_worker) as pool:
        needed = set()
        for i, (card_id, blobs, error) in enumerate(pool.map(required_blobs, selected, chunksize=8), 1):
            needed.update(blobs)
            if error:
                failures[card_id] = error
            if i % 200 == 0:
                log(f'Analysing: {i}/{len(selected)}')
        log(f'{len(needed)} files to load ({time.time()-started:.0f} s)')
    # Only files never seen converted are opened; the updater gives a changed file a new content hash.
    done = json.loads(CONVERTED.read_text(encoding='utf-8')) if CONVERTED.is_file() else {}
    todo = sorted(blob for blob in needed if done.get(game.by_blob[blob]['name']) != game.by_blob[blob]['content'])
    # A fresh pool: no worker may still hold one of these files open.
    with concurrent.futures.ProcessPoolExecutor(workers, initializer=init_worker) as pool:
        converted = 0
        for i, changed in enumerate(pool.map(convert_blob, todo, chunksize=4), 1):
            converted += changed
            if i % 500 == 0:
                log(f'Converting: {i}/{len(todo)}')
    done.update({game.by_blob[blob]['name']: game.by_blob[blob]['content'] for blob in todo})
    CONVERTED.write_text(json.dumps(done, separators=(',', ':')), encoding='utf-8')
    log(f'{converted} files converted, {len(needed)-len(todo)} already done ({time.time()-started:.0f} s)')
    # Gold templates are exposed from converted bundles, so compilation comes last.
    summaries = {}
    with concurrent.futures.ProcessPoolExecutor(workers, initializer=init_worker) as pool:
        todo = [c for c in selected if c not in failures]
        for i, (card_id, summary, error, trace) in enumerate(pool.map(compile_worker, todo, chunksize=4), 1):
            if error:
                failures[card_id] = error
            else:
                summaries[card_id] = summary
            if i % 100 == 0:
                log(f'Cards: {i}/{len(todo)}')
    index_path = CARDS/'index.json'
    previous = json.loads(index_path.read_text(encoding='utf-8')) if index_path.is_file() else {}
    cards = previous.get('cards', {}) if card_ids else {}
    cards.update(summaries)
    report = dict(version=game.version, versionCode=game.version_code, cards=cards)
    errors = (previous.get('failures', {}) if card_ids else {})
    errors.update(failures)
    for card_id in summaries:
        errors.pop(card_id, None)
    report['failures'] = errors
    index_path.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding='utf-8')
    log(f'{len(summaries)} cards ready, {len(failures)} failed, {time.time()-started:.0f} s')
    return summaries, failures
