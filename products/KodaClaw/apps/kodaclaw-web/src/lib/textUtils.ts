/** Strip XML/tool-call blocks (e.g. <function_calls>…</function_calls>) from LLM output. */
export function stripXml(value: string | null | undefined, maxLength = 200): string {
  if (!value) return "";
  const cleaned = value
    .replace(/<[^>]+>[\s\S]*?<\/[^>]+>/g, "")
    .replace(/<[^>]+\/>/g, "")
    .replace(/\s+/g, " ")
    .trim();
  if (cleaned.length <= maxLength) return cleaned;
  return `${cleaned.slice(0, maxLength - 3)}...`;
}
