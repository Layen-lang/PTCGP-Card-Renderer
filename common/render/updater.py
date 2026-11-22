"""Update data/game from the game installed on an Android emulator or rooted device, over ADB only.

Every Aladin blob is named after the hash of its encrypted bytes, and the ALI2
indexes give each original asset name its blob and the hash of its plain bytes.
Only files whose content changed are transferred. game/ is touched at the very
end, after every new file has been decrypted and checked.
"""
import concurrent.futures
import json
import os
import shutil
import struct
import subprocess
import tarfile
import threading
import time
import warnings
import zipfile
from pathlib import Path
import lz4.block
import numpy as np
import UnityPy
import xxhash
from .paths import GAME, UNITY_VERSION
from .tools import adb

UnityPy.config.FALLBACK_UNITY_VERSION = UNITY_VERSION
warnings.filterwarnings('ignore', category=UserWarning)
PACKAGE = 'jp.pokemon.pokemontcgp'
REMOTE_FILES = f'/sdcard/Android/data/{PACKAGE}/files'
WORK = GAME.with_name('game-update')
GLOBAL_KEY = bytes.fromhex('cfd3f5cb760b81e73b3d3b41173f115fe042949dc46042749ecc877d58d2296c')
MASTER_KEY = bytes.fromhex('f961066d49fed6f59639ea917e286602f572998ed16b73f9c6517490ba87e61b')
SIGMA = b'A3AxwtfWD<PbxMx$'
TURNS = (6, 5, 6, 5, 5, 6, 5, 6, 6, 6, 5, 5, 5, 6, 6, 5)
LOCALE_TEXTS = ('Master', 'UI')
TAR_BATCH = 400
# ADB streams at once: two to four times one stream's throughput, more brings nothing.
TRANSFER_STREAMS = 4
# The cipher holds about five times a file in memory: eight processes stay well within 16 GB.
DECRYPT_WORKERS = min(os.cpu_count() or 1, 8)
DECRYPT_BACKLOG = 512*1024*1024
# Emulators that ADB only lists once connected: the usual 5555 (BlueStacks, LDPlayer), MuMu,
# Nox and MEmu. 127.0.0.2:5555 reaches LDPlayer when BlueStacks holds 127.0.0.1:5555.
EMULATOR_ADDRESSES = tuple(f'127.0.0.1:{port}' for port in (5555, 7555, 16384, 16416, 62001, 62025, 21503))+('127.0.0.2:5555',)
# A command as root: su -c (BlueStacks, MuMu, LDPlayer, Nox, Magisk), su 0 (Android Studio images), adbd already root.
ROOT_FORMS = ("su -c '{script}'", "su 0 sh -c '{script}'", '{script}')


def transform(key, nonce, data):
    """The Aladin stream cipher, vectorized over its 64-byte blocks."""
    if not data:
        return b''
    k = struct.unpack('<8I', key)
    n = struct.unpack('<3I', nonce)
    mask = 0xffffffff
    u = ((((k[5]+k[0]) & mask) ^ k[7])+(((n[1]+n[0]) & mask) ^ n[2])) & mask
    turns = TURNS[((u >> 7) & 2) | ((u >> 2) & 1) | ((u >> 13) & 4) | ((u >> 2) & 8)]
    blocks = (len(data)+63)//64
    initial = np.zeros((blocks, 16), dtype=np.uint32)
    initial[:, :4] = struct.unpack('<4I', SIGMA)
    initial[:, 4:12] = k
    initial[:, 12] = np.arange(1, blocks+1, dtype=np.uint64) & mask
    initial[:, 13:16] = n
    s = [initial[:, i].copy() for i in range(16)]

    def rotate(v, c):
        return (v << c) | (v >> (32-c))

    def quarter(a, b, c, d):
        s[a] += s[b]
        s[d] = rotate(s[d] ^ s[a], 16)
        s[c] += s[d]
        s[b] = rotate(s[b] ^ s[c], 12)
        s[a] += s[b]
        s[d] = rotate(s[d] ^ s[a], 8)
        s[c] += s[d]
        s[b] = rotate(s[b] ^ s[c], 7)
    for _ in range(turns):
        quarter(0, 4, 8, 12)
        quarter(1, 5, 9, 13)
        quarter(2, 6, 10, 14)
        quarter(3, 7, 11, 15)
        quarter(0, 5, 10, 15)
        quarter(1, 6, 11, 12)
        quarter(2, 7, 8, 13)
        quarter(3, 4, 9, 14)
    stream = (np.stack(s, axis=1)+initial).astype('<u4').tobytes()
    return (np.frombuffer(data, dtype=np.uint8) ^ np.frombuffer(stream, dtype=np.uint8, count=len(data))).tobytes()


