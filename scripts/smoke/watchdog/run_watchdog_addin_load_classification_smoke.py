#!/usr/bin/env python3
# ruff: noqa: E402
from __future__ import annotations

import argparse
import pathlib
import subprocess
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

# Live-boot regression smoke for the PowerPac add-in load classification decision
# table documented in docs/watchdog-addin-load-classification.md.
#
# Uses the in-repo DummyPowerPacAddin fixture (AddInType=PowerPac) instead of a
# real third-party PowerPac add-in, so this smoke does not depend on any
# sibling/private repo. RobotStudio stages PowerPac add-ins (AddinAdded) but
# never auto-loads them regardless of whether the assembly implements a real
# PowerPac entrypoint, so the fixture does not need a working implementation to
# reproduce that log signature.

EXPECTED_CODE = "ADDIN_AWAITING_ACTIVATION"
EXPECTED_STATUS = "awaiting manual activation"


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


def _finalize_and_print(state: RunLogState, template: str, latest_log: pathlib.Path) -> None:
    write_markdown(state, template, latest_log, overall=compute_overall(state))
    print(str(latest_log))


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Live RobotStudio smoke test for PowerPac add-in load classification (#167), using the in-repo DummyPowerPacAddin fixture."
    )
    parser.add_argument("--latest", default=None, help="Path to persistent latest markdown log")
    parser.add_argument("--watchdog-exe", default=None, help="Path to the local watchdog exe.")
    parser.add_argument("--rsaddin-path", default=None, help="Override path to the PowerPac fixture .rsaddin; defaults to the in-repo DummyPowerPacAddin build output.")
    parser.add_argument("--start-timeout-seconds", type=int, default=90)
    parser.add_argument("--ready-timeout-seconds", type=int, default=60)
    parser.add_argument("--cli-command-timeout-seconds", type=int, default=150)
    args = parser.parse_args()

    repo_root, _skill_root, latest_log, state, template = _create_run_log(
        pathlib.Path(__file__),
        args.latest,
        "watchdog-addin-load-classification-smoke-latest.md",
        source="robotstudio-watchdog-mcp",
        mode="smoke/addin-load-classification",
        tools="robotstudio-watchdog start/ready/stop",
        inputs="live RobotStudio boot classifying the in-repo DummyPowerPacAddin fixture (#167)",
    )

    harness = WatchdogSmokeHarness(
        repo_root=repo_root,
        request_timeout_seconds=30,
        print_prefix="[watchdog-addin-load]",
        watchdog_exe_path=pathlib.Path(args.watchdog_exe) if args.watchdog_exe else None,
    )

    rsaddin_path = (
        pathlib.Path(args.rsaddin_path)
        if args.rsaddin_path
        else repo_root / "test" / "DummyPowerPacAddin" / "bin" / "Debug" / "net48" / "DummyPowerPacAddin.rsaddin"
    )
    # DisplayName in the fixture manifest matches the rsaddin filename exactly, so
    # the rsaddinId the watchdog derives from --rsaddin-path (filename without
    # extension) lines up with what RobotStudio actually logs - avoiding the
    # filename-vs-logged-id mismatch tracked separately in issue #180.
    package_id = "DummyPowerPacAddin"
    usage_logs_root = pathlib.Path(r"C:\ProgramData\ABB\RobotStudio\UsageLogs")
    error_logs_root = pathlib.Path(r"C:\ProgramData\ABB\RobotStudio\ErrorLogs")

    try:
        _force_cleanup_robotstudio()

        watchdog_exe_path = harness.watchdog_exe()
        if not watchdog_exe_path.exists():
            state.record_step("S1", StepStatus.FAIL, f"Watchdog exe not found: {watchdog_exe_path}.")
            _finalize_and_print(state, template, latest_log)
            return 1

        if not rsaddin_path.exists():
            state.record_step(
                "S1",
                StepStatus.FAIL,
                f"PowerPac fixture rsaddin not found: {rsaddin_path}. Build it first: make dummy-powerpac-addin-build",
            )
            _finalize_and_print(state, template, latest_log)
            return 1

        state.record_step("S1", StepStatus.PASS, f"Watchdog exe present: {watchdog_exe_path}; PowerPac fixture present: {rsaddin_path}")

        start_payload = harness.run_cli(
            "start",
            [
                "--rsaddin-path", str(rsaddin_path),
                "--kill-all-robotstudio",
                "--kill-all-robvc",
                "--start-timeout-seconds", str(args.start_timeout_seconds),
            ],
            timeout_seconds=args.cli_command_timeout_seconds,
        )
        start_result = start_payload.get("payload") or {}
        if start_payload["returncode"] != 0 or start_result.get("command") != "start" or (start_result.get("start") or {}).get("started") is not True:
            state.record_step("S2", StepStatus.FAIL, f"CLI start failed: returncode={start_payload['returncode']}; stdout={start_payload['stdout']!r}; stderr={start_payload['stderr']!r}; payload={start_result}")
            state.record_step("S3", StepStatus.SKIP, "S2 failed; ready check skipped.")
            state.record_step("S4", StepStatus.SKIP, "S2 failed; stop skipped.")
            _force_cleanup_robotstudio()
            _finalize_and_print(state, template, latest_log)
            return 1

        state.record_step("S2", StepStatus.PASS, f"RobotStudio started with PowerPac fixture rsaddin: {rsaddin_path}")

        ready_payload = harness.run_cli(
            "ready",
            [
                "--package-id", package_id,
                "--rsaddin-path", str(rsaddin_path),
                "--usage-logs-root", str(usage_logs_root),
                "--error-logs-root", str(error_logs_root),
                "--timeout-seconds", str(args.ready_timeout_seconds),
                "--retry-delay-ms", "1000",
            ],
            timeout_seconds=args.cli_command_timeout_seconds,
        )
        ready_result = ready_payload.get("payload") or {}
        ready_inner = ready_result.get("ready") or {}
        runtime_readiness = ready_inner.get("runtimeReadiness") or {}
        rsaddin_load = runtime_readiness.get("rsaddinLoad") or {}

        code = rsaddin_load.get("code")
        status_text = rsaddin_load.get("status")
        ok = rsaddin_load.get("ok")

        cli_ok: bool
        if code == EXPECTED_CODE and status_text == EXPECTED_STATUS and ok is True:
            state.record_step(
                "S3",
                StepStatus.PASS,
                f"PowerPac fixture correctly classified as awaiting manual activation: rsaddinLoad={rsaddin_load}",
            )
            cli_ok = True
        else:
            state.record_step(
                "S3",
                StepStatus.FAIL,
                f"PowerPac fixture was not classified as awaiting manual activation "
                f"(expected code={EXPECTED_CODE!r}, status={EXPECTED_STATUS!r}, ok=True). "
                f"Got rsaddinLoad={rsaddin_load}. See docs/watchdog-addin-load-classification.md.",
            )
            cli_ok = False

        stop_payload = harness.run_cli(
            "stop",
            ["--graceful-timeout-seconds", "1", "--force-kill-only"],
            timeout_seconds=args.cli_command_timeout_seconds,
        )
        stop_result = stop_payload.get("payload") or {}
        if stop_payload["returncode"] != 0 or stop_result.get("command") != "stop" or (stop_result.get("stop") or {}).get("stopped") is not True:
            state.record_step("S4", StepStatus.FAIL, f"CLI stop failed: returncode={stop_payload['returncode']}; stdout={stop_payload['stdout']!r}; stderr={stop_payload['stderr']!r}; payload={stop_result}")
            cli_ok = False
        else:
            state.record_step("S4", StepStatus.PASS, f"CLI stop returned JSON: {stop_result}")

        _force_cleanup_robotstudio()
        state.record_step(
            "S5",
            StepStatus.PASS if cli_ok else StepStatus.SKIP,
            "Cleanup completed after PowerPac add-in load classification smoke." if cli_ok else "Cleanup completed despite earlier failure.",
        )
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
