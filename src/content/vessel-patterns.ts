// Practical windows adopted from 28 trials on 2026-09-24, relative to first notice match.
// Last-hit 9300ms has mixed outcomes; these are not universal game boundaries.
export const vesselPattern = [
  [3150, 3450, 3300], [5200, 5600, 5400], [6100, 6500, 6300],
  [7350, 7650, 7500], [8900, 9300, 9100],
] as const;

export const vesselRecording = {
  firstSourceFrame: 430, endSourceFrameExclusive: 1425, originSourceFrame: 715,
  preparationDodgeDownFrames: [495, 564], preparationLastReleaseFrame: 573,
  preparationForwardStartFrame: 487, preparationForwardEndFrameExclusive: 503,
} as const;