def nonce(value, tail):
    return struct.pack('<QI', value, tail)


def decrypt_blob(data, key, name_hash):
    return transform(key, nonce(name_hash, 0x63686368), data)


def default_key(path):
    identifier = int(path.stem, 16)
    return transform(GLOBAL_KEY, nonce(identifier, 0), path.read_bytes())


def index_rows(data):
    """Rows of a plain ALI2 index, with their original asset names."""
    if data[4:8] != b'ALI2':
        raise ValueError('Index ALI2 illisible')
    u32 = lambda p: struct.unpack_from('<I', data, p)[0]
    root = u32(0)
    vt = root-struct.unpack_from('<i', data, root)[0]

    def vector(field):
        p = root+struct.unpack_from('<H', data, vt+4+field*2)[0]
        return p+u32(p)
    rows, names = vector(0), vector(2)
    if u32(rows) != u32(names):
        raise ValueError('ALI2 row/name count mismatch')
    for i in range(u32(rows)):
        p = names+4+i*4
        p += u32(p)
        name = data[p+4:p+4+u32(p)].decode('utf-8')
        content, _, blob, size, key, _ = struct.unpack_from('<6Q', data, rows+4+i*48)
        yield dict(name=name, blob=f'{blob:016x}', content=f'{content:016x}', keyId=f'{key:016x}', bytes=size)


class Reader:
    """MessagePack reader keeping the master tables' JSON shape."""

    def __init__(self, data):
        self.data = data
        self.pos = 0

    def take(self, count):
        value = self.data[self.pos:self.pos+count]
        if len(value) != count:
            raise ValueError('Truncated MessagePack')
        self.pos += count
        return value

    def number(self, form):
        return struct.unpack('>'+form, self.take(struct.calcsize('>'+form)))[0]

    def array(self, count):
        return [self.value() for _ in range(count)]

    def map(self, count):
        result = {}
        for _ in range(count):
            key = self.value()
            key = key if isinstance(key, str) else json.dumps(key)
            if key in result:
                raise ValueError(f'Duplicate MessagePack key {key}')
            result[key] = self.value()
        return result

    def ext(self, count):
        kind = struct.unpack('b', self.take(1))[0]
        return {'$ext': kind, '$data': self.take(count).hex()}

    def value(self):
        m = self.take(1)[0]
        if m < 0x80: return m
        if m < 0x90: return self.map(m & 15)
        if m < 0xa0: return self.array(m & 15)
        if m < 0xc0: return self.take(m & 31).decode('utf-8')
        if m >= 0xe0: return m-256
        simple = {0xc0: lambda: None, 0xc2: lambda: False, 0xc3: lambda: True,
                  0xc4: lambda: {'$bytes': self.take(self.number('B')).hex()},
                  0xc5: lambda: {'$bytes': self.take(self.number('H')).hex()},
                  0xc6: lambda: {'$bytes': self.take(self.number('I')).hex()},
                  0xc7: lambda: self.ext(self.number('B')), 0xc8: lambda: self.ext(self.number('H')), 0xc9: lambda: self.ext(self.number('I')),
                  0xca: lambda: self.number('f'), 0xcb: lambda: self.number('d'),
                  0xcc: lambda: self.number('B'), 0xcd: lambda: self.number('H'), 0xce: lambda: self.number('I'), 0xcf: lambda: self.number('Q'),
                  0xd0: lambda: self.number('b'), 0xd1: lambda: self.number('h'), 0xd2: lambda: self.number('i'), 0xd3: lambda: self.number('q'),
                  0xd4: lambda: self.ext(1), 0xd5: lambda: self.ext(2), 0xd6: lambda: self.ext(4), 0xd7: lambda: self.ext(8), 0xd8: lambda: self.ext(16),
                  0xd9: lambda: self.take(self.number('B')).decode('utf-8'), 0xda: lambda: self.take(self.number('H')).decode('utf-8'), 0xdb: lambda: self.take(self.number('I')).decode('utf-8'),
                  0xdc: lambda: self.array(self.number('H')), 0xdd: lambda: self.array(self.number('I')),
                  0xde: lambda: self.map(self.number('H')), 0xdf: lambda: self.map(self.number('I'))}
        if m not in simple:
            raise ValueError(f'Unsupported MessagePack marker {m:#x}')
        return simple[m]()


