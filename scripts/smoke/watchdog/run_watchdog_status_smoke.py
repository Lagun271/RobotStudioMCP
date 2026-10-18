#!/usr/bin/env python3
# ruff: noqa: E402
from __future__ import annotations

import argparse
import pathlib
import sys
from typing import Optional

SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
SMOKE_ROOT = SCRIPT_DIR.parent
LIB_DIR = SMOKE_ROOT / "_lib"
if str(LIB_DIR) not in sys.path:
    sys.path.insert(0, str(LIB_DIR))

from run_log_enums import StepStatus
from run_log_progress_logger import RunLogProgressLogger
from run_log_renderer import compute_overall, write_markdown
from run_log_state import RunLogState
from smoke_log_utils import load_template, resolve_repo_root, resolve_skill_root
from watchdog_smoke_common import WatchdogSmokeHarness
from watchdog_tool_name import WatchdogToolName

REQUIRED_MCP_TOOLS = (WatchdogToolName.GET_ROBOTSTUDIO_PROCESS_STATUS,)


def _compare_status_payloads(cli_payload: dict, mcp_payload: dict) -> tuple[bool, str]:
    cli_status = cli_payload.get("status") or {}
    mcp_status = mcp_payload or {}
    cli_vs_mcp_keys = ["isRunning", "count", "processIds"]
    mismatched = [key for key in cli_vs_mcp_keys if cli_status.get(key) != mcp_status.get(key)]

    if mismatched:
        return False, f"CLI status differs from MCP status for keys: {mismatched}. CLI={cli_status}; MCP={mcp_status}"

    return True, "CLI status matches MCP status for shared fields."


def _create_run_log(current_file: pathlib.Path, latest_arg: Optional[str], default_latest_name: str, *, source: str, mode: str, tools: str, inputs: str) -> tuple[pathlib.Path, pathlib.Path, pathlib.Path, RunLogState, str]:
    repo_root = resolve_repo_root(current_file)
    skill_root = resolve_skill_root(current_file)
    latest_log = pathlib.Path(latest_arg) if latest_arg else skill_root / "runs" / default_latest_name
    latest_log.parent.mkdir(parents=True, exist_ok=True)

    template = load_template(skill_root)
    state = RunLogState(path=latest_log, source=source, mode=mode, tools=tools, inputs=inputs)
    state.subscribe(RunLogProgressLogger().handle)
    state.start()
    write_markdown(state, template, latest_log)
    return repo_root, skill_root, latest_log, state, template


def _record_skips(state: RunLogState, from_step: int, to_step: int, reason: str) -> None:
    for step in range(from_step, to_step + 1):
        state.record_step(f"S{step}", StepStatus.SKIP, reason)


def _finalize_and_print(state: RunLogState, template: str, latest_log: pathlib.Path) -> None:
    write_markdown(state, template, latest_log, overall=compute_overall(state))
    print(str(latest_log))


