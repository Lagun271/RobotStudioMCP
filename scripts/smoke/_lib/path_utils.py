#!/usr/bin/env python3
from __future__ import annotations

import os
import re
import subprocess
from typing import Optional

_WINDOWS_PATH_RE = re.compile(r"^(?:[A-Za-z]:[\\/]|\\\\)")


def _run_wslpath(flag: str, text: str) -> str:
    try:
        converted = subprocess.run(["wslpath", flag, text], capture_output=True, text=True, check=True)
        return converted.stdout.strip()
    except Exception:
        return text


def to_windows_path(path_value: Optional[str]) -> Optional[str]:
    if path_value is None:
        return None

    text = str(path_value).strip()
    if not text:
        return text

    if os.name == "nt" or _WINDOWS_PATH_RE.match(text):
        return text

    return _run_wslpath("-w", text)


def to_local_path(path_value: Optional[str]) -> Optional[str]:
    if path_value is None:
        return None

    text = str(path_value).strip()
    if not text:
        return text

    if os.name == "nt":
        return text

    return _run_wslpath("-u", text)
