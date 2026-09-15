# AGENTS.md

<!-- BEGIN TRELLIS CODEX WORKFLOW -->
## Mandatory Trellis, Superpowers, Grill-Me, And GStack Review Workflow

Trellis is mandatory for this repository. Do not treat Trellis as optional, cosmetic, or validation-only.

Trellis is the project memory and task-context source of truth. Superpowers is the engineering discipline layer. Grill-me is the questioning style for high-risk decisions. GStack is the review-panel layer. Use them together as **Trellis-first, Superpowers-selective, grill-me-on-risk, GStack-review-only**.

### Trellis Effectiveness Self-Check

At the start of a Codex session, or before the first non-trivial code edit, verify that Trellis is actually active rather than merely installed.

Run or inspect:

```bash
npm view @mindfoldhq/trellis dist-tags --json
trellis --version
python ./.trellis/scripts/get_context.py --mode packages
python ./.trellis/scripts/task.py current --source
python scripts/check_trellis_codex_effective.py --repo .   # if this script is present
```

If the available npm `rc` dist-tag is newer than `latest`, treat that rc as the newest available Trellis line unless the project explicitly requires stable-only Trellis. Do not assume local Trellis is current.

If the current prompt contains a `<workflow-state>` block, obey it. If no `<workflow-state>` block appears in a Trellis project, treat that as a hook/injection warning and explicitly check the Codex hook setup before proceeding with major work.

Required signs of an effective Codex + Trellis setup:

- `.trellis/` exists with `workflow.md`, `scripts/task.py`, `scripts/get_context.py`, and `spec/`.
- `AGENTS.md` contains this mandatory workflow section.
- `.agents/skills/` contains `trellis-start`, `trellis-before-dev`, `trellis-check`, and `trellis-update-spec`.
- `.codex/hooks.json` wires `UserPromptSubmit` to `.codex/hooks/inject-workflow-state.py`.
- `.codex/config.toml` keeps `AGENTS.md` as a project instruction source.
- The user has enabled Codex hooks at user level and approved project hooks via `/hooks` when the Codex client requires it.
- There is an active Trellis task for implementation work, or the assistant creates/starts one before editing.
- AGENTS.md includes GStack review-panel triggers, and completion of non-trivial tasks includes the applicable GStack reviews described below.
- AGENTS.md requires a workflow component audit before completion: Superpowers, Grill-me, and GStack must each be recorded as triggered with evidence or explicitly not applicable with rationale.

If any required sign is missing, say so clearly and repair it or give the exact user action needed. Do not pretend Trellis is active just because the `.trellis` directory exists.

For every non-trivial implementation, refactor, product architecture change, bug fix, frontend behavior change, backend API change, database migration, or documentation decision that affects future behavior:

1. Check the current Trellis task:
   ```bash
   python ./.trellis/scripts/task.py current --source
   ```
2. If no active task exists, create or start one before editing files.
3. In Codex inline mode, load/use `trellis-before-dev` before writing code.
4. Keep the active task's PRD, notes, implementation log, and check log aligned with the real scope.
5. Add affected implementation and verification files into Trellis context when the local workflow supports it.
6. After implementation, load/use `trellis-check`, run the relevant tests/builds, and record results.
7. Update `.trellis/spec/` when a bug, validation gap, or repeatable convention should persist.
8. Add a workflow component audit entry to the active Trellis task before completion:
   - `Superpowers`: list triggered skill(s) or checklist equivalents, verification evidence, and gaps.
   - `Grill-me`: list triggered high-risk questioning or `not applicable` with concrete reason.
   - `GStack`: list pre/post review gates run, blockers fixed, and any deferred findings with rationale.
9. Do not report completion until the active Trellis task has enough context for a future resumed session to continue without guessing.

Trellis may be skipped only when the user explicitly says to skip Trellis.

### Superpowers, Grill-Me, And GStack Coordination

Superpowers-style discipline is required for high-risk work even when the client does not have the Superpowers plugin installed. Use it as a checklist:

- Brainstorm before committing to a design when multiple solutions are plausible.
- Use systematic debugging before patching bugs: reproduce or reason from evidence, isolate root cause, then fix.
- Write or state a short plan before multi-file or semantic changes.
- Review changes before completion.
- Verify before completion with the relevant build, test, runtime, or data-semantics check.

For every non-trivial task, Superpowers must be explicit rather than implicit. Load/use the applicable Superpowers skill when available (`brainstorming`, `systematic-debugging`, `test-driven-development`, `requesting-code-review`, `verification-before-completion`, or another relevant Superpowers skill). If a dedicated skill is unavailable or genuinely not applicable, record the checklist equivalent and reason in the workflow component audit.

Use grill-me style questioning when work touches:

- Identity binding, user attribution, `keyBy`, operator `uid`, Flink state, or Kafka offsets.
- MySQL metadata, StarRocks DDL, Stream Load headers, partial update, or dynamic columns.
- Migrations, destructive operations, project reset, historical import, data repair, or business semantics.

For every non-trivial task, Grill-me must be explicit rather than silent. If the work touches any high-risk area above, run Grill-me questioning before implementation or before finalizing the decision. If it does not, record `Grill-me: not applicable` plus the concrete reason in the workflow component audit.

