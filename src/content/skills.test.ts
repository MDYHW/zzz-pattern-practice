import { expect, test } from 'vitest';
import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import evidence from '../../docs/evidence/service-media-20260920.json';
import sunEvidence from '../../docs/evidence/sun-service-media-20260924.json';
import vesselEvidence from '../../docs/evidence/vessel-service-media-20260924.json';
import girReview from '../../docs/evidence/girtablullu-off-20260920.json';
import rmbEvidence from '../../docs/evidence/mirage-archer-unit-rmb-20260922.json';
import { girtablulluExecute01, kusarikkuExecute01, mirageArcherUnitAttack09, phaethonExecute01, phaethonIntegratedExecute02, vesselExecute01, skills, vesperExecute01, vesperExecute02, withEntryKey, withMirageLastResponse } from './skills';
import { phaethonRecording } from './phaethon-patterns';
import { cuesForRecording, vesperPatterns } from './vesper-patterns';
import { createAttempt, press, validateCues } from '../practice/judge';
import type { PracticeContent } from '../practice/session';

type BundledProfile = {
  Id: string; BossId: string; Skill: string;
  Actions: { Key: string; Timing: { EarlyMs: number; BaselineMs: number; LateMs: number } }[];
};
const profilesDirectory = resolve('tools/experiment/profiles');
// Only shipped defaults participate here; editable .local profiles are independent.
const bundledProfiles: BundledProfile[] = readdirSync(profilesDirectory).filter(name => name.endsWith('.json')).sort()
  .map(name => JSON.parse(readFileSync(resolve(profilesDirectory, name), 'utf8').replace(/^\uFEFF/, '')));
const reviewedRecordings = [...evidence.recordings, ...sunEvidence.recordings, ...vesselEvidence.recordings];
type ReviewedRecording = (typeof reviewedRecordings)[number];

// The temporary web-only allowance must not rewrite measured windows or bundled experiment profiles.
const serviceAllowanceMs = (contentId: string, index: number) =>
  (contentId === 'mirage-archer-unit-attack-09' && (index === 0 || index === 3))
  || (contentId === 'phaethon-integrated-execute-02' && index === 0) ? 25 : 0;

function assertReviewedContent(content: PracticeContent, recordings: readonly ReviewedRecording[], profiles: BundledProfile[]) {
  const matches = recordings.filter(item => item.contentId === content.id);
  expect(matches, `${content.id}: one representative recording`).toHaveLength(1);
  const profileMatches = profiles.filter(item => item.Id === content.id);
  expect(profileMatches, `${content.id}: one bundled profile`).toHaveLength(1);
  const recording = matches[0], profile = profileMatches[0];
  expect([recording.bossId, recording.skill, recording.clip]).toEqual([content.bossId, content.title, content.video]);
  expect([profile.BossId, profile.Skill]).toEqual([content.bossId, content.title]);
  expect(content.duration).toBe((recording.endSourceFrameExclusive - recording.firstSourceFrame) / recording.fps);
  expect(content.cues).toHaveLength(recording.windowsMs.length);
  expect(profile.Actions).toHaveLength(content.cues.length);
  content.cues.forEach((cue, index) => {
    const [start, end] = recording.windowsMs[index];
    const allowance = serviceAllowanceMs(content.id, index);
    const action = profile.Actions[index];
    expect([action.Timing.EarlyMs, action.Timing.LateMs]).toEqual([start, end]);
    expect(cue.key).toBe(action.Key === 'RMB' ? 'MouseRight' : action.Key);
    expect(cue.alternateKey).toBeUndefined();
    expect(cue.start - recording.patternTimeZeroAtClipTime).toBeCloseTo((start - allowance) / 1000, 10);
    expect(cue.end - cue.start).toBeCloseTo((end - start + 2 * allowance) / 1000, 10);
    const recorded = (recording.sourceOverlayDownFrames[index] - recording.firstSourceFrame) / recording.fps;
    expect(Math.abs(cue.reference - recorded)).toBeLessThanOrEqual(2 / recording.fps + 1e-10);
    expect(recorded).toBeGreaterThanOrEqual(cue.start);
    expect(recorded).toBeLessThanOrEqual(cue.end);
  });
  // BaselineMs schedules experiments; it is deliberately not compared with video input references.
  expect(() => validateCues(content.cues, content.duration)).not.toThrow();
}

