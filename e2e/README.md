# MudBlazor CRM Agent Playwright E2E Suite (`AGENT_ASK`)

Implements the `automation-spec.md` architecture with TypeScript domain helpers and `data-testid`-driven `AgentPanelDriver`.

## Offline Domain Unit Tests (Zero external dependencies)

Node v24+ runs the TypeScript domain tests natively (`AskParam`, `PermissionPolicy`, `StatsParser`):

```bash
npm run test:unit
```

## Live Playwright Execution

1. Install Playwright (`npm install && npx playwright install chromium`).
2. Start `MudBlazorCrmApp` on `http://localhost:5025` with a configured LLM API key.
3. Run:

```bash
RUN_LIVE_E2E=1 npm run test:e2e
```
