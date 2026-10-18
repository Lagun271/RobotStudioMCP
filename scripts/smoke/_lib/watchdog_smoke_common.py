#!/usr/bin/env python3
from __future__ import annotations

import json
import pathlib
import subprocess
from dataclasses import dataclass
from typing import Any, Dict, Iterable, Optional

from watchdog_stdio_common import (
    StdioMcpClient,
    ensure_watchdog_tools,
    start_watchdog,
    watchdog_exe,
    watchdog_tool_call,
)
from watchdog_tool_name import WatchdogToolName


@dataclass
class WatchdogSmokeHarness:
    repo_root: pathlib.Path
    request_timeout_seconds: int
    print_prefix: Optional[str] = None
    watchdog_exe_path: Optional[pathlib.Path] = None

    def start_mcp(self) -> tuple[subprocess.Popen, StdioMcpClient]:
        watchdog = start_watchdog(self.repo_root, print_prefix=self.print_prefix)
        return watchdog, StdioMcpClient(watchdog)

    def ensure_mcp_tools(
        self,
        client: StdioMcpClient,
        required_tools: Iterable[WatchdogToolName],
        *,
        print_prefix: Optional[str] = None,
    ) -> None:
        ensure_watchdog_tools(
            client,
            required_tools,
            timeout_seconds=self.request_timeout_seconds,
            print_prefix=print_prefix or self.print_prefix,
        )

    def run_cli(self, command: str, arguments: Optional[list[str]] = None, *, timeout_seconds: int = 60) -> Dict[str, Any]:
        exe = self.watchdog_exe()
        args = [str(exe), command]
        if arguments:
            args.extend(arguments)

        result = subprocess.run(
            args,
            cwd=str(self.repo_root),
            capture_output=True,
            text=True,
            timeout=max(15, timeout_seconds),
        )
        payload = self._parse_json_stdout(result.stdout)
        return {
            "command": command,
            "arguments": arguments or [],
            "returncode": int(result.returncode),
            "stdout": result.stdout,
            "stderr": result.stderr,
            "payload": payload,
        }

    def call_mcp(
        self,
        client: StdioMcpClient,
        tool_name: WatchdogToolName,
        arguments: Optional[Dict[str, Any]] = None,
    ) -> Dict[str, Any]:
        return watchdog_tool_call(
            client,
            tool_name,
            arguments or {},
            timeout_seconds=self.request_timeout_seconds,
        )

    def watchdog_exe(self) -> pathlib.Path:
        if self.watchdog_exe_path is not None:
            return self.watchdog_exe_path
        return watchdog_exe(self.repo_root)

    @staticmethod
    def _parse_json_stdout(stdout: str) -> Dict[str, Any]:
        text = (stdout or "").strip()
        if not text:
            return {}

        try:
            return json.loads(text)
        except Exception:
            return {}
