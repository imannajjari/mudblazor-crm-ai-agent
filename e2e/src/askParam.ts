export type ExpectedStatus = 'Finished' | 'Stopped' | 'Any';

export interface AskOptions {
  prompt: string;
  allow: RegExp[];
  deny: RegExp[];
  answer?: string;
  maxQuestions: number;
  maxSeconds: number;
  maxCalls: number;
  contains: string[];
  notContains: string[];
  matches: RegExp[];
  minAllowed: number;
  minDenied: number;
  status: ExpectedStatus;
  out: string;
}

const KNOWN_KEYS = new Set([
  'allow', 'deny', 'answer', 'maxQuestions', 'maxSeconds', 'maxCalls',
  'contains', 'notContains', 'notcontains', 'matches', 'minAllowed', 'minDenied', 'status', 'out',
]);

function parsePositiveInt(raw: string, key: string): number {
  if (!/^\d+$/.test(raw.trim())) throw new Error(`Invalid integer for ${key}: "${raw}"`);
  const n = Number(raw.trim());
  if (!Number.isSafeInteger(n) || n <= 0) throw new Error(`${key} must be positive: ${raw}`);
  return n;
}

function parseNonNegativeInt(raw: string, key: string): number {
  if (!/^\d+$/.test(raw.trim())) throw new Error(`Invalid integer for ${key}: "${raw}"`);
  const n = Number(raw.trim());
  if (!Number.isSafeInteger(n) || n < 0) throw new Error(`${key} must be >= 0: ${raw}`);
  return n;
}

function parseRegex(raw: string, key: string): RegExp {
  if (!raw) throw new Error(`Empty regex for ${key}`);
  try {
    return new RegExp(raw, 'i');
  } catch (err) {
    throw new Error(`Invalid regex for ${key} (${raw}): ${(err as Error).message}`);
  }
}

/**
 * Parses `<prompt text>[||key=value;key=value;...]` per automation-spec section 2.
 * Splits on the LAST `||` and throws strictly on unknown keys, invalid regexes, or empty prompts.
 */
export function parseAskParam(rawSpec: string): AskOptions {
  const splitIdx = rawSpec.lastIndexOf('||');
  const prompt = (splitIdx >= 0 ? rawSpec.slice(0, splitIdx) : rawSpec).trim();
  const tail = splitIdx >= 0 ? rawSpec.slice(splitIdx + 2).trim() : '';

  if (!prompt) throw new Error('Prompt cannot be empty.');

  const opts: AskOptions = {
    prompt,
    allow: [],
    deny: [],
    maxQuestions: 5,
    maxSeconds: 240,
    maxCalls: 40,
    contains: [],
    notContains: [],
    matches: [],
    minAllowed: 0,
    minDenied: 0,
    status: 'Finished',
    out: 'Agent',
  };

  if (!tail) return opts;

  for (const part of tail.split(';').map(s => s.trim()).filter(Boolean)) {
    const eq = part.indexOf('=');
    if (eq <= 0) throw new Error(`Malformed option token: "${part}"`);
    const key = part.slice(0, eq).trim();
    const val = part.slice(eq + 1);
    if (!KNOWN_KEYS.has(key)) throw new Error(`Unknown option key: "${key}"`);

    switch (key) {
      case 'allow': opts.allow.push(parseRegex(val, key)); break;
      case 'deny': opts.deny.push(parseRegex(val, key)); break;
      case 'answer': opts.answer = val; break;
      case 'maxQuestions': opts.maxQuestions = parsePositiveInt(val, key); break;
      case 'maxSeconds': opts.maxSeconds = parsePositiveInt(val, key); break;
      case 'maxCalls': opts.maxCalls = parsePositiveInt(val, key); break;
      case 'contains': opts.contains.push(val); break;
      case 'notContains':
      case 'notcontains': opts.notContains.push(val); break;
      case 'matches': opts.matches.push(parseRegex(val, key)); break;
      case 'minAllowed': opts.minAllowed = parseNonNegativeInt(val, key); break;
      case 'minDenied': opts.minDenied = parseNonNegativeInt(val, key); break;
      case 'status':
        if (val !== 'Finished' && val !== 'Stopped' && val !== 'Any') {
          throw new Error(`Invalid status "${val}"`);
        }
        opts.status = val;
        break;
      case 'out': opts.out = val.trim() || 'Agent'; break;
    }
  }
  return opts;
}