def main() -> int:
    parser = argparse.ArgumentParser(description="Watchdog smoke test for shared CLI/MCP status surface.")
    parser.add_argument("--latest", default=None, help="Path to persistent latest markdown log")
    parser.add_argument("--watchdog-exe", default=None, help="Path to the local watchdog exe.")
    parser.add_argument("--request-timeout-seconds", type=int, default=60)
    parser.add_argument("--cli-command-timeout-seconds", type=int, default=60)
    args = parser.parse_args()

    repo_root, _skill_root, latest_log, state, template = _create_run_log(
        pathlib.Path(__file__),
        args.latest,
        "watchdog-status-smoke-latest.md",
        source="robotstudio-watchdog-mcp",
        mode="smoke/status",
        tools="robotstudio-watchdog status; get_robotstudio_process_status",
        inputs="status checks on CLI and MCP surfaces",
    )

    harness = WatchdogSmokeHarness(
        repo_root=repo_root,
        request_timeout_seconds=args.request_timeout_seconds,
        print_prefix="[watchdog-status]",
        watchdog_exe_path=pathlib.Path(args.watchdog_exe) if args.watchdog_exe else None,
    )

    watchdog = None
    try:
        watchdog_exe_path = harness.watchdog_exe()
        if not watchdog_exe_path.exists():
            state.record_step("S1", StepStatus.FAIL, f"Watchdog exe not found: {watchdog_exe_path}.")
            _finalize_and_print(state, template, latest_log)
            return 1

        watchdog, client = harness.start_mcp()
        init = client.initialize("watchdog-status-smoke", "0.1.0", timeout_seconds=args.request_timeout_seconds)
        if "result" not in init:
            state.record_step("S1", StepStatus.FAIL, f"Watchdog initialize failed: {init}")
            _record_skips(state, 2, 3, "S1 failed; CLI status skipped.")
            _finalize_and_print(state, template, latest_log)
            return 1

        harness.ensure_mcp_tools(client, REQUIRED_MCP_TOOLS)
        state.record_step("S1", StepStatus.PASS, f"Watchdog initialized; MCP tool available: {str(REQUIRED_MCP_TOOLS[0])}")

        cli_status = harness.run_cli("status", timeout_seconds=args.cli_command_timeout_seconds)
        if cli_status["returncode"] != 0:
            state.record_step("S2", StepStatus.FAIL, f"CLI status failed: returncode={cli_status['returncode']}; stdout={cli_status['stdout']!r}; stderr={cli_status['stderr']!r}")
            state.record_step("S3", StepStatus.SKIP, "S2 failed; MCP status skipped.")
            _finalize_and_print(state, template, latest_log)
            return 1

        cli_payload = cli_status.get("payload") or {}
        if cli_payload.get("command") != "status" or cli_payload.get("ok") is not True:
            state.record_step("S2", StepStatus.FAIL, f"CLI status payload invalid: {cli_payload}")
            state.record_step("S3", StepStatus.SKIP, "S2 failed; MCP status skipped.")
            _finalize_and_print(state, template, latest_log)
            return 1

        state.record_step("S2", StepStatus.PASS, f"CLI status returned JSON: {cli_payload}")

        mcp_status = harness.call_mcp(client, WatchdogToolName.GET_ROBOTSTUDIO_PROCESS_STATUS, {})
        ok, evidence = _compare_status_payloads(cli_payload, mcp_status)
        if ok:
            state.record_step("S3", StepStatus.PASS, evidence)
            state.record_step("S4", StepStatus.SKIP, "Status surface is read-only; no state mutation to clean up.")
            state.record_step("S5", StepStatus.PASS, "No cleanup required for shared status surface smoke.")
            state.record_step("S6", StepStatus.SKIP, "Status smoke does not exercise lifecycle verbs.")
            state.record_step("S7", StepStatus.SKIP, "Status smoke does not exercise lifecycle verbs.")
            state.record_step("S8", StepStatus.PASS, "Read-only status surface completed without cleanup.")
        else:
            state.record_step("S3", StepStatus.FAIL, evidence)
            state.record_step("S4", StepStatus.SKIP, "S3 failed; cleanup not required.")
            state.record_step("S5", StepStatus.PASS, "No cleanup required for shared status surface smoke.")
            state.record_step("S6", StepStatus.SKIP, "Status smoke does not exercise lifecycle verbs.")
            state.record_step("S7", StepStatus.SKIP, "Status smoke does not exercise lifecycle verbs.")
            state.record_step("S8", StepStatus.PASS, "Read-only status surface completed without cleanup.")

        _finalize_and_print(state, template, latest_log)
        return 0 if ok else 1

    except Exception as ex:
        for step in ["S1", "S2", "S3", "S4", "S5"]:
            if step not in state.steps:
                state.record_step(step, StepStatus.FAIL if step == "S1" else StepStatus.SKIP, f"Error: {ex}" if step == "S1" else "Skipped after failure.")
        _finalize_and_print(state, template, latest_log)
        return 1
    finally:
        if watchdog is not None:
            try:
                watchdog.kill()
            except Exception:
                pass


if __name__ == "__main__":
    raise SystemExit(main())