def master_tables(plain):
    """Table name -> compressed chunk of a MasterMemory container, or None."""
    try:
        reader = Reader(plain)
        header = reader.value()
        if not isinstance(header, dict) or not header:
            return None
        start = reader.pos
        return {name: plain[start+offset:start+offset+length] for name, (offset, length) in header.items()}
    except (ValueError, TypeError, UnicodeDecodeError, struct.error):
        return None


def decode_table(chunk):
    if chunk[0] in (0xc7, 0xc8, 0xc9):
        size_bytes = {0xc7: 1, 0xc8: 2, 0xc9: 4}[chunk[0]]
        kind = chunk[1+size_bytes]
        payload = chunk[2+size_bytes:]
        if kind == 99:
            sizes = Reader(payload)
            size = sizes.value()
            chunk = lz4.block.decompress(payload[sizes.pos:], uncompressed_size=size)
    reader = Reader(chunk)
    value = reader.value()
    if reader.pos != len(chunk):
        raise ValueError('MessagePack value left trailing bytes')
    return value


def flatten_kvrf(data):
    """All string entries of a localization file, as one dictionary."""
    import zlib
    if data[:4] != b'KVRF':
        raise ValueError('Invalid localization header')
    u32 = lambda p: struct.unpack_from('<I', data, p)[0]
    values = {}
    seen = set()
    for bucket in range(u32(0x14)):
        offset = u32(u32(0x20)+bucket*4)
        while offset and offset not in seen:
            seen.add(offset)
            following = u32(offset)
            compression = data[offset+0x0a]
            size = u32(offset+0x0c)
            body = data[offset+16:offset+16+size]
            if compression == 1:
                body = zlib.decompress(body[4:], -15)
            if compression in (0, 1) and len(body) >= 2:
                key_size = struct.unpack_from('<H', body)[0]
                if len(body) >= 2+key_size:
                    key = body[2:2+key_size].decode('utf-16-le', 'replace')
                    if key:
                        value = body[2+key_size:].decode('utf-16-le', 'replace')
                        if not (values.get(key) and not value):
                            values[key] = value
            offset = following
    return values


