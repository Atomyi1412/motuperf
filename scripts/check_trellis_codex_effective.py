#!/usr/bin/env python3
"""Check whether Trellis is likely to be effective in Codex, not just installed."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys


BEGIN = "<!-- BEGIN TRELLIS CODEX WORKFLOW -->"
END = "<!-- END TRELLIS CODEX WORKFLOW -->"

REQUIRED_AGENTS_PHRASES = [
    "Trellis is mandatory",
    "Trellis Effectiveness Self-Check",
    "Superpowers",
    "grill-me",
    "GStack",
    "GStack review",
    "Workflow Component Audit",
    "trellis-before-dev",
    "trellis-check",
    "workflow-state",
]

REQUIRED_WORKFLOW_PHRASES = [
    "GStack review gates",
    "GStack pre-implementation review gates",
    "workflow component audit",
    "ship-readiness review",
]

REQUIRED_SKILLS = [
    "trellis-start",
    "trellis-before-dev",
    "trellis-check",
    "trellis-update-spec",
]


def codex_home() -> Path:
    return Path.home() / ".codex"


def rel(path: Path, repo: Path) -> str:
    try:
        return path.relative_to(repo).as_posix()
    except ValueError:
        return str(path)


def add_file_check(errors: list[str], repo: Path, path: str, label: str | None = None) -> Path:
    target = repo / path
    if not target.exists():
        errors.append(f"Missing {label or path}: {path}")
    return target


def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        return path.read_text(encoding="utf-8-sig")


def check_hooks_json(repo: Path, errors: list[str]) -> None:
    hooks_json = repo / ".codex" / "hooks.json"
    if not hooks_json.exists():
        return

    try:
        data = json.loads(read_text(hooks_json))
    except json.JSONDecodeError as exc:
        errors.append(f".codex/hooks.json is not valid JSON: {exc}")
        return

    user_prompt_hooks = data.get("hooks", {}).get("UserPromptSubmit")
    serialized = json.dumps(user_prompt_hooks, ensure_ascii=False)
    if not user_prompt_hooks:
        errors.append(".codex/hooks.json does not define a UserPromptSubmit hook")
    if "inject-workflow-state.py" not in serialized:
        errors.append(".codex/hooks.json does not call inject-workflow-state.py")


def check_config_toml(repo: Path, warnings: list[str]) -> None:
    config = repo / ".codex" / "config.toml"
    if not config.exists():
        return

    text = read_text(config)
    if "AGENTS.md" not in text:
        warnings.append(".codex/config.toml does not mention AGENTS.md as an instruction source")
    if "hooks = true" not in text and "codex_hooks" not in text:
        warnings.append(
            "Project config cannot enable Codex hooks; document the user-level `[features].hooks = true` requirement"
        )


def check_repo(repo: Path) -> tuple[list[str], list[str]]:
    errors: list[str] = []
    warnings: list[str] = []

    add_file_check(errors, repo, ".trellis/workflow.md")
    add_file_check(errors, repo, ".trellis/scripts/task.py")
    add_file_check(errors, repo, ".trellis/scripts/get_context.py")
    add_file_check(errors, repo, ".trellis/spec", ".trellis/spec directory")

    agents = add_file_check(errors, repo, "AGENTS.md")
    if agents.exists():
        text = read_text(agents)
        if BEGIN not in text or END not in text:
            errors.append("AGENTS.md is missing guarded Trellis/Codex workflow block markers")
        for phrase in REQUIRED_AGENTS_PHRASES:
            if phrase not in text:
                errors.append(f"AGENTS.md is missing required phrase: {phrase}")

    workflow = repo / ".trellis" / "workflow.md"
    if workflow.exists():
        workflow_text = read_text(workflow)
        for phrase in REQUIRED_WORKFLOW_PHRASES:
            if phrase not in workflow_text:
                errors.append(f".trellis/workflow.md is missing required GStack phrase: {phrase}")

    for skill in REQUIRED_SKILLS:
        add_file_check(errors, repo, f".agents/skills/{skill}/SKILL.md")

    superpowers_skill = codex_home() / "skills" / "superpowers-workflow"
    if not (superpowers_skill / "SKILL.md").exists():
        warnings.append(
            "Local Codex skill superpowers-workflow is not installed; install it or use the official Codex App Superpowers plugin"
        )
    elif not (superpowers_skill / "scripts" / "check_superpowers_upstream.py").exists():
        warnings.append("superpowers-workflow is installed but missing check_superpowers_upstream.py")

    add_file_check(errors, repo, ".codex/config.toml")
    add_file_check(errors, repo, ".codex/hooks.json")
    add_file_check(errors, repo, ".codex/hooks/inject-workflow-state.py")
    check_hooks_json(repo, errors)
    check_config_toml(repo, warnings)

    runtime = repo / ".trellis" / ".runtime"
    if not runtime.exists():
        warnings.append("No .trellis/.runtime directory found; active task state may not exist yet")

    warnings.append(
        "Cannot verify from repository files whether the current Codex user enabled hooks in user-level config"
    )
    warnings.append(
        "Cannot verify from repository files whether the current Codex client approved project hooks with /hooks"
    )
    warnings.append(
        "Cannot verify from repository files whether the current client has the official Superpowers plugin installed; local superpowers-workflow can still provide portable Superpowers-style discipline"
    )

    return errors, warnings


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Check whether a repository has the Trellis files, Codex hooks, and AGENTS guardrails needed for effective Codex use."
    )
    parser.add_argument("--repo", default=".", help="Target repository path")
    parser.add_argument(
        "--strict-warnings",
        action="store_true",
        help="Treat warnings as failures. Useful in CI when user-level hook state is documented elsewhere.",
    )
    args = parser.parse_args()

    repo = Path(args.repo).resolve()
    if not repo.exists():
        print(f"[FAIL] Repository path does not exist: {repo}", file=sys.stderr)
        return 2

    errors, warnings = check_repo(repo)
    for error in errors:
        print(f"[FAIL] {error}")
    for warning in warnings:
        print(f"[WARN] {warning}")

    if errors or (args.strict_warnings and warnings):
        print("[SUMMARY] Trellis is not fully effective for Codex yet.")
        return 1

    print("[OK] Trellis/Codex project files look effective.")
    print("[NOTE] Still confirm the live prompt shows <workflow-state> or run /hooks in Codex when hooks are newly installed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
