import test from 'node:test';
import assert from 'node:assert/strict';
import { parseAskParam } from './askParam.ts';
import { decideQuestionAction } from './permissionPolicy.ts';
import { parseStats } from './statsParser.ts';

test('parseAskParam splits on last ||, supports lowercase notcontains, and rejects bad keys', () => {
  const parsed = parseAskParam('Find total || contains=12; notcontains=error; maxCalls=15; status=Stopped');
  assert.equal(parsed.prompt, 'Find total');
  assert.deepEqual(parsed.contains, ['12']);
  assert.deepEqual(parsed.notContains, ['error']);
  assert.equal(parsed.maxCalls, 15);
  assert.equal(parsed.status, 'Stopped');

  assert.throws(() => parseAskParam('   || contains=ok'), /empty/i);
  assert.throws(() => parseAskParam('Run || unknownKey=1'), /Unknown option/);
  assert.throws(() => parseAskParam('Run || allow=[invalid('), /Invalid regex/);
  assert.throws(() => parseAskParam('Run || maxCalls=0'), /positive/);
});

test('decideQuestionAction matches AppText English and Persian permission captions', () => {
  const opts = parseAskParam('Do work || allow=Save; deny=Delete; answer=Acme; maxQuestions=3');

  // Deny beats allow in English and Persian
  const d1 = decideQuestionAction('Allow Save and Delete?', ['Allow Once', "Don't Allow"], false, 0, opts);
  assert.deepEqual(d1, { kind: 'click', caption: "Don't Allow", record: 'deny' });

  const d2 = decideQuestionAction('Save?', ['اجازه این بار', 'رد کردن'], false, 0, parseAskParam('Save || allow=Save'));
  assert.deepEqual(d2, { kind: 'click', caption: 'اجازه این بار', record: 'allow' });

  const d3 = decideQuestionAction('Export?', ['اجازه این بار', 'رد کردن'], false, 0, opts);
  assert.deepEqual(d3, { kind: 'click', caption: 'رد کردن', record: 'deny' });

  // Typographic apostrophe on Allow Once
  const d4 = decideQuestionAction('Allow Save?', ['Allow Once', 'Don\u2019t Allow'], false, 0, opts);
  assert.deepEqual(d4, { kind: 'click', caption: 'Allow Once', record: 'allow' });

  // Clarifying question uses answer= or stops
  const d5 = decideQuestionAction('Which customer?', [], true, 0, opts);
  assert.deepEqual(d5, { kind: 'type', text: 'Acme' });
  const d6 = decideQuestionAction('Which customer?', [], true, 0, parseAskParam('Do work'));
  assert.equal(d6.kind, 'stop');

  // Delete test rejects allow=
  assert.throws(() => decideQuestionAction('Allow?', ['Allow Once'], false, 0, opts, true), /Delete tests/);
});

test('parseStats parses AppText colon format (fa/en) and trailing-label format', () => {
  const enAppText = parseStats('Calls: 6 | In: 1,240 | Out: 85 | Cost: $0.0042');
  assert.equal(enAppText.present, true);
  assert.equal(enAppText.calls, 6);
  assert.equal(enAppText.tokensIn, 1240);
  assert.equal(enAppText.tokensOut, 85);
  assert.equal(enAppText.costUsd, 0.0042);

  const faAppText = parseStats('فراخوانی: 3 | ورودی: 400 | خروجی: 50 | هزینه: $0.0015');
  assert.equal(faAppText.present, true);
  assert.equal(faAppText.calls, 3);
  assert.equal(faAppText.tokensIn, 400);
  assert.equal(faAppText.tokensOut, 50);
  assert.equal(faAppText.costUsd, 0.0015);

  const legacy = parseStats('6 LLM calls — tokens used: 1,240 in, 320 cached, 85 out — $0.0042');
  assert.equal(legacy.present, true);
  assert.equal(legacy.calls, 6);
  assert.equal(legacy.tokensCached, 320);
});
