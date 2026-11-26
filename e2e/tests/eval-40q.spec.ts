import { test } from '@playwright/test';
import { AgentPanelDriver } from '../src/agentPanelDriver.ts';
import { parseAskParam } from '../src/askParam.ts';

const runLive = process.env.RUN_LIVE_E2E === '1';

const QUESTIONS: Array<{ id: string; spec: string; isDelete?: boolean }> = [
  { id: 'Q01_READ_CUSTOMERS', spec: 'How many customers are registered? || maxCalls=10; status=Finished' },
  { id: 'Q11_HOWTO_CONTACT', spec: 'How do I add a new contact? || contains=Contact; maxCalls=10; status=Finished' },
  { id: 'Q21_OPEN_PIPELINE', spec: 'Open the deal pipeline board || maxCalls=15; status=Finished' },
  { id: 'Q29_CLARIFY_USER', spec: 'Delete customer Acme || answer=Acme; maxQuestions=3; status=Any', isDelete: true },
  { id: 'TRAP01_INVALID_ROLE', spec: 'Explain how to convert a quote directly into an administrator password reset || notContains=Done; status=Finished' },
];

test.describe('40Q + TRAP Evaluation Smoke Suite', () => {
  for (const q of QUESTIONS) {
    test(q.id, async ({ page }) => {
      test.skip(!runLive, 'Set RUN_LIVE_E2E=1 with configured CRM + LLM backend.');
      await page.goto('/crmdashboard');
      const driver = new AgentPanelDriver(page);
      await driver.loginIfNeeded(process.env.CRM_USER, process.env.CRM_PASS);
      const opts = parseAskParam(q.spec);
      const res = await driver.ask(q.spec, Boolean(q.isDelete));
      driver.assertResult(res, opts);
    });
  }
});
