// Shared helpers for the components that render src/generated/architecture.json
// (ArchitectureDiagram, SystemDiagram, RequestJourney). One definition, so a tier's
// numbers are typeset the same way wherever they appear.

/** Token counts the way the docs write them: 5000000 -> "5M", 20000 -> "20K". */
export function tokens(n: number): string {
  if (n >= 1_000_000) return `${n / 1_000_000}M`;
  if (n >= 1000) return `${n / 1000}K`;
  return String(n);
}

/**
 * A Function's schedule in mid-sentence position. Lower-cases the leading word only —
 * `String.toLowerCase()` on the whole thing turns "Daily at 02:00 UTC" into "utc".
 */
export function scheduleInline(schedule: string): string {
  return schedule.charAt(0).toLowerCase() + schedule.slice(1);
}