class Device:
    """The Android device or emulator that has the game, rooted, over ADB."""

    def __init__(self, log):
        self.log = log
        # Most emulators only appear once ADB connects to their port, even while another one is listed.
        for address in EMULATOR_ADDRESSES:
            subprocess.run([adb(), 'connect', address], capture_output=True, timeout=10)
        # ADB can report no device for a few seconds while its connection comes back.
        for attempt in range(6):
            serials = self.devices()
            if serials:
                break
            time.sleep(3)
        if not serials:
            raise ValueError('No device answers ADB: start the emulator, enable ADB in its settings, and try again.')
        self.serial = self.choose(serials)
        self.root = self.root_form()

    def devices(self):
        """Each device once: an emulator is often listed by its name and by one or two ports."""
        lines = self.adb('devices').splitlines()[1:]
        listed = [line.split('\t')[0] for line in lines if line.strip().endswith('device')]
        # emulator-5554 first, then 127.0.0.1: the names that survive an ADB restart.
        listed.sort(key=lambda serial: (not serial.startswith('emulator-'), not serial.startswith('127.0.0.1:')))
        devices = {}
        for serial in listed:
            try:
                identity = self.adb('shell', 'getprop ro.product.model; settings get secure android_id', serial=serial).strip()
            except ValueError:
                continue
            devices.setdefault(identity, serial)
        return list(devices.values())

    def choose(self, serials):
        wanted = os.environ.get('ANDROID_SERIAL')
        if wanted:
            if wanted not in serials:
                raise ValueError(f'ANDROID_SERIAL={wanted} is not among the devices: {", ".join(serials)}')
            return wanted
        with_game = [serial for serial in serials if 'package:' in self.adb('shell', f'pm path {PACKAGE}', serial=serial)]
        if not with_game:
            raise ValueError(f'The game is not installed on {", ".join(serials)}.')
        if len(with_game) > 1:
            self.log(f'Several devices have the game, using {with_game[0]} (set ANDROID_SERIAL to choose).')
        return with_game[0]

    def root_form(self):
        """How this device runs a command as root: su -c (most emulators), su 0 (Android Studio), or adbd already root."""
        for form in ROOT_FORMS:
            try:
                if self.adb('shell', form.format(script='id -u'), serial=self.serial).strip() == '0':
                    return form
            except ValueError:
                continue
        raise ValueError('Root access is needed to read the game key: enable root in the emulator settings.')

    def adb(self, *args, serial=None):
        command = [adb()]+(['-s', serial] if serial else [])+list(args)
        result = subprocess.run(command, capture_output=True)
        if result.returncode and serial and ':' in serial:
            # An emulator's own ADB can restart the server, which drops the connections made by address.
            subprocess.run([adb(), 'connect', serial], capture_output=True, timeout=10)
            result = subprocess.run(command, capture_output=True)
        if result.returncode:
            message = (result.stderr or result.stdout).decode(errors='replace').strip()
            raise ValueError(f'ADB failed on {serial or "the server"}: {message}')
        return result.stdout.decode('utf-8', 'replace')

    def shell(self, script):
        return self.adb('shell', self.root.format(script=script), serial=self.serial)

    def version(self):
        output = self.adb('shell', f'dumpsys package {PACKAGE}', serial=self.serial)
        fields = {}
        for line in output.splitlines():
            for token in line.split():
                if token.startswith(('versionName=', 'versionCode=')):
                    key, value = token.split('=', 1)
                    fields.setdefault(key, value)
        if 'versionName' not in fields:
            raise ValueError('The game is not installed on this device.')
        return fields['versionName'], fields['versionCode']

    def apk(self, part, label):
        for line in self.adb('shell', f'pm path {PACKAGE}', serial=self.serial).splitlines():
            if part in line:
                return line.split(':', 1)[1].strip()
        raise ValueError(f'{label} not found on the device.')

    def asset_pack(self):
        return self.apk('bundledtree', 'The asset pack')

    def base_apk(self):
        return self.apk('base.apk', 'The game APK')

    def pull(self, remote, local):
        local.parent.mkdir(parents=True, exist_ok=True)
        self.adb('pull', remote, str(local), serial=self.serial)

    def untar(self, base, names, receive):
        """Stream files from the device: one batched tar is far faster than one pull per file,
        and a few streams at once twice as fast as one (measured on BlueStacks: 13 to 24 MB/s).
        Each file goes to receive(name, bytes) as it arrives: written to disk and read back,
        it waited 5 to 9 ms for the antivirus, about 9 minutes for a whole game."""
        batches = [names[start:start+TAR_BATCH] for start in range(0, len(names), TAR_BATCH)]
        done = [0]
        lock = threading.Lock()

        def stream(batch):
            process = subprocess.Popen([adb(), '-s', self.serial, 'exec-out', f"cd {base} && tar -cf - {' '.join(batch)} 2>/dev/null"], stdout=subprocess.PIPE)
            with tarfile.open(fileobj=process.stdout, mode='r|') as archive:
                for member in archive:
                    if member.isfile():
                        with archive.extractfile(member) as source:
                            receive(member.name, source.read())
            process.wait()
            with lock:
                done[0] += len(batch)
                # The last batches stream alone, more slowly: the final count says the transfer is over.
                if done[0] % (TAR_BATCH*10) < len(batch) or done[0] == len(names):
                    self.log(f'Transfer: {done[0]}/{len(names)}')
        with concurrent.futures.ThreadPoolExecutor(TRANSFER_STREAMS) as pool:
            list(pool.map(stream, batches))


def plan_update(device, log):
    """Read the indexes and report what differs from game/."""
    WORK.mkdir(parents=True, exist_ok=True)
    index_dir = WORK/'index'
    shutil.rmtree(index_dir, ignore_errors=True)
    keys = device.shell(f'find {REMOTE_FILES}/DefaultMasterData -type f').split()
    if len(keys) != 1:
        raise ValueError('Game key not found: open the game once on the device.')
    key_path = WORK/Path(keys[0]).name
    device.pull(keys[0], key_path)
    key = default_key(key_path)
    device.pull(f'{REMOTE_FILES}/Sharin.Resources/Default/index', index_dir)
    pack = WORK/'split_bundledtree.apk'
    log('Reading the asset pack…')
    device.pull(device.asset_pack(), pack)
    records = {}
    with zipfile.ZipFile(pack) as archive:
        for name in archive.namelist():
            if name.startswith('assets/assetpack/index/') and name.endswith('.aladin'):
                raw = archive.read(name)
                plain = raw if raw[4:8] == b'ALI2' else decrypt_blob(raw, key, int(Path(name).stem, 16))
                for row in index_rows(plain):
                    records[row['name']] = dict(row, namespace='AssetPack')
    for path in sorted(index_dir.rglob('*.aladin')):
        raw = path.read_bytes()
        plain = raw if raw[4:8] == b'ALI2' else decrypt_blob(raw, key, int(path.stem, 16))
        for row in index_rows(plain):
            records[row['name']] = dict(row, namespace='Default')
    wanted = {name: row for name, row in records.items() if name.endswith('_bundles') or name in ('UnityMonoScripts', 'UnityBuiltInShaders')}
    return key, pack, wanted


