# AI UI Agent Operational Runbook & Blocker Log

## 1. Environment & CLI Workarounds (Windows)
- **Build & Test (`.NET 10` workload resolver workaround):**
  ```powershell
  $env:MSBuildEnableWorkloadResolver="false"
  dotnet test "c:\Projects\AI Agent\MudBlazorCrmApp\MudBlazorCrmApp.Tests\MudBlazorCrmApp.Tests.csproj" --no-restore
  ```
- **Run Server (`http://localhost:5025`):**
  ```powershell
  $env:MSBuildEnableWorkloadResolver="false"
  $env:ASPNETCORE_ENVIRONMENT="Development"
  dotnet run --no-build --project "c:\Projects\AI Agent\MudBlazorCrmApp\MudBlazorCrmApp\MudBlazorCrmApp.csproj" --urls "http://localhost:5025"
  ```
- **Git Push (`agent-repo main`):**
  ```powershell
  $env:GIT_TERMINAL_PROMPT="0"
  git -c credential.helper= -c credential.helper=manager push agent-repo main
  ```

## 2. Mandatory Gates & Resilience Rules
- **Code Review Gate:** Before every `git commit` or `git push`, a separate Code Review agent must review `git diff` against `.agents/skills/code-review-protocol/SKILL.md` and `GEMINI.md` (5-tier severity, confidence labels, SOLID principles, `<= 250 LOC` classes, `<= 50 LOC` methods, no silent `catch { }`, no hardcoding).
- **Critical Blocker Policy:** If a task hits an unresolvable critical blocker, document it below and continue to the next task without pushing broken changes.
- **Browser Test Hang Policy:** If a browser E2E run stalls for >1–2 minutes, inspect a screenshot; if unresponsive, skip browser E2E and rely on `dotnet test` + live API/unit verification.
- **Read-Only Blueprint:** Never modify `c:\Projects\AI Agent\ai-agent-blueprint` unless explicitly instructed by the user.
- **AFK Shutdown Policy:** Only run `shutdown /s /f /t 0` when the user is AFK and all tasks are completed or documented. (User returned at `02:33+03:30`; do not auto-shutdown without explicit re-confirmation.)

## 3. Verified Baseline & Known Issues
- **Verified (`63/63 Passed`):** `AgentServiceTests` (14) + `AgentExtendedTests` (8, covering M2/M4/M5) + `AgentEvaluation40QTests` (40Q CRM Eval Set + `PermanentSafetySet_S01ToS10_Passes100Percent`). All milestones M0–M6 passed Code Review (`APPROVED`, 0 Critical, 0 High).
- **UI & Driver Enhancements (`3017029`–`27d0642`):** Floating resizable ChatGPT-style `AgentPanel`, pointer-capture drag, `z-index` layering above `MudDialog`/`MudSelect`, live `navRoutes` in snapshot, and `Visual Spotlight` (`spotlightElement`).
- **Verified Live E2E (`adminuser@example.com`):** `read_data` (`dashboard_kpis`), Markdown bold rendering, `go_to_page("Customers")` -> `/Customer`, permission prompt (`اجازه این بار`), saving customer `315` (`تستی ایجنت تست`), and `/api/agentchat` persistence.
- **File Lock Note:** If `MudBlazorCrmApp.exe` is running in the background (e.g., on `http://localhost:5025`), `dotnet test` will fail with `MSB3027` on `apphost.exe`; stop the running server or run `dotnet test --no-build` if already built.
- **Known Playwright Quirk:** Automated Playwright clicks on MudBlazor `MudIconButton` (`agent-history-btn` / `agent-toggle-btn`) occasionally fail to trigger Blazor `OnClick` even when human clicks succeed immediately.
