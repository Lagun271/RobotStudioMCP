from __future__ import annotations

import pathlib
from typing import Iterable

from run_log_enums import StepStatus
from run_log_state import RunLogState


def _render_step_results(state: RunLogState) -> str:
    if not state.steps:
        return ""
    lines = []
    for key in sorted(state.steps.keys()):
        step = state.steps[key]
        lines.append(f"- {step.step}: {step.status.value} — {step.evidence}")
    return "\n".join(lines)


def _render_notes(notes: Iterable[str]) -> str:
    return "\n".join(f"- {note}" for note in notes)


def compute_overall(state: RunLogState) -> str:
    if not state.steps:
        return "IN-PROGRESS"
    statuses = [item.status for item in state.steps.values()]
    if any(status == StepStatus.FAIL for status in statuses):
        return StepStatus.FAIL.value
    if all(status == StepStatus.PASS for status in statuses):
        return StepStatus.PASS.value
    if all(status in {StepStatus.PASS, StepStatus.SKIP} for status in statuses):
        return "PASS-WITH-SKIPS"
    return "IN-PROGRESS"


def render_markdown(state: RunLogState, template: str, overall: str = "IN-PROGRESS") -> str:
    return (
        template.replace("{{RUN_ID}}", state.run_id)
        .replace("{{TIMESTAMP_UTC}}", state.timestamp_utc)
        .replace("{{SOURCE_SLUG}}", state.source)
        .replace("{{MODE}}", state.mode)
        .replace("{{TOOLS}}", state.tools)
        .replace("{{INPUT_SET}}", state.inputs)
        .replace("{{STEP_RESULTS}}", _render_step_results(state))
        .replace("{{FINAL_RESULT}}", f"- Overall: {overall}\n- Notes:\n{_render_notes(state.notes)}")
    )


def write_markdown(state: RunLogState, template: str, path: pathlib.Path, overall: str = "IN-PROGRESS") -> None:
    path.write_text(render_markdown(state, template, overall=overall), encoding="utf-8")
