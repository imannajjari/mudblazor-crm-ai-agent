import type { AskOptions } from './askParam.ts';

export type QuestionAction =
  | { kind: 'click'; caption: string; record: 'allow' | 'deny' }
  | { kind: 'type'; text: string }
  | { kind: 'stop'; reason: string };

const ALLOW_CAPTIONS = ['Allow Once', 'Allow This Once', 'اجازه این بار'];
const DENY_CAPTIONS = ["Don't Allow", 'رد کردن'];

const norm = (s: string) => s.replace(/[\u2018\u2019]/g, "'").trim().toLowerCase();

function findMatchingCaption(availableOptions: string[], candidates: string[]): string | undefined {
  const normalizedCandidates = candidates.map(norm);
  return availableOptions.find(opt => normalizedCandidates.includes(norm(opt)));
}

/**
 * Pure decision policy for panel permission/clarifying prompts.
 * Order: delete guard -> limit -> free-text clarifying -> deny (beats allow) -> allow -> default deny.
 */
export function decideQuestionAction(
  questionText: string,
  availableOptions: string[],
  hasOtherInput: boolean,
  questionsAnsweredSoFar: number,
  opts: Pick<AskOptions, 'allow' | 'deny' | 'answer' | 'maxQuestions'>,
  isDeleteTest = false
): QuestionAction {
  if (isDeleteTest && opts.allow.length > 0) {
    throw new Error('Delete tests cannot configure allow= rules.');
  }
  if (questionsAnsweredSoFar >= opts.maxQuestions) {
    return { kind: 'stop', reason: `Reached maxQuestions (${opts.maxQuestions}).` };
  }

  const allowCap = findMatchingCaption(availableOptions, ALLOW_CAPTIONS);
  const denyCap = findMatchingCaption(availableOptions, DENY_CAPTIONS);
  const isPermissionPrompt = Boolean(allowCap || denyCap);

  if (!isPermissionPrompt && hasOtherInput) {
    if (opts.answer !== undefined) {
      return { kind: 'type', text: opts.answer };
    }
    return { kind: 'stop', reason: 'Clarifying question encountered without answer=.' };
  }

  if (opts.deny.some(re => re.test(questionText))) {
    return denyCap
      ? { kind: 'click', caption: denyCap, record: 'deny' }
      : { kind: 'stop', reason: 'Matched deny rule, but Deny caption was absent.' };
  }

  if (opts.allow.some(re => re.test(questionText))) {
    return allowCap
      ? { kind: 'click', caption: allowCap, record: 'allow' }
      : { kind: 'stop', reason: 'Matched allow rule, but Allow Once caption was absent.' };
  }

  return denyCap
    ? { kind: 'click', caption: denyCap, record: 'deny' }
    : { kind: 'stop', reason: 'Default deny triggered, but Deny caption was absent.' };
}
