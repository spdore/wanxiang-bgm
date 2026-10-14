"""Private JSON-line-free CLI for the embedded local import converter.

This wrapper exposes only byte-identified NCM, QMC v1/v2, and KGM/VPR
decoders. It deliberately provides no client, cookie, key-store, network,
or external-key options.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "vendor" / "music-geshizhuanhuan"))

from music_unlock.errors import FormatError, KeyError_, UnsupportedError, UnlockError
from music_unlock.formats import pick_decoder
from music_unlock.formats.base import DecodeOptions
from music_unlock.formats.kgm import KGM_MAGIC, VPR_MAGIC
from music_unlock.formats.ncm import MAGIC as NCM_MAGIC
from music_unlock.model import sniff_container

EXIT_OK = 0
EXIT_KEY_UNAVAILABLE = 20
EXIT_UNSUPPORTED = 21
EXIT_INVALID = 22
EXIT_FAILURE = 23
EXIT_FILE_TOO_LARGE = 24
MAX_ENCRYPTED_INPUT_BYTES = 256 * 1024 * 1024
MAX_DECODED_OUTPUT_BYTES = 512 * 1024 * 1024
KNOWN_CONTAINERS = {"mp3", "flac", "ogg", "m4a", "wav", "aac"}


def emit(status: str, message: str = "", container: str = "", variant: str = "") -> int:
    sys.stdout.write(json.dumps({
        "status": status,
        "message": message,
        "container": container,
        "variant": variant,
    }, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()
    return {
        "converted": EXIT_OK,
        "KeyUnavailable": EXIT_KEY_UNAVAILABLE,
        "UnsupportedVariant": EXIT_UNSUPPORTED,
        "InvalidFile": EXIT_INVALID,
        "FileTooLarge": EXIT_FILE_TOO_LARGE,
    }.get(status, EXIT_FAILURE)


def decode(source: Path, destination: Path) -> int:
    try:
        if not source.is_file():
            return emit("InvalidFile", "找不到所选文件。")
        if source.stat().st_size > MAX_ENCRYPTED_INPUT_BYTES:
            return emit("FileTooLarge", "该加密文件超过 256 MiB 的安全转换上限；源文件未修改。")

        decoder = pick_decoder(source)
        with source.open("rb") as stream:
            head = stream.read(32)

        if head.startswith(NCM_MAGIC):
            kind = "NCM"
        elif head[:16] in (KGM_MAGIC, VPR_MAGIC):
            kind = "KGM/VPR"
        else:
            # QMC variant recognition is based on its parsed trailer or
            # recovered audio signature, never on .mflac/.mgg suffixes.
            kind = "QMC"

        result = decoder.decode(source, DecodeOptions())
        actual_container = sniff_container(result.payload[:64])
        if actual_container not in KNOWN_CONTAINERS:
            return emit("InvalidFile", "解密内容没有可识别的标准音频容器。", variant=result.source)
        if not result.payload or len(result.payload) < 8:
            return emit("InvalidFile", "解密结果为空或不完整。", variant=result.source)
        if len(result.payload) > MAX_DECODED_OUTPUT_BYTES:
            return emit("FileTooLarge", "解密结果超过 512 MiB 的安全导入上限；源文件未修改。", variant=result.source)

        destination.parent.mkdir(parents=True, exist_ok=True)
        partial = destination.with_name(destination.name + ".partial")
        try:
            with partial.open("xb") as stream:
                stream.write(result.payload)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(partial, destination)
        finally:
            try:
                partial.unlink()
            except FileNotFoundError:
                pass

        return emit("converted", "", actual_container, result.source or kind)
    except KeyError_:
        return emit("KeyUnavailable", "此 QMC v2 变体不含可用的内嵌密钥；不会读取客户端或密钥库。")
    except UnsupportedError as exc:
        return emit("UnsupportedVariant", str(exc))
    except FormatError as exc:
        return emit("InvalidFile", str(exc))
    except UnlockError as exc:
        return emit("InvalidFile", str(exc))
    except Exception as exc:
        # Avoid exception representations that may disclose absolute paths.
        return emit("InvalidFile", "文件损坏或格式不受支持：" + type(exc).__name__)


def main(argv: list[str]) -> int:
    if len(argv) != 4 or argv[1] != "--decode":
        return emit("UnsupportedVariant", "转换助手参数无效。")
    return decode(Path(argv[2]), Path(argv[3]))


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
