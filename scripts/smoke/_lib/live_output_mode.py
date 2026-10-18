#!/usr/bin/env python3
from enum import Enum


class LiveOutputMode(str, Enum):
    NONE = "none"
    ERROR = "error"
    INFO = "info"


DEFAULT_LIVE_OUTPUT_MODE = LiveOutputMode.ERROR
