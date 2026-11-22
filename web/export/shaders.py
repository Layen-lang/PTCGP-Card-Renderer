"""Translate the game's Vulkan shader programs to GLSL ES 3.0 for the browser.

The bundles only hold stripped SMOL-V programs (platform 18). Nothing is rewritten
by hand: SMOL-V -> SPIR-V -> SPIRV-Cross. Only the names are restored, from the
shader's serialized parameters, so the engine binds everything by its Unity name.
"""
import ctypes as C
import struct
import subprocess
import tempfile
from pathlib import Path
import lz4.block
from UnityPy.export.ShaderConverter import ShaderSubProgram
from UnityPy.streams import EndianBinaryReader
from .paths import SMOLV_DECODE, SPIRV_CROSS

GLSL = 0x2000000
OPTION_VERSION, OPTION_ES, OPTION_FLOAT_HIGHP, OPTION_INT_HIGHP, OPTION_PLAIN_UNIFORMS = GLSL | 8, GLSL | 9, GLSL | 11, GLSL | 12, GLSL | 35
BACKEND_GLSL = 1
CAPTURE_SPIRV = 1
RESOURCE_UNIFORM_BUFFER, RESOURCE_STAGE_INPUT, RESOURCE_STAGE_OUTPUT, RESOURCE_SAMPLED_IMAGE, RESOURCE_SEPARATE_IMAGE = 1, 3, 4, 7, 10
DECORATION_LOCATION, DECORATION_BINDING, DECORATION_OFFSET = 30, 33, 35
VULKAN_PROGRAM = 25
# Parameter index: stage bits | binding.
VERTEX_STAGE, FRAGMENT_STAGE, CONSTANT_BUFFER = 0x4000000, 0x8000000, 0x10000
# Unity vertex channels by source index; target 13 is the first vertex input location.
CHANNELS = {0: 'position', 1: 'normal', 2: 'tangent', 3: 'color', 4: 'uv0', 5: 'uv1', 6: 'uv2', 7: 'uv3'}
FIRST_INPUT_TARGET = 13
ptr = C.c_void_p


class Resource(C.Structure):
    _fields_ = [('id', C.c_uint), ('base_type_id', C.c_uint), ('type_id', C.c_uint), ('name', C.c_char_p)]


class Combined(C.Structure):
    _fields_ = [('combined_id', C.c_uint), ('image_id', C.c_uint), ('sampler_id', C.c_uint)]


def library():
    dll = C.CDLL(str(SPIRV_CROSS))
    signatures = {
        'spvc_context_create': ([C.POINTER(ptr)], C.c_int),
        'spvc_context_destroy': ([ptr], None),
        'spvc_context_parse_spirv': ([ptr, C.POINTER(C.c_uint32), C.c_size_t, C.POINTER(ptr)], C.c_int),
        'spvc_context_create_compiler': ([ptr, C.c_int, ptr, C.c_int, C.POINTER(ptr)], C.c_int),
        'spvc_context_get_last_error_string': ([ptr], C.c_char_p),
        'spvc_compiler_compile': ([ptr, C.POINTER(C.c_char_p)], C.c_int),
        'spvc_compiler_build_combined_image_samplers': ([ptr], C.c_int),
        'spvc_compiler_get_combined_image_samplers': ([ptr, C.POINTER(C.POINTER(Combined)), C.POINTER(C.c_size_t)], C.c_int),
        'spvc_compiler_create_compiler_options': ([ptr, C.POINTER(ptr)], C.c_int),
        'spvc_compiler_options_set_uint': ([ptr, C.c_int, C.c_uint], C.c_int),
        'spvc_compiler_install_compiler_options': ([ptr, ptr], C.c_int),
        'spvc_compiler_create_shader_resources': ([ptr, C.POINTER(ptr)], C.c_int),
        'spvc_resources_get_resource_list_for_type': ([ptr, C.c_int, C.POINTER(C.POINTER(Resource)), C.POINTER(C.c_size_t)], C.c_int),
        'spvc_compiler_get_decoration': ([ptr, C.c_uint, C.c_int], C.c_uint),
        'spvc_compiler_get_name': ([ptr, C.c_uint], C.c_char_p),
        'spvc_compiler_set_name': ([ptr, C.c_uint, C.c_char_p], None),
        'spvc_compiler_get_type_handle': ([ptr, C.c_uint], ptr),
        'spvc_type_get_num_member_types': ([ptr], C.c_uint),
        'spvc_compiler_get_member_decoration': ([ptr, C.c_uint, C.c_uint, C.c_int], C.c_uint),
        'spvc_compiler_set_member_name': ([ptr, C.c_uint, C.c_uint, C.c_char_p], None),
    }
    for name, (arguments, result) in signatures.items():
        function = getattr(dll, name)
        function.argtypes, function.restype = arguments, result
    return dll


