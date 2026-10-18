#!/usr/bin/env python3
import json
import pathlib
import time
import urllib.request
from dataclasses import dataclass
from typing import Any, Dict, Iterable, Optional, Tuple

from live_output_mode import DEFAULT_LIVE_OUTPUT_MODE, LiveOutputMode
from run_log_enums import StepStatus
from run_log_progress_logger import RunLogProgressLogger
from run_log_renderer import compute_overall, write_markdown
from run_log_state import RunLogState
from smoke_log_utils import load_template, resolve_repo_root, resolve_skill_root


def add_addin_smoke_args(
    parser,
    *,
    base_url_default: str = "http://127.0.0.1:39000",
    request_timeout_default: float = 30.0,
) -> None:
    parser.add_argument("--base-url", default=base_url_default, help="Add-in base URL")
    parser.add_argument(
        "--live-output",
        default=LiveOutputMode.ERROR.value,
        choices=[m.value for m in LiveOutputMode],
        help="Live progress output mode: none, error, info",
    )
    parser.add_argument("--latest", default=None, help="Path to persistent latest markdown log")
    parser.add_argument(
        "--request-timeout-seconds",
        type=float,
        default=request_timeout_default,
        help="Timeout per MCP JSON-RPC request (waits for response; increase for long operations)",
    )


def _create_run_log(
    current_file: pathlib.Path,
    latest_arg: Optional[str],
    default_latest_name: str,
    *,
    source: str,
    mode: str,
    tools: str,
    inputs: str,
    live_output: str,
) -> tuple[pathlib.Path, pathlib.Path, pathlib.Path, RunLogState, str]:
    repo_root = resolve_repo_root(current_file)
    skill_root = resolve_skill_root(current_file)
    latest_log = pathlib.Path(latest_arg) if latest_arg else skill_root / "runs" / default_latest_name
    latest_log.parent.mkdir(parents=True, exist_ok=True)

    template = load_template(skill_root)
    state = RunLogState(path=latest_log, source=source, mode=mode, tools=tools, inputs=inputs)
    raw_mode = (live_output or DEFAULT_LIVE_OUTPUT_MODE.value).strip().lower()
    mode_aliases = {
        "errors": LiveOutputMode.ERROR,
        "error": LiveOutputMode.ERROR,
        "info": LiveOutputMode.INFO,
        "none": LiveOutputMode.NONE,
    }
    selected_mode = mode_aliases.get(raw_mode)
    if selected_mode is None:
        allowed = ", ".join([m.value for m in LiveOutputMode])
        raise ValueError(f"Invalid live_output '{raw_mode}'. Expected one of: {allowed}.")
    state.subscribe(RunLogProgressLogger(mode=selected_mode).handle)
    state.start()
    write_markdown(state, template, latest_log)
    return repo_root, skill_root, latest_log, state, template


def _parse_mcp_response_body(body: str) -> dict:
    """Parses a Streamable HTTP POST response body.

    The MCP SDK transport always answers a POST with an SSE-framed body (`event: ...` /
    `data: <json>` lines), even for a single non-streaming reply, never a bare JSON object.
    """
    stripped = body.strip()
    if not stripped:
        return {}

    data_lines = [line[len("data:"):].strip() for line in stripped.splitlines() if line.startswith("data:")]
    return json.loads("\n".join(data_lines)) if data_lines else json.loads(stripped)


