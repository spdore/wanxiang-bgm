"""Synthetic decoder/packaging smoke tests; these are not platform samples."""
from __future__ import annotations

import base64
import contextlib
import importlib.util
import io
import json
import lzma
import os
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[3]
VENDOR = ROOT / "vendor" / "music-geshizhuanhuan"
sys.path.insert(0, str(VENDOR))

from Crypto.Cipher import AES  # noqa: E402

from music_unlock.ciphers import (  # noqa: E402
    TEA_DELTA,
    _SIMPLE_KEY,
    make_qmc2_stream,
    ncm_stream_bytes,
    qmc1_transform,
    tea_cbc_decrypt,
)
from music_unlock.formats.kgm import (  # noqa: E402
    BLOCK,
    HEADER_LEN,
    KGM_MAGIC,
    MEND_TABLE,
    OWN_KEY_LEN,
    VPR_MAGIC,
    _inverse_scramble,
    _scramble,
)
from music_unlock.formats.ncm import CORE_KEY, MAGIC as NCM_MAGIC  # noqa: E402
from music_unlock.formats.qmc import V1_STATIC_KEY  # noqa: E402

HELPER_SPEC = importlib.util.spec_from_file_location("encrypted_audio_helper", ROOT / "tools" / "encrypted_audio_helper.py")
assert HELPER_SPEC is not None and HELPER_SPEC.loader is not None
HELPER_MODULE = importlib.util.module_from_spec(HELPER_SPEC)
HELPER_SPEC.loader.exec_module(HELPER_MODULE)


def tea_encrypt_block(block: int, key16: bytes) -> int:
    k0, k1, k2, k3 = struct.unpack(">IIII", key16)
    hi = (block >> 32) & 0xFFFFFFFF
    lo = block & 0xFFFFFFFF
    total = 0
    for _ in range(16):
        total = (total + TEA_DELTA) & 0xFFFFFFFF
        hi = (hi + ((((lo << 4) & 0xFFFFFFFF) + k0) ^ ((total + lo) & 0xFFFFFFFF) ^ ((lo >> 5) + k1))) & 0xFFFFFFFF
        lo = (lo + ((((hi << 4) & 0xFFFFFFFF) + k2) ^ ((total + hi) & 0xFFFFFFFF) ^ ((hi >> 5) + k3))) & 0xFFFFFFFF
    return (hi << 32) | lo


def tea_cbc_encrypt(body: bytes, key16: bytes) -> bytes:
    plain = b"\x00\x00\x00" + body + b"\x00" * 7
    if len(plain) % 8:
        raise ValueError("synthetic EKey body must make an aligned TEA block")
    iv_prev = 0
    iv_cur = 0
    output = bytearray()
    for offset in range(0, len(plain), 8):
        value = int.from_bytes(plain[offset : offset + 8], "big")
        next_iv = value ^ iv_prev
        cipher = tea_encrypt_block(next_iv, key16) ^ iv_cur
        output += cipher.to_bytes(8, "big")
        iv_prev = cipher
        iv_cur = next_iv
    return bytes(output)


def make_ekey(master: bytes) -> str:
    header = bytes(range(8))
    key = bytearray()
    for simple, item in zip(_SIMPLE_KEY, header):
        key.extend((simple, item))
    encrypted = tea_cbc_encrypt(master, bytes(key))
    assert tea_cbc_decrypt(encrypted, bytes(key)) == master
    return base64.b64encode(header + encrypted).decode("ascii")


