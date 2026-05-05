export interface RelativeTimeText {
  justNow: string;
  minutesAgo: (n: number) => string;
  hoursAgo: (n: number) => string;
  daysAgo: (n: number) => string;
}

/**
 * Format an ISO date string as a relative time description.
 * Returns relative text for past dates (e.g. "3 分钟前", "2h ago").
 * Returns empty string if isoString is null/undefined/invalid.
 */
export function formatRelativeTime(
  isoString: string | null | undefined,
  text: RelativeTimeText,
  nowMs?: number,
): string {
  if (!isoString) return "";
  const date = new Date(isoString);
  if (isNaN(date.getTime())) return "";
  const diff = (nowMs ?? Date.now()) - date.getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 1) return text.justNow;
  if (mins < 60) return text.minutesAgo(mins);
  const hours = Math.floor(mins / 60);
  if (hours < 24) return text.hoursAgo(hours);
  const days = Math.floor(hours / 24);
  return text.daysAgo(days);
}
