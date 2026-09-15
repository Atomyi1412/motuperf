#!/usr/bin/env python3
"""Install or check the Trellis/Codex workflow block in AGENTS.md."""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

from check_trellis_codex_effective import check_repo as check_effectiveness


BEGIN = "<!-- BEGIN TRELLIS CODEX WORKFLOW -->"
END = "<!-- END TRELLIS CODEX WORKFLOW -->"
REQUIRED_PHRASES = [
    "Trellis is mandatory",
    "Trellis Effectiveness Self-Check",
    "Superpowers",
    "grill-me",
    "GStack",
    "GStack review",
    "trellis-before-dev",
    "trellis-check",
    "workflow-state",
    "Required Failure Capture",
    "Frontend Quality Gate",
    "Backend Quality Gate",
]


def skill_root() -> Path:
    return Path(__file__).resolve().parents[1]


def load_block() -> str:
    block_path = skill_root() / "references" / "agents-block.md"
    return block_path.read_text(encoding="utf-8").strip() + "\n"


def upsert_block(existing: str, block: str) -> tuple[str, str]:
    if BEGIN in existing and END in existing:
        before, rest = existing.split(BEGIN, 1)
        _, after = rest.split(END, 1)
        return before.rstrip() + "\n\n" + block + after.lstrip(), "updated"
    if existing.strip():
        return existing.rstrip() + "\n\n" + block, "appended"
    return "# AGENTS.md\n\n" + block, "created"


def check_repo(repo: Path) -> list[str]:
    errors: list[str] = []
    agents = repo / "AGENTS.md"
    if not agents.exists():
        errors.append("AGENTS.md is missing")
        return errors
    text = agents.read_text(encoding="utf-8")
    if BEGIN not in text or END not in text:
        errors.append("Trellis/Codex workflow block markers are missing")
    for phrase in REQUIRED_PHRASES:
        if phrase not in text:
            errors.append(f"Required phrase missing: {phrase}")
    if not (repo / ".trellis").exists():
        errors.append(".trellis directory is missing; run trellis init before relying on this workflow")
    effectiveness_errors, _ = check_effectiveness(repo)
    for error in effectiveness_errors:
        if error not in errors:
            errors.append(error)
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description="Install or check Trellis/Superpowers/grill-me Codex AGENTS.md workflow guardrails.")
    parser.add_argument("--repo", default=".", help="Target repository path")
    parser.add_argument("--check", action="store_true", help="Only validate; do not modify AGENTS.md")
    args = parser.parse_args()

    repo = Path(args.repo).resolve()
    if not repo.exists():
        print(f"Repository path does not exist: {repo}", file=sys.stderr)
        return 2

    if args.check:
        errors = check_repo(repo)
        _, warnings = check_effectiveness(repo)
        if errors:
            for error in errors:
                print(f"[FAIL] {error}")
            for warning in warnings:
                print(f"[WARN] {warning}")
            return 1
        for warning in warnings:
            print(f"[WARN] {warning}")
        print("[OK] AGENTS.md contains Trellis/Codex workflow guardrails")
        return 0

    agents = repo / "AGENTS.md"
    existing = agents.read_text(encoding="utf-8") if agents.exists() else ""
    new_text, action = upsert_block(existing, load_block())
    agents.write_text(new_text, encoding="utf-8")
    print(f"[OK] {action} Trellis/Codex workflow block in {agents}")

    trellis = repo / ".trellis"
    if not trellis.exists():
        print("[WARN] .trellis directory not found. Run `trellis init` before expecting task/spec commands to work.")
    print("[NEXT] Run `python scripts/check_trellis_codex_effective.py --repo <repo-path>` to verify Codex hook effectiveness.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
