export interface CronScheduleText {
  everyMinutes: (interval: number) => string;
  hourly: string;
  everyHours: (interval: number) => string;
  daily: string;
  dailyAt: (localTime: string) => string;
  weekdays: (localTime: string) => string;
  weekly: (days: string, localTime?: string | null) => string;
}

export interface CronDayNames {
  Monday: string;
  Tuesday: string;
  Wednesday: string;
  Thursday: string;
  Friday: string;
  Saturday: string;
  Sunday: string;
}

/**
 * Convert a 5-field cron expression to a human-readable description.
 * Only handles the common dom=* month=* subset; returns the raw expression
 * for unsupported patterns.
 */
export function parseCronHuman(
  expr: string,
  schedule: CronScheduleText,
  dayNames: CronDayNames,
): string {
  if (!expr) return "";
  const parts = expr.trim().split(/\s+/);
  if (parts.length !== 5) return expr;
  const [min, hour, dom, month, dow] = parts;

  if (dom !== "*" || month !== "*") return expr;

  // Every N minutes: */N * * * *
  if (min.startsWith("*/") && hour === "*" && dow === "*") {
    const n = parseInt(min.slice(2), 10);
    if (Number.isFinite(n) && n > 0) return schedule.everyMinutes(n);
  }

  // Every N hours: (0|M) */N * * *
  if (hour.startsWith("*/") && dow === "*") {
    const n = parseInt(hour.slice(2), 10);
    if (Number.isFinite(n) && n > 0)
      return n === 1 ? schedule.hourly : schedule.everyHours(n);
  }

  // Specific time of day
  const h = /^\d+$/.test(hour) ? parseInt(hour, 10) : NaN;
  const m = /^\d+$/.test(min) ? parseInt(min, 10) : NaN;
  if (!Number.isFinite(h) || !Number.isFinite(m)) return expr;

  const timeStr = `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;

  if (dow === "*") return schedule.dailyAt(timeStr);
  if (dow === "1-5") return schedule.weekdays(timeStr);

  const dayKeyMap: Record<string, keyof CronDayNames> = {
    "0": "Sunday", "1": "Monday", "2": "Tuesday", "3": "Wednesday",
    "4": "Thursday", "5": "Friday", "6": "Saturday", "7": "Sunday",
  };
  const dayLabel = dow
    .split(",")
    .map((d) => { const k = dayKeyMap[d.trim()]; return k ? dayNames[k] : d; })
    .join("/");
  return schedule.weekly(dayLabel, timeStr);
}