test('published IDs are unique', () => {
  expect(new Set(skills.map(content => content.id)).size).toBe(skills.length);
});

test('Vessel keeps two preparation dodges and W outside the five scored responses', () => {
  const content = vesselExecute01;
  const recording = vesselEvidence.recordings[0];
  const toClip = (frame: number) => (frame - recording.firstSourceFrame) / recording.fps;
  expect(content.cues.map(cue => cue.key)).toEqual(['MouseRight', 'Space', 'Space', 'Space', 'Space']);
  expect(content.entryOptions?.map(option => option.key)).toEqual(['MouseRight']);
  expect(() => withEntryKey(content, 'Space')).toThrow(/unavailable/);
  expect(content.preparation!.end).toBe(toClip(573));
  expect(content.preparation!.end).toBeLessThan(content.cues[0].start);
  expect(content.preparation!.dodges.map(dodge => dodge.time)).toEqual([495, 564].map(toClip));
  expect(content.duration).toBe(1175 / 60);
  expect(content.preparation!.dodges[0].time).toBeCloseTo(245 / 60, 10);
  expect(content.cues[0].reference).toBeCloseTo(11.05, 10);
  expect(content.preparation!.movements).toEqual([{ key: 'W', start: toClip(487), end: toClip(503) }]);
  expect(bundledProfiles.find(profile => profile.Id === content.id)!.Actions.map(action => action.Timing.BaselineMs))
    .toEqual([3300, 5400, 6300, 7500, 9100]);
  expect(recording.recordingLink.trialId).toBeNull();
});

test('Kusarikku keeps preparation outside five scored windows and closes its RMB exemption before entry', () => {
  const content = kusarikkuExecute01;
  const recording = evidence.recordings.find(item => item.contentId === content.id)!;
  expect(content.cues).toHaveLength(5);
  expect(content.entryOptions?.map(option => option.key)).toEqual(['MouseRight']);
  expect(() => withEntryKey(content, 'Space')).toThrow(/unavailable/);
  expect(content.preparation!.end).toBeLessThan(content.cues[0].start);
  expect(content.preparation!.dodges).toHaveLength(2);
  content.preparation!.dodges.forEach(dodge => {
    expect(dodge.time).toBeGreaterThan(0);
    expect(dodge.time).toBeLessThan(content.preparation!.end);
  });
  const source = recording.preparation!;
  const toClip = (frame: number) => (frame - recording.firstSourceFrame) / recording.fps;
  expect(content.preparation!.end).toBe(toClip(source.sourceLastDodgeReleaseFrame));
  expect(content.preparation!.dodges.map(dodge => dodge.time)).toEqual(source.sourceDodgeDownFrames.map(toClip));
  expect(content.preparation!.movements).toEqual(source.sourceMovements.map(movement => ({
    key: movement.key, start: toClip(movement.startFrame), end: toClip(movement.endFrameExclusive),
  })));
  expect(bundledProfiles.find(profile => profile.Id === content.id)!.Actions.map(action => action.Timing.BaselineMs))
    .toEqual([4100, 6850, 7900, 9650, 10750]);
});

test('Mirage Archer Unit exposes five scored cues with RMB-only entry, no preparation, and adopted baselines', () => {
  const content = mirageArcherUnitAttack09;
  expect(content.cues).toHaveLength(5);
  expect(content.cues.map(cue => cue.key)).toEqual(['MouseRight', 'Space', 'Space', 'Space', 'Space']);
  expect(content.entryOptions?.map(option => option.key)).toEqual(['MouseRight']);
  expect(() => withEntryKey(content, 'Space')).toThrow(/unavailable/);
  expect(content.preparation).toBeUndefined();
  expect(bundledProfiles.find(profile => profile.Id === content.id)!.Actions.map(action => action.Timing.BaselineMs))
    .toEqual([4250, 6400, 8150, 9550, 11850]);
});

