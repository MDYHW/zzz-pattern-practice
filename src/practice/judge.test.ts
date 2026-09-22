import { describe, expect, test } from 'vitest';
import {
  advance,
  cueEnvelope,
  cueWindows,
  createAttempt,
  finish,
  press,
  validateCues,
  type Cue,
} from './judge';

const cues: readonly Cue[] = [
  { id: 'entry', label: '진입', key: 'MouseRight', start: 1, end: 2, reference: 1.5 },
  { id: 'attack', label: '공격', key: 'Space', start: 3, end: 4, reference: 3.5 },
];

describe('judge', () => {
  test.each([
    [0.999, 'pending', 1],
    [1, 'success', 0],
    [2, 'success', 0],
    [2.001, 'miss', 1],
  ] as const)('press at %s observes inclusive boundaries', (time, status, extras) => {
    const result = press(cues, createAttempt(cues), 'MouseRight', time);
    expect(result.results[0].status).toBe(status);
    expect(result.extras).toBe(extras);
  });

  test('early and wrong inputs allow recovery while remaining extra inputs', () => {
    const initial = createAttempt(cues);
    const early = press(cues, initial, 'MouseRight', 0.9);
    const wrong = press(cues, early, 'Space', 1.1);
    const recovered = press(cues, wrong, 'MouseRight', 1.2);

    expect(early.feedback).toBe('outside');
    expect(wrong.feedback).toBe('wrong');
    expect(recovered.results[0]).toEqual({
      cueId: 'entry', status: 'success', inputTime: 1.2, inputKey: 'MouseRight',
    });
    expect(recovered.extras).toBe(2);
    expect(recovered.extraInputs).toEqual([
      { time: .9, key: 'MouseRight', reason: 'outside' },
      { time: 1.1, key: 'Space', reason: 'wrong' },
    ]);
    expect(recovered.feedback).toBe('success');
    expect(recovered.lastInputTime).toBe(1.2);
    expect(initial).toEqual(createAttempt(cues));
    expect(early.results[0].status).toBe('pending');
    expect(wrong.results[0].status).toBe('pending');
  });

  test('duplicates and inputs between attacks do not revoke success', () => {
    const first = press(cues, createAttempt(cues), 'MouseRight', 1.5);
    const duplicate = press(cues, first, 'MouseRight', 1.6);
    const between = press(cues, duplicate, 'Space', 2.5);
    const complete = finish(cues, press(cues, between, 'Space', 3.5));

    expect(duplicate.feedback).toBe('outside');
    expect(between.feedback).toBe('outside');
    expect(complete.results.map((result) => result.status)).toEqual(['success', 'success']);
    expect(complete.results[0].inputTime).toBe(1.5);
    expect(complete.extras).toBe(2);
    expect(complete.extraInputs).toEqual([
      { time: 1.6, key: 'MouseRight', reason: 'after-success' },
      { time: 2.5, key: 'Space', reason: 'outside' },
    ]);
    expect(createAttempt(cues).extraInputs).toEqual([]);
  });

  test('expiration is strictly after the end and does not expire successes', () => {
    const initial = createAttempt(cues);
    const boundary = advance(cues, initial, 2);
    const expired = advance(cues, boundary, 2.001);
    expect(boundary.results[0].status).toBe('pending');
    expect(expired.results[0].status).toBe('miss');
    expect(expired.feedback).toBe('miss');
    expect(expired.extras).toBe(0);
    expect(initial.results[0].status).toBe('pending');

    const succeeded = press(cues, initial, 'MouseRight', 2);
    expect(advance(cues, succeeded, 3).results[0].status).toBe('success');
  });

  test('a later press expires missed attacks and still succeeds on the next one', () => {
    const result = press(cues, createAttempt(cues), 'Space', 3.5);
    expect(result.results.map((entry) => entry.status)).toEqual(['miss', 'success']);
    expect(result.extras).toBe(0);
    expect(result.feedback).toBe('success');
  });

  test('unrelated input does not change judging, feedback or input history', () => {
    const initial = createAttempt(cues);
    for (const key of ['Other', 'KeyA', 'MouseLeft', 'MouseMiddle']) {
      expect(press(cues, initial, key, 1.5)).toBe(initial);
      expect(press(cues, initial, key, 4.1)).toBe(initial);
    }
  });

  test.each(['MouseRight', 'Space'] as const)('entry accepts %s once without widening its window', key => {
    const dual: Cue[] = [{ ...cues[0], alternateKey: 'Space' }, cues[1]];
    for (const time of [1, 1.5, 2]) {
      const result = press(dual, createAttempt(dual), key, time);
      expect(result.results[0]).toMatchObject({ status: 'success', inputKey: key, inputTime: time });
      expect(result.extras).toBe(0);
      const duplicate = press(dual, result, key === 'Space' ? 'MouseRight' : 'Space', time);
      expect(duplicate.results).toEqual(result.results);
      expect(duplicate.extraInputs).toEqual([{ key: key === 'Space' ? 'MouseRight' : 'Space', time, reason: 'after-success' }]);
    }
    expect(press(dual, createAttempt(dual), key, .999).results[0].status).toBe('pending');
    expect(press(dual, createAttempt(dual), key, 2.001).results[0].status).toBe('miss');
    expect(press(dual, createAttempt(dual), 'MouseRight', 3.5).feedback).toBe('wrong');
  });

  test.each([
    ['contained', { key: 'Space', start: 1.2, end: 1.8, reference: 1.5 }],
    ['separate', { key: 'Space', start: 2.5, end: 3, reference: 2.75 }],
    ['partially overlapping', { key: 'Space', start: 1.5, end: 2.5, reference: 2.1 }],
  ] as const)('%s alternate window accepts either key only in its own interval', (_name, alternateWindow) => {
    const dual: Cue[] = [{ ...cues[0], alternateWindow }];
    for (const window of cueWindows(dual[0])) {
      const result = press(dual, createAttempt(dual), window.key, window.reference);
      expect(result.results[0]).toMatchObject({ status: 'success', inputKey: window.key, inputTime: window.reference });
      expect(result.extras).toBe(0);
    }
  });

  test('does not treat the union of key-specific windows as valid for either key', () => {
    const dual: Cue[] = [{ ...cues[0], alternateWindow: { key: 'Space', start: 1.5, end: 2.5, reference: 2 } }];
    const earlySpace = press(dual, createAttempt(dual), 'Space', 1.25);
    const lateMouse = press(dual, createAttempt(dual), 'MouseRight', 2.25);

    expect(earlySpace.results[0].status).toBe('pending');
    expect(earlySpace.extraInputs).toEqual([{ time: 1.25, key: 'Space', reason: 'wrong' }]);
    expect(lateMouse.results[0].status).toBe('pending');
    expect(lateMouse.extraInputs).toEqual([{ time: 2.25, key: 'MouseRight', reason: 'wrong' }]);
  });

  test('a gap between alternate windows is outside and expiration waits for the latest end', () => {
    const dual: Cue[] = [{ ...cues[0], alternateWindow: { key: 'Space', start: 3, end: 4, reference: 3.5 } }];
    const gap = press(dual, createAttempt(dual), 'MouseRight', 2.5);

    expect(gap.results[0].status).toBe('pending');
    expect(gap.extraInputs).toEqual([{ time: 2.5, key: 'MouseRight', reason: 'outside' }]);
    expect(advance(dual, gap, 4).results[0].status).toBe('pending');
    expect(advance(dual, gap, 4.001).results[0].status).toBe('miss');
  });

  test('early and wrong inputs can recover through the other key window, then duplicates are after-success', () => {
    const dual: Cue[] = [{ ...cues[0], alternateWindow: { key: 'Space', start: 1.5, end: 2.5, reference: 2 } }];
    const early = press(dual, createAttempt(dual), 'Space', .9);
    const wrong = press(dual, early, 'Space', 1.2);
    const recovered = press(dual, wrong, 'MouseRight', 1.4);
    const duplicate = press(dual, recovered, 'Space', 2.2);

    expect(early.extraInputs.at(-1)?.reason).toBe('outside');
    expect(wrong.extraInputs.at(-1)?.reason).toBe('wrong');
    expect(recovered.results[0]).toMatchObject({ status: 'success', inputKey: 'MouseRight', inputTime: 1.4 });
    expect(duplicate.results).toEqual(recovered.results);
    expect(duplicate.extraInputs.at(-1)).toEqual({ time: 2.2, key: 'Space', reason: 'after-success' });
  });

  test('finish marks every unattempted cue missed without creating extra inputs', () => {
    const initial = createAttempt(cues);
    const result = finish(cues, initial);
    expect(result.results.map((entry) => entry.status)).toEqual(['miss', 'miss']);
    expect(result.extras).toBe(0);
    expect(result.feedback).toBe('miss');
    expect(result.lastInputTime).toBeUndefined();
    expect(initial.results.every((entry) => entry.status === 'pending')).toBe(true);
    expect(finish(cues, result)).toEqual(result);
  });
});

