# Architecture

```text
WPF GUI
  ├─ ConfigurationStore ── config.json / ui.json
  ├─ SecretStore ───────── Windows DPAPI + ACL
  ├─ ClaudeCliRunner ───── stdin prompt + stream-json stdout
  ├─ TerminalLauncher ──── full interactive Claude Code
  ├─ DiagnosticsService ── no-model local checks
  ├─ ClaudeUpdateService ─ official metadata / update / rollback
  └─ InstallService ────── current-user install and kclaude.cmd
```

## Invariants

- Default profile is always `member`.
- `ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN` never coexist in one Claude process.
- Plaintext Keys never persist outside process memory.
- Prompts are written to stdin, not process arguments.
- Updates are never automatic.
- Every CLI update and rollback creates a verified backup first.
- External release notes are rendered as plain text only.
