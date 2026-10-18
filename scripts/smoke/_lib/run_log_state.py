from __future__ import annotations

import datetime as dt
import pathlib
import uuid
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional

from run_log_enums import RunEventKind, StepStatus
from run_log_events import RunLogEvent


@dataclass(frozen=True)
class StepResult:
    step: str
    status: StepStatus
    evidence: str


@dataclass
class RunLogState:
    path: pathlib.Path
    source: str
    mode: str
    tools: str
    inputs: str
    run_id: str = field(default_factory=lambda: f"run-{dt.datetime.now(dt.timezone.utc).strftime('%Y%m%d-%H%M%S')}-{uuid.uuid4().hex[:6]}")
    timestamp_utc: str = field(default_factory=lambda: dt.datetime.now(dt.timezone.utc).isoformat())
    notes: List[str] = field(default_factory=list)
    steps: Dict[str, StepResult] = field(default_factory=dict)
    finalized: bool = False
    _listeners: List[Callable[[RunLogEvent], None]] = field(default_factory=list, init=False, repr=False)

    def subscribe(self, listener: Callable[[RunLogEvent], None]) -> None:
        self._listeners.append(listener)

    def _emit(self, kind: RunEventKind, **payload: object) -> None:
        event = RunLogEvent(kind=kind, payload=payload)
        for listener in list(self._listeners):
            listener(event)

    def start(self) -> None:
        self._emit(
            RunEventKind.RUN_STARTED,
            run_id=self.run_id,
            timestamp_utc=self.timestamp_utc,
            source=self.source,
            mode=self.mode,
            tools=self.tools,
            inputs=self.inputs,
            path=self.path,
        )

    def record_step(self, step: str, status: StepStatus, evidence: str) -> None:
        result = StepResult(step=step, status=status, evidence=evidence)
        self.steps[step] = result
        self._emit(RunEventKind.STEP_UPDATED, step=result)

    def add_note(self, text: str) -> None:
        self.notes.append(text)
        self._emit(RunEventKind.NOTE_ADDED, note=text)

    def finalize(self, overall: Optional[StepStatus] = None) -> None:
        self.finalized = True
        self._emit(RunEventKind.RUN_FINALIZED, overall=overall)
