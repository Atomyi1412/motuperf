# trellis-codex-workflow 技能包摘要

## 来源

- 用户提供压缩包: `E:\download\trellis-codex-workflow.zip`
- 已安装全局技能目录: `D:\Codex\Skills\agents-skills\trellis-codex-workflow`

## 包内容

- `SKILL.md`: Trellis + Superpowers + Grill-Me + GStack Review 的工作流说明。
- `agents/openai.yaml`: 技能展示信息。
- `references/agents-block.md`: 可写入项目 `AGENTS.md` 的受保护工作流块。
- `references/spec-guidelines.md`: `.trellis/spec/` 规范建议。
- `scripts/resolve_trellis_version.py`: 查询 npm dist-tags，给出 Trellis CLI 推荐版本。
- `scripts/apply_trellis_codex_workflow.py`: 写入或检查 `AGENTS.md` 工作流块。
- `scripts/check_trellis_codex_effective.py`: 检查项目是否具备 `.trellis/`、`.codex/hooks`、`.agents/skills/trellis-*` 等有效性文件。

## 当前项目状态

- 已用 `apply_trellis_codex_workflow.py` 创建 `AGENTS.md` 工作流块。
- 已将三个脚本复制到项目 `scripts/`。
- `check_trellis_codex_effective.py --repo .` 当前仍会失败，因为 `.trellis/`、`.codex/hooks` 和本地 `.agents/skills/trellis-*` 未初始化。
- 该压缩包不是 `trellis` CLI 本体，不能直接提供 `trellis init` 命令。

## 对本项目的使用方式

- 使用技能包提供的工作流规则约束开发。
- 使用项目内 `scripts/check_trellis_codex_effective.py --repo .` 记录当前治理缺口。
- 不恢复用户已删除的多组全局 `trellis-*` 技能；本项目继续只依赖 `trellis-codex-workflow` 这个单技能包和 `docs/` 文档门禁。