def make_ncm(plain: bytes) -> bytes:
    key = b"synthetic-key"
    key_plain = b"neteasecloudmusic" + key
    pad = 16 - (len(key_plain) % 16)
    key_blob = AES.new(CORE_KEY, AES.MODE_ECB).encrypt(key_plain + bytes([pad]) * pad)
    key_blob = bytes(value ^ 0x64 for value in key_blob)
    box = ncm_stream_bytes(key)
    stream = (box * ((len(plain) + 256) // 256 + 1))[1 : 1 + len(plain)]
    audio = bytes(left ^ right for left, right in zip(plain, stream))
    return (
        NCM_MAGIC
        + b"\x00\x00"
        + struct.pack("<I", len(key_blob))
        + key_blob
        + struct.pack("<I", 0)
        + b"\x00" * 5
        + struct.pack("<II", 0, 0)
        + audio
    )


def make_qmc2(plain: bytes, master: bytes, footer_kind: str) -> bytes:
    ekey = make_ekey(master).encode("ascii")
    # EKey v1 derivation preserves the eight-byte header and appends the
    # decrypted body; QMC stream selection uses that full derived key.
    derived_master = bytes(range(8)) + master
    encrypted = make_qmc2_stream(derived_master).decrypt(plain)
    if footer_kind == "QTag":
        metadata = ekey + b",123456,2"
        return encrypted + metadata + struct.pack(">I", len(metadata)) + b"QTag"
    if footer_kind == "PcV1Legacy":
        return encrypted + ekey + struct.pack("<I", len(ekey))
    raise ValueError(footer_kind)


def make_musicex() -> bytes:
    inner = bytes(0xC0 - 0x10)
    return b"\x55" * 32 + inner + struct.pack("<I", 0xC0) + struct.pack("<I", 1) + b"musicex\x00"


def make_stag() -> bytes:
    body = b"123,2,synthetic"
    return b"\x55" * 32 + body + struct.pack(">I", len(body)) + b"STag"


def make_kgm(plain: bytes, version: int, is_vpr: bool = False) -> bytes:
    pub_key = lzma.decompress((VENDOR / "assets" / "kugou_key.xz").read_bytes())
    crypto_test = bytes(range(16))
    own_key = crypto_test + b"\x00"
    cipher = bytearray(len(plain))
    for offset, plain_byte in enumerate(plain):
        block_index = offset // BLOCK
        phase = block_index % OWN_KEY_LEN
        column = offset % BLOCK
        pub_value = pub_key[block_index]
        pub_index = (phase * BLOCK + column) % len(MEND_TABLE)
        xor_mask = _scramble(pub_value ^ MEND_TABLE[pub_index])
        cipher[offset] = _inverse_scramble(plain_byte ^ xor_mask, 0) ^ own_key[(phase * BLOCK + column) % OWN_KEY_LEN]

    header = bytearray(HEADER_LEN)
    header[:16] = VPR_MAGIC if is_vpr else KGM_MAGIC
    header[0x10:0x14] = struct.pack("<I", HEADER_LEN)
    header[0x14:0x18] = struct.pack("<I", version)
    header[0x1C:0x2C] = crypto_test
    return bytes(header) + bytes(cipher)


class HelperRoundTripTests(unittest.TestCase):
    helper: Path

    def run_helper(self, name: str, payload: bytes):
        with tempfile.TemporaryDirectory(prefix="Bgm Crypto 测试 ") as temporary:
            folder = Path(temporary)
            source = folder / name
            destination = folder / "decoded output.bin"
            source.write_bytes(payload)
            completed = subprocess.run(
                [str(self.helper), "--decode", str(source), str(destination)],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=90,
                check=False,
            )
            response = json.loads(completed.stdout.strip())
            output = destination.read_bytes() if destination.exists() else b""
            return completed.returncode, response, output

    def test_ncm_detected_from_bytes_even_with_wrong_suffix(self):
        payload = b"fLaC" + bytes(range(64))
        code, result, output = self.run_helper("网易云内容.fake", make_ncm(payload))
        self.assertEqual(code, 0)
        self.assertEqual(result["status"], "converted")
        self.assertEqual(result["container"], "flac")
        self.assertEqual(output, payload)

    def test_qmc_v1_detected_from_recovered_audio(self):
        payload = b"OggS" + bytes(range(80))
        encrypted = qmc1_transform(payload, V1_STATIC_KEY)
        code, result, output = self.run_helper("QMCv1.fake.ncm", encrypted)
        self.assertEqual(code, 0)
        self.assertEqual(result["status"], "converted")
        self.assertEqual(result["container"], "ogg")
        self.assertEqual(output, payload)

    def test_qmc_v2_qtag_map_and_rc4_use_content_format(self):
        flac = b"fLaC" + bytes(range(96))
        ogg = b"OggS" + bytes(range(96))
        code, result, output = self.run_helper("QQ-map.mgg", make_qmc2(flac, b"123456", "QTag"))
        self.assertEqual((code, result["status"], result["container"], output), (0, "converted", "flac", flac))
        long_master = bytes((index * 17) & 0xFF for index in range(302))
        code, result, output = self.run_helper("QQ-rc4.mflac", make_qmc2(ogg, long_master, "QTag"))
        self.assertEqual((code, result["status"], result["container"], output), (0, "converted", "ogg", ogg))

    def test_qmc_v2_pc_v1_legacy_embedded_ekey(self):
        payload = b"fLaC" + bytes(range(64))
        code, result, output = self.run_helper("legacy.mflac", make_qmc2(payload, b"abcdef", "PcV1Legacy"))
        self.assertEqual((code, result["status"], result["container"], output), (0, "converted", "flac", payload))

    def test_qmc_v2_missing_ekey_returns_key_unavailable(self):
        for name, payload in (("musicex.mgg", make_musicex()), ("stag.mgg", make_stag())):
            with self.subTest(name=name):
                code, result, output = self.run_helper(name, payload)
                self.assertEqual(code, 20)
                self.assertEqual(result["status"], "KeyUnavailable")
                self.assertEqual(output, b"")

    def test_encrypted_size_cap_is_checked_before_decoder_reads_input(self):
        with tempfile.TemporaryDirectory(prefix="Bgm size limit test ") as temporary:
            source = Path(temporary) / "oversized.mflac"
            destination = Path(temporary) / "decoded.bin"
            source.write_bytes(b"short fixture")
            original_stat = Path.stat

            def fake_stat(path, *args, **kwargs):
                actual = original_stat(path, *args, **kwargs)
                if path == source:
                    return SimpleNamespace(st_mode=actual.st_mode, st_size=HELPER_MODULE.MAX_ENCRYPTED_INPUT_BYTES + 1)
                return actual

            output = io.StringIO()
            with mock.patch.object(Path, "stat", fake_stat):
                with contextlib.redirect_stdout(output):
                    exit_code = HELPER_MODULE.decode(source, destination)
            response = json.loads(output.getvalue().strip())
            self.assertEqual((exit_code, response["status"]), (24, "FileTooLarge"))
            self.assertFalse(destination.exists())

    def test_decoded_output_cap_prevents_disk_write(self):
        class OversizedPayload:
            def __len__(self):
                return HELPER_MODULE.MAX_DECODED_OUTPUT_BYTES + 1

            def __getitem__(self, key):
                return b"fLaC" + bytes(60)

        class FakeDecoder:
            def decode(self, _source, _options):
                return SimpleNamespace(payload=OversizedPayload(), source="test")

        with tempfile.TemporaryDirectory(prefix="Bgm output size limit test ") as temporary:
            source = Path(temporary) / "synthetic.mflac"
            destination = Path(temporary) / "decoded.bin"
            source.write_bytes(b"synthetic input")
            output = io.StringIO()
            with mock.patch.object(HELPER_MODULE, "pick_decoder", return_value=FakeDecoder()):
                with contextlib.redirect_stdout(output):
                    exit_code = HELPER_MODULE.decode(source, destination)
            response = json.loads(output.getvalue().strip())
            self.assertEqual((exit_code, response["status"]), (24, "FileTooLarge"))
            self.assertFalse(destination.exists())

    def test_kgm_and_vpr_v3_v4_headers(self):
        payload = b"fLaC" + bytes(range(64))
        for version, vpr in ((3, False), (4, False), (3, True), (4, True)):
            with self.subTest(version=version, vpr=vpr):
                suffix = "vpr" if vpr else "kgm"
                code, result, output = self.run_helper(f"sample-{version}.{suffix}", make_kgm(payload, version, vpr))
                self.assertEqual((code, result["status"], result["container"], output), (0, "converted", "flac", payload))

    def test_unsupported_kgm_version_and_unknown_or_truncated_data(self):
        payload = b"fLaC" + bytes(range(32))
        code, result, output = self.run_helper("old.kgm", make_kgm(payload, 2))
        self.assertEqual(code, 21)
        self.assertEqual(result["status"], "UnsupportedVariant")
        self.assertEqual(output, b"")
        code, result, _ = self.run_helper("fake.mflac", b"not an encrypted container")
        self.assertEqual((code, result["status"]), (21, "UnsupportedVariant"))
        code, result, _ = self.run_helper("truncated.ncm", NCM_MAGIC)
        self.assertEqual((code, result["status"]), (22, "InvalidFile"))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: encrypted_audio_helper_roundtrip.py <BgmCryptoHelper directory>")
    HelperRoundTripTests.helper = Path(sys.argv[1]).resolve() / "BgmCryptoHelper.exe"
    if not HelperRoundTripTests.helper.is_file():
        raise SystemExit(f"helper not found: {HelperRoundTripTests.helper}")
    unittest.main(argv=[sys.argv[0]])
