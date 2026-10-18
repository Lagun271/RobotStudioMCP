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
    "robotstudio_assembly_list",
]

EXPECTED_ASSEMBLY_NAMES = {
    "RobotStudioMCP",
}


def _assembly_names(structured):
    assemblies = (structured or {}).get("assemblies") or []
    names = []
    for row in assemblies:
        if not isinstance(row, dict):
            continue
        name = row.get("assemblyName")
        if name:
            names.append(name)
    return names


def main() -> int:
    parser = argparse.ArgumentParser(description="Smoke for RobotStudio managed assembly inspection with optional add-in loading.")
    add_addin_smoke_args(parser)
    args = parser.parse_args()

    ctx = AddinSmokeContext.initialize(
        current_file=pathlib.Path(__file__),
        latest_arg=args.latest,
        default_latest_name="addin-assembly-list-smoke-latest.md",
        source="robotstudio-addin-mcp",
        mode="smoke/assembly-list",
        tools=REQUIRED_TOOLS,
        inputs=f"baseUrl={args.base_url}; requestTimeoutSeconds={args.request_timeout_seconds}",
        base_url=args.base_url,
        request_timeout_seconds=args.request_timeout_seconds,
        live_output=args.live_output,
    )

    try:
        pre = ctx.run_preconditions(client_name="addin-assembly-list-smoke", client_version="0.1.0")
        if not pre["ok"]:
            return ctx.fail(
                "S1",
                f"health={pre['status_code']}; initializeResult={pre['initialize_ok']}; missingTools={pre['missing_tools']}",
                skip_from=2,
                skip_reason="S1 failed; remaining steps skipped.",
            )
        ctx.update("S1", StepStatus.PASS, f"health=200; initialize=ok; tools/list contains required tools ({len(REQUIRED_TOOLS)}).")

        call = ctx.call_tool("robotstudio_assembly_list", {})
        structured = call.get("structuredContent") or {}
        if call.get("isError") and not structured:
            return ctx.fail("S2", "robotstudio_assembly_list returned an error without structured payload.", skip_from=3, skip_reason="S2 failed; remaining steps skipped.")

        contract_checks = [
            ("ok", "ok" in structured),
            ("scope", "scope" in structured),
            ("assemblies", "assemblies" in structured),
            ("hint", "hint" in structured),
        ]
        missing = [label for label, ok in contract_checks if not ok]
        if missing:
            return ctx.fail("S2", f"Missing contract keys: {missing}", skip_from=3, skip_reason="S2 failed; remaining steps skipped.")

        ctx.update("S2", StepStatus.PASS, f"robotstudio_assembly_list returned {len((structured.get('assemblies') or []))} add-in assemblies and included a filter hint.")

        names = set(_assembly_names(structured))
        missing_expected = sorted(EXPECTED_ASSEMBLY_NAMES.difference(names))
        if missing_expected:
            return ctx.fail("S3", f"Missing expected assemblies: {missing_expected}; actual={sorted(names)}", skip_from=4, skip_reason="S3 failed; remaining steps skipped.")

        ctx.update("S3", StepStatus.PASS, f"Default response stayed narrow and included RobotStudioMCP only: {sorted(EXPECTED_ASSEMBLY_NAMES)}.")

        filtered_robotstudio = ctx.call_tool("robotstudio_assembly_list", {"includeGlob": "RobotStudioMCP"})
        filtered_robotstudio_payload = filtered_robotstudio.get("structuredContent") or {}
        filtered_robotstudio_names = set(_assembly_names(filtered_robotstudio_payload))
        if filtered_robotstudio.get("isError") or filtered_robotstudio_payload.get("scope") != "filtered" or filtered_robotstudio_payload.get("includeGlob") != "RobotStudioMCP":
            return ctx.fail("S4", f"Filtered RobotStudioMCP response malformed: {filtered_robotstudio_payload}", skip_from=5, skip_reason="S4 failed; remaining steps skipped.")
        if filtered_robotstudio_names != {"RobotStudioMCP"}:
            return ctx.fail("S4", f"Filtered RobotStudioMCP expected only RobotStudioMCP, got {sorted(filtered_robotstudio_names)}", skip_from=5, skip_reason="S4 failed; remaining steps skipped.")

        filtered_dummy = ctx.call_tool("robotstudio_assembly_list", {"includeGlob": "DummyRobotStudioAddin"})
        filtered_dummy_payload = filtered_dummy.get("structuredContent") or {}
        filtered_dummy_names = set(_assembly_names(filtered_dummy_payload))
        if filtered_dummy.get("isError") or filtered_dummy_payload.get("scope") != "filtered" or filtered_dummy_payload.get("includeGlob") != "DummyRobotStudioAddin":
            return ctx.fail("S4", f"Filtered DummyRobotStudioAddin response malformed: {filtered_dummy_payload}", skip_from=5, skip_reason="S4 failed; remaining steps skipped.")
        if filtered_dummy_names != {"DummyRobotStudioAddin"}:
            return ctx.fail("S4", f"Filtered DummyRobotStudioAddin expected only DummyRobotStudioAddin, got {sorted(filtered_dummy_names)}", skip_from=5, skip_reason="S4 failed; remaining steps skipped.")

        excluded_robotstudio = ctx.call_tool("robotstudio_assembly_list", {"excludeGlob": "RobotStudioMCP"})
        excluded_robotstudio_payload = excluded_robotstudio.get("structuredContent") or {}
        excluded_robotstudio_names = set(_assembly_names(excluded_robotstudio_payload))
        if excluded_robotstudio.get("isError") or excluded_robotstudio_payload.get("scope") != "filtered" or excluded_robotstudio_payload.get("excludeGlob") != "RobotStudioMCP":
            return ctx.fail("S4", f"Exclude RobotStudioMCP response malformed: {excluded_robotstudio_payload}", skip_from=5, skip_reason="S4 failed; remaining steps skipped.")
        if "RobotStudioMCP" in excluded_robotstudio_names:
            return ctx.fail("S4", f"Exclude RobotStudioMCP unexpectedly retained RobotStudioMCP: {sorted(excluded_robotstudio_names)}", skip_from=5, skip_reason="S4 failed; remaining steps skipped.")
        if "DummyRobotStudioAddin" not in excluded_robotstudio_names:
            return ctx.fail("S4", f"Exclude RobotStudioMCP expected DummyRobotStudioAddin to remain, got {sorted(excluded_robotstudio_names)}", skip_from=5, skip_reason="S4 failed; remaining steps skipped.")

        ctx.update("S4", StepStatus.PASS, "Filter coverage ok: includeGlob and excludeGlob narrowed the assembly list as expected.")
        ctx.update("S5", StepStatus.PASS, f"Hint present on default call: {(structured.get('hint') or '').strip()}")
        return ctx.print_log_path()
    except Exception as ex:
        return ctx.fail("S1", f"Precondition error: {ex}", skip_from=2, skip_reason="S1 failed; remaining steps skipped.")


if __name__ == "__main__":
    raise SystemExit(main())
