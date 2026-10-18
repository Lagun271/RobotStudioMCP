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

from run_log_enums import StepStatus
from smoke_common import AddinSmokeContext, add_addin_smoke_args

REQUIRED_TOOLS = [
    "get_robotstudio_status",
    "check_active_station",
]


def main() -> int:
    parser = argparse.ArgumentParser(description="Smoke for the check_active_station MCP tool payload.")
    add_addin_smoke_args(parser)
    args = parser.parse_args()

    ctx = AddinSmokeContext.initialize(
        current_file=pathlib.Path(__file__),
        latest_arg=args.latest,
        default_latest_name="addin-station-state-smoke-latest.md",
        source="robotstudio-addin-mcp",
        mode="smoke/station-state",
        tools=REQUIRED_TOOLS,
        inputs=f"baseUrl={args.base_url}; requestTimeoutSeconds={args.request_timeout_seconds}",
        base_url=args.base_url,
        request_timeout_seconds=args.request_timeout_seconds,
        live_output=args.live_output,
    )

    try:
        pre = ctx.run_preconditions(client_name="addin-station-state-smoke", client_version="0.1.0")
        if not pre["ok"]:
            return ctx.fail(
                "S1",
                f"health={pre['status_code']}; initializeResult={pre['initialize_ok']}; missingTools={pre['missing_tools']}",
                skip_from=2,
                skip_reason="S1 failed; remaining steps skipped.",
            )
        ctx.update("S1", StepStatus.PASS, f"health=200; initialize=ok; tools/list contains required tools ({len(REQUIRED_TOOLS)}).")

        call = ctx.call_tool("check_active_station", {})
        structured = call.get("structuredContent") or {}
        if call.get("isError") and not structured:
            return ctx.fail("S2", "check_active_station returned an error without structured payload.", skip_from=3, skip_reason="S2 failed; remaining steps skipped.")

        required_keys = ["stationState", "ok", "isActive"]
        missing = [key for key in required_keys if key not in structured]
        if missing:
            return ctx.fail("S2", f"Missing contract keys: {missing}", skip_from=3, skip_reason="S2 failed; remaining steps skipped.")

        station_state = structured.get("stationState") or {}
        state_keys = ["hasActiveStation", "controllerCount", "controllers", "allControllersReady"]
        missing_state_keys = [key for key in state_keys if key not in station_state]
        if missing_state_keys:
            return ctx.fail("S2", f"Missing stationState keys: {missing_state_keys}", skip_from=3, skip_reason="S2 failed; remaining steps skipped.")

        ctx.update(
            "S2",
            StepStatus.PASS,
            f"check_active_station contract ok; hasActiveStation={station_state.get('hasActiveStation')}; controllerCount={station_state.get('controllerCount')}; allControllersReady={station_state.get('allControllersReady')}.",
        )
        ctx.update("S3", StepStatus.PASS, "check_active_station returned the shared stationState payload.")
        ctx.update("S4", StepStatus.SKIP, "No side effects expected.")
        ctx.update("S5", StepStatus.PASS, "No cleanup required.")
        return ctx.print_log_path()
    except Exception as ex:
        return ctx.fail("S1", f"Precondition error: {ex}", skip_from=2, skip_reason="S1 failed; remaining steps skipped.")


if __name__ == "__main__":
    raise SystemExit(main())
