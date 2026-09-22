"""Read one timing curve from a supplied original mhy1 game block.

The decoder follows Escartem/AnimeStudio (MIT), tree
02994d5c30a43d0d5c5124557ec361ccbbad24c0. Its C# tables and native
Ooz library are external inputs pinned below, never distributed here.
"""
import argparse
import ctypes
import hashlib
import io
import json
import math
import os
import re
import struct
import tempfile
from pathlib import Path


PINNED = {
    "crypto-source": "68d3169d9d0bce93c1a70b858ac0843136fb197a426cb040ab9e5f85f0b0688d",
    "mhy-source": "de4d184056dbf43db7c730fd68c660a4a608470b2d74cb004c660b18cdedcf6a",
    "ooz-dll": "c4bdb9df0ef484e5e17ee1d32359769662055da7460569cb10aa9f303d706d27",
}
MAX_BUNDLE = 256 * 1024 * 1024


def require(condition, message):
    if not condition:
        raise ValueError(message)


def pinned_bytes(path, kind):
    data = path.read_bytes()
    actual = hashlib.sha256(data).hexdigest()
    require(actual == PINNED[kind], f"{kind} SHA-256 mismatch: {actual}")
    return data


def cs_array(source, name):
    match = re.search(r"\b" + re.escape(name) + r"\s*=\s*new byte\[.*?\]\s*\{(.*?)\}", source, re.S)
    require(match is not None, f"missing decoder table {name}")
    return [int(part.strip(), 0) for part in match.group(1).split(",") if part.strip()]


def decoder_tables(crypto_source, mhy_source):
    crypto = crypto_source.decode("utf-8-sig")
    mhy = mhy_source.decode("utf-8-sig")
    tables = [cs_array(crypto, name) for name in
              ("GIMhyShiftRow", "GIMhyKey", "GIMhyMul", "GISBox", "GF256Exp", "GF256Log")]
    skey = cs_array(mhy, "Key")
    require(len(skey) == 256 and len(tables[0]) >= 48 and len(tables[1]) >= 8 and
            len(tables[2]) >= 8 and len(tables[3]) >= 1024 and
            len(tables[4]) >= 255 and len(tables[5]) >= 256,
            "decoder table length mismatch")
    return (*tables, skey)


