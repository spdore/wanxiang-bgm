"""解码器基类与格式注册。"""
from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from pathlib import Path

from ..model import DecodeResult


@dataclass(slots=True)
class DecodeOptions:
    """No external keys or client-derived material are accepted."""


class Decoder(ABC):
    """一个加密格式的解码器。"""

    name: str = "?"
    extensions: frozenset[str] = frozenset()

    @abstractmethod
    def decode(self, path: Path, opts: DecodeOptions) -> DecodeResult:
        """解密单个文件，返回 DecodeResult。"""
        raise NotImplementedError

    def can_handle(self, path: Path) -> bool:
        return path.suffix.lower() in self.extensions
