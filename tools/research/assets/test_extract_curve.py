"""Small synthetic checks for the promoted read-only parser."""
import os
import hashlib
import struct
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import extract_curve as reader


def serialized_curve(*, count=2, object_length=None, first_value=1.0):
    name = b"Monster_Execute_AvatarEvade_WitchSlowDownCurve"
    body = bytearray(struct.pack("<I", len(name)) + name)
    body.extend(b"\0" * (-len(body) % 4))
    body.extend(struct.pack("<I", count))
    for time, value in ((0.0, first_value), (35.0, 0.15)):
        body.extend(struct.pack("<ffffiff", time, value, 0, 0, 0, 1 / 3, 1 / 3))
    body.extend(struct.pack("<iii", 2, 2, 4))
    meta = bytearray(b"test\0")
    meta.extend(struct.pack("<iBi", 19, 0, 1))
    meta.extend(struct.pack("<i", 114) + b"\0" * 3 + b"\0" * 32)
    meta.extend(struct.pack("<i", 1))
    meta.extend(b"\0" * (-len(meta) % 4))
    meta.extend(struct.pack("<qIIi", 42, 0, len(body) if object_length is None else object_length, 0))
    metadata_size = 20 + len(meta)
    file_size = metadata_size + len(body)
    return struct.pack(">4I", metadata_size, file_size, 21, metadata_size) + b"\0" * 4 + meta + body


class ParserTests(unittest.TestCase):
    def test_extract_known_curve_shape(self):
        curve = reader.extract_curve(serialized_curve(), "Monster_Execute_AvatarEvade_WitchSlowDownCurve")
        self.assertEqual(curve["objectId"], "42")
        self.assertEqual([key["time"] for key in curve["keys"]], [0.0, 35.0])

    def test_rejects_object_past_file(self):
        with self.assertRaisesRegex(ValueError, "object bounds"):
            reader.unity_objects(serialized_curve(object_length=1000))

    def test_rejects_truncated_key_array(self):
        with self.assertRaisesRegex(ValueError, "curve keys truncated"):
            reader.extract_curve(serialized_curve(count=3), "Monster_Execute_AvatarEvade_WitchSlowDownCurve")

    def test_rejects_non_finite_curve(self):
        with self.assertRaisesRegex(ValueError, "non-finite"):
            reader.extract_curve(serialized_curve(first_value=float("nan")),
                                 "Monster_Execute_AvatarEvade_WitchSlowDownCurve")

    def test_rejects_wrong_external_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fake.dll"
            path.write_bytes(b"wrong")
            with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
                reader.pinned_bytes(path, "ooz-dll")

    def test_verified_snapshot_survives_source_update(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "source.cs"
            path.write_bytes(b"first")
            with patch.dict(reader.PINNED, {"crypto-source": hashlib.sha256(b"first").hexdigest()}):
                snapshot = reader.pinned_bytes(path, "crypto-source")
            path.write_bytes(b"second")
            self.assertEqual(snapshot, b"first")

    def test_rejects_bundle_bounds_and_short_header(self):
        with tempfile.TemporaryDirectory() as directory:
            block = b"mhy1" + struct.pack("<I", 60) + b"short"
            with self.assertRaisesRegex(ValueError, "bundle bounds"):
                reader.unpack_bundle(block, 0, 100, (), None)
            with self.assertRaisesRegex(ValueError, "mhy header size"):
                reader.unpack_bundle(block, 0, len(block), (), None)

    def test_output_cannot_overwrite_input_or_hardlink(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.blk"
            source.write_bytes(b"original")
            alias = Path(directory) / "alias.json"
            os.link(source, alias)
            with self.assertRaisesRegex(ValueError, "output aliases"):
                reader.write_new_output(alias, "replacement", (source,))
            with self.assertRaisesRegex(ValueError, "output aliases"):
                reader.write_new_output(source, "replacement", (source,))
            self.assertEqual(source.read_bytes(), b"original")
            result = Path(directory) / "new.json"
            reader.write_new_output(result, "result", (source,))
            with self.assertRaises(FileExistsError):
                reader.write_new_output(result, "replacement", (source,))
            self.assertEqual(result.read_text(encoding="utf-8"), "result")


if __name__ == "__main__":
    unittest.main()
