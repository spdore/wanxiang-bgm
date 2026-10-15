"""Create a disposable, synthetic QMCv2 file containing a valid WAV stream."""
from __future__ import annotations

import argparse
import importlib.util
import io
import json
import math
import struct
import subprocess
import sys
import wave
from pathlib import Path

sys.dont_write_bytecode = True
TEST_FILE = Path(__file__).with_name("encrypted_audio_helper_roundtrip.py")
SPEC = importlib.util.spec_from_file_location("crypto_roundtrip_fixtures", TEST_FILE)
FIXTURES = importlib.util.module_from_spec(SPEC)
assert SPEC is not None and SPEC.loader is not None
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
    parser = argparse.ArgumentParser()
    parser.add_argument("helper_directory", type=Path)
    parser.add_argument("workspace", type=Path)
    args = parser.parse_args()
    helper = args.helper_directory.resolve() / "BgmCryptoHelper.exe"
    workspace = args.workspace.resolve()
    workspace.mkdir(parents=True, exist_ok=False)
    expected = valid_wav()
    source = workspace / "中文 合成 QMCv2.mflac"
    destination = workspace / "decoded.wav"
    source.write_bytes(FIXTURES.make_qmc2(expected, b"123456", "QTag"))
    completed = subprocess.run(
        [str(helper), "--decode", str(source), str(destination)],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=90,
        check=False,
    )
    response = json.loads(completed.stdout.strip())
    actual = destination.read_bytes() if destination.exists() else b""
    if completed.returncode != 0 or response.get("status") != "converted":
        raise SystemExit(f"helper conversion failed: {response}")
    if response.get("container") != "wav" or actual != expected:
        raise SystemExit("helper output did not match the valid WAV payload")
    print(f"QMCv2 synthetic crypto round-trip: OK ({len(actual)} byte WAV)")
    print(f"Decoder smoke input: {destination}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