describe('validateCues', () => {
  test('accepts ordered windows within the clip, including endpoint references', () => {
    expect(() => validateCues(cues, 4)).not.toThrow();
    expect(() => validateCues([{ ...cues[0], reference: 1 }], 2)).not.toThrow();
    expect(() => validateCues([{ ...cues[0], reference: 2 }], 2)).not.toThrow();
  });

  test.each([NaN, Infinity, -1])('rejects invalid duration %s', (duration) => {
    expect(() => validateCues(cues, duration)).toThrow();
  });

  test.each([
    { start: NaN },
    { end: Infinity },
    { reference: NaN },
    { start: -1 },
    { end: -1 },
    { reference: -1 },
    { start: 2.1 },
    { reference: 0.9 },
    { reference: 2.1 },
    { end: 5 },
    { id: '' },
    { id: '  ' },
  ])('rejects invalid cue values %j', (changes) => {
    expect(() => validateCues([{ ...cues[0], ...changes }], 4)).toThrow();
  });

  test('rejects duplicate IDs, unordered windows, overlap and a shared endpoint', () => {
    expect(() => validateCues([cues[0], { ...cues[1], id: cues[0].id }], 4)).toThrow();
    expect(() => validateCues([cues[1], cues[0]], 4)).toThrow();
    expect(() => validateCues([cues[0], { ...cues[1], start: 1.9 }], 4)).toThrow();
    expect(() => validateCues([cues[0], { ...cues[1], start: 2 }], 4)).toThrow();
  });

  test('normalizes legacy same-width alternatives and reports the full envelope', () => {
    const legacy: Cue = { ...cues[0], alternateKey: 'Space' };
    expect(cueWindows(legacy)).toEqual([
      { key: 'MouseRight', start: 1, end: 2, reference: 1.5 },
      { key: 'Space', start: 1, end: 2, reference: 1.5 },
    ]);
    expect(cueEnvelope({ ...cues[0], alternateWindow: { key: 'Space', start: .5, end: 3, reference: 2.5 } }))
      .toEqual({ start: .5, end: 3 });
  });

  test.each([
    { key: 'MouseRight', start: 2.5, end: 3, reference: 2.75 },
    { key: 'Other', start: 2.5, end: 3, reference: 2.75 },
    { key: 'Space', start: NaN, end: 3, reference: 2.75 },
    { key: 'Space', start: 2.5, end: Infinity, reference: 2.75 },
    { key: 'Space', start: 2.5, end: 3, reference: 2.4 },
    { key: 'Space', start: 2.5, end: 3, reference: 3.1 },
    { key: 'Space', start: 3.1, end: 3, reference: 3.05 },
    { key: 'Space', start: 2.5, end: 5, reference: 3 },
  ])('rejects invalid alternate window %j', alternateWindow => {
    expect(() => validateCues([{ ...cues[0], alternateWindow } as Cue], 4)).toThrow();
  });

  test('rejects both alternate forms together and envelope overlap between cues', () => {
    expect(() => validateCues([{ ...cues[0], alternateKey: 'Space', alternateWindow: {
      key: 'Space', start: 1, end: 2, reference: 1.5,
    } }], 4)).toThrow();
    expect(() => validateCues([
      { ...cues[0], alternateWindow: { key: 'Space', start: 1, end: 3.1, reference: 2.5 } },
      cues[1],
    ], 4)).toThrow();
  });
});
