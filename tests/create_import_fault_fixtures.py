"""Create isolated, synthetic encrypted-import fixtures; no platform files are used."""
from __future__ import annotations

import importlib.util
import io
import math
import struct
import sys
import wave
from pathlib import Path

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "vendor" / "music-geshizhuanhuan"))
SPEC = importlib.util.spec_from_file_location("crypto_roundtrip_fixtures", Path(__file__).with_name("encrypted_audio_helper_roundtrip.py"))
assert SPEC is not None and SPEC.loader is not None
FIXTURES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FIXTURES)


def valid_wav() -> bytes:
    rate = 44100
    frames = bytearray()
    for index in range(rate * 2):
        sample = int(math.sin(2.0 * math.pi * 440.0 * index / rate) * 6000)
        frames.extend(struct.pack("<hh", sample, sample))
    output = io.BytesIO()
    with wave.open(output, "wb") as audio:
        audio.setnchannels(2)
        audio.setsampwidth(2)
        audio.setframerate(rate)
        audio.writeframes(bytes(frames))
    return output.getvalue()


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: create_import_fault_fixtures.py <new-workspace>")
    workspace = Path(sys.argv[1]).resolve()
    workspace.mkdir(parents=True, exist_ok=False)
    (workspace / "有效中文 QMCv2.mflac").write_bytes(FIXTURES.make_qmc2(valid_wav(), b"123456", "QTag"))
    (workspace / "缺密钥 MusicEx.mflac").write_bytes(FIXTURES.make_musicex())
    (workspace / "未知变体.mflac").write_bytes(b"not a recognized encrypted container")
    (workspace / "截断 NCM.ncm").write_bytes(FIXTURES.NCM_MAGIC)
    print(f"Synthetic import fixtures created: {workspace}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
