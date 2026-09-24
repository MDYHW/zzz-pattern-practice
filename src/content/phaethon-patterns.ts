// Adopted practical windows from 2026-09-24 trials, relative to the first notice match.
// Mixed endpoint outcomes remain documented; these are not universal game boundaries.
export const phaethonPattern = [
  [1900, 2200, 2050], [4500, 4900, 4700],
  [6500, 6850, 6650], [7800, 8150, 7950],
] as const;

export const phaethonRecording = {
  firstSourceFrame: 162, endSourceFrameExclusive: 880, originSourceFrame: 267,
  preparationDodgeDownFrame: 295, preparationDodgeReleaseFrame: 301,
} as const;

export const phaethonIntegratedPattern = [
  [2050, 2250, 2150], [5100, 5500, 5300], [7150, 7550, 7350],
  [9350, 9750, 9550], [10950, 11400, 11150],
] as const;

export const phaethonIntegratedRecording = {
  firstSourceFrame: 210, endSourceFrameExclusive: 1020, originSourceFrame: 252,
} as const;
