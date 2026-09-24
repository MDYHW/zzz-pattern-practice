// Notice-relative service windows approved on 2026-09-24.
// Space3 late, Space4 early and Space5 late include a web-only 25ms allowance;
// those extra edges are not measured game-success boundaries.
export const graymanePattern = [
  [2550, 2850, 2700], [4050, 4500, 4250], [5550, 6000, 5750],
  [6850, 7275, 7050], [8225, 8650, 8450], [10200, 10625, 10400],
] as const;

export const graymaneRecording = {
  firstSourceFrame: 0, endSourceFrameExclusive: 1135, originSourceFrame: 322,
  preparationDodgeDownFrame: 231, preparationDodgeReleaseFrame: 238,
  forwardMovements: [[100, 130], [213, 265]],
} as const;
