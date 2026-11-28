import { expect, type Page } from '@playwright/test';
import { parseAskParam, type AskOptions } from './askParam.ts';
import { decideQuestionAction } from './permissionPolicy.ts';
import { parseStats, type StatsInfo } from './statsParser.ts';

export type RunStatus = 'Finished' | 'Stopped' | 'Error' | 'TimedOut';

export interface AskResult {
  status: RunStatus;
  answer: string;
  statsLine: string;
  stats: StatsInfo;
  asked: string[];
  allowed: string[];
  denied: string[];
  modeAfter?: string;
  error?: string;
}

const norm = (s: string) => s.replace(/[\u2018\u2019]/g, "'").trim().toLowerCase();

export class AgentPanelDriver {
  private readonly page: Page;
  constructor(page: Page) {
    this.page = page;
  }

  async loginIfNeeded(email?: string, password?: string): Promise<void> {
    if (!email || !password) return;
    if (await this.page.getByTestId('agent-open-button').isVisible().catch(() => false)) return;
    // Wait for the WASM app to boot before deciding (cold start can exceed 30s; without this
    // wait the login form is not yet rendered and the whole login is silently skipped).
    const pwd = this.page.locator('input[type="password"]');
    try {
      await pwd.waitFor({ state: 'visible', timeout: 20_000 });
    } catch {
      if (await this.page.getByTestId('agent-open-button').isVisible().catch(() => false)) return; // already authenticated
      // Unauthenticated [Authorize] pages render a "Not authorized" view without a login redirect;
      // go to /login explicitly.
      await this.page.goto('/login');
      await pwd.waitFor({ state: 'visible', timeout: 60_000 });
    }
    // MudBlazor renders the email MudTextField as an unnamed type=text input (no name attr).
    await this.page.locator('input:not([type="password"]):not([type="checkbox"]):not([type="hidden"])').first().fill(email);
    await pwd.first().fill(password);
    await this.page.locator('button[type="submit"]').first().click();
    // Post-login layout (NavMenu + agent button) can take a while on cold WASM; wait, do not assume networkidle.
    await this.page.getByTestId('agent-open-button').waitFor({ state: 'visible', timeout: 60_000 }).catch(() => {});
  }

  async openCleanManualChat(): Promise<void> {
    const panel = this.page.getByTestId('agent-panel');
    if (!(await panel.isVisible())) {
      const openBtn = this.page.getByTestId('agent-open-button');
      await expect(openBtn).toBeVisible({ timeout: 10_000 });
      await openBtn.click();
      await expect(panel).toBeVisible();
    }
    await this.page.getByTestId('agent-new-chat').click();
    const mode = this.page.getByTestId('agent-mode');
    await expect(mode).toHaveValue('Manual');
    await this.page.getByTestId('agent-input').focus();
  }

  async ask(spec: string, isDeleteTest = false): Promise<AskResult> {
    const opts = parseAskParam(spec);
    if (isDeleteTest && opts.allow.length > 0) {
      throw new Error('Delete tests cannot configure allow= rules.');
    }

    await this.openCleanManualChat();
    await this.page.getByTestId('agent-input').fill(opts.prompt);
    await this.page.getByTestId('agent-run').click();

    const asked: string[] = [];
    const allowed: string[] = [];
    const denied: string[] = [];
    let errorPollCount = 0;
    const deadline = Date.now() + opts.maxSeconds * 1000;

    while (Date.now() < deadline) {
      const qBox = this.page.getByTestId('agent-question');
      if (await qBox.isVisible()) {
        const qText = (await this.page.getByTestId('agent-question-text').innerText()).trim();
        const optLocators = this.page.getByTestId('agent-question-option');
        const captions = await optLocators.allInnerTexts();
        const hasOther = await this.page.getByTestId('agent-question-other-input').isVisible();
        const decision = decideQuestionAction(qText, captions, hasOther, asked.length, opts, isDeleteTest);

        asked.push(qText);
        if (decision.kind === 'stop') {
          await this.clickStopIfVisible();
          return await this.collectResult('Stopped', asked, allowed, denied, decision.reason);
        }
        if (decision.kind === 'type') {
          await this.page.getByTestId('agent-question-other-input').fill(decision.text);
          await this.page.getByTestId('agent-question-other-send').click();
        } else {
          await this.clickExactOption(optLocators, decision.caption);
          if (decision.record === 'allow') allowed.push(qText);
          else denied.push(qText);
        }
        await expect(qBox).toBeHidden({ timeout: 10_000 });
        continue;
      }

      const errEl = this.page.getByTestId('agent-error');
      if (await errEl.isVisible()) {
        if (++errorPollCount >= 8) {
          return await this.collectResult('Error', asked, allowed, denied, await errEl.innerText());
        }
      } else {
        errorPollCount = 0;
        const statsEl = this.page.getByTestId('agent-stats');
        const workingEl = this.page.getByTestId('agent-working');
        if ((await statsEl.isVisible()) && !(await workingEl.isVisible())) {
          return await this.collectResult('Finished', asked, allowed, denied);
        }
      }
      await this.page.waitForTimeout(400);
    }

    await this.clickStopIfVisible();
    return await this.collectResult('TimedOut', asked, allowed, denied, 'Timed out waiting for completion.');
  }

  assertResult(res: AskResult, opts: AskOptions): void {
    if (opts.status !== 'Any') expect(res.status).toBe(opts.status);
    if (res.status === 'Finished') {
      expect(res.error).toBeFalsy();
      expect(res.stats.present).toBe(true);
    }
    if (res.modeAfter !== undefined) expect(res.modeAfter).toBe('Manual');
    if (opts.maxCalls && res.stats.calls !== undefined) expect(res.stats.calls).toBeLessThanOrEqual(opts.maxCalls);
    expect(res.allowed.length).toBeGreaterThanOrEqual(opts.minAllowed);
    expect(res.denied.length).toBeGreaterThanOrEqual(opts.minDenied);
    for (const c of opts.contains) expect(res.answer.toLowerCase()).toContain(c.toLowerCase());
    for (const nc of opts.notContains) expect(res.answer.toLowerCase()).not.toContain(nc.toLowerCase());
    for (const re of opts.matches) expect(res.answer).toMatch(re);
  }

  private async clickExactOption(optLocators: ReturnType<Page['getByTestId']>, targetCaption: string): Promise<void> {
    const count = await optLocators.count();
    const target = norm(targetCaption);
    for (let i = 0; i < count; i++) {
      const cap = await optLocators.nth(i).innerText();
      if (norm(cap) === target) {
        await optLocators.nth(i).click();
        return;
      }
    }
    throw new Error(`Expected option caption "${targetCaption}" was not found.`);
  }

  private async clickStopIfVisible(): Promise<void> {
    const stopBtn = this.page.getByTestId('agent-stop');
    if (await stopBtn.isVisible()) await stopBtn.click();
  }

  private async collectResult(
    status: RunStatus,
    asked: string[],
    allowed: string[],
    denied: string[],
    error?: string
  ): Promise<AskResult> {
    const results = this.page.getByTestId('agent-result');
    const answer = (await results.count()) > 0 ? await results.last().innerText() : '';
    const statsEl = this.page.getByTestId('agent-stats');
    const statsLine = (await statsEl.count()) > 0 ? await statsEl.innerText() : '';
    const modeEl = this.page.getByTestId('agent-mode');
    const modeAfter = (await modeEl.count()) > 0 ? await modeEl.inputValue() : undefined;
    return { status, answer, statsLine, stats: parseStats(statsLine), asked, allowed, denied, modeAfter, error };
  }
}
