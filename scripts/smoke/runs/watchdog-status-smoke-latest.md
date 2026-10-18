# MCP Smoke Test Run Log

- Run ID: run-20261018-112704-ca38b2
- Timestamp (UTC): 2026-10-18T11:27:04.413964+00:00
- Source: robotstudio-watchdog-mcp
- Mode: smoke/status
- Tester: (agent/harness name)

## Inputs
- Tools under test: robotstudio-watchdog status; get_robotstudio_process_status
- Deterministic input set: status checks on CLI and MCP surfaces

## Step Results
- S1: PASS — Watchdog initialized; MCP tool available: WatchdogToolName.GET_ROBOTSTUDIO_PROCESS_STATUS
- S2: PASS — CLI status returned JSON: {'schemaVersion': 1, 'command': 'status', 'ok': True, 'status': {'isRunning': False, 'count': 0, 'processIds': [], 'trackedProcess': {'processId': None, 'processStartTimeUtc': None, 'recordedAtUtc': None, 'staleStateClearedOnStartup': False, 'startupReconciliationMessage': 'No persisted watchdog state found.'}}, 'trackedProcess': {'processId': None, 'processStartTimeUtc': None, 'recordedAtUtc': None, 'staleStateClearedOnStartup': False, 'startupReconciliationMessage': 'No persisted watchdog state found.'}}
- S3: PASS — CLI status matches MCP status for shared fields.
- S4: SKIP — Status surface is read-only; no state mutation to clean up.
- S5: PASS — No cleanup required for shared status surface smoke.
- S6: SKIP — Status smoke does not exercise lifecycle verbs.
- S7: SKIP — Status smoke does not exercise lifecycle verbs.
- S8: PASS — Read-only status surface completed without cleanup.

## Final Result
- Overall: PASS-WITH-SKIPS
- Notes:
