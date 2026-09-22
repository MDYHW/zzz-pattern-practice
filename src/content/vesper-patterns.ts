import type { Cue } from '../practice/judge';

// Milliseconds after the experiment's first matching notice capture.
// These are conservative service windows, not exact game boundaries.
// Tuple reference follows the representative recording; it can differ from the normalized experiment baseline.
export const vesperPatterns = {
  execute01: [[4100, 4300, 4200], [6250, 6600, 6250], [7500, 7950, 7533.333333333333],
    [9350, 9750, 9500], [10650, 11150, 10700], [12950, 13450, 13250]],
  execute02: [[3050, 3300, 3150], [4200, 4700, 4450], [5750, 6200, 5950],
    [7400, 7850, 7600], [9150, 9600, 9400]],
} as const;

// Execute_02 support endpoints: approved OFF review on 2026-09-19.
// Entry uses the selected key: RMB3050~3300, Space2900~3300.
// Execute_01 hits3/4 late endpoints extended by user approval after recorded follow-ups (2026-09-20).
// Execute_01 OFF review closed on 2026-09-20; both entry keys adopt 4100~4300.

export const vesperSpaceEntries = {
  execute01: vesperPatterns.execute01[0],
  execute02: [2900, 3300, 3150],
} as const;

// A single translation per recording preserves every measured width and gap.
// EX01:23:01:39 manual success, notice threshold at source frame1020; no input-fitted offset.
// Alignment evidence: docs/evidence/service-media-20260920.json.
export const vesperRecordings = {
  execute01: { firstSourceFrame: 1008, endSourceFrameExclusive: 1980, originSourceFrame: 1020 },
  execute02: { firstSourceFrame: 222, endSourceFrameExclusive: 960, originSourceFrame: 233 },
} as const;

export function cuesForRecording(pattern: readonly (readonly [number, number, number])[], offset: number): Cue[] {
  return pattern.map(([start, end, reference], index) => ({
    id: index === 0 ? 'entry' : `attack-${index}`,
    label: index === 0 ? '진입' : `${index}타`,
    key: index === 0 ? 'MouseRight' : 'Space',
    start: start / 1000 + offset, end: end / 1000 + offset, reference: reference / 1000 + offset,
  }));
}
