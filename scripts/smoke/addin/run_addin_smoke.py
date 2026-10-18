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
import json
import time

from live_output_mode import LiveOutputMode
from path_utils import to_windows_path
from smoke_common import is_station_active, tool_call
from smoke_log_utils import resolve_repo_root
from watchdog_stdio_common import (
    StdioMcpClient,
    default_robotstudio_exe,
    parse_env_args,
    start_watchdog,
    watchdog_tool_call,
)
from watchdog_tool_name import WatchdogToolName


def _find_latest_rspak(repo_root: pathlib.Path) -> pathlib.Path:
    search_roots = [
        repo_root / "src" / "RobotStudioMCP.AddinMain",
        repo_root / "artifacts",
    ]

    candidates: list[pathlib.Path] = []
    for root in search_roots:
        if not root.exists():
            continue
        candidates.extend(root.rglob("RobotStudioMCP-*.rspak"))

    if not candidates:
        raise RuntimeError("No RobotStudioMCP-*.rspak found. Run `make addin-pack` first.")

    return sorted(candidates, key=lambda p: p.stat().st_mtime, reverse=True)[0]


def _dummy_addin_rsaddin(repo_root: pathlib.Path) -> pathlib.Path:
    dummy_rsaddin = repo_root / "test" / "DummyRobotStudioAddin" / "bin" / "Debug" / "net10.0-windows" / "DummyRobotStudioAddin.rsaddin"
    if not dummy_rsaddin.exists():
        raise RuntimeError(f"Dummy add-in rsaddin not found: {dummy_rsaddin}. Build it with make dummy-addin-build first.")
    return dummy_rsaddin


