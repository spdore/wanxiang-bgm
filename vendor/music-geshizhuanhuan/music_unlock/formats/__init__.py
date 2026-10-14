"""Restricted decoder set used by the local BGM import helper."""
from pathlib import Path

from ..errors import FormatError, UnsupportedError
from ..model import sniff_container
from .base import DecodeOptions, Decoder
from .kgm import KGM_MAGIC, KgmDecoder, VPR_MAGIC
from .ncm import MAGIC as NCM_MAGIC
from .ncm import NcmDecoder
from .qmc import QmcDecoder, V1_STATIC_KEY, parse_footer
from ..ciphers import qmc1_transform

DECODERS: list[Decoder] = [NcmDecoder(), QmcDecoder(), KgmDecoder()]


def pick_decoder(path: Path) -> Decoder:
    """Identify an encrypted format from its bytes, not its file suffix."""
    with path.open("rb") as fp:
        size = path.stat().st_size
        head = fp.read(64)
        tail = b""
        if size >= 8:
            fp.seek(max(0, size - 1024))
            tail = fp.read(1024)

    if head.startswith(NCM_MAGIC):
        return DECODERS[0]
    if head[:16] in (KGM_MAGIC, VPR_MAGIC):
        return DECODERS[2]
    if tail.endswith((b"QTag", b"STag", b"musicex\x00")):
        return DECODERS[1]

    try:
        footer = parse_footer(tail) if tail else None
    except FormatError:
        footer = None
    if footer is not None:
        return DECODERS[1]

    # QMC v1 has no stable container magic. Its fixed-key plaintext header is
    # recognized by the recovered standard audio signature.
    if sniff_container(qmc1_transform(head, V1_STATIC_KEY)) != "bin":
        return DECODERS[1]
    raise UnsupportedError(f"{path.name}: 未识别的加密音乐结构")


def supported_extensions() -> frozenset[str]:
    return frozenset({
        ".ncm", ".tkm", ".bkcmp3", ".bkcm4a", ".bkcflac", ".bkcwav",
        ".bkcape", ".bkcogg", ".bkcwma", ".666c6163", ".6d7033",
        ".6f6767", ".6d3461", ".776176", ".mflac", ".mflac0", ".mgg",
        ".mgg0", ".mgg1", ".mggl", ".mmp4", ".qmcflac", ".qmcogg",
        ".qmc0", ".qmc2", ".qmc3", ".qmc4", ".qmc6", ".qmc8",
        ".kgm", ".kgma", ".vpr", ".kgm.flac", ".vpr.flac",
    })
