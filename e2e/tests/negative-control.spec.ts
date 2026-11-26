import { test, expect } from '@playwright/test';
import { AgentPanelDriver } from '../src/agentPanelDriver.ts';
import { parseAskParam } from '../src/askParam.ts';

const runLive = process.env.RUN_LIVE_E2E === '1';

test.describe('Negative control (deliberately failing assertion when executed live)', () => {
  test('ZZ_AGENT_ASK_NEGATIVE_CONTROL @negative-control', async ({ page }) => {
    test.skip(!runLive, 'Set RUN_LIVE_E2E=1 with a running CRM & LLM server to execute negative control.');
    await page.goto('/');
    const driver = new AgentPanelDriver(page);
    await driver.loginIfNeeded(process.env.CRM_USER, process.env.CRM_PASS);
    const spec = 'How many customers exist in the CRM? || contains=__IMPOSSIBLE_NEGATIVE_CONTROL_MARKER__';
    const res = await driver.ask(spec);
    expect(res.status).toBe('Finished');
    expect(() => driver.assertResult(res, parseAskParam(spec))).toThrow(/__IMPOSSIBLE_NEGATIVE_CONTROL_MARKER__/);
  });
});
