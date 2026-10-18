#!/usr/bin/env python3
# ruff: noqa: E402
import pathlib
import subprocess
import sys

SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
SMOKE_ROOT = SCRIPT_DIR.parent
LIB_DIR = SMOKE_ROOT / "_lib"
if str(LIB_DIR) not in sys.path:
    sys.path.insert(0, str(LIB_DIR))

import argparse
from typing import Optional

from run_log_enums import StepStatus
from run_log_progress_logger import RunLogProgressLogger
from run_log_renderer import compute_overall, write_markdown
from run_log_state import RunLogState
from smoke_log_utils import load_template, resolve_repo_root, resolve_skill_root
from watchdog_smoke_common import WatchdogSmokeHarness



def _expect(payload: dict, key: str, expected, label: str) -> tuple[bool, str]:
    actual = payload.get(key)
    if actual == expected:
        return True, f"{label}={expected}."
    return False, f"{label} expected {expected!r} but got {actual!r}. Payload={payload}"


def _force_cleanup_robotstudio() -> None:
    subprocess.run(
        [
            "powershell.exe",
            "-NoProfile",
            "-Command",
            (
                "Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'RobotStudio.exe') -or ($_.Name -eq 'RobVC.exe') } "
                "| ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; exit 0"
            ),
        ],
        check=False,
        capture_output=True,
        text=True,
    )


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


def _record_skips(state: RunLogState, start: int, end: int, reason: str) -> None:
    for step in range(start, end + 1):
        state.record_step(f"S{step}", StepStatus.SKIP, reason)


def _finalize_and_print(state: RunLogState, template: str, latest_log: pathlib.Path) -> None:
    write_markdown(state, template, latest_log, overall=compute_overall(state))
    print(str(latest_log))


def _validate_cli_phase(state: RunLogState, template: str, latest_log: pathlib.Path, harness: WatchdogSmokeHarness, *, start_timeout_seconds: int, cli_command_timeout_seconds: int) -> bool:
    start_payload = harness.run_cli(
        "start",
        [
            "--kill-all-robotstudio",
            "--kill-all-robvc",
            "--start-timeout-seconds",
            str(start_timeout_seconds),
        ],
        timeout_seconds=cli_command_timeout_seconds,
    )
    start_result = start_payload.get("payload") or {}
    if start_payload["returncode"] != 0 or start_result.get("command") != "start" or start_result.get("ok") is not True:
        state.record_step("S2", StepStatus.FAIL, f"CLI start failed: returncode={start_payload['returncode']}; stdout={start_payload['stdout']!r}; stderr={start_payload['stderr']!r}; payload={start_result}")
        state.record_step("S3", StepStatus.SKIP, "S2 failed; CLI status skipped.")
        state.record_step("S4", StepStatus.SKIP, "S2 failed; CLI stop skipped.")
        return False

    ok, evidence = _expect((start_result.get("start") or {}), "started", True, "CLI start.started")
    if not ok:
        state.record_step("S2", StepStatus.FAIL, evidence)
        state.record_step("S3", StepStatus.SKIP, "S2 failed; CLI status skipped.")
        state.record_step("S4", StepStatus.SKIP, "S2 failed; CLI stop skipped.")
        return False

    state.record_step("S2", StepStatus.PASS, f"CLI start returned JSON: {start_result}")

    status_payload = harness.run_cli(
        "status",
        [],
        timeout_seconds=cli_command_timeout_seconds,
    )
    status_result = status_payload.get("payload") or {}
    if status_payload["returncode"] != 0 or status_result.get("command") != "status" or status_result.get("ok") is not True:
        state.record_step("S3", StepStatus.FAIL, f"CLI status failed: returncode={status_payload['returncode']}; stdout={status_payload['stdout']!r}; stderr={status_payload['stderr']!r}; payload={status_result}")
        state.record_step("S4", StepStatus.SKIP, "S3 failed; CLI stop skipped.")
        return False

    ok, evidence = _expect((status_result.get("status") or {}), "isRunning", True, "CLI status.isRunning")
    if not ok:
        state.record_step("S3", StepStatus.FAIL, evidence)
        state.record_step("S4", StepStatus.SKIP, "S3 failed; CLI stop skipped.")
        return False

    state.record_step("S3", StepStatus.PASS, f"CLI status returned JSON: {status_result}")

    stop_payload = harness.run_cli(
        "stop",
        ["--graceful-timeout-seconds", "1", "--force-kill-only"],
        timeout_seconds=cli_command_timeout_seconds,
    )
    stop_result = stop_payload.get("payload") or {}
    if stop_payload["returncode"] != 0 or stop_result.get("command") != "stop" or stop_result.get("ok") is not True:
        state.record_step("S4", StepStatus.FAIL, f"CLI stop failed: returncode={stop_payload['returncode']}; stdout={stop_payload['stdout']!r}; stderr={stop_payload['stderr']!r}; payload={stop_result}")
        return False

    ok, evidence = _expect((stop_result.get("stop") or {}), "stopped", True, "CLI stop.stopped")
    if not ok:
        state.record_step("S4", StepStatus.FAIL, evidence)
        return False

    state.record_step("S4", StepStatus.PASS, f"CLI stop returned JSON: {stop_result}")
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description="Deterministic watchdog smoke test for CLI lifecycle surface.")
    parser.add_argument("--latest", default=None, help="Path to persistent latest markdown log")
    parser.add_argument("--watchdog-exe", default=None, help="Path to the local watchdog exe.")
    parser.add_argument("--cli-command-timeout-seconds", type=int, default=120)
    parser.add_argument("--start-timeout-seconds", type=int, default=90)
    args = parser.parse_args()

    repo_root, _skill_root, latest_log, state, template = _create_run_log(
        pathlib.Path(__file__),
        args.latest,
        "watchdog-lifecycle-smoke-latest.md",
        source="robotstudio-watchdog-mcp",
        mode="smoke/lifecycle",
        tools="robotstudio-watchdog start/status/stop",
        inputs="deterministic CLI lifecycle smoke using a shared station fixture and no MCP readiness probing",
    )

    harness = WatchdogSmokeHarness(
        repo_root=repo_root,
        request_timeout_seconds=30,
        print_prefix="[watchdog-life]",
        watchdog_exe_path=pathlib.Path(args.watchdog_exe) if args.watchdog_exe else None,
    )

    try:
        _force_cleanup_robotstudio()
        watchdog_exe_path = harness.watchdog_exe()
        if not watchdog_exe_path.exists():
            state.record_step("S1", StepStatus.FAIL, f"Watchdog exe not found: {watchdog_exe_path}.")
            _finalize_and_print(state, template, latest_log)
            return 1

        state.record_step("S1", StepStatus.PASS, f"Watchdog exe present: {watchdog_exe_path}")
        cli_ok = _validate_cli_phase(
            state,
            template,
            latest_log,
            harness,
            start_timeout_seconds=args.start_timeout_seconds,
            cli_command_timeout_seconds=args.cli_command_timeout_seconds,
        )
        _force_cleanup_robotstudio()
        state.record_step("S5", StepStatus.PASS, "Cleanup completed after deterministic CLI lifecycle smoke.")
        _finalize_and_print(state, template, latest_log)
        return 0 if cli_ok else 1

    except Exception as ex:
        for step in ["S1", "S2", "S3", "S4", "S5"]:
            if step not in state.steps:
                state.record_step(step, StepStatus.FAIL if step == "S1" else StepStatus.SKIP, f"Error: {ex}" if step == "S1" else "Skipped after failure.")
        _force_cleanup_robotstudio()
        _finalize_and_print(state, template, latest_log)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