def read_master(device, log):
    """Master tables from the Sharin.Resources/aladin namespace."""
    folder = WORK/'aladin'
    shutil.rmtree(folder, ignore_errors=True)
    device.pull(f'{REMOTE_FILES}/Sharin.Resources/aladin', folder)
    rows = {}
    for index in folder.rglob('*.aladin'):
        if 'index' in index.parts:
            raw = index.read_bytes()
            plain = raw if raw[4:8] == b'ALI2' else decrypt_blob(raw, MASTER_KEY, int(index.stem, 16))
            rows.update({row['blob']: row for row in index_rows(plain)})
    for path in folder.rglob('*.aladin'):
        row = rows.get(path.stem)
        if 'blob' not in path.parts or row is None:
            continue
        raw = path.read_bytes()
        plain = raw if int(row['keyId'], 16) == 0 else decrypt_blob(raw, MASTER_KEY, int(row['content'], 16))
        tables = master_tables(plain)
        if tables:
            return {name: decode_table(chunk) for name, chunk in tables.items()}
    raise ValueError('Game tables not found on the device.')


_packs = {}


def decrypt_plain(row, key, raw, target):
    """Decrypt one changed file into game-update/new and check its content hash.
    Returns its name and the serialized files it holds, read while still in memory."""
    plain = raw if int(row['keyId'], 16) == 0 else decrypt_blob(raw, key, int(row['content'], 16))
    if xxhash.xxh64(plain).hexdigest() != row['content']:
        raise ValueError(f"Wrong content hash for {row['name']}")
    output = Path(target)/row['name']
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(plain)
    return row['name'], cab_names(plain)


def decrypt_packed(job):
    """A changed file the asset pack holds."""
    row, key, pack, target = job
    archive = _packs.get(pack)
    if archive is None:
        archive = _packs[pack] = zipfile.ZipFile(pack)
    blob = row['blob']
    return decrypt_plain(row, key, archive.read(f'assets/assetpack/blob/{blob[:2]}/{blob}.aladin'), target)


def fetch_changed(device, key, pack, changed, log):
    """Decrypt each changed file into game-update/new and verify its content hash."""
    target = WORK/'new'
    shutil.rmtree(target, ignore_errors=True)
    log(f'{len(changed)} files to fetch')
    with zipfile.ZipFile(pack) as archive:
        entries = set(archive.namelist())
    # A blob comes from the assets pack when it holds it, otherwise from the downloaded files.
    packed, remote = [], {}
    for row in changed:
        name = f"{row['blob'][:2]}/{row['blob']}.aladin"
        if 'assets/assetpack/blob/'+name in entries:
            packed.append(row)
        else:
            remote[name] = row
    received = set()
    # One file per job on every core: the cipher runs at about 60 MB/s on one.
    with concurrent.futures.ProcessPoolExecutor(DECRYPT_WORKERS) as pool:
        jobs = [pool.submit(decrypt_packed, (row, key, str(pack), str(target))) for row in packed]

        # Files waiting for a process stay in memory: past this, the transfer waits for them.
        pending, room = [0], threading.Condition()

        def finished(size):
            with room:
                pending[0] -= size
                room.notify_all()

        def receive(name, raw):
            received.add(name)
            with room:
                room.wait_for(lambda: pending[0] < DECRYPT_BACKLOG)
                pending[0] += len(raw)
            job = pool.submit(decrypt_plain, remote[name], key, raw, str(target))
            job.add_done_callback(lambda _, size=len(raw): finished(size))
            jobs.append(job)
        device.untar(f'{REMOTE_FILES}/Sharin.Resources/Default/blob', list(remote), receive)
        cabs = {}
        for i, job in enumerate(concurrent.futures.as_completed(jobs), 1):
            name, cabs[name] = job.result()
            if i % 10000 == 0:
                log(f'Decrypting: {i}/{len(jobs)}')
    missing = [row['name'] for name, row in remote.items() if name not in received]
    if missing:
        (WORK/'missing.txt').write_text(chr(10).join(missing), encoding='utf-8')
        raise ValueError(f'{len(missing)} files missing on the device: see {WORK/"missing.txt"}')
    return target, cabs


