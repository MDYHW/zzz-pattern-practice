export type PracticeKey = 'Space' | 'MouseRight';

export interface Cue {
  id: string;
  label: string;
  key: PracticeKey;
  alternateKey?: PracticeKey;
  alternateWindow?: CueWindow;
  start: number;
  end: number;
  reference: number;
}

export interface CueWindow {
  key: PracticeKey;
  start: number;
  end: number;
  reference: number;
}

export interface CueResult {
  cueId: string;
  status: 'pending' | 'success' | 'miss';
  inputTime?: number;
  inputKey?: PracticeKey;
}

export function cueWindows(cue: Cue): readonly CueWindow[] {
  const primary = { key: cue.key, start: cue.start, end: cue.end, reference: cue.reference };
  if (cue.alternateWindow) return [primary, cue.alternateWindow];
  return cue.alternateKey
    ? [primary, { ...primary, key: cue.alternateKey }]
    : [primary];
}

export const cueKeys = (cue: Cue): readonly PracticeKey[] => cueWindows(cue).map(window => window.key);

export function cueEnvelope(cue: Cue): { start: number; end: number } {
  const windows = cueWindows(cue);
  return {
    start: Math.min(...windows.map(window => window.start)),
    end: Math.max(...windows.map(window => window.end)),
  };
}

export interface Attempt {
  results: CueResult[];
  extras: number;
  extraInputs: { time: number; key: PracticeKey; reason: 'wrong' | 'outside' | 'after-success' }[];
  feedback: 'none' | 'outside' | 'wrong' | 'success' | 'miss';
  lastInputTime?: number;
}

export function validateCues(cues: readonly Cue[], duration: number): void {
  if (!Number.isFinite(duration) || duration < 0) {
    throw new Error('Clip duration must be finite and nonnegative.');
  }

  const ids = new Set<string>();
  let previousEnd = -Infinity;

  for (const cue of cues) {
    if (!cue.id.trim() || ids.has(cue.id)) {
      throw new Error('Cue IDs must be nonempty and unique.');
    }
    ids.add(cue.id);

    if (cue.alternateKey !== undefined && cue.alternateWindow !== undefined) {
      throw new Error(`Cue ${cue.id} cannot have both alternate key forms.`);
    }
    const windows = cueWindows(cue);
    if (new Set(windows.map(window => window.key)).size !== windows.length) {
      throw new Error(`Cue ${cue.id} must use a different key for each window.`);
    }
    for (const window of windows) {
      if (window.key !== 'Space' && window.key !== 'MouseRight') {
        throw new Error(`Invalid key for cue ${cue.id}.`);
      }
      if (
        ![window.start, window.end, window.reference].every(
        (time) => Number.isFinite(time) && time >= 0,
        ) ||
        window.start > window.end ||
        window.reference < window.start ||
        window.reference > window.end ||
        window.end > duration
      ) {
        throw new Error(`Invalid timing for cue ${cue.id}.`);
      }
    }
    const envelope = cueEnvelope(cue);
    if (envelope.start <= previousEnd) {
      throw new Error('Cue windows must be ordered and must not overlap.');
    }
    previousEnd = envelope.end;
  }
}

export function createAttempt(cues: readonly Cue[]): Attempt {
  return {
    results: cues.map((cue) => ({ cueId: cue.id, status: 'pending' })),
    extras: 0,
    extraInputs: [],
    feedback: 'none',
  };
}

export function advance(
  cues: readonly Cue[],
  attempt: Attempt,
  time: number,
): Attempt {
  let missed = false;
  const results = attempt.results.map((result, index): CueResult => {
    if (result.status === 'pending' && time > cueEnvelope(cues[index]).end) {
      missed = true;
      return { ...result, status: 'miss' };
    }
    return result;
  });
  return missed ? { ...attempt, results, feedback: 'miss' } : attempt;
}

export function press(
  cues: readonly Cue[],
  attempt: Attempt,
  key: string,
  time: number,
): Attempt {
  if (key !== 'Space' && key !== 'MouseRight') return attempt;
  const current = advance(cues, attempt, time);
  const activeIndex = cues.findIndex(
    (cue, index) =>
      cueWindows(cue).some(window => time >= window.start && time <= window.end) &&
      current.results[index].status === 'pending',
  );

  if (activeIndex >= 0 && cueWindows(cues[activeIndex]).some(
    window => window.key === key && time >= window.start && time <= window.end,
  )) {
    return {
      ...current,
      results: current.results.map((result, index): CueResult =>
        index === activeIndex
          ? { ...result, status: 'success', inputTime: time, inputKey: key }
          : result,
      ),
      feedback: 'success',
      lastInputTime: time,
    };
  }

  return {
    ...current,
    extras: current.extras + 1,
    extraInputs: [...current.extraInputs, {
      time, key, reason: activeIndex >= 0 ? 'wrong'
        : cues.some((cue, index) => cueWindows(cue).some(window => time >= window.start && time <= window.end)
          && current.results[index].status === 'success')
          ? 'after-success' : 'outside',
    }],
    feedback: activeIndex >= 0 ? 'wrong' : 'outside',
    lastInputTime: time,
  };
}

export function finish(cues: readonly Cue[], attempt: Attempt): Attempt {
  // A valid attempt has one result per cue, in the same order.
  const results = attempt.results.map((result, index): CueResult =>
    cues[index] && result.status === 'pending'
      ? { ...result, status: 'miss' }
      : result,
  );
  const missed = results.some((result, index) => result !== attempt.results[index]);
  return missed ? { ...attempt, results, feedback: 'miss' } : attempt;
}
