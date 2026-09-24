import { type PracticeKey, validateCues } from '../practice/judge';
import type { PracticeContent } from '../practice/session';
import { girtablulluPattern, girtablulluRecording } from './girtablullu-patterns';
import { kusarikkuPattern, kusarikkuRecording } from './kusarikku-patterns';
import { mirageArcherUnitPattern, mirageArcherUnitRecording, mirageLastRmbWindowMs, mirageRmbRecording } from './mirage-archer-unit-patterns';
import { phaethonPattern, phaethonRecording, phaethonIntegratedPattern, phaethonIntegratedRecording } from './phaethon-patterns';
import { cuesForRecording, vesperPatterns, vesperRecordings, vesperSpaceEntries } from './vesper-patterns';
import { vesselPattern, vesselRecording } from './vessel-patterns';
import { graymanePattern, graymaneRecording } from './graymane-patterns';

// Cover the full lower input-icon row in the 1280×720 recordings.
// Start above the small Q key circle (below the large skill icon) and reach the frame edges.
const gameInputMasks = [
  { x: 928 / 1280 * 100, y: 580 / 720 * 100, width: 352 / 1280 * 100, height: 140 / 720 * 100 },
] as const;

function vesperContent(key: keyof typeof vesperPatterns, number: string): PracticeContent {
  const recording = vesperRecordings[key];
  const content: PracticeContent = {
    bossId: 'vesper', bossLabel: '베스퍼',
    id: `vesper-execute-${number}`, title: `Execute_${number}`,
    video: `media/vesper-execute-${number}-lufs26.mp4`, poster: `media/vesper-execute-${number}.jpg`,
    duration: (recording.endSourceFrameExclusive - recording.firstSourceFrame) / 60,
    cues: cuesForRecording(vesperPatterns[key], (recording.originSourceFrame - recording.firstSourceFrame) / 60),
    gameInputMasks,
    recordedInputMasks: [{ x: 0, y: 560 / 720 * 100, width: 315 / 1280 * 100, height: 160 / 720 * 100 }],
  };
  const offset = (recording.originSourceFrame - recording.firstSourceFrame) / 60;
  const [start, end, reference] = vesperSpaceEntries[key];
  const entry = content.cues[0];
  content.entryOptions = [
    { key: 'MouseRight', start: entry.start, end: entry.end, reference: entry.reference },
    { key: 'Space', start: start / 1000 + offset, end: end / 1000 + offset, reference: reference / 1000 + offset },
  ];
  validateCues(content.cues, content.duration);
  return content;
}

export const vesperExecute01 = vesperContent('execute01', '01');
export const vesperExecute02 = vesperContent('execute02', '02');
const girOffset = (girtablulluRecording.originSourceFrame - girtablulluRecording.firstSourceFrame) / 60;
const girCues = cuesForRecording(girtablulluPattern, girOffset);
export const girtablulluExecute01: PracticeContent = {
  id: 'girtablullu-stagnant-execute-01', bossId: 'gir-reborn', bossLabel: '기르타블리르·퇴행 변종', title: 'Execute_01',
  video: 'media/girtablullu-stagnant-execute-01-lufs26.mp4', poster: 'media/girtablullu-stagnant-execute-01.jpg',
  duration: (girtablulluRecording.endSourceFrameExclusive - girtablulluRecording.firstSourceFrame) / 60,
  cues: girCues,
  gameInputMasks,
  entryOptions: [{ key: 'MouseRight', start: girCues[0].start, end: girCues[0].end, reference: girCues[0].reference }],
  recordedInputMasks: [{ x: 0, y: 560 / 720 * 100, width: 315 / 1280 * 100, height: 160 / 720 * 100 }],
};
validateCues(girtablulluExecute01.cues, girtablulluExecute01.duration);
const kusaClipTime = (frame: number) => (frame - kusarikkuRecording.firstSourceFrame) / 60;
const kusaCues = cuesForRecording(kusarikkuPattern, kusaClipTime(kusarikkuRecording.originSourceFrame));
export const kusarikkuExecute01: PracticeContent = {
  id: 'kusarikku-execute-01', bossId: 'larval', bossLabel: '쿠사리쿠', title: 'Execute_01',
  video: 'media/kusarikku-execute-01-lufs26.mp4', poster: 'media/kusarikku-execute-01.jpg',
  duration: kusaClipTime(kusarikkuRecording.endSourceFrameExclusive),
  cues: kusaCues,
  gameInputMasks,
  entryOptions: [{ key: 'MouseRight', start: kusaCues[0].start, end: kusaCues[0].end, reference: kusaCues[0].reference }],
  preparation: {
    end: kusaClipTime(623),
    dodges: [
      { id: 'prepare-rmb-1', label: '준비 회피1', time: kusaClipTime(531) },
      { id: 'prepare-rmb-2', label: '준비 회피2', time: kusaClipTime(614) },
    ],
    movements: [
      { key: 'D', start: kusaClipTime(509), end: kusaClipTime(570) },
      { key: 'W', start: kusaClipTime(593), end: kusaClipTime(679) },
    ],
  },
  recordedInputMasks: [{ x: 0, y: 560 / 720 * 100, width: 315 / 1280 * 100, height: 160 / 720 * 100 }],
};
validateCues(kusarikkuExecute01.cues, kusarikkuExecute01.duration);
const mirageClipTime = (frame: number) => (frame - mirageArcherUnitRecording.firstSourceFrame) / 60;
const mirageCues = cuesForRecording(mirageArcherUnitPattern,
  mirageClipTime(mirageArcherUnitRecording.originSourceFrame));