_dll = None


def to_glsl(binary, stage, parameters):
    """One SPIR-V module to GLSL ES 3.0, with its constants and textures named."""
    global _dll
    dll = _dll = _dll or library()
    context, ir, compiler, options, resources = ptr(), ptr(), ptr(), ptr(), ptr()

    def check(value):
        if value != 0:
            raise RuntimeError(f'SPIRV-Cross : {dll.spvc_context_get_last_error_string(context)}')

    def rows(kind):
        items, size = C.POINTER(Resource)(), C.c_size_t()
        check(dll.spvc_resources_get_resource_list_for_type(resources, kind, C.byref(items), C.byref(size)))
        return [items[i] for i in range(size.value)]
    check(dll.spvc_context_create(C.byref(context)))
    try:
        words = (C.c_uint32*(len(binary)//4)).from_buffer_copy(binary)
        check(dll.spvc_context_parse_spirv(context, words, len(words), C.byref(ir)))
        check(dll.spvc_context_create_compiler(context, BACKEND_GLSL, ir, CAPTURE_SPIRV, C.byref(compiler)))
        check(dll.spvc_compiler_create_compiler_options(compiler, C.byref(options)))
        for option, value in ((OPTION_VERSION, 300), (OPTION_ES, 1), (OPTION_FLOAT_HIGHP, 1), (OPTION_INT_HIGHP, 1), (OPTION_PLAIN_UNIFORMS, 1)):
            check(dll.spvc_compiler_options_set_uint(options, option, value))
        check(dll.spvc_compiler_install_compiler_options(compiler, options))
        check(dll.spvc_compiler_create_shader_resources(compiler, C.byref(resources)))
        prefix = 'vs' if stage == 'vertex' else 'fs'
        for number, row in enumerate(rows(RESOURCE_UNIFORM_BUFFER)):
            binding = dll.spvc_compiler_get_decoration(compiler, row.id, DECORATION_BINDING)
            members = parameters['buffers'][stage].get(binding)
            if members is None:
                raise ValueError(f'Unknown constant block: {stage} {binding}')
            handle = dll.spvc_compiler_get_type_handle(compiler, row.base_type_id)
            for index in range(dll.spvc_type_get_num_member_types(handle)):
                offset = dll.spvc_compiler_get_member_decoration(compiler, row.base_type_id, index, DECORATION_OFFSET)
                # SRP batcher blocks may list no names: the export fails if such a constant is used.
                name = members.get(offset, f'unnamed{binding}_{offset}')
                members.setdefault(offset, name)
                dll.spvc_compiler_set_member_name(compiler, row.base_type_id, index, name.encode())
            # Plain uniforms keep the block's instance: each stage needs its own name.
            dll.spvc_compiler_set_name(compiler, row.id, f'{prefix}{number}'.encode())
            dll.spvc_compiler_set_name(compiler, row.base_type_id, f'{prefix}Block{number}'.encode())
        for kind in (RESOURCE_SAMPLED_IMAGE, RESOURCE_SEPARATE_IMAGE):
            for row in rows(kind):
                binding = dll.spvc_compiler_get_decoration(compiler, row.id, DECORATION_BINDING)
                if binding not in parameters['textures']:
                    raise ValueError(f'Texture inconnue : binding {binding}')
                dll.spvc_compiler_set_name(compiler, row.id, parameters['textures'][binding].encode())
        # A combined sampler keeps the name of its texture: that is the name materials know.
        check(dll.spvc_compiler_build_combined_image_samplers(compiler))
        combined, count = C.POINTER(Combined)(), C.c_size_t()
        check(dll.spvc_compiler_get_combined_image_samplers(compiler, C.byref(combined), C.byref(count)))
        for i in range(count.value):
            dll.spvc_compiler_set_name(compiler, combined[i].combined_id, dll.spvc_compiler_get_name(compiler, combined[i].image_id))
        # GLSL ES 3.0 links varyings by name: both stages name them after their location.
        for row in rows(RESOURCE_STAGE_OUTPUT if stage == 'vertex' else RESOURCE_STAGE_INPUT):
            location = dll.spvc_compiler_get_decoration(compiler, row.id, DECORATION_LOCATION)
            dll.spvc_compiler_set_name(compiler, row.id, f'varying{location}'.encode())
        source = C.c_char_p()
        check(dll.spvc_compiler_compile(compiler, C.byref(source)))
        return polyfill(source.value.decode())
    finally:
        dll.spvc_context_destroy(context)


class BlobReader:
    def __init__(self, data):
        self.data, self.offset = data, 0

    def word(self):
        value = struct.unpack_from('<I', self.data, self.offset)[0]
        self.offset += 4
        return value

    def name(self):
        size = self.word()
        text = self.data[self.offset:self.offset+size].decode()
        self.offset += (size+3) & ~3
        return text


def variant_parameters(entry):
    """Constant buffers, buffer bindings and textures a shader with several variants stores per variant."""
    reader = BlobReader(entry)
    reader.word()
    buffers = reader.word()-1
    if any(reader.word() for _ in range(4)):
        raise ValueError('Variant parameters not ported')
    blocks, bindings, textures = {}, {}, {}
    for _ in range(buffers):
        name, _size, members = reader.name(), reader.word(), {}
        for _ in range(reader.word()):
            member = reader.name()
            _type, _rows, _columns, _matrix, _array, offset = [reader.word() for _ in range(6)]
            members[offset] = member
        if reader.word():
            raise ValueError('Constant structure not ported')
        blocks[name] = members
    for _ in range(reader.word()):
        name, _array, index, _sampler = reader.name(), reader.word(), reader.word(), reader.word()
        if index & CONSTANT_BUFFER:
            bindings[name] = index
        else:
            reader.word()
            textures[index & 0xffff] = name
    return blocks, bindings, textures


def polyfill(source):
    """GLSL ES 3.0 lacks bitfieldInsert, which full-screen triangles use on gl_VertexID."""
    if 'bitfieldInsert' not in source:
        return source
    head, body = source.split('\n', 1)
    helper = 'int bitfieldInsert(int base, int insert, int offset, int bits) { int mask = ((1 << bits) - 1) << offset; return (base & ~mask) | ((insert << offset) & mask); }\n'
    return f'{head}\n{helper}{body}'


def parameters_of(program, entry):
    """Binding to Unity name: the pass's common parameters, then those of the variant."""
    names = {index: name for name, index in program['m_NameIndices']}
    common = program['progVertex']['m_CommonParameters']
    textures = {row['m_Index'] & 0xffff: names[row['m_NameIndex']] for row in common['m_TextureParams']}
    blocks = {names[row['m_NameIndex']]: {p['m_Index']: names[p['m_NameIndex']] for key in ('m_MatrixParams', 'm_VectorParams') for p in row[key]}
              for row in common['m_ConstantBuffers']}
    bindings = {names[row['m_NameIndex']]: row['m_Index'] for row in common['m_ConstantBufferBindings']}
    variant_blocks, variant_bindings, variant_textures = variant_parameters(entry)
    for name, members in variant_blocks.items():
        blocks.setdefault(name, {}).update(members)
    bindings.update(variant_bindings)
    textures.update(variant_textures)
    buffers = dict(vertex={}, fragment={})
    for name, index in bindings.items():
        if index & VERTEX_STAGE:
            buffers['vertex'][index & 0xffff] = blocks[name]
        if index & FRAGMENT_STAGE:
            buffers['fragment'][index & 0xffff] = blocks[name]
    return dict(textures=textures, buffers=buffers)


def programs_of(tree):
    """Decompressed Vulkan segment of a Shader type tree."""
    platform = tree['platforms'].index(18)
    begin = tree['offsets'][platform][0]
    length = tree['compressedLengths'][platform][0]
    return lz4.block.decompress(bytes(tree['compressedBlob'])[begin:begin+length], uncompressed_size=tree['decompressedLengths'][platform][0])


def entries(segment):
    """Blob table of a segment: offset and size of each program or parameter entry."""
    count = struct.unpack_from('<I', segment)[0]
    return [struct.unpack_from('<II', segment, 4+i*12) for i in range(count)]


def read_program(entry):
    """The SMOL-V modules of a Vulkan program in stage order (vertex, then fragment), and {location: channel}."""
    if struct.unpack_from('<II', entry)[1] != VULKAN_PROGRAM:
        raise ValueError('Programme Vulkan attendu')
    reader = EndianBinaryReader(entry, endian='<')
    code = bytes(ShaderSubProgram(reader).m_ProgramCode)
    # The channel table follows the code, aligned, after one word.
    offset = (reader.Position+3) & ~3
    count = struct.unpack_from('<I', entry, offset+4)[0]
    pairs = struct.unpack_from(f'<{count*2}I', entry, offset+8)
    channels = {str(target-FIRST_INPUT_TARGET): CHANNELS[source] for source, target in zip(pairs[::2], pairs[1::2])}
    first = code.find(b'LOMS')
    if first < 0:
        raise ValueError('Programme Vulkan sans SMOL-V')
    found, seen = [], set()
    for offset in range(4, first-7, 4):
        start, length = struct.unpack_from('<II', code, offset)
        if start in seen or length < 24 or start+length > len(code) or code[start:start+4] != b'LOMS':
            continue
        seen.add(start)
        found.append(code[start:start+length])
    return found, channels


def decode_smolv(smol):
    with tempfile.TemporaryDirectory() as folder:
        source, target = Path(folder)/'in.smolv', Path(folder)/'out.spv'
        source.write_bytes(smol)
        subprocess.run([str(SMOLV_DECODE), str(source), str(target)], check=True)
        binary = target.read_bytes()
    if binary[:4] != bytes.fromhex('03022307') or len(binary) % 4:
        raise ValueError('SPIR-V invalide')
    return binary


def variant(program, keywords):
    """The sub-program and parameter blob whose keywords are exactly the active ones."""
    stage = program['progVertex']
    for rows, blobs in zip(stage['m_PlayerSubPrograms'], stage['m_ParameterBlobIndices']):
        for row, blob in zip(rows, blobs):
            if row['m_GpuProgramType'] == VULKAN_PROGRAM and set(row['m_KeywordIndices']) == keywords:
                return row['m_BlobIndex'], blob
    raise ValueError(f'No variant for the keywords {sorted(keywords)}')


def translate_pass(tree, program, keywords):
    """GLSL of one pass for the keyword indices active on its material."""
    segment = programs_of(tree)
    table = entries(segment)

    def entry_at(index):
        offset, size = table[index]
        return segment[offset:offset+size]
    code, blob = variant(program, keywords)
    stages, channels = read_program(entry_at(code))
    if len(stages) != 2:
        raise ValueError(f'{len(stages)} stages in the Vulkan program')
    parameters = parameters_of(program, entry_at(blob))
    # Layers share the same plane and depth test: their positions must match to the last bit.
    vertex = to_glsl(decode_smolv(stages[0]), 'vertex', parameters).replace('#version 300 es\n', '#version 300 es\ninvariant gl_Position;\n', 1)
    return dict(vertex=vertex,
                fragment=to_glsl(decode_smolv(stages[1]), 'fragment', parameters),
                attributes=channels,
                # Variants name their constants in their own blob, not in the pass's name table.
                samplers=sorted(parameters['textures'].values()),
                names=sorted({*parameters['textures'].values(), *(name for stage in parameters['buffers'].values() for members in stage.values() for name in members.values())}))