def descramble(raw, entry_size, tables):
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

    shift, key, mul, box, exp, log, skey = tables
    out = bytearray(raw)
    require(len(out) >= 55, "short mhy header")

    def scramble_chunk(chunk):
        values = list(chunk)
        for i in range(3):
            next_values = []
            for j in range(len(values)):
                a, b = mul[j % 8], values[shift[(2 - i) * 16 + j] % len(values)]
                product = 0 if a == 0 or b == 0 else exp[(log[a] + log[b]) % 255]
                next_values.append(key[j % 8] ^ box[(j % 4 * 256) | product])
            values = next_values
        return bytes(values)

    out[4:20] = scramble_chunk(out[4:20])
    require(out[4:12] == b"mhynewec", "mhy signature mismatch")
    out[20:36] = scramble_chunk(out[20:36])
    encryptor = Cipher(algorithms.AES(bytes(out[:16])), modes.ECB()).encryptor()
    out[20:36] = encryptor.update(bytes(out[20:36])) + encryptor.finalize()
    for i in range(4):
        out[i] ^= out[20 + i]
    cipher_key, operations = out[20:28], out[28:36]
    state = skey.copy()
    j = 0
    for i in range(256):
        j = (j + state[i] + cipher_key[i % 8]) % 256
        state[j], state[i] = state[i], state[j]
    i = j = 0
    for p in range(20 + ((entry_size + 15) // 16 * 16), min(len(out), 128)):
        i = (i + 1) % 256
        j = (j + state[i]) % 256
        state[j], state[i] = state[i], state[j]
        value = state[(state[j] + state[i]) % 256]
        mode = operations[i % 8] % 3
        out[p] = (out[p] ^ value) if mode == 0 else ((out[p] - value if mode == 1 else out[p] + value) & 255)
    return bytes(out)


def mhy_uint(data):
    require(len(data) == 7, "short mhy unsigned integer")
    return data[1] | data[6] << 8 | data[3] << 16 | data[2] << 24


def mhy_int(data):
    require(len(data) == 6, "short mhy integer")
    return data[2] | data[4] << 8 | data[0] << 16 | data[5] << 24


def ooz_decompress(lib, packed, size):
    require(0 < size <= MAX_BUNDLE, "invalid decompressed size")
    fn = lib.Ooz_Decompress
    ptr, integer, usize = ctypes.c_void_p, ctypes.c_int, ctypes.c_size_t
    fn.argtypes = [ptr, integer, ptr, usize, integer, integer, integer, ptr, usize, ptr, ptr, ptr, usize, integer]
    fn.restype = integer
    source = ctypes.create_string_buffer(packed, len(packed) + 64)
    destination = ctypes.create_string_buffer(size + 64)
    actual = fn(source, len(packed), destination, size, 1, 0, 0, None, 0, None, None, None, 0, 3)
    require(actual == size, f"Ooz decompression failed: {actual} != {size}")
    return destination.raw[:size]


def read_exact(stream, length):
    require(0 <= length <= MAX_BUNDLE, "invalid read length")
    data = stream.read(length)
    require(len(data) == length, "truncated mhy bundle")
    return data


def unpack_bundle(block, offset, bundle_size, tables, lib):
    require(0 <= offset < len(block) and 8 <= bundle_size <= MAX_BUNDLE and
            offset + bundle_size <= len(block), "bundle bounds invalid")
    with io.BytesIO(block) as stream:
        stream.seek(offset)
        header = read_exact(stream, 8)
        require(header[:4] == b"mhy1", "mhy1 magic mismatch")
        header_size = struct.unpack_from("<I", header, 4)[0]
        require(55 <= header_size <= 1_000_000 and header_size + 8 <= bundle_size,
                "mhy header size invalid")
        info = descramble(read_exact(stream, header_size), 28, tables)
        decoded = ooz_decompress(lib, info[55:], mhy_uint(info[48:55]))
        reader = io.BytesIO(decoded)
        node_count = mhy_int(read_exact(reader, 6))
        require(0 < node_count < 100_000 and node_count * 275 <= len(decoded), "node directory bounds invalid")
        nodes = []
        for _ in range(node_count):
            name = read_exact(reader, 261).split(b"\0", 1)[0].decode("utf-8")
            flag = read_exact(reader, 1)[0]
            start = mhy_int(read_exact(reader, 6))
            size = mhy_uint(read_exact(reader, 7))
            nodes.append((name, flag, start, size))
        block_count = mhy_int(read_exact(reader, 6))
        require(0 < block_count < 100_000 and block_count * 13 <= len(decoded) - reader.tell(),
                "block directory bounds invalid")
        blocks = [(mhy_int(read_exact(reader, 6)), mhy_uint(read_exact(reader, 7))) for _ in range(block_count)]
        full = bytearray()
        for packed, unpacked in blocks:
            require(28 <= packed <= bundle_size and len(full) + unpacked <= MAX_BUNDLE and
                    stream.tell() + packed <= offset + bundle_size, "packed block bounds invalid")
            data = descramble(read_exact(stream, packed), 8, tables)
            full.extend(ooz_decompress(lib, data[28:], unpacked))
        require(stream.tell() == offset + bundle_size, "bundle length mismatch")
        for name, flag, start, size in nodes:
            require(start + size <= len(full), "node bounds invalid")
        return [(name, flag, bytes(full[start:start + size])) for name, flag, start, size in nodes]


def unity_objects(data):
    """Only the Unity v21 object directory needed for this curve bundle."""
    require(len(data) >= 32, "short serialized file")
    meta, size, version, start = struct.unpack_from(">4I", data)
    require(version == 21 and size == len(data) and data[16] == 0 and
            20 <= meta <= start <= size, "invalid Unity v21 header")
    p = data.index(0, 20, meta) + 1

    def take(fmt):
        nonlocal p
        amount = struct.calcsize(fmt)
        require(p + amount <= meta, "truncated Unity metadata")
        result = struct.unpack_from(fmt, data, p)
        p += amount
        return result if len(result) != 1 else result[0]

    take("<i")  # platform
    tree = take("<B")
    count = take("<i")
    require(0 <= count < 100_000, "type count invalid")
    types = []
    for _ in range(count):
        class_id = take("<i")
        take("<3B")
        if class_id == 114:
            take("<16s")
        take("<16s")
        types.append(class_id)
        if tree:
            nodes, strings = take("<ii")
            require(0 <= nodes < 100_000 and 0 <= strings < 10_000_000 and
                    p + nodes * 32 + strings <= meta, "type tree bounds invalid")
            p += nodes * 32 + strings
            dependencies = take("<i")
            require(0 <= dependencies < 100_000 and p + dependencies * 4 <= meta,
                    "type dependencies bounds invalid")
            p += dependencies * 4
    count = take("<i")
    require(0 <= count < 1_000_000, "object count invalid")
    result = []
    for _ in range(count):
        p = (p + 3) // 4 * 4
        path_id, offset, length, typ = take("<qIIi")
        require(0 <= typ < len(types) and start + offset + length <= size,
                "object bounds invalid")
        result.append(dict(id=path_id, type=types[typ], start=start + offset,
                           size=length, data=data[start + offset:start + offset + length]))
    return result


CURVE_PATTERN = re.compile(rb"Monster_Execute_[A-Za-z_]+_WitchSlowDownCurve")


def extract_curve(data, name):
    require(CURVE_PATTERN.fullmatch(name.encode("ascii")) is not None, "invalid curve name")
    objects = [obj for obj in unity_objects(data) if obj["type"] == 114]
    require(len(objects) == 1, "expected one MonoBehaviour object")
    obj = objects[0]
    body = obj["data"]
    matches = [match for match in CURVE_PATTERN.finditer(body) if match.group().decode() == name]
    require(len(matches) == 1, "named curve missing or ambiguous")
    match = matches[0]
    require(match.start() >= 4 and struct.unpack_from("<I", body, match.start() - 4)[0] == len(name),
            "curve string length mismatch")
    p = (match.end() + 3) // 4 * 4
    require(p + 4 <= len(body), "curve key count truncated")
    count = struct.unpack_from("<I", body, p)[0]
    require(1 <= count <= 32 and p + 4 + count * 28 + 12 <= len(body), "curve keys truncated or invalid")
    p += 4
    keys = []
    for _ in range(count):
        values = struct.unpack_from("<ffffiff", body, p)
        p += 28
        require(all(math.isfinite(value) for value in (values[0], values[1], values[2], values[3], values[5], values[6])),
                "curve contains non-finite number")
        keys.append(dict(zip(("time", "value", "inSlope", "outSlope", "weightedMode", "inWeight", "outWeight"), values)))
    require(all(keys[i]["time"] < keys[i + 1]["time"] for i in range(len(keys) - 1)),
            "curve key times invalid")
    pre, post, rotation = struct.unpack_from("<iii", body, p)
    return dict(objectId=str(obj["id"]), objectStart=obj["start"], objectSize=obj["size"],
                curveOffset=match.start() - 4, name=name, keys=keys,
                preInfinity=pre, postInfinity=post, rotationOrder=rotation)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--block", required=True, type=Path)
    parser.add_argument("--block-sha256", required=True)
    parser.add_argument("--offset", required=True, type=int)
    parser.add_argument("--bundle-size", required=True, type=int)
    parser.add_argument("--name", required=True)
    parser.add_argument("--crypto-source", required=True, type=Path)
    parser.add_argument("--mhy-source", required=True, type=Path)
    parser.add_argument("--ooz-dll", required=True, type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    require(re.fullmatch(r"[0-9a-f]{64}", args.block_sha256) is not None,
            "block SHA-256 must be lowercase hex")
    block = args.block.read_bytes()
    require(hashlib.sha256(block).hexdigest() == args.block_sha256, "block SHA-256 mismatch")
    crypto = pinned_bytes(args.crypto_source, "crypto-source")
    mhy = pinned_bytes(args.mhy_source, "mhy-source")
    ooz = pinned_bytes(args.ooz_dll, "ooz-dll")
    tables = decoder_tables(crypto, mhy)
    require(os.name == "nt", "pinned Ooz DLL requires Windows")
    with tempfile.TemporaryDirectory(prefix="curve-ooz-") as directory:
        dll_snapshot = Path(directory) / "pinned-ooz.dll"
        with dll_snapshot.open("xb") as stream:
            stream.write(ooz)
        require(hashlib.sha256(dll_snapshot.read_bytes()).hexdigest() == PINNED["ooz-dll"],
                "private Ooz snapshot SHA-256 mismatch")
        lib = ctypes.CDLL(str(dll_snapshot))
        try:
            nodes = unpack_bundle(block, args.offset, args.bundle_size, tables, lib)
        finally:
            # Windows keeps a loaded DLL locked until its module handle is freed.
            require(ctypes.windll.kernel32.FreeLibrary(ctypes.c_void_p(lib._handle)) != 0,
                    "could not unload private Ooz snapshot")
    candidates = [(node, data) for node, _flag, data in nodes if node.startswith("CAB-")]
    require(len(candidates) == 1, "expected one CAB serialized node")
    node, data = candidates[0]
    curve = extract_curve(data, args.name)
    result = dict(scope="Static installed asset; curve coordinates are not seconds or physical input bounds.",
                  source=dict(block=str(args.block), blockSha256=args.block_sha256,
                              bundleOffset=args.offset, bundleSize=args.bundle_size,
                              serializedNode=node, serializedSha256=hashlib.sha256(data).hexdigest(),
                              decoder="Escartem/AnimeStudio MIT tree 02994d5c30a43d0d5c5124557ec361ccbbad24c0",
                              decoderSha256=PINNED), curve=curve)
    output = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        write_new_output(args.output, output,
                         (args.block, args.crypto_source, args.mhy_source, args.ooz_dll))
    else:
        print(output, end="")


def write_new_output(path, content, inputs):
    # Exclusive creation protects existing files (including same-file aliases and
    # hard links to the game source). Never create or overwrite a game artifact.
    for source in inputs:
        if path.exists() and os.path.samefile(path, source):
            raise ValueError("output aliases an input")
    with path.open("x", encoding="utf-8") as stream:
        stream.write(content)


if __name__ == "__main__":
    main()
