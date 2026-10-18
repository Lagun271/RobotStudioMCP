#!/usr/bin/env python3
from __future__ import annotations

from enum import Enum


class WatchdogToolName(str, Enum):
    START_ROBOTSTUDIO_PROCESS = "start_robot_studio_process"
    VERIFY_ADDIN_MCP_READY = "verify_addin_mcp_ready"
    STOP_STARTED_ROBOTSTUDIO_PROCESS = "stop_started_robot_studio_process"
    INSTALL_RSPAK_PACKAGE = "install_rspak_package"
    RSPAK_UNINSTALL = "rspak_uninstall"
    RSPAK_LIST = "rspak_list"
    VERIFY_ADDIN_INSTALLATION = "verify_addin_installation"
    GET_ROBOTSTUDIO_PROCESS_STATUS = "get_robot_studio_process_status"
