from __future__ import annotations

from enum import Enum


class StepStatus(str, Enum):
    TODO = "TODO"
    PASS = "PASS"
    FAIL = "FAIL"
    SKIP = "SKIP"


class MissingStationPolicy(str, Enum):
    SKIP = "skip"
    FAIL = "fail"


class RunEventKind(str, Enum):
    RUN_STARTED = "run_started"
    STEP_UPDATED = "step_updated"
    NOTE_ADDED = "note_added"
    RUN_FINALIZED = "run_finalized"


class RunMode(str, Enum):
    SMOKE = "smoke"
    SMOKE_PLUS = "smoke+"


class StationPrereq(str, Enum):
    NONE = "none"
    EMPTY_STATION = "empty_station"
    SPECIFIC_STATION = "specific_station"


class InteractionRequirement(str, Enum):
    UNATTENDED = "unattended"
    USER_INTERACTION_REQUIRED = "user_interaction_required"
    UNKNOWN = "unknown"


class ObservedReliability(str, Enum):
    STABLE = "stable"
    FLAKY = "flaky"
    UNKNOWN = "unknown"