def fetch_player_data(device, log):
    """The APK's assets/bin/Data: plain serialized files (bloom, vignette, flip clips, TMP settings)."""
    apk = WORK/'base.apk'
    log('Reading the APK…')
    device.pull(device.base_apk(), apk)
    target = WORK/'Data'
    shutil.rmtree(target, ignore_errors=True)
    with zipfile.ZipFile(apk) as archive:
        for name in archive.namelist():
            relative = name.removeprefix('assets/bin/Data/')
            # Managed/ holds the IL2CPP metadata: the renderer never reads it.
            if relative == name or name.endswith('/') or relative.startswith('Managed/'):
                continue
            if '..' in Path(relative).parts:
                raise ValueError(f'Unsafe path in the APK: {name}')
            output = target/relative
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(archive.read(name))
    if not (target/'globalgamemanagers').is_file():
        raise ValueError('assets/bin/Data is missing from the game APK.')
    return target


def cab_names(data):
    # Read from memory: read back from disk, each new file waited 5 to 9 ms for the antivirus.
    env = UnityPy.load(data)
    return [name.lower() for name in env.file.files if not name.lower().endswith('.ress')]


def locale_tables(root_of):
    """Locale dictionaries read from the Common/Locale bundles."""
    result = {}
    for language, path in root_of.items():
        texts = {}
        for obj in UnityPy.load(path.read_bytes()).objects:
            if obj.type.name != 'TextAsset':
                continue
            text = obj.read()
            name = text.m_Name.removesuffix('.bytes')
            if name in LOCALE_TEXTS:
                raw = text.m_Script
                payload = raw.encode('utf-8', 'surrogateescape') if isinstance(raw, str) else bytes(raw)
                texts[name+'.bytes'] = flatten_kvrf(payload)
        result[language] = texts
    return result


def update(log=print):
    """Bring data/game to the version installed on the device. Returns True if it changed."""
    device = Device(log)
    version, code = device.version()
    log(f'{device.serial}: game {version}')
    key, pack, wanted = plan_update(device, log)
    index_path = GAME/'index.json'
    current = json.loads(index_path.read_text(encoding='utf-8')) if index_path.is_file() else dict(files=[])
    known = {row['name']: row for row in current['files']}
    changed = [row for name, row in wanted.items() if name not in known or known[name]['content'] != row['content']]
    removed = [name for name in known if name not in wanted]
    player_data = GAME/'Data'
    data_stale = current.get('versionCode') != code or not (player_data/'globalgamemanagers').is_file()
    if not changed and not removed and not data_stale:
        shutil.rmtree(WORK, ignore_errors=True)
        log(f'Already up to date: game {version}')
        return False
    new_data = fetch_player_data(device, log) if data_stale else None
    new_root, cabs = fetch_changed(device, key, pack, changed, log)
    tables = read_master(device, log)
    files = []
    for name, row in wanted.items():
        previous = known.get(name)
        if previous and previous['content'] == row['content']:
            files.append(previous)
        else:
            files.append(dict(name=name, blob=row['blob'], content=row['content'], cabs=cabs[name]))
    locale_paths = {path.name.removesuffix('_bundles'): path for path in (new_root/'Common/Locale').glob('*_bundles')} if (new_root/'Common/Locale').is_dir() else {}
    locales = locale_tables(locale_paths)
    # Everything is checked: game/ changes only from here on.
    for name in removed:
        (GAME/name).unlink(missing_ok=True)
    for row in changed:
        destination = GAME/row['name']
        destination.parent.mkdir(parents=True, exist_ok=True)
        (new_root/row['name']).replace(destination)
    if new_data:
        shutil.rmtree(player_data, ignore_errors=True)
        new_data.replace(player_data)
    master = GAME/'MasterData'
    master.mkdir(parents=True, exist_ok=True)
    for name, value in tables.items():
        (master/(name+'.json')).write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    (master/'Locale').mkdir(exist_ok=True)
    for language, content in locales.items():
        (master/'Locale'/(language+'.json')).write_text(json.dumps(content, ensure_ascii=False), encoding='utf-8')
    index_path.write_text(json.dumps(dict(version=version, versionCode=code, files=files)), encoding='utf-8')
    shutil.rmtree(WORK, ignore_errors=True)
    log(f'{len(changed)} files updated, {len(removed)} removed: game {version}')
    return True
