// Adopted notice-relative service windows; references follow the selected video's input overlay.
// One source-frame translation aligns the recording without changing the adopted windows.
// Entry and hit 3 include a user-requested 25ms extension at each edge (2026-09-22).
// This service adjustment awaits gameplay validation; measured evidence/profiles stay unchanged.
export const mirageArcherUnitPattern = [
  [4125, 4375, (898 - 643) / 60 * 1000],
  [6250, 6550, (1026 - 643) / 60 * 1000],
  [7950, 8350, (1131 - 643) / 60 * 1000],
  [9375, 9725, (1215 - 643) / 60 * 1000],
  [11650, 12050, (1353 - 643) / 60 * 1000],
] as const;

export const mirageArcherUnitRecording = {
  firstSourceFrame: 523, endSourceFrameExclusive: 1500, originSourceFrame: 643,
} as const;

// User adopted 12050ms as the late endpoint; its failed-entry condition is
// preserved separately in the research evidence.
export const mirageLastRmbWindowMs = [11850, 12050] as const;
export const mirageRmbRecording = {
  firstSourceFrame: 648, endSourceFrameExclusive: 1730, originSourceFrame: 768,
  inputSourceFrames: [1024, 1152, 1257, 1341, 1485],
} as const;