export const mirageArcherUnitAttack09: PracticeContent = {
  id: 'mirage-archer-unit-attack-09', bossId: 'mirage-archer-unit', bossLabel: '환영의 화살 유닛', title: 'Attack_09',
  recordedFinalKey: 'Space',
  video: 'media/mirage-archer-unit-attack-09-lufs26.mp4', poster: 'media/mirage-archer-unit-attack-09.jpg',
  duration: mirageClipTime(mirageArcherUnitRecording.endSourceFrameExclusive),
  cues: mirageCues,
  gameInputMasks,
  entryOptions: [{ key: 'MouseRight', start: mirageCues[0].start, end: mirageCues[0].end, reference: mirageCues[0].reference }],
  recordedInputMasks: [{ x: 0, y: 560 / 720 * 100, width: 315 / 1280 * 100, height: 160 / 720 * 100 }],
};
validateCues(mirageArcherUnitAttack09.cues, mirageArcherUnitAttack09.duration);

const phaethonClipTime = (frame: number) => (frame - phaethonRecording.firstSourceFrame) / 60;
const phaethonCues = cuesForRecording(phaethonPattern, phaethonClipTime(phaethonRecording.originSourceFrame));
export const phaethonExecute01: PracticeContent = {
  id: 'phaethon-execute-01', bossId: 'phaethon', bossLabel: '태양의 잔불·파에톤', title: 'Execute_01',
  video: 'media/phaethon-execute-01-lufs26.mp4', poster: 'media/phaethon-execute-01.jpg',
  duration: phaethonClipTime(phaethonRecording.endSourceFrameExclusive),
  cues: phaethonCues,
  gameInputMasks,
  entryOptions: [{ key: 'MouseRight', start: phaethonCues[0].start, end: phaethonCues[0].end, reference: phaethonCues[0].reference }],
  preparation: {
    end: phaethonClipTime(phaethonRecording.preparationDodgeReleaseFrame),
    dodges: [{ id: 'prepare-rmb-1', label: '준비 회피', time: phaethonClipTime(phaethonRecording.preparationDodgeDownFrame) }],
    movements: [],
  },
  recordedInputMasks: [{ x: 0, y: 560 / 720 * 100, width: 315 / 1280 * 100, height: 160 / 720 * 100 }],
};
validateCues(phaethonExecute01.cues, phaethonExecute01.duration);

const phaethonIntegratedClipTime = (frame: number) => (frame - phaethonIntegratedRecording.firstSourceFrame) / 60;
const phaethonIntegratedCues = cuesForRecording(phaethonIntegratedPattern,
  phaethonIntegratedClipTime(phaethonIntegratedRecording.originSourceFrame));
export const phaethonIntegratedExecute02: PracticeContent = {
  id: 'phaethon-integrated-execute-02', bossId: 'phaethon-integrated', bossLabel: '융합·태양의 잔불', title: 'Execute_02',
  video: 'media/phaethon-integrated-execute-02-lufs26.mp4', poster: 'media/phaethon-integrated-execute-02.jpg',
  duration: phaethonIntegratedClipTime(phaethonIntegratedRecording.endSourceFrameExclusive),
  cues: phaethonIntegratedCues,
  gameInputMasks,
  entryOptions: [{ key: 'MouseRight', start: phaethonIntegratedCues[0].start, end: phaethonIntegratedCues[0].end, reference: phaethonIntegratedCues[0].reference }],
  recordedInputMasks: [{ x: 0, y: 560 / 720 * 100, width: 315 / 1280 * 100, height: 160 / 720 * 100 }],
};
validateCues(phaethonIntegratedExecute02.cues, phaethonIntegratedExecute02.duration);