test('Phaethon forms remain separate routes with RMB entry and four or five scored cues', () => {
  expect(skills).toHaveLength(8);
  expect(new Set(skills.map(content => content.bossId)).size).toBe(7);
  for (const [content, count] of [[phaethonExecute01, 4], [phaethonIntegratedExecute02, 5]] as const) {
    expect(content.cues).toHaveLength(count);
    expect(content.cues.map(cue => cue.key)).toEqual(['MouseRight', ...Array(count - 1).fill('Space')]);
    expect(content.entryOptions?.map(option => option.key)).toEqual(['MouseRight']);
    expect(() => withEntryKey(content, 'Space')).toThrow(/unavailable/);
  }
  expect(phaethonExecute01.preparation!.dodges).toHaveLength(1);
  expect(phaethonExecute01.preparation!.movements).toEqual([]);
  expect(phaethonExecute01.preparation!.end).toBeLessThan(phaethonExecute01.cues[0].start);
  const toClip = (frame: number) => (frame - phaethonRecording.firstSourceFrame) / 60;
  expect(phaethonExecute01.preparation!.dodges[0].time).toBe(toClip(phaethonRecording.preparationDodgeDownFrame));
  expect(phaethonExecute01.preparation!.end).toBe(toClip(phaethonRecording.preparationDodgeReleaseFrame));
  expect(sunEvidence.recordings.find(item => item.contentId === phaethonExecute01.id)).toMatchObject({
    preparation: {
      sourceDodgeDownFrames: [phaethonRecording.preparationDodgeDownFrame],
      sourceLastDodgeReleaseFrame: phaethonRecording.preparationDodgeReleaseFrame,
      sourceMovements: [],
    },
  });
  expect(phaethonIntegratedExecute02.preparation).toBeUndefined();
});

test('Mirage final choices preserve the five attacks and use the reviewed final-response videos', () => {
  const space = withMirageLastResponse('selected', 'Space');
  const rmb = withMirageLastResponse('selected', 'MouseRight');
  const both = withMirageLastResponse('overlap', 'Space');
  const recording = rmbEvidence.recordings[0];
  expect(space).toBe(mirageArcherUnitAttack09);
  expect(both).toEqual(withMirageLastResponse('overlap', 'MouseRight'));
  for (const resolved of [rmb, both]) {
    expect(resolved.cues).toHaveLength(5);
    expect(resolved.entryOptions?.map(option => option.key)).toEqual(['MouseRight']);
    expect(resolved.video).toBe(recording.clip);
    expect(resolved.duration).toBe((recording.endSourceFrameExclusive - recording.firstSourceFrame) / 60);
    expect(resolved.recordedFinalKey).toBe('MouseRight');
    expect(() => validateCues(resolved.cues, resolved.duration)).not.toThrow();
    resolved.cues.forEach((cue, i) => {
      const allowance = serviceAllowanceMs(resolved.id, i);
      expect(cue.start - 2).toBeCloseTo((recording.windowsMs[i][0] - allowance) / 1000, 10);
      expect(cue.end - 2).toBeCloseTo((recording.windowsMs[i][1] + allowance) / 1000, 10);
      expect(cue.reference).toBeCloseTo((recording.sourceOverlayDownFrames[i] - recording.firstSourceFrame) / 60);
      if (i < 4) expect({ key: cue.key, start: cue.start, end: cue.end })
        .toEqual({ key: space.cues[i].key, start: space.cues[i].start, end: space.cues[i].end });
    });
  }
  expect(createHash('sha256').update(readFileSync(resolve('public', rmb.video))).digest('hex')).toBe(recording.clipSha256);
  const last = both.cues[4];
  expect(last.alternateWindow).toEqual({ key: 'Space', start: space.cues[4].start, end: space.cues[4].end, reference: space.cues[4].reference });
  expect(rmb.cues[4].alternateWindow).toBeUndefined();
  for (const [key, time] of [['MouseRight', 13.85], ['MouseRight', 14.05], ['Space', 13.7]] as const)
    expect(press(both.cues, createAttempt(both.cues), key, time).results[4].status).toBe('success');
  expect(press(both.cues, createAttempt(both.cues), 'MouseRight', 13.7).results[4].status).toBe('pending');
  const early = press(both.cues, createAttempt(both.cues), 'Space', 13.4);
  const recovered = press(both.cues, early, 'MouseRight', 13.95);
  expect(recovered.results[4].status).toBe('success');
  expect(recovered.extras).toBe(1);
});

