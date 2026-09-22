// Adopted notice-relative windows; reference follows the representative video's input overlay.
// LMB5 is complete before the clip; its residual attack effects may still be visible.
export const kusarikkuPattern = [
  [3950, 4250, 247 / 60 * 1000], [6700, 7050, 6850], [7700, 8150, 7900],
  [9450, 9850, 9650], [10550, 11000, 10750],
] as const;
export const kusarikkuRecording = {
  firstSourceFrame: 330, endSourceFrameExclusive: 1230, originSourceFrame: 455,
} as const;
