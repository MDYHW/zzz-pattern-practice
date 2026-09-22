import test from 'node:test';
import assert from 'node:assert/strict';
import { profileChecks } from './check.mjs';

function fixture() {
  const data = {
    Id: 'vesper-execute-01', BossId: 'vesper', Skill: 'Execute_01',
    Actions: [
      { Timing: { EarlyMs: 9350, BaselineMs: 9500, LateMs: 9750 } },
      { Timing: { EarlyMs: 10650, BaselineMs: 10850, LateMs: 11150 } },
    ],
  };
  return {
    profiles: [{ path: 'bundled/vesper-execute-01.json', data }],
    recordings: [{ contentId: data.Id, bossId: data.BossId, skill: data.Skill,
      windowsMs: [[9350, 9750], [10650, 11150]], referenceMs: [9500, 10700] }],
    baselines: new Map([[data.Id, [9500, 10850]]]),
  };
}

test('adopted service extensions preserve baselines distinct from video references', () => {
  const { profiles, recordings, baselines } = fixture();
  assert.deepEqual(profileChecks(profiles, recordings, baselines), profiles);
  profiles[0].data.Actions[1].Timing.BaselineMs = 10700;
  assert.throws(() => profileChecks(profiles, recordings, baselines), /baseline settings/);
});

test('old EX01 upper bounds cannot pass against current service evidence', () => {
  for (const [index, oldEnd] of [[0, 9700], [1, 11100]]) {
    const { profiles, recordings, baselines } = fixture();
    profiles[0].data.Actions[index].Timing.LateMs = oldEnd;
    assert.throws(() => profileChecks(profiles, recordings, baselines), /adopted service ranges/);
  }
});

test('unlinked media does not require shipping an experiment profile', () => {
  const { profiles, recordings, baselines } = fixture();
  const historical = { ...recordings[0], contentId: 'historical-unpublished' };
  assert.deepEqual(profileChecks(profiles, [...recordings, historical], baselines), profiles);
});

test('duplicate and missing identities and mismatched boss or skill fail', () => {
  const { profiles, recordings, baselines } = fixture();
  assert.throws(() => profileChecks([...profiles, ...profiles], recordings), /Duplicate bundled profile/);
  assert.throws(() => profileChecks(profiles, [...recordings, ...recordings]), /Duplicate service recording/);
  assert.throws(() => profileChecks([{ data: { Id: '' } }], []), /valid Id/);
  for (const field of ['bossId', 'skill']) {
    assert.throws(() => profileChecks(profiles, [{ ...recordings[0], [field]: 'other' }], baselines), /mismatch/);
  }
});

test('a newly discovered candidate without media remains in the runner check list', () => {
  const { profiles, recordings, baselines } = fixture();
  const candidate = { path: 'bundled/new-candidate.json', data: {
    Id: 'new-candidate', BossId: 'new-boss', Skill: 'Unmeasured',
    Actions: [{ Timing: { EarlyMs: 100, BaselineMs: 150, LateMs: 200 } }],
  } };
  const checked = profileChecks([...profiles, candidate], recordings, baselines);
  assert.deepEqual(checked.map(profile => profile.path), [profiles[0].path, candidate.path]);
  assert.deepEqual(profileChecks([candidate], []), [candidate]);
});
