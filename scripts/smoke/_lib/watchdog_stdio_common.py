#!/usr/bin/env python3
from __future__ import annotations

import json
import os
import pathlib
import queue
import subprocess
import threading
from typing import Any, Dict, Iterable, Optional, Set

from watchdog_tool_name import WatchdogToolName


class StdioMcpClient:
    def __init__(self, process: subprocess.Popen):
        self.process = process
        self._next_id = 1

    @staticmethod
    def _encode_message(payload: Dict[str, Any]) -> bytes:
        return (json.dumps(payload, separators=(",", ":")) + "\n").encode("utf-8")

    def _read_message(self) -> Dict[str, Any]:
        stdout = self.process.stdout
        if stdout is None:
            raise RuntimeError("Watchdog stdout stream is not available.")

        while True:
            line = stdout.readline()
            if not line:
                raise RuntimeError("Watchdog MCP stream closed unexpectedly.")

            text = line.decode("utf-8", errors="replace").strip()
            if not text:
                continue

            try:
                return json.loads(text)
            except json.JSONDecodeError:
                continue

    def request(self, method: str, params: Optional[Dict[str, Any]] = None, timeout_seconds: int = 30) -> Dict[str, Any]:
        req_id = self._next_id
        self._next_id += 1

        payload: Dict[str, Any] = {
            "jsonrpc": "2.0",
            "id": req_id,
            "method": method,
        }
        if params is not None:
            payload["params"] = params

        result_q: "queue.Queue[Any]" = queue.Queue(maxsize=1)

        def _worker() -> None:
            try:
                stdin = self.process.stdin
                if stdin is None:
                    raise RuntimeError("Watchdog stdin stream is not available.")

                stdin.write(self._encode_message(payload))
                stdin.flush()

                while True:
                    message = self._read_message()
                    if message.get("id") == req_id:
                        result_q.put(("ok", message))
                        return
            except Exception as ex:
                result_q.put(("err", ex))

        thread = threading.Thread(target=_worker, daemon=True)
        thread.start()

        try:
            status, value = result_q.get(timeout=max(1, timeout_seconds))
        except queue.Empty:
            try:
                self.process.kill()
            except Exception:
                pass
            raise TimeoutError(f"Timeout waiting for watchdog response to method '{method}'.")

        if status == "err":
            raise value

        return value

    def initialize(self, client_name: str, client_version: str, timeout_seconds: int = 30) -> Dict[str, Any]:
        response = self.request(
            "initialize",
            {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": client_name, "version": client_version},
            },
            timeout_seconds=timeout_seconds,
        )

        notify = {
            "jsonrpc": "2.0",
            "method": "notifications/initialized",
            "params": {},
        }

        stdin = self.process.stdin
        if stdin is not None:
            stdin.write(self._encode_message(notify))
            stdin.flush()

        return response


def default_robotstudio_exe() -> str:
    program_files_x86 = os.environ.get("ProgramFiles(x86)") or r"C:\\Program Files (x86)"
    return str(pathlib.Path(program_files_x86) / "ABB" / "RobotStudio 2026" / "Bin" / "RobotStudio.exe")


def parse_env_args(values: Optional[list[str]]) -> Dict[str, str]:
    env: Dict[str, str] = {}
    for raw in values or []:
        text = raw.strip()
        if not text:
            continue
        if "=" not in text:
            raise ValueError(f"Invalid --env value '{raw}'. Expected KEY=VALUE.")
        key, value = text.split("=", 1)
        key = key.strip()
        if not key:
            raise ValueError(f"Invalid --env value '{raw}'. Key must not be empty.")
        env[key] = value
    return env


def watchdog_exe(repo_root: pathlib.Path) -> pathlib.Path:
    exe = repo_root / "src" / "RobotStudioMCP.Watchdog" / "bin" / "Debug" / "net10.0" / "RobotStudioMCP.Watchdog.exe"
    if exe.exists():
        return exe

    raise RuntimeError(f"Watchdog exe not found: {exe}. Build the watchdog first with 'make watchdog-build'.")


def start_watchdog(repo_root: pathlib.Path, print_prefix: Optional[str] = None) -> subprocess.Popen:
    exe = watchdog_exe(repo_root)

    if print_prefix:
        print(f"{print_prefix} Starting watchdog from local exe: {exe}", flush=True)

    return subprocess.Popen(
        [str(exe)],
        cwd=str(repo_root),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        bufsize=0,
    )


def ensure_watchdog_tools(
    client: StdioMcpClient,
    required_tools: Iterable[WatchdogToolName],
    *,
    timeout_seconds: int = 30,
    print_prefix: Optional[str] = None,
) -> Set[str]:
    response = client.request("tools/list", {}, timeout_seconds=timeout_seconds)
    tools = (response.get("result") or {}).get("tools") or []

    available: Set[str] = set()
    for entry in tools:
        if isinstance(entry, dict):
            name = entry.get("name")
            if isinstance(name, str):
                available.add(name)

    normalized_required = {tool.value if isinstance(tool, WatchdogToolName) else str(tool) for tool in required_tools}
    missing = sorted(name for name in normalized_required if name not in available)
    if missing:
        available_list = ", ".join(sorted(available)) or "<none>"
        raise RuntimeError(
            "Missing watchdog tool(s): " + ", ".join(missing) + f". Available tools: {available_list}"
        )

    if print_prefix:
        print(
            f"{print_prefix} Watchdog tools available: {sorted(normalized_required)}",
            flush=True,
        )

    return normalized_required


def watchdog_tool_call(client: StdioMcpClient, tool_name: WatchdogToolName | str, arguments: Dict[str, Any], timeout_seconds: int) -> Dict[str, Any]:
    name = tool_name.value if isinstance(tool_name, WatchdogToolName) else str(tool_name)
    response = client.request("tools/call", {"name": name, "arguments": arguments}, timeout_seconds=timeout_seconds)
    if "error" in response:
        raise RuntimeError(f"Watchdog tool {tool_name} failed: {response['error']}")

    result = response.get("result") or {}
    structured = result.get("structuredContent")
    if isinstance(structured, dict):
        return structured

    content = result.get("content") or []
    if content and isinstance(content, list) and isinstance(content[0], dict):
        text = content[0].get("text")
        if text:
            try:
                return json.loads(text)
            except Exception:
                pass

    return {"rawResult": result}


def watchdog_start_robotstudio_process(
    client: StdioMcpClient,
    *,
    robotstudio_exe_path: str,
    timeout_seconds: int,
    rsaddin_path: Optional[str] = None,
    station_path: Optional[str] = None,
    environment: Optional[Dict[str, str]] = None,
    kill_all_robotstudio: bool = False,
    kill_all_robvc: bool = False,
    request_timeout_seconds: int = 60,
) -> Dict[str, Any]:
    arguments: Dict[str, Any] = {
        "robotStudioExePath": robotstudio_exe_path,
        "timeoutSeconds": max(1, timeout_seconds),
    }
    if rsaddin_path:
        arguments["rsaddinPath"] = rsaddin_path
    if station_path:
        arguments["stationPath"] = station_path
    if environment:
        arguments["environment"] = environment
    if kill_all_robotstudio:
        arguments["killAllRobotStudio"] = True
    if kill_all_robvc:
        arguments["killAllRobVC"] = True
    arguments["loadAddins"] = True

    return watchdog_tool_call(
        client,
        WatchdogToolName.START_ROBOTSTUDIO_PROCESS,
        arguments,
        timeout_seconds=max(request_timeout_seconds, timeout_seconds + 15),
    )
