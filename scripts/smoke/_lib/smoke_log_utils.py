#!/usr/bin/env python3
from __future__ import annotations

import pathlib
import shutil
from typing import Optional, Tuple

from run_log_progress_logger import RunLogProgressLogger
from run_log_renderer import write_markdown
from run_log_state import RunLogState


def resolve_repo_root(current_file: pathlib.Path) -> pathlib.Path:
    current = current_file.resolve()
    for candidate in [current, *current.parents]:
        if (candidate / ".git").exists() or (candidate / "RobotStudioMCP.slnx").exists():
            return candidate
    raise RuntimeError(f"Could not resolve repo root from path: {current_file}")


def resolve_skill_root(current_file: pathlib.Path) -> pathlib.Path:
    return pathlib.Path(__file__).resolve().parent.parent


def load_template(skill_root: pathlib.Path) -> str:
    template_path = pathlib.Path(__file__).resolve().parent / "run-log-template.md"
    return template_path.read_text(encoding="utf-8")


def initialize_run_log(
    current_file: pathlib.Path,
    latest_arg: Optional[str],
    default_latest_name: str,
    *,
    source: str,
    mode: str,
    tools: str,
    inputs: str,
) -> Tuple[pathlib.Path, pathlib.Path, pathlib.Path, str, RunLogState]:
    repo_root = resolve_repo_root(current_file)
    skill_root = resolve_skill_root(current_file)
    latest_log = pathlib.Path(latest_arg) if latest_arg else skill_root / "runs" / default_latest_name
    latest_log.parent.mkdir(parents=True, exist_ok=True)

    template = load_template(skill_root)
    state = RunLogState(path=latest_log, source=source, mode=mode, tools=tools, inputs=inputs)
    state.subscribe(RunLogProgressLogger().handle)
    state.start()
    write_markdown(state, template, latest_log)
    return repo_root, skill_root, latest_log, template, state


def initialize_artifact_dir(current_file: pathlib.Path, artifact_slug: str) -> pathlib.Path:
    repo_root = resolve_repo_root(current_file)
    artifact_dir = repo_root / "artifacts" / "smoke" / artifact_slug
    if artifact_dir.exists():
        shutil.rmtree(artifact_dir, ignore_errors=True)
    artifact_dir.mkdir(parents=True, exist_ok=True)
    return artifact_dir
