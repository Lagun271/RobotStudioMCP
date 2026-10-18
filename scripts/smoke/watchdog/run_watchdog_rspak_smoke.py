#!/usr/bin/env python3
# ruff: noqa: E402
import pathlib
import sys

SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
SMOKE_ROOT = SCRIPT_DIR.parent
LIB_DIR = SMOKE_ROOT / "_lib"
if str(LIB_DIR) not in sys.path:
    sys.path.insert(0, str(LIB_DIR))

import argparse
from typing import Optional
import zipfile

from path_utils import to_local_path, to_windows_path
from run_log_enums import StepStatus
from run_log_progress_logger import RunLogProgressLogger
from run_log_renderer import compute_overall, write_markdown
from run_log_state import RunLogState
from smoke_log_utils import initialize_artifact_dir, load_template, resolve_repo_root, resolve_skill_root
from watchdog_stdio_common import (
    StdioMcpClient,
    watchdog_exe,
    start_watchdog,
    watchdog_tool_call,
)
from watchdog_tool_name import WatchdogToolName

REQUIRED_TOOLS = [
    "install_rspak_package",
    "rspak_uninstall",
    "verify_addin_installation",
]


def _create_fixture_rspak(temp_root: pathlib.Path, package_dir_name: str) -> pathlib.Path:
    rspak_path = temp_root / f"{package_dir_name}.rspak"
    with zipfile.ZipFile(rspak_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        zf.writestr(f"{package_dir_name}/manifest.xml", "<DistributionPackage><Title>RobotStudioMCP</Title></DistributionPackage>")
        zf.writestr(f"{package_dir_name}/RobotStudio/Add-In/RobotStudioMCP.dll", "fixture")
        zf.writestr(f"{package_dir_name}/RobotStudio/Add-In/RobotStudioMCP.rsaddin", "fixture")
    return rspak_path


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
    parser = argparse.ArgumentParser(description="Watchdog smoke test for rspak install/uninstall tool lifecycle.")
    parser.add_argument("--latest", default=None, help="Path to persistent latest markdown log")
    parser.add_argument("--request-timeout-seconds", type=int, default=60)
    args = parser.parse_args()

    repo_root, _skill_root, latest_log, state, template = _create_run_log(
        pathlib.Path(__file__),
        args.latest,
        "watchdog-rspak-smoke-latest.md",
        source="robotstudio-watchdog-mcp",
        mode="smoke/rspak-lifecycle",
        tools=", ".join(REQUIRED_TOOLS),
        inputs="tempDistributionRoot=<temp>; packageRoot=RobotStudioMCP-9.9.9; overwriteExisting=true",
    )

    watchdog = None
    client = None
    temp_root = None

    install_payload = None
    verify_after_install = None
    uninstall_payload = None
    verify_after_uninstall = None

    try:
        watchdog_exe_path = watchdog_exe(repo_root)
        if not watchdog_exe_path.exists():
            raise RuntimeError(f"Watchdog exe not found: {watchdog_exe_path}. Build it with make watchdog-build first.")
        watchdog = start_watchdog(repo_root)
        client = StdioMcpClient(watchdog)

        init = client.initialize("watchdog-rspak-smoke", "0.1.0", timeout_seconds=args.request_timeout_seconds)
        if "result" not in init:
            state.record_step("S1", StepStatus.FAIL, f"Watchdog initialize failed: {init}")
            for step in ["S2", "S3", "S4", "S5"]:
                state.record_step(step, StepStatus.SKIP, "S1 failed; remaining steps skipped.")
            _finalize_and_print(state, template, latest_log)
            return 1

        state.record_step(
            "S1",
            StepStatus.PASS,
            "Watchdog initialized; tools resolved: "
            f"{[WatchdogToolName.INSTALL_RSPAK_PACKAGE, WatchdogToolName.RSPAK_UNINSTALL, WatchdogToolName.VERIFY_ADDIN_INSTALLATION]}",
        )

        temp_root = initialize_artifact_dir(pathlib.Path(__file__), "watchdog-rspak")
        distribution_root = temp_root / "DistributionPackages"
        distribution_root.mkdir(parents=True, exist_ok=True)

        package_dir_name = "RobotStudioMCP-9.9.9"
        rspak_path = _create_fixture_rspak(temp_root, package_dir_name)
        distribution_root_for_watchdog = to_windows_path(str(distribution_root)) or str(distribution_root)
        missing_root_for_watchdog = to_windows_path(str(distribution_root.parent / "MissingRoot")) or str(distribution_root.parent / "MissingRoot")
        rspak_path_for_watchdog = to_windows_path(str(rspak_path)) or str(rspak_path)

        install_payload = watchdog_tool_call(
            client,
            WatchdogToolName.INSTALL_RSPAK_PACKAGE,
            {
                "rspakPath": rspak_path_for_watchdog,
                "distributionPackagesRoot": distribution_root_for_watchdog,
                "overwriteExisting": True,
                "dryRun": False,
            },
            timeout_seconds=args.request_timeout_seconds,
        )

        if install_payload.get("installed") is not True:
            state.record_step("S2", StepStatus.FAIL, f"InstallRspak failed: {install_payload}")
            for step in ["S3", "S4", "S5"]:
                state.record_step(step, StepStatus.SKIP, "S2 failed; remaining steps skipped.")
            _finalize_and_print(state, template, latest_log)
            return 1

        state.record_step("S2", StepStatus.PASS, f"Installed fixture rspak into {distribution_root}")

        required_install_keys = ["installed", "packageRootDirName", "targetPackageDir", "rspakPath"]
        missing_install = [k for k in required_install_keys if k not in install_payload]
        if missing_install:
            state.record_step("S3", StepStatus.FAIL, f"Missing install response keys: {missing_install}")
        else:
            state.record_step("S3", StepStatus.PASS, "Install response contract keys present.")

        verify_after_install = watchdog_tool_call(
            client,
            WatchdogToolName.VERIFY_ADDIN_INSTALLATION,
            {
                "packageId": "RobotStudioMCP",
                "distributionPackagesRoot": distribution_root_for_watchdog,
                "distributionPackagesRoot2": missing_root_for_watchdog,
            },
            timeout_seconds=args.request_timeout_seconds,
        )

        target_dir_raw = install_payload.get("targetPackageDir", "")
        target_dir_local = pathlib.Path(to_local_path(target_dir_raw) or target_dir_raw)
        if verify_after_install.get("installed") is True and target_dir_local.exists():
            state.record_step("S4", StepStatus.PASS, "Installation verified via filesystem and verify_addin_installation installed=true.")
        else:
            state.record_step("S4", StepStatus.FAIL, f"Install verification failed: verify={verify_after_install}; targetExists={target_dir_local.exists()}")

        uninstall_payload = watchdog_tool_call(
            client,
            WatchdogToolName.RSPAK_UNINSTALL,
            {
                "packageDirectoryName": install_payload.get("packageRootDirName", "RobotStudioMCP-9.9.9"),
                "distributionPackagesRoot": distribution_root_for_watchdog,
                "dryRun": False,
            },
            timeout_seconds=args.request_timeout_seconds,
        )

        verify_after_uninstall = watchdog_tool_call(
            client,
            WatchdogToolName.VERIFY_ADDIN_INSTALLATION,
            {
                "packageId": "RobotStudioMCP",
                "distributionPackagesRoot": distribution_root_for_watchdog,
                "distributionPackagesRoot2": missing_root_for_watchdog,
            },
            timeout_seconds=args.request_timeout_seconds,
        )

        second_uninstall = watchdog_tool_call(
            client,
            WatchdogToolName.RSPAK_UNINSTALL,
            {
                "packageDirectoryName": install_payload.get("packageRootDirName", "RobotStudioMCP-9.9.9"),
                "distributionPackagesRoot": distribution_root_for_watchdog,
                "dryRun": False,
            },
            timeout_seconds=args.request_timeout_seconds,
        )

        ok_uninstall = uninstall_payload.get("uninstalled") is True
        ok_verify_gone = verify_after_uninstall.get("installed") is False
        ok_idempotent = second_uninstall.get("nothingToRemove") is True

        if ok_uninstall and ok_verify_gone and ok_idempotent:
            retained = [str(path) for path in sorted(temp_root.rglob("*")) if path.is_file()]
            state.record_step(
                "S5",
                StepStatus.PASS,
                f"RspakUninstall removed package; verify_addin_installation now false; repeated uninstall returns nothingToRemove; retained artifacts under {temp_root}: {retained}",
            )
        else:
            state.record_step(
                "S5",
                StepStatus.FAIL,
                f"Cleanup/idempotency failed: uninstall={uninstall_payload}; verifyGone={verify_after_uninstall}; second={second_uninstall}",
            )

        _finalize_and_print(state, template, latest_log)
        return 0

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
