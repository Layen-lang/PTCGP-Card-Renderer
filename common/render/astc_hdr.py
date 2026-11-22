"""Decode ASTC HDR into RGBA half floats through Unity's bundled Arm codec.

API: https://github.com/ARM-software/astc-encoder/blob/main/Source/astcenc.h
No compression, tone mapping, gamma conversion, mip generation or flip.
"""
import ctypes as C
from pathlib import Path
import numpy as np
from .paths import TOOLS

DLL = TOOLS/'astcenc/astcenc-sse2.dll'


class Image(C.Structure):
    _fields_ = [('x', C.c_uint), ('y', C.c_uint), ('z', C.c_uint), ('type', C.c_int), ('data', C.POINTER(C.c_void_p))]


def decode(data, width, height, block):
    codec = C.CDLL(str(DLL))
    codec.astcenc_config_init.argtypes = [C.c_int, C.c_uint, C.c_uint, C.c_uint, C.c_float, C.c_uint, C.c_void_p]
    codec.astcenc_context_alloc.argtypes = [C.c_void_p, C.c_uint, C.POINTER(C.c_void_p), C.c_void_p]
    codec.astcenc_decompress_image.argtypes = [C.c_void_p, C.c_void_p, C.c_size_t, C.POINTER(Image), C.c_void_p, C.c_uint]
    codec.astcenc_context_free.argtypes = [C.c_void_p]
    codec.astcenc_context_free.restype = None
    codec.astcenc_get_error_string.argtypes = [C.c_int]
    codec.astcenc_get_error_string.restype = C.c_char_p

    def check(result):
        if result:
            raise RuntimeError(codec.astcenc_get_error_string(result).decode())
    # Only the codec accesses its config layout. Overallocate aligned storage
    # to avoid depending on the version-specific compression tuning fields.
    config = (C.c_uint64*1024)()
    check(codec.astcenc_config_init(3, block, block, 1, 0, 16, config))
    if bytes(config)[4096:] != bytes(4096):
        raise RuntimeError('Unexpected config ABI size')
    context = C.c_void_p()
    check(codec.astcenc_context_alloc(config, 1, C.byref(context), None))
    try:
        output = (C.c_uint16*(width*height*4))()
        slices = (C.c_void_p*1)(C.addressof(output))
        image = Image(width, height, 1, 1, slices)
        swizzle = (C.c_int*4)(0, 1, 2, 3)
        source = C.create_string_buffer(data)
        check(codec.astcenc_decompress_image(context, source, len(data), C.byref(image), swizzle, 0))
        raw = bytes(output)
        if not np.isfinite(np.frombuffer(raw, dtype='<f2')).all():
            raise ValueError('ASTC HDR invalid/nonfinite texels')
        return raw
    finally:
        codec.astcenc_context_free(context)