def http_json(url: str, payload: dict, timeout: float = 5.0) -> dict:
    data = json.dumps(payload).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        # The Streamable HTTP spec requires both; the server returns 406 if either is missing.
        "Accept": "application/json, text/event-stream",
    }
    req = urllib.request.Request(url, data=data, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        body = resp.read().decode("utf-8")
        return _parse_mcp_response_body(body)


def http_get(url: str, timeout: float = 5.0) -> Tuple[int, str]:
    req = urllib.request.Request(url, method="GET")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        body = resp.read().decode("utf-8")
        return resp.getcode(), body


def jsonrpc(url: str, method: str, params, req_id: int, timeout: float = 5.0) -> dict:
    payload = {
        "jsonrpc": "2.0",
        "id": req_id,
        "method": method,
    }
    if params is not None:
        payload["params"] = params
    return http_json(url, payload, timeout=timeout)


def tool_call(mcp_url: str, req_id: int, tool_name: str, arguments=None, timeout: float = 10.0) -> dict:
    arguments = arguments or {}
    response = jsonrpc(
        mcp_url,
        "tools/call",
        {"name": tool_name, "arguments": arguments},
        req_id,
        timeout=timeout,
    )
    result = response.get("result", {})
    return {
        "isError": bool(result.get("isError", False)),
        "structuredContent": result.get("structuredContent") or {},
        "content": result.get("content"),
        "raw": response,
    }


def has_response_keys(call: dict, *keys: str) -> bool:
    data = call.get("structuredContent") or {}
    return all(k in data for k in keys)


def is_station_active(call: dict) -> bool:
    return bool((call.get("structuredContent") or {}).get("isActive", False))


def check_health_initialize_tools(
    *,
    health_url: str,
    mcp_url: str,
    req_id: int,
    required_tools: Iterable[str],
    client_name: str,
    client_version: str,
    timeout: float = 5.0,
) -> Dict[str, Any]:
    status_code, _ = http_get(health_url, timeout=3.0)
    init = jsonrpc(
        mcp_url,
        "initialize",
        {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": client_name, "version": client_version},
        },
        req_id,
        timeout=timeout,
    )
    req_id += 1

    tools_list = jsonrpc(mcp_url, "tools/list", {}, req_id, timeout=timeout)
    req_id += 1

    listed = [t.get("name") for t in (tools_list.get("result", {}).get("tools") or [])]
    missing = [t for t in required_tools if t not in listed]

    return {
        "ok": status_code == 200 and "result" in init and not missing,
        "status_code": status_code,
        "initialize_ok": "result" in init,
        "missing_tools": missing,
        "next_req_id": req_id,
    }


def probe_station(mcp_url: str, req_id: int, timeout: float = 10.0) -> Dict[str, Any]:
    status_call = tool_call(mcp_url, req_id, "get_robotstudio_status", timeout=timeout)
    req_id += 1
    station_call = tool_call(mcp_url, req_id, "check_active_station", {}, timeout=timeout)
    req_id += 1

    return {
        "status_call": status_call,
        "station_call": station_call,
        "station_active": is_station_active(station_call),
        "next_req_id": req_id,
    }


def summarize_station_probe(probe: Dict[str, Any]) -> str:
    status_call = (probe or {}).get("status_call") or {}
    station_call = (probe or {}).get("station_call") or {}
    status = status_call.get("structuredContent") or {}
    station = station_call.get("structuredContent") or {}
    station_state = station.get("stationState") or {}

    parts = [
        f"processRunning={status.get('isProcessRunning', status.get('isRunning', 'unknown'))}",
        f"processCount={status.get('count', 'unknown')}",
        f"stationActive={bool((probe or {}).get('station_active'))}",
        f"stationName={station.get('stationName') or station.get('stationDisplayName') or station.get('name') or 'unknown'}",
    ]

    if isinstance(station_state, dict) and station_state:
        parts.extend(
            [
                f"hasActiveStation={station_state.get('hasActiveStation', 'unknown')}",
                f"controllerCount={station_state.get('controllerCount', 'unknown')}",
                f"allControllersReady={station_state.get('allControllersReady', 'unknown')}",
            ]
        )

    error = station.get("error") or station_call.get("raw", {}).get("error")
    if error:
        parts.append(f"error={error}")

    return "; ".join(parts)


def require_active_station_probe(probe: Optional[Dict[str, Any]], *, subject: str) -> Tuple[bool, str]:
    if probe is None:
        return False, f"Missing prerequisite probe for {subject}; the previous step did not capture station state."

    if not bool(probe.get("station_active")):
        return False, f"No active station for {subject}. Probe: {summarize_station_probe(probe)}"

    return True, summarize_station_probe(probe)


def wait_for_active_station_probe(
    ctx: "AddinSmokeContext",
    *,
    subject: str,
    timeout_seconds: float = 30.0,
    poll_interval_seconds: float = 1.0,
) -> Tuple[bool, str, Optional[Dict[str, Any]]]:
    deadline = time.monotonic() + max(1.0, float(timeout_seconds))
    last_probe: Optional[Dict[str, Any]] = None

    while True:
        last_probe = ctx.probe_station()
        ok, summary = require_active_station_probe(last_probe, subject=subject)
        if ok:
            return True, summary, last_probe

        if time.monotonic() >= deadline:
            return False, summary, last_probe

        time.sleep(max(0.25, float(poll_interval_seconds)))


def apply_missing_station_policy(ctx: "AddinSmokeContext", on_missing_station: str) -> int:
    policy = str(on_missing_station).strip().lower()
    if policy == "fail":
        ctx.update("S2", StepStatus.FAIL, "No active station open; open a station and rerun.")
        ctx.skip_remaining(3, "Station pre-req missing.")
        return 1

    ctx.update("S2", StepStatus.SKIP, "No active station open; CRUD smoke skipped by policy.")
    ctx.update("S3", StepStatus.SKIP, "No active station; CRUD contract checks skipped.")
    ctx.update("S4", StepStatus.SKIP, "No active station; side-effect checks skipped.")
    ctx.update("S5", StepStatus.PASS, "No station state modified; cleanup not required.")
    return 0


@dataclass
class AddinSmokeContext:
    repo_root: pathlib.Path
    skill_root: pathlib.Path
    latest_log: pathlib.Path
    state: RunLogState
    template: str
    base_url: str
    request_timeout_seconds: float
    required_tools: Iterable[str]
    step_count: int = 5
    req_id: int = 1

    @property
    def mcp_url(self) -> str:
        return self.base_url.rstrip("/") + "/mcp"

    @property
    def health_url(self) -> str:
        return self.base_url.rstrip("/") + "/health"

    @property
    def req_timeout(self) -> float:
        return max(3.0, float(self.request_timeout_seconds))

    @classmethod
    def initialize(
        cls,
        *,
        current_file: pathlib.Path,
        latest_arg: Optional[str],
        default_latest_name: str,
        source: str,
        mode: str,
        tools: Iterable[str],
        inputs: str,
        base_url: str,
        request_timeout_seconds: float,
        live_output: str = DEFAULT_LIVE_OUTPUT_MODE.value,
        step_count: int = 5,
    ) -> "AddinSmokeContext":
        repo_root, skill_root, latest_log, state, template = _create_run_log(
            current_file,
            latest_arg,
            default_latest_name,
            source=source,
            mode=mode,
            tools=", ".join(tools),
            inputs=inputs,
            live_output=live_output,
        )
        return cls(
            repo_root=repo_root,
            skill_root=skill_root,
            latest_log=latest_log,
            state=state,
            template=template,
            base_url=base_url,
            request_timeout_seconds=request_timeout_seconds,
            required_tools=list(tools),
            step_count=step_count,
        )

    def update(self, step: str, status: StepStatus, evidence: str) -> None:
        self.state.record_step(step, status, evidence)
        write_markdown(self.state, self.template, self.latest_log)

    def skip_remaining(self, from_step: int, reason: str) -> None:
        for step in range(from_step, max(1, int(self.step_count)) + 1):
            self.state.record_step(f"S{step}", StepStatus.SKIP, reason)
        write_markdown(self.state, self.template, self.latest_log)

    def print_log_path(self) -> int:
        overall = compute_overall(self.state)
        self.state.finalize()
        write_markdown(self.state, self.template, self.latest_log, overall=overall)
        print(str(self.latest_log))
        return 0 if overall in {StepStatus.PASS.value, "PASS-WITH-SKIPS"} else 1

    def fail(self, step: str, evidence: str, *, skip_from: Optional[int] = None, skip_reason: Optional[str] = None, exit_code: int = 1) -> int:
        self.update(step, StepStatus.FAIL, evidence)
        if skip_from is not None:
            self.skip_remaining(skip_from, skip_reason or evidence)
        self.print_log_path()
        return exit_code

    def run_preconditions(self, *, client_name: str, client_version: str) -> Dict[str, Any]:
        pre = check_health_initialize_tools(
            health_url=self.health_url,
            mcp_url=self.mcp_url,
            req_id=self.req_id,
            required_tools=self.required_tools,
            client_name=client_name,
            client_version=client_version,
            timeout=self.req_timeout,
        )
        self.req_id = int(pre["next_req_id"])
        return pre

    def probe_station(self) -> Dict[str, Any]:
        probe = probe_station(self.mcp_url, self.req_id, timeout=self.req_timeout)
        self.req_id = int(probe["next_req_id"])
        return probe

    def call_tool(self, tool_name: str, arguments=None) -> dict:
        call = tool_call(self.mcp_url, self.req_id, tool_name, arguments, timeout=self.req_timeout)
        self.req_id += 1
        return call

    def call_tool_ok(self, tool_name: str, arguments=None, *, error_prefix: Optional[str] = None) -> dict:
        call = self.call_tool(tool_name, arguments)
        if call.get("isError"):
            payload = call.get("structuredContent") or {}
            message = payload.get("error", "unknown error")
            prefix = error_prefix or tool_name
            raise RuntimeError(f"{prefix}: {message}")
        return call

    def ensure_active_station(self, *, create_if_missing: bool, create_error_prefix: str = "create_station failed") -> bool:
        probe = self.probe_station()
        station_active = bool(probe["station_active"])
        if station_active or not create_if_missing:
            return station_active

        create_call = self.call_tool("create_station", {})
        if create_call.get("isError"):
            payload = create_call.get("structuredContent") or {}
            message = payload.get("error", "unknown error")
            raise RuntimeError(f"{create_error_prefix}: {message}")

        reprobe = self.probe_station()
        return bool(reprobe["station_active"])

    def require_contract(self, checks: Iterable[Tuple[str, bool]], *, step: str, success_evidence: str) -> None:
        missing = [label for label, ok in checks if not ok]
        if missing:
            self.update(step, StepStatus.FAIL, f"Missing contract keys in: {missing}")
        else:
            self.update(step, StepStatus.PASS, success_evidence)
