from __future__ import annotations

import logging
import sys
from typing import Optional

from live_output_mode import DEFAULT_LIVE_OUTPUT_MODE, LiveOutputMode
from run_log_enums import RunEventKind
from run_log_events import RunLogEvent


class RunLogProgressLogger:
    def __init__(self, logger: Optional[logging.Logger] = None, *, mode: LiveOutputMode = DEFAULT_LIVE_OUTPUT_MODE) -> None:
        self._mode = mode
        self._logger = logger or logging.getLogger("smoke")
        if not self._logger.handlers and self._mode != LiveOutputMode.NONE:
            handler = logging.StreamHandler(sys.stdout)
            handler.setFormatter(logging.Formatter("%(message)s"))
            self._logger.addHandler(handler)
        self._logger.setLevel(logging.INFO)
        self._logger.propagate = False

    def _allow_info(self) -> bool:
        return self._mode == LiveOutputMode.INFO

    def _allow_error(self) -> bool:
        return self._mode in {LiveOutputMode.INFO, LiveOutputMode.ERROR}

    def handle(self, event: RunLogEvent) -> None:
        if self._mode == LiveOutputMode.NONE:
            return

        if event.kind == RunEventKind.RUN_STARTED:
            if self._allow_info():
                self._logger.info(
                    "RUN STARTED run_id=%s source=%s mode=%s",
                    event.payload.get("run_id"),
                    event.payload.get("source"),
                    event.payload.get("mode"),
                )
            return

        if event.kind == RunEventKind.STEP_UPDATED:
            step = event.payload.get("step")
            status = getattr(step, "status", None)
            status_value = getattr(status, "value", status)
            message = "STEP %s %s: %s"
            args = (
                getattr(step, "step", step),
                status_value,
                getattr(step, "evidence", ""),
            )
            if status_value == "FAIL":
                if self._allow_error():
                    self._logger.error(message, *args)
            elif self._allow_info():
                self._logger.info(message, *args)
            return

        if event.kind == RunEventKind.NOTE_ADDED:
            if self._allow_info():
                self._logger.info("NOTE: %s", event.payload.get("note"))
            return

        if event.kind == RunEventKind.RUN_FINALIZED:
            overall = str(event.payload.get("overall") or "")
            if overall == "FAIL":
                if self._allow_error():
                    self._logger.error("RUN FINALIZED overall=%s", overall)
            elif self._allow_info():
                self._logger.info("RUN FINALIZED overall=%s", overall)