const vesselClipTime = (frame: number) => (frame - vesselRecording.firstSourceFrame) / 60;
const vesselCues = cuesForRecording(vesselPattern, vesselClipTime(vesselRecording.originSourceFrame));
export const vesselExecute01: PracticeContent = {
  id: 'vessel-execute-01', bossId: 'vessel', bossLabel: '태초의 악몽·창조주', title: 'Execute_01',
  video: 'media/vessel-execute-01-lufs26.mp4', poster: 'media/vessel-execute-01.jpg',
  duration: vesselClipTime(vesselRecording.endSourceFrameExclusive),
  cues: vesselCues,
  gameInputMasks,
  entryOptions: [{ key: 'MouseRight', start: vesselCues[0].start, end: vesselCues[0].end, reference: vesselCues[0].reference }],
  preparation: {
    end: vesselClipTime(vesselRecording.preparationLastReleaseFrame),
    dodges: vesselRecording.preparationDodgeDownFrames.map((frame, index) => ({
      id: `prepare-rmb-${index + 1}`, label: `준비 회피${index + 1}`, time: vesselClipTime(frame),
    })),
    movements: [{ key: 'W', start: vesselClipTime(vesselRecording.preparationForwardStartFrame),
      end: vesselClipTime(vesselRecording.preparationForwardEndFrameExclusive) }],
  },
  recordedInputMasks: [{ x: 0, y: 560 / 720 * 100, width: 315 / 1280 * 100, height: 160 / 720 * 100 }],
};
validateCues(vesselExecute01.cues, vesselExecute01.duration);

const graymaneClipTime = (frame: number) => (frame - graymaneRecording.firstSourceFrame) / 60;
const graymaneCues = cuesForRecording(graymanePattern, graymaneClipTime(graymaneRecording.originSourceFrame));
export const graymaneExecute01: PracticeContent = {
  id: 'graymane-execute-01', bossId: 'graymane', bossLabel: '피의 청소부', title: 'Attack_Excute_Pre / Attack_Excute_Combo',
  video: 'media/graymane-execute-01-lufs26.mp4', poster: 'media/graymane-execute-01.jpg',
  duration: graymaneClipTime(graymaneRecording.endSourceFrameExclusive),
  cues: graymaneCues,
  gameInputMasks,
  entryOptions: [{ key: 'MouseRight', start: graymaneCues[0].start, end: graymaneCues[0].end, reference: graymaneCues[0].reference }],
  preparation: {
    end: graymaneClipTime(graymaneRecording.preparationDodgeReleaseFrame),
    dodges: [{ id: 'prepare-rmb-1', label: '준비 회피', time: graymaneClipTime(graymaneRecording.preparationDodgeDownFrame) }],
    movements: graymaneRecording.forwardMovements.map(([start, end]) => ({
      key: 'W', start: graymaneClipTime(start), end: graymaneClipTime(end),
    })),
  },
  recordedInputMasks: [{ x: 0, y: 560 / 720 * 100, width: 315 / 1280 * 100, height: 160 / 720 * 100 }],
};
validateCues(graymaneExecute01.cues, graymaneExecute01.duration);

/** B uses the RMB example throughout; its video is not a simulation of user input. */
export function withMirageLastResponse(layout: 'selected' | 'overlap', key: PracticeKey): PracticeContent {
  const base = mirageArcherUnitAttack09;
  if (layout === 'selected' && key === 'Space') return base;
  const recording = mirageRmbRecording;
  const clipTime = (frame: number) => (frame - recording.firstSourceFrame) / 60;
  const offset = clipTime(recording.originSourceFrame);
  const [start, end] = mirageLastRmbWindowMs;
  const cues = base.cues.map((cue, index) => {
    const reference = clipTime(recording.inputSourceFrames[index]);
    if (index !== 4) return { ...cue, reference };
    return { ...cue, key: 'MouseRight' as const, start: start / 1000 + offset, end: end / 1000 + offset, reference,
      ...(layout === 'overlap' ? { alternateWindow: { key: 'Space' as const, start: cue.start, end: cue.end, reference: cue.reference } } : {}) };
  });
  const resolved: PracticeContent = { ...base, cues, recordedFinalKey: 'MouseRight',
    video: 'media/mirage-archer-unit-attack-09-rmb-lufs26.mp4', poster: 'media/mirage-archer-unit-attack-09-rmb.jpg',
    duration: clipTime(recording.endSourceFrameExclusive) };
  validateCues(resolved.cues, resolved.duration);
  return resolved;
}
export const skills = [vesperExecute01, vesperExecute02, girtablulluExecute01, kusarikkuExecute01,
  mirageArcherUnitAttack09, phaethonExecute01, phaethonIntegratedExecute02, vesselExecute01, graymaneExecute01];

/** The returned cue list belongs to a single selected practice route. */
export function withEntryKey(content: PracticeContent, key: PracticeKey): PracticeContent {
  const option = content.entryOptions?.find(item => item.key === key);
  if (!option) throw new Error(`Entry key ${key} is unavailable for ${content.id}.`);
  const resolved = { ...content, cues: content.cues.map((cue, index) => index === 0 ? { ...cue, ...option } : cue) };
  validateCues(resolved.cues, resolved.duration);
  return resolved;
}
