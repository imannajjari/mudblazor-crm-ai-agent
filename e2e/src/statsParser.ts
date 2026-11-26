export interface StatsInfo {
  calls?: number;
  tokensIn?: number;
  tokensCached?: number;
  tokensOut?: number;
  costUsd?: number;
  present: boolean;
}

const parseNum = (s: string) => Number(s.replace(/[,\s]/g, ''));

/**
 * Extracts call count, token counts, and optional USD cost from English/Persian panel stats lines.
 */
export function parseStats(line: string): StatsInfo {
  const calls = /(?:(?:calls?|فراخوانی)\s*[:：]\s*(\d[\d,]*)|(\d[\d,]*)\s+(?:LLM\s+|model\s+)?(?:calls?\b|فراخوانی))/i.exec(line);
  const tin = /(?:(?:input|in|ورودی)\s*[:：]\s*(\d[\d,]*)|(\d[\d,]*)\s*(?:(?:input|in)\b|ورودی))/i.exec(line);
  const cached = /(?:(?:cached|کش)\s*[:：]\s*(\d[\d,]*)|(\d[\d,]*)\s*(?:cached\b|کش))/i.exec(line);
  const tout = /(?:(?:output|out|خروجی)\s*[:：]\s*(\d[\d,]*)|(\d[\d,]*)\s*(?:(?:output|out)\b|خروجی))/i.exec(line);
  const cost = /\$\s*(\d+(?:\.\d+)?)/.exec(line);
  return {
    calls: calls ? parseNum(calls[1] ?? calls[2]) : undefined,
    tokensIn: tin ? parseNum(tin[1] ?? tin[2]) : undefined,
    tokensCached: cached ? parseNum(cached[1] ?? cached[2]) : undefined,
    tokensOut: tout ? parseNum(tout[1] ?? tout[2]) : undefined,
    costUsd: cost ? Number(cost[1]) : undefined,
    present: /(?:tokens|calls|توکن|فراخوانی)/i.test(line),
  };
}