def main() -> int:
    repo_root = resolve_repo_root(pathlib.Path(__file__))

    parser = argparse.ArgumentParser(
        description="Generic watchdog-backed E2E runner for add-in smoke scripts.",
    )
    parser.add_argument("--script", required=True, help="Smoke script filename under scripts/smoke/addin or absolute path")
    parser.add_argument("--base-url", default="http://127.0.0.1:39000", help="Add-in base URL used for readiness checks")
    parser.add_argument("--robotstudio-exe", default=None, help="Full path to RobotStudio.exe (passed to watchdog start tool)")
    parser.add_argument("--rsaddin", default=None, help="Optional absolute full path to an extra user add-in `.rsaddin` for explicit /addin launch alongside the installed MCP add-in")
    parser.add_argument("--use-dummy-rsaddin", action="store_true", help="Use DummyRobotStudioAddin build output as --rsaddin")
    parser.add_argument("--station-path", default=None, help="Optional station project path to open when starting RobotStudio (relative paths are resolved from the repo root). When set, runner waits for an active station before invoking the smoke.")
    parser.add_argument("--install-rspak", action="store_true", help="Install RobotStudioMCP rspak via watchdog before starting RobotStudio")
    parser.add_argument("--rspak-path", default=None, help="Optional explicit path to RobotStudioMCP .rspak (used with --install-rspak). If omitted, latest local RobotStudioMCP-*.rspak is used")
    parser.add_argument("--env", action="append", default=None, help="Repeatable process-scoped environment variable in KEY=VALUE form passed to watchdog StartRobotStudioProcess")
    parser.add_argument("--kill-all-robotstudio", action="store_true", help="Kill all RobotStudio processes before watchdog launch")
    parser.add_argument("--kill-all-robvc", action="store_true", help="Kill all RobVC processes before watchdog launch")
    parser.add_argument("--start-timeout-seconds", type=int, default=45, help="Timeout for watchdog StartRobotStudioProcess")
    parser.add_argument("--ready-timeout-seconds", type=int, default=45, help="Timeout for watchdog VerifyAddinMcpReady")
    parser.add_argument("--retry-delay-ms", type=int, default=500, help="Retry delay for watchdog VerifyAddinMcpReady")
    parser.add_argument("--request-timeout-seconds", type=int, default=60, help="Timeout per watchdog MCP request")
    parser.add_argument("--smoke-timeout-seconds", type=int, default=300, help="Timeout for target smoke script")
    parser.add_argument("--keep-started-rs", action="store_true", help="Keep started RobotStudio process running after smoke run (default is to stop it)")
    parser.add_argument("--live-output", default=LiveOutputMode.ERROR.value, choices=[m.value for m in LiveOutputMode], help="Live progress output mode: none, error, info")
    parser.add_argument("script_args", nargs=argparse.REMAINDER, help="Arguments passed through to target smoke script; prefix with --")
    args = parser.parse_args()

    if args.use_dummy_rsaddin and args.rsaddin:
        print("ERROR: --use-dummy-rsaddin and --rsaddin are mutually exclusive.")
        return 2

    script_path = pathlib.Path(args.script)
    if not script_path.is_absolute():
        script_path = repo_root / "scripts" / "smoke" / "addin" / script_path
    if not script_path.exists():
        print(f"ERROR: Smoke script not found: {script_path}")
        return 2

    forwarded_args = list(args.script_args)
    if forwarded_args and forwarded_args[0] == "--":
        forwarded_args = forwarded_args[1:]

    rsaddin_input = args.rsaddin
    if args.use_dummy_rsaddin:
        dummy_rsaddin = _dummy_addin_rsaddin(repo_root)
        print(f"[e2e] Using built dummy user add-in rsaddin: {dummy_rsaddin}", flush=True)
        rsaddin_input = str(dummy_rsaddin)

    exe = to_windows_path(args.robotstudio_exe or default_robotstudio_exe()) or default_robotstudio_exe()
    rsaddin = to_windows_path(rsaddin_input)
    station_path = None
    if args.station_path:
        station_path = pathlib.Path(args.station_path)
        if not station_path.is_absolute():
            station_path = repo_root / station_path
        station_path = to_windows_path(str(station_path))
    environment = parse_env_args(args.env)

    watchdog = None
    client = None
    try:
        watchdog = start_watchdog(repo_root, print_prefix="[e2e]")
        client = StdioMcpClient(watchdog)

        print("[e2e] Initializing watchdog MCP...", flush=True)
        init = client.initialize("addin-generic-smoke-e2e", "0.1.0", timeout_seconds=args.request_timeout_seconds)
        if "result" not in init:
            raise RuntimeError(f"Watchdog initialize failed: {init}")

        if args.install_rspak:
            rspak = pathlib.Path(args.rspak_path).resolve() if args.rspak_path else _find_latest_rspak(repo_root)
            if not rspak.exists():
                raise RuntimeError(f"RobotStudioMCP rspak not found: {rspak}")
            print(f"[e2e] Installing RobotStudioMCP rspak: {rspak}", flush=True)
            install_payload = watchdog_tool_call(
                client,
                WatchdogToolName.INSTALL_RSPAK_PACKAGE,
                {
                    "rspakPath": to_windows_path(str(rspak)) or str(rspak),
                    "overwriteExisting": True,
                    "dryRun": False,
                },
                timeout_seconds=max(args.request_timeout_seconds, 60),
            )
            print(f"[e2e] InstallRspakPackage => {json.dumps(install_payload)}", flush=True)
            if not install_payload.get("installed"):
                print("ERROR: InstallRspakPackage reported installed=false.", flush=True)
                return 1

        print("[e2e] Calling StartRobotStudioProcess...", flush=True)
        start_arguments = {
            "robotStudioExePath": exe,
            "timeoutSeconds": max(1, args.start_timeout_seconds),
            "loadAddins": True,
        }
        if rsaddin:
            start_arguments["rsaddinPath"] = rsaddin
        if station_path:
            start_arguments["stationPath"] = station_path
        if environment:
            start_arguments["environment"] = environment
        if args.kill_all_robotstudio:
            start_arguments["killAllRobotStudio"] = True
        if args.kill_all_robvc:
            start_arguments["killAllRobVC"] = True

        start_payload = watchdog_tool_call(
            client,
            WatchdogToolName.START_ROBOTSTUDIO_PROCESS,
            start_arguments,
            timeout_seconds=max(args.request_timeout_seconds, args.start_timeout_seconds + 15),
        )
        print(f"[e2e] StartRobotStudioProcess => {json.dumps(start_payload)}", flush=True)
        started = start_payload.get("started") is True
        already_running = start_payload.get("alreadyRunning") is True
        if not started and not already_running:
            print("ERROR: StartRobotStudioProcess returned started=false and alreadyRunning=false.", flush=True)
            return 1
        if already_running:
            print("[e2e] Reusing an already running tracked RobotStudio process.", flush=True)

        print("[e2e] Checking VerifyAddinMcpReady...", flush=True)
        verify_arguments = {
            "baseUrl": args.base_url,
            "timeoutSeconds": max(1, min(15, args.ready_timeout_seconds)),
            "retryDelayMs": max(100, args.retry_delay_ms),
            "probeTimeoutMs": 1000,
            "requireInstalled": True,
            "requireAddinLoaded": True,
        }
        if rsaddin:
            verify_arguments["rsaddinPath"] = rsaddin

        ready_payload = None
        deadline = max(1, args.ready_timeout_seconds)
        elapsed = 0
        attempt = 0
        while elapsed < deadline:
            attempt += 1
            slice_seconds = min(1, max(1, deadline - elapsed))
            print(f"[e2e] VerifyAddinMcpReady attempt {attempt} (slice={slice_seconds}s, elapsed={elapsed}s)...", flush=True)
            ready_payload = watchdog_tool_call(
                client,
                WatchdogToolName.VERIFY_ADDIN_MCP_READY,
                {**verify_arguments, "timeoutSeconds": slice_seconds},
                timeout_seconds=max(args.request_timeout_seconds, slice_seconds + 20),
            )

            package_paths = ready_payload.get("mcpPackagePaths") or {}
            runtime_readiness = ready_payload.get("runtimeReadiness") or {}
            mcp_addin_load = runtime_readiness.get("mcpAddinLoad") or {}
            package_installed = package_paths.get("installed") is True
            addin_loaded = mcp_addin_load.get("status") == "loaded"
            runtime_ready = ready_payload.get("ready") is True
            stage_diag = ready_payload.get("stageDiagnostics") or {}
            stage = stage_diag.get("stage") or "unknown"
            waiting_for = (stage_diag.get("progress") or {}).get("waitingFor") or "unknown"
            blocker = stage_diag.get("lastFailureMessage") or stage_diag.get("progress", {}).get("message") or waiting_for
            addin_status = mcp_addin_load.get("status") or "unknown"
            addin_hint = mcp_addin_load.get("hint") or ""
            addin_evidence = mcp_addin_load.get("evidence") or []
            print(
                "[e2e] Package readiness => "
                f"installed={package_installed}; "
                f"addinStatus={addin_status}; "
                f"addinLoaded={addin_loaded}; "
                f"addinHint={addin_hint}; "
                f"addinEvidence={addin_evidence[:1]}; "
                f"healthOk={(runtime_readiness.get('health') or {}).get('ok')}; "
                f"mcpInitialized={(runtime_readiness.get('mcpInitialize') or {}).get('ok')}; "
                f"ready={runtime_ready}; "
                f"stage={stage}; waitingFor={waiting_for}; blocker={blocker}",
                flush=True,
            )

            if package_installed and addin_loaded and runtime_ready:
                break

            elapsed += slice_seconds
            if elapsed < deadline:
                print(f"[e2e] Not ready yet; blocker={blocker}; retrying (elapsed={elapsed}s/{deadline}s).", flush=True)

        if ready_payload is None:
            print("ERROR: VerifyAddinMcpReady produced no response.", flush=True)
            return 1

        package_paths = ready_payload.get("mcpPackagePaths") or {}
        runtime_readiness = ready_payload.get("runtimeReadiness") or {}
        mcp_addin_load = runtime_readiness.get("mcpAddinLoad") or {}
        package_installed = package_paths.get("installed") is True
        addin_loaded = mcp_addin_load.get("status") == "loaded"
        runtime_ready = ready_payload.get("ready") is True

        if not package_installed:
            print("ERROR: Package readiness failed because the RobotStudioMCP package is not installed.", flush=True)
            return 1
        if not addin_loaded:
            print("ERROR: Package readiness failed because the RobotStudioMCP add-in did not reach AddinLoaded.", flush=True)
            return 1
        if not runtime_ready:
            print("ERROR: VerifyAddinMcpReady returned ready=false.", flush=True)
            return 1

        if args.station_path:
            print("[e2e] Waiting for active station after station launch...", flush=True)
            timeout_seconds = max(10.0, float(args.request_timeout_seconds))
            deadline = time.monotonic() + timeout_seconds
            station_active = False
            while time.monotonic() < deadline:
                probe = tool_call(args.base_url + "/mcp", 1, "check_active_station", timeout=5.0)
                station_active = is_station_active(probe)
                if station_active:
                    break
                time.sleep(1.0)
            if not station_active:
                print("ERROR: Station did not become active after start; check_active_station returned inactive.", flush=True)
                return 1

        cmd = [sys.executable, str(script_path), *forwarded_args]
        print(f"[e2e] Running smoke script: {' '.join(cmd)}", flush=True)
        smoke_result = subprocess.run(cmd, timeout=max(30, args.smoke_timeout_seconds))
        return int(smoke_result.returncode)

    except subprocess.TimeoutExpired as ex:
        print(f"ERROR: Timeout: {ex}", flush=True)
        return 1
    except Exception as ex:
        print(f"ERROR: {ex}", flush=True)
        return 1
    finally:
        if client is not None and not args.keep_started_rs:
            try:
                print("[e2e] Calling StopStartedRobotStudioProcess...", flush=True)
                stop_payload = watchdog_tool_call(
                    client,
                    WatchdogToolName.STOP_STARTED_ROBOTSTUDIO_PROCESS,
                    {"gracefulTimeoutSeconds": 1, "forceKillOnly": True},
                    timeout_seconds=max(20, args.request_timeout_seconds),
                )
                print(f"[e2e] StopStartedRobotStudioProcess => {json.dumps(stop_payload)}", flush=True)
            except Exception as ex:
                print(f"WARN: Failed to stop started RobotStudio via watchdog: {ex}", flush=True)
        elif args.keep_started_rs:
            print("[e2e] Keeping started RobotStudio process running (--keep-started-rs).", flush=True)

        if watchdog is not None:
            try:
                watchdog.kill()
            except Exception:
                pass


if __name__ == "__main__":
    raise SystemExit(main())
