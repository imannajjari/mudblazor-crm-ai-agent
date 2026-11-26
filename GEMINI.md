# Project Rules & Code Review Standards — MudBlazorCrmApp

This file is the single source of truth for AI assistants working in `MudBlazorCrmApp`. Full review protocol lives in [`.agents/skills/code-review-protocol/SKILL.md`](.agents/skills/code-review-protocol/SKILL.md).

## 1. Mandatory Between-Commit Code Review Gate
- Before **every** `git commit` or `git push`, invoke an **independent Code Review subagent** on `git diff` using [`.agents/skills/code-review-protocol/SKILL.md`](.agents/skills/code-review-protocol/SKILL.md).
- Do not commit if any **Critical** or **High** finding is unresolved.
- During a read-only review turn, **never** modify or commit code.

## 2. Severity & Confidence Standards
- Classify every finding into: **Critical -> High -> Medium -> Low -> Info**.
- Tag every finding with confidence: **Confirmed** (run command/test cited), **From reading**, or **Unconfirmed** (check named).
- Measure before flagging: grep the repo before claiming a convention violation.

## 3. Engineering Pillars
- **SOLID & Sizing:** Classes `<= 250 LOC`, methods `<= 50 LOC`, one top-level type per new file, C# 12 primary constructors for DI, `decimal` for money/cost.
- **No Silent Exceptions:** Never use empty `catch { }`; log with context or return explicit error results.
- **Config Integrity:** Every `AgentOptions` key must have a safe default and an active consumer.
- **Security & AI Agent Guards:** Enforce safety in C#/JS code (not prompt text only), redact attachments (`RedactAttachmentBlocks`) and sensitive inputs, validate loopback responses, and maintain exact frontend/backend contract parity (`AgentToolSchemas`, `UiToolHandlers`, `AgentServerTools`).
- **Comments:** Default to none; short one-line "Why" comments only for non-obvious invariants.

## 4. Build & Test Verification (Windows `.NET 10`)
```powershell
$env:MSBuildEnableWorkloadResolver="false"
dotnet test "c:\Projects\AI Agent\MudBlazorCrmApp\MudBlazorCrmApp.Tests\MudBlazorCrmApp.Tests.csproj" --no-restore
```
Note: If `MudBlazorCrmApp.exe` is running (e.g. `http://localhost:5025`), rebuilds hit `MSB3027` file lock; stop the running dev process or build cleanly first.