test('Mirage accepts the temporary 25ms edge extensions in every route but rejects outside them', () => {
  for (const layout of ['selected', 'overlap'] as const) {
    for (const key of ['Space', 'MouseRight'] as const) {
      const content = withEntryKey(withMirageLastResponse(layout, key), 'MouseRight');
      // Notice-relative 4125–4375 and 9375–9725ms, with the unchanged 2s clip origin.
      for (const [index, start, end] of [[0, 6.125, 6.375], [3, 11.375, 11.725]]) {
        const cue = content.cues[index];
        expect([cue.start, cue.end]).toEqual([start, end]);
        for (const time of [start, start + .0125, end - .0125, end])
          expect(press(content.cues, createAttempt(content.cues), cue.key, time).results[index].status).toBe('success');
        const early = press(content.cues, createAttempt(content.cues), cue.key, start - .001);
        expect(early.results[index].status).toBe('pending');
        expect(early.extras).toBe(1);
        expect(press(content.cues, createAttempt(content.cues), cue.key, end + .001).results[index].status).toBe('miss');
      }
    }
  }
});

test('missing or duplicate published connections fail without requiring media for research candidates', () => {
  const content = vesperExecute01;
  const recording = evidence.recordings.find(item => item.contentId === content.id)!;
  const profile = bundledProfiles.find(item => item.Id === content.id)!;
  for (const recordings of [[], [recording, recording], [{ ...recording, bossId: 'wrong-boss' }]])
    expect(() => assertReviewedContent(content, recordings, [profile])).toThrow();
  for (const profiles of [[], [profile, profile], [{ ...profile, Skill: 'wrong-skill' }]])
    expect(() => assertReviewedContent(content, [recording], profiles)).toThrow();
  const candidate = { ...profile, Id: 'unpublished-candidate' };
  expect(() => assertReviewedContent(content, [recording], [profile, candidate])).not.toThrow();
  const next = { ...content, id: 'new-boss-pattern', bossId: 'new-boss' };
  expect(() => assertReviewedContent(next, [recording], [profile])).toThrow();
  expect(() => assertReviewedContent(next, [{ ...recording, contentId: next.id, bossId: next.bossId }],
    [{ ...profile, Id: next.id, BossId: next.bossId }])).not.toThrow();
});

test('historical EX01 upper endpoints fail while different experiment and video references remain valid', () => {
  const recording = evidence.recordings.find(item => item.contentId === vesperExecute01.id)!;
  const profile = bundledProfiles.find(item => item.Id === vesperExecute01.id)!;
  expect(profile.Actions.slice(3, 5).map(action => action.Timing.BaselineMs)).toEqual([9500, 10850]);
  expect(profile.Actions.map(action => action.Timing.BaselineMs)).not.toEqual(recording.referenceMs);
  expect(() => assertReviewedContent(vesperExecute01, [recording], [profile])).not.toThrow();
  for (const [index, oldEnd] of [[3, 9700], [4, 11100]]) {
    const oldContent = structuredClone(vesperExecute01);
    oldContent.cues[index].end = oldEnd / 1000 + recording.patternTimeZeroAtClipTime;
    expect(() => assertReviewedContent(oldContent, [recording], [profile])).toThrow();
  }
});