Use GStack-style review panels for every non-trivial task. GStack is review-only: it may raise findings, blockers, or follow-up checks, but it must not create/archive Trellis tasks, replace `.trellis/tasks` context, override workflow-state, commit, push, publish, deploy, or release.

Required GStack review gates:

- Before implementation planning: run a product/CEO review for scope, user value, and hidden product risk when the work changes user behavior, policy, pricing, permissions, or operations.
- Before implementation planning: run an engineering review for architecture, ownership boundaries, migration risk, data flow, tests, and rollback on multi-file, backend, API, database, scheduling, auth, billing, or sync work.
- Before implementation planning: run a design/UX review for user-visible frontend, admin-console, copy, navigation, or interaction changes.
- After implementation and before completion: run a code review for bugs, regressions, maintainability, and missing tests.
- After implementation and before completion: run a QA review for real user paths, runtime checks, screenshots/browser checks where relevant, and edge cases not covered by automated tests.
- After implementation and before completion: run a security review for auth, permissions, secrets, injection, SSRF, destructive actions, sensitive logs, dependency or supply-chain risk, and data exposure.
- Before commit, push, PR, release, deployment, Docker export, or desktop handoff: run a ship-readiness review that verifies required tests/builds ran, Trellis context is updated, no unrelated user changes were reverted, and release artifacts match the requested version/target.

For trivial tasks such as answering a question, reading status, or running a harmless command, state that GStack review is not needed. For urgent tiny edits, at minimum run the post-implementation code/QA/security review mentally and mention any blockers.

Workflow component audit template:

```markdown
## Workflow Component Audit

- Superpowers: <skills/checklist used; verification evidence; gaps or not-applicable reason>
- Grill-me: <triggered questions/decisions, or not applicable because ...>
- GStack: <pre/post/ship review gates run; blockers fixed; deferrals with rationale>
```

Coordination rules:

- Trellis records the shared facts: task, scope, context, changed files, decisions, verification, and failures.
- Superpowers governs the work method: brainstorm, debug, plan, review, verify.
- Grill-me exposes missing assumptions and unresolved branches.
- GStack contributes role-specific review findings at the required gates; actionable findings must be fixed or explicitly deferred with rationale before completion.
- If Superpowers, grill-me, or GStack conclusions matter beyond the current turn, write them back to the Trellis task, `.trellis/spec/`, `AGENTS.md`, or another stable project document.
- If methods conflict, project semantics and explicit user direction win.

### Codex Operating Rules

- Prefer inline implementation unless this repository explicitly opts into Trellis sub-agent dispatch.
- Do not spawn Trellis implementation/check sub-agents unless the project workflow says to do so.
- If no Trellis breadcrumb is injected, manually read `trellis-start` or `.agents/skills/trellis-start/SKILL.md` once before routing the task.
- Read code and specs before changing behavior.
- Do not revert user changes unless explicitly requested.
- Keep edits scoped to the user request and the affected modules.
- If the repository is not a git repository, state that commit steps cannot be performed instead of pretending to commit.

### Required Failure Capture

If a bug reaches the user, or if validation missed a user-visible issue:

1. Fix the root cause.
2. Add a concise entry to the active Trellis task explaining root cause, fix, and verification.
3. If reusable, add the lesson to `.trellis/spec/`.

Common mandatory captures:

- A passing build is not enough for frontend work.
- Restart and verify the actual running frontend/backend after code changes.
- Browser-triggered downloads must be tested through the real click path.
- Frontend authenticated calls should prefer same-origin `/api/...` through the dev proxy.
- Nullable database fields must be scanned safely before JSON responses.
- Migrations and seed repair logic must not delete administrator-created data unless explicitly requested.

### Frontend Quality Gate

For user-visible frontend changes:

- Run the frontend build/type-check.
- Restart or verify the dev server serving the page.
- Use browser-level verification for the changed path.
- For layout changes, check representative desktop and mobile viewports.
- Verify page-level horizontal overflow is absent.
- Check top navigation/action overflow on desktop.
- Save screenshots for each affected primary page and inspect them manually.
- Do not mark a UI task done after checking only the exact widget changed.

For management consoles:

- Prefer readable cards or dense but clear tables.
- Do not squeeze status/action columns into vertical text.
- Keep pagination visually attached to the list it controls.
- Do not default-render long AI reports or logs in the first viewport.

### Backend Quality Gate

For backend changes:

- Run Go tests or the relevant backend test command.
- Restart or verify the actual process listening on the expected port.
- Call the exact API endpoint directly before testing the frontend button.
- Preserve audit/history records.
- Avoid destructive migration or seed repair logic unless the user explicitly requested data cleanup.
- Add regression tests for bugs caused by migrations, permissions, or lifecycle state.

### Documentation Discipline

When product or architecture decisions change:

- Update the stable planning documents used by the project.
- Update AI-facing API/reference documents when API routes, auth, permissions, response models, or workflows change.
- Keep filenames stable.
- Prefer explicit lifecycle states and concrete examples.
<!-- END TRELLIS CODEX WORKFLOW -->
