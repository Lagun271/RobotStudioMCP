from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict

from run_log_enums import RunEventKind


@dataclass(frozen=True)
class RunLogEvent:
    kind: RunEventKind
    payload: Dict[str, Any]