for (const content of skills) {
  const recording = reviewedRecordings.find(item => item.contentId === content.id)!;
  test(`${content.id} preserves measured evidence with only explicit service allowances and aligns with recorded inputs`, () => {
    assertReviewedContent(content, reviewedRecordings, bundledProfiles);
  });
  test(`${content.id} ships the reviewed trimmed asset`, () => {
    const clip = readFileSync(resolve('public', content.video));
    expect(createHash('sha256').update(clip).digest('hex')).toBe(recording.clipSha256);
  });
}

test('changing the recording origin translates all times without stretching the pattern', () => {
  const before = cuesForRecording(vesperPatterns.execute01, 0);
  const after = cuesForRecording(vesperPatterns.execute01, .25);
  after.forEach((cue, index) => {
    for (const field of ['start', 'end', 'reference'] as const) expect(cue[field] - before[index][field]).toBeCloseTo(.25, 12);
  });
});

test('OFF-approved extensions accept the extra 50ms while the mixed fourth endpoint stays outside', () => {
  const cues = skills.find(content => content.title === 'Execute_02')!.cues;
  for (const index of [1, 2, 3]) {
    const inside = press(cues, createAttempt(cues), 'Space', cues[index].end - .025);
    expect(inside.results[index].status).toBe('success');
    const outside = press(cues, createAttempt(cues), 'Space', cues[index].end + .001);
    expect(outside.results[index].status).toBe('miss');
  }
  const mixed = press(cues, createAttempt(cues), 'Space', cues[4].end + .050);
  expect(mixed.results[4].status).toBe('miss');
});

test('entry selection resolves only the chosen key and window without changing later cues or video', () => {
  const rmb = withEntryKey(vesperExecute02, 'MouseRight');
  const space = withEntryKey(vesperExecute02, 'Space');
  expect(rmb.cues[0].start - space.cues[0].start).toBeCloseTo(.15);
  expect(rmb.cues[0].end).toBeCloseTo(space.cues[0].end);
  const spaceOnly = space.cues[0].start + .05;
  const shared = rmb.cues[0].reference;
  expect(press(rmb.cues, createAttempt(rmb.cues), 'MouseRight', spaceOnly).results[0].status).toBe('pending');
  expect(press(rmb.cues, createAttempt(rmb.cues), 'Space', shared).results[0].status).toBe('pending');
  expect(press(space.cues, createAttempt(space.cues), 'Space', spaceOnly).results[0].status).toBe('success');
  expect(press(space.cues, createAttempt(space.cues), 'MouseRight', shared).results[0].status).toBe('pending');
  expect(space.cues.slice(1)).toEqual(rmb.cues.slice(1));
  expect(space.video).toBe(rmb.video);
  expect(vesperExecute02.cues[0].key).toBe('MouseRight');
  const oldRmb = withEntryKey(vesperExecute01, 'MouseRight');
  const oldSpace = withEntryKey(vesperExecute01, 'Space');
  expect(oldSpace.cues[0]).toEqual({ ...oldRmb.cues[0], key: 'Space' });
});

test('Girtablullu exposes only the measured RMB entry and accepts both adopted crosses', () => {
  expect(girtablulluExecute01.entryOptions?.map(option => option.key)).toEqual(['MouseRight']);
  expect(() => withEntryKey(girtablulluExecute01, 'Space')).toThrow(/unavailable/);
  const recording = evidence.recordings.find(item => item.contentId === girtablulluExecute01.id)!;
  for (const times of girReview.decision.crosses) {
    let attempt = createAttempt(girtablulluExecute01.cues);
    times.forEach((ms, index) => {
      attempt = press(girtablulluExecute01.cues, attempt, index === 0 ? 'MouseRight' : 'Space', ms / 1000 + recording.patternTimeZeroAtClipTime);
    });
    expect(attempt.results.every(result => result.status === 'success')).toBe(true);
  }
});
