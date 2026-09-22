import test from 'node:test';
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, rmSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { importTrial, annotateTrial, saveBatch, summarize, validateStore } from './records.mjs';

function fixture(t) {
  const root = mkdtempSync(join(tmpdir(), 'zzz-records-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const file = (name, value) => {
    const path = join(root, name); mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, typeof value === 'string' ? value : JSON.stringify(value)); return path;
  };
  const raw = (extra = {}) => ({ Version: '2.1.0', Skill: 'Execute_02', Status: 'completed',
    Recording: 'OFF', Pattern: 'baseline', DelaysMs: [3150, 4450, 5950], GameOutcome: 'success',
    Inputs: [{ Attack: 0, Key: 'RMB', Action: 'down', Inserted: 1 }, { Attack: 1, Key: 'SPACE', Action: 'down', Inserted: 1 }], ...extra });
  const store = join(root, '.local', 'records.json');
  return { root, file, raw, store };
}
const user = { kind: 'user-report', reference: 'test participant confirmed this trial' };
const inputEvent = (Attack, Key, Action, DueMs, extra = {}) => ({ Attack, Key, Action, DueMs,
  BeginMs: DueMs + 1, EndMs: DueMs + 2, Inserted: Action.startsWith('virtual-') ? 0 : 1, Win32Error: 0, ...extra });

test('auxiliary inputs import as unconfirmed evidence without changing main action results', t => {
  const { file, raw, store } = fixture(t);
  const planned = [
    { Id: 'early-space', Label: 'Early dodge', Key: 'Space', AtMs: 100, HoldMs: 50, Enabled: true },
    { Id: 'unused-lmb', Label: 'Disabled attack', Key: 'LMB', AtMs: 200, HoldMs: 50, Enabled: false },
    { Id: 'follow-rmb', Label: 'Follow-up dodge', Key: 'RMB', AtMs: 300, HoldMs: 100, Enabled: true },
  ];
  const inputs = [inputEvent(0, 'Space', 'down', 1100), inputEvent(0, 'Space', 'up', 1152),
    inputEvent(2, 'RMB', 'down', 1300), inputEvent(2, 'RMB', 'up', 1402)];
  const path = file('auxiliary.json', raw({ TrialId: 'auxiliary', Status: 'submitted-not-game-verified',
    ActionIds: ['a', 'b', 'c'], DueMs: [4150, 5450, 6950],
    ProfileSnapshot: { SchemaVersion: 2, AuxiliaryInputs: planned }, AuxiliaryInputs: inputs }));
  const original = readFileSync(path);
  file('auxiliary.json.result.json', resultHistory(path, 'auxiliary', '101'));
  importTrial(store, path);
  const trial = summarize(store).trials[0];
  assert.deepEqual(trial.auxiliaryInputs, { planned, inputs, outcome: 'unconfirmed' });
  assert.deepEqual(trial.actions.map(row => row.outcome), ['success', 'failure', 'success']);
  assert.equal(trial.actions.length, 3);
  assert.deepEqual(readFileSync(path), original);
  assert.equal(validateStore(store).valid, true);
});

test('auxiliary inputs accept virtual observation and interrupted cleanup release evidence', t => {
  const planned = [{ Id: 'space', Label: 'Dodge', Key: 'Space', AtMs: 100, HoldMs: 100, Enabled: true }];
  {
    const { file, raw, store } = fixture(t);
    const inputs = [inputEvent(0, 'Space', 'virtual-down', 1100), inputEvent(0, 'Space', 'virtual-up', 1202)];
    const path = file('observe.json', raw({ TrialId: 'observe-aux', Status: 'observed-no-input', Mode: 'observe',
      DueMs: [4150, 5450, 6950], ProfileSnapshot: { SchemaVersion: 2, AuxiliaryInputs: planned }, AuxiliaryInputs: inputs }));
    importTrial(store, path);
    assert.deepEqual(summarize(store).trials[0].auxiliaryInputs.inputs, inputs);
  }
  {
    const { file, raw, store } = fixture(t);
    const inputs = [inputEvent(0, 'Space', 'down', 1100), inputEvent(0, 'Space', 'up', 1125)];
    const path = file('cancelled.json', raw({ TrialId: 'cancelled-aux', Status: 'cancelled', DueMs: [4150, 5450, 6950],
      ProfileSnapshot: { SchemaVersion: 2, AuxiliaryInputs: planned }, AuxiliaryInputs: inputs }));
    importTrial(store, path);
    assert.deepEqual(summarize(store).trials[0].auxiliaryInputs.inputs, inputs);
  }
  {
    const { file, raw, store } = fixture(t);
    const inputs = [inputEvent(0, 'Space', 'down', 1100)];
    importTrial(store, file('failed.json', raw({ TrialId: 'failed-aux', Status: 'failed', DueMs: [4150, 5450, 6950],
      ProfileSnapshot: { SchemaVersion: 2, AuxiliaryInputs: planned }, AuxiliaryInputs: inputs })));
    assert.equal(summarize(store).trials[0].auxiliaryInputs.inputs.length, 1);
  }
  {
    const { file, raw, store } = fixture(t);
    const inputs = [inputEvent(0, 'Space', 'down', 1100),
      inputEvent(0, 'Space', 'up', 1202, { Inserted: 0, Win32Error: 5 }), inputEvent(0, 'Space', 'up', 1210)];
    importTrial(store, file('release-retry.json', raw({ TrialId: 'release-retry', Status: 'release-failed', DueMs: [4150, 5450, 6950],
      ProfileSnapshot: { SchemaVersion: 2, AuxiliaryInputs: planned }, AuxiliaryInputs: inputs })));
    assert.equal(summarize(store).trials[0].auxiliaryInputs.inputs.length, 3);
  }
});

test('auxiliary input import rejects incompatible schema and events that disagree with the plan', t => {
  const plan = [{ Id: 'space', Label: 'Dodge', Key: 'Space', AtMs: 100, HoldMs: 100, Enabled: true },
    { Id: 'rmb', Label: 'Disabled', Key: 'RMB', AtMs: 300, HoldMs: 100, Enabled: false }];
  const cases = [
    { snapshot: { SchemaVersion: 1, AuxiliaryInputs: plan }, inputs: [], label: 'v1 with auxiliary plan' },
    { snapshot: { SchemaVersion: 2, AuxiliaryInputs: plan }, inputs: [], label: 'completed row without events' },
    { snapshot: { SchemaVersion: 2, AuxiliaryInputs: plan }, inputs: [inputEvent(2, 'Space', 'down', 1100)], label: 'source row' },
    { snapshot: { SchemaVersion: 2, AuxiliaryInputs: plan }, inputs: [inputEvent(1, 'RMB', 'down', 1300)], label: 'disabled row' },
    { snapshot: { SchemaVersion: 2, AuxiliaryInputs: plan }, inputs: [inputEvent(0, 'RMB', 'down', 1100)], label: 'key' },
    { snapshot: { SchemaVersion: 2, AuxiliaryInputs: plan }, inputs: [inputEvent(0, 'Space', 'down', 1150)], label: 'timing' },
    { snapshot: { SchemaVersion: 2, AuxiliaryInputs: plan }, inputs: [inputEvent(0, 'Space', 'down', 1100), inputEvent(0, 'Space', 'up', 1150)], label: 'hold timing' },
    { snapshot: { SchemaVersion: 2, AuxiliaryInputs: plan }, inputs: [inputEvent(0, 'Space', 'up', 1200)], label: 'up without down' },
    { snapshot: { SchemaVersion: 2, AuxiliaryInputs: plan }, inputs: [inputEvent(0, 'Space', 'virtual-down', 1100)], label: 'virtual action outside observe' },
  ];
  for (const [index, row] of cases.entries()) {
    const { file, raw, store } = fixture(t);
    const path = file(`invalid-${index}.json`, raw({ TrialId: `invalid-${index}`, Status: 'submitted-not-game-verified',
      DueMs: [4150, 5450, 6950], ProfileSnapshot: row.snapshot, AuxiliaryInputs: row.inputs }));
    assert.throws(() => importTrial(store, path), { code: 'INVALID_DATA' }, row.label);
  }
  const { file, raw, store } = fixture(t);
  importTrial(store, file('legacy.json', raw({ TrialId: 'legacy', ProfileSnapshot: { SchemaVersion: 1 } })));
  assert.equal(Object.hasOwn(summarize(store).trials[0], 'auxiliaryInputs'), false);
});

test('automatic starting attacks stay separate from preparation and five response outcomes', t => {
  const { file, raw, store } = fixture(t);
  const planned = { AtMs: [500, 1050, 1700, 2300, 3100], HoldMs: 100 };
  const inputs = [{ Attack: 0, Key: 'LMB', Action: 'down', DueMs: 500, Inserted: 1 }];
  const path = file('start.json', raw({ TrialId: 'start', ActionIds: ['entry', 'hit-1', 'hit-2', 'hit-3', 'hit-4'],
    ActionKeys: ['RMB', 'Space', 'Space', 'Space', 'Space'], DelaysMs: [4100, 7050, 8050, 9600, 10850],
    ProfileSnapshot: { StartAttack: planned }, StartAttackInputs: inputs }));
  const bytes = readFileSync(path);
  file('start.json.result.json', resultHistory(path, 'start', '11111'));
  importTrial(store, path);
  const trial = summarize(store).trials[0];
  assert.deepEqual(trial.startAttack, { planned, inputs, outcome: 'unconfirmed' });
  assert.equal(trial.actions.length, 5);
  assert.ok(trial.actions.every(action => action.outcome === 'success'));
  assert.equal(Object.hasOwn(trial, 'preparation'), false);
  assert.deepEqual(readFileSync(path), bytes);
  const legacy = file('legacy-start.json', raw({ TrialId: 'legacy-start' }));
  importTrial(store, legacy);
  assert.equal(Object.hasOwn(summarize(store).trials[1], 'startAttack'), false);
  assert.equal(validateStore(store).valid, true);
});
test('automatic preparation remains separate from response results and does not imply preparation success', t => {
  const { file, raw, store } = fixture(t);
  const planned = [{ Id: 'move-a', Label: '준비 이동', Key: 'A', AtMs: 550, HoldMs: 650 },
    { Id: 'prepare-rmb', Label: '준비 회피', Key: 'RMB', AtMs: 1400, HoldMs: 150 }];
  const inputs = [{ Attack: 0, Key: 'A', Action: 'down', Inserted: 1 }, { Attack: 1, Key: 'RMB', Action: 'down', Inserted: 1 }];
  const path = file('automatic.json', raw({ TrialId: 'automatic', ActionIds: ['entry', 'hit-1', 'hit-2', 'hit-3', 'hit-4'],
    ActionKeys: ['RMB', 'Space', 'Space', 'Space', 'Space'], DelaysMs: [3700, 6550, 7650, 9100, 10450],
    ProfileSnapshot: { Preparation: planned }, PreparationInputs: inputs }));
  file('automatic.json.result.json', resultHistory(path, 'automatic', '11111'));
  importTrial(store, path);
  const trial = summarize(store).trials[0];
  assert.equal(trial.actions.length, 5);
  assert.ok(trial.actions.every(action => action.outcome === 'success'));
  assert.deepEqual(trial.preparation, { planned, inputs, outcome: 'unconfirmed' });
  assert.equal(validateStore(store).valid, true);
});
const note = (overrides = {}) => ({ reason: 'initial report', recording: { actual: 'OFF', source: user }, videos: [],
  actions: [{ attack: 0, outcome: 'success', source: user }], ...overrides });

test('preparation reports import, survive other annotations and clear without rewriting legacy fingerprints', t => {
  const { file, raw, store } = fixture(t);
  const path = file('preparation.json', raw({ TrialId: 'preparation', StartCharacter: '아리아', ActionIds: ['a', 'b', 'c'],
    ProfileSnapshot: { Preparation: [{ Id: 'prep', Key: 'RMB', AtMs: 1400, HoldMs: 150 }] } }));
  const original = readFileSync(path);
  const history = resultHistory(path, 'preparation', '111');
  file('preparation.json.result.json', history); importTrial(store, path);
  const firstHash = JSON.parse(readFileSync(store, 'utf8')).trials[0].resultRevisionHashes[0];
  assert.equal(Object.hasOwn(summarize(store).trials[0].preparation, 'note'), false);
  const withNote = { ...resultHistory(path, 'preparation', '111').revisions[0], at: '2026-09-19T02:00:00.000Z',
    preparationNote: '준비 중 피격', preparationSource: user };
  history.revisions.push(withNote);
  file('preparation.json.result.json', history); importTrial(store, path);
  assert.equal(summarize(store).trials[0].preparation.note, '준비 중 피격');
  assert.equal(summarize(store).trials[0].preparation.outcome, 'unconfirmed');
  annotateTrial(store, 'preparation', file('recording.json', { reason: 'OBS corrected', recording: { actual: 'ON', source: user } }));
  assert.equal(summarize(store).trials[0].preparation.note, '준비 중 피격');
  history.revisions.push({ ...resultHistory(path, 'preparation', '101').revisions[0], at: '2026-09-19T03:00:00.000Z' });
  file('preparation.json.result.json', history); importTrial(store, path);
  assert.equal(summarize(store).trials[0].preparation.note, '준비 중 피격');
  history.revisions.push({ ...withNote, at: '2026-09-19T04:00:00.000Z', preparationNote: '' });
  file('preparation.json.result.json', history); importTrial(store, path);
  const cleared = summarize(store).trials[0];
  assert.equal(cleared.preparation.note, ''); assert.deepEqual(cleared.preparation.source, user);
  assert.equal(cleared.preparation.outcome, 'unconfirmed'); assert.equal(cleared.recordingActual, 'ON');
  assert.deepEqual(readFileSync(path), original);
  assert.equal(JSON.parse(readFileSync(store, 'utf8')).trials[0].resultRevisionHashes[0], firstHash);
  importTrial(store, path); assert.equal(summarize(store).trials[0].revision, cleared.revision);
  correctCharacterFixture(path, '수나'); importTrial(store, path);
  assert.equal(summarize(store).trials[0].preparation.note, '');
  assert.equal(JSON.parse(readFileSync(store, 'utf8')).trials[0].resultRevisionHashes[0], firstHash);
  annotateTrial(store, 'preparation', file('note-only.json', { reason: 'Preparation note corrected', preparationNote: '위치 다름', preparationSource: user }));
  annotateTrial(store, 'preparation', file('actions-only.json', { reason: 'Action corrected', actions: [{ attack: 0, outcome: 'failure', source: user }] }));
  assert.equal(summarize(store).trials[0].preparation.note, '위치 다름');
  assert.equal(validateStore(store).valid, true);
});

test('preparation note schema rejects absent raw preparation, nonstrings, oversized and unsupported sources', t => {
  for (const invalid of [null, 123, {}, 'x'.repeat(501)]) {
    const { file, raw, store } = fixture(t);
    const path = file('trial.json', raw({ TrialId: 'p', ActionIds: ['a', 'b', 'c'], ProfileSnapshot: { Preparation: [{}] } }));
    const history = resultHistory(path, 'p', '111');
    Object.assign(history.revisions[0], { preparationNote: invalid, preparationSource: user });
    file('trial.json.result.json', history);
    assert.throws(() => importTrial(store, path), { code: 'INVALID_DATA' });
  }
  for (const snapshot of [undefined, {}, { Preparation: [] }]) {
    const { file, raw, store } = fixture(t);
    const path = file('legacy.json', raw({ TrialId: 'p', ActionIds: ['a', 'b', 'c'], ProfileSnapshot: snapshot }));
    const history = resultHistory(path, 'p', '111');
    Object.assign(history.revisions[0], { preparationNote: '피격', preparationSource: user });
    file('legacy.json.result.json', history);
    assert.throws(() => importTrial(store, path), { code: 'INVALID_DATA' });
  }
  for (const report of [{ preparationNote: 'note' }, { preparationSource: user },
    { preparationNote: 'note', preparationSource: { kind: 'video', reference: 'clip' } }]) {
    const { file, raw, store } = fixture(t);
    const path = file('trial.json', raw({ TrialId: 'p', ActionIds: ['a', 'b', 'c'], ProfileSnapshot: { Preparation: [{}] } }));
    const history = resultHistory(path, 'p', '111'); Object.assign(history.revisions[0], report);
    file('trial.json.result.json', history);
    assert.throws(() => importTrial(store, path), { code: 'INVALID_DATA' });
  }
});

function resultHistory(path, trialId, ...reports) {
  return { schemaVersion: 1, trialId, rawSha256: createHash('sha256').update(readFileSync(path)).digest('hex'),
    revisions: reports.map((bits, index) => ({ at: `2026-09-19T01:00:0${index}.000Z`, bits, reason: index ? 'User correction' : 'User report',
      actions: [...bits].map((bit, attack) => ({ attack, outcome: bit === '1' ? 'success' : 'failure', source: user })) })) };
}

function obsLedger(recordingId, extra = {}) {
  return { recordingId, observedFromUtc: '2026-09-19T01:00:00.000Z', observedStartedUtc: null, observedStoppedUtc: null,
    continuityLost: false, outputPaths: [], observations: [{ atUtc: '2026-09-19T01:00:00.000Z', state: 'ON', outputPath: null, detail: 'GetRecordStatus' }], ...extra };
}

function correctCharacterFixture(path, after) {
  const text = readFileSync(path, 'utf8');
  const old = JSON.parse(text.replace(/^\uFEFF/, ''));
  const beforeJson = JSON.stringify(old.StartCharacter), afterJson = JSON.stringify(after);
  const marker = '"StartCharacter":';
  const offset = text.indexOf(marker) + marker.length;
  assert.equal(text.slice(offset, offset + beforeJson.length), beforeJson);
  const updated = text.slice(0, offset) + afterJson + text.slice(offset + beforeJson.length);
  const sidecar = JSON.parse(readFileSync(`${path}.result.json`, 'utf8'));
  const digest = value => createHash('sha256').update(value).digest('hex');
  (sidecar.startCharacterCorrections ??= []).push({ at: new Date().toISOString(), beforeRawSha256: digest(text),
    afterRawSha256: digest(updated), before: old.StartCharacter, after, offset, beforeJson, afterJson });
  sidecar.rawSha256 = digest(updated);
  writeFileSync(path, updated); writeFileSync(`${path}.result.json`, JSON.stringify(sidecar));
  return sidecar;
}

test('verified raw character correction reimports same trial and preserves user annotations and results', t => {
  const { file, raw, store } = fixture(t);
  const path = file('trial.json', '\uFEFF' + JSON.stringify(raw({ TrialId: 'character', StartCharacter: '아리아',
    ActionIds: ['a', 'b', 'c'], ProfileSnapshot: { StartCharacter: '아리아' } })) + '\n');
  file('trial.json.result.json', resultHistory(path, 'character', '101'));
  importTrial(store, path);
  annotateTrial(store, 'character', file('note.json', { reason: 'manual OBS correction', recording: { actual: 'ON', source: user } }));
  correctCharacterFixture(path, '수나');
  assert.equal(importTrial(store, path).corrected, true);
  let data = JSON.parse(readFileSync(store, 'utf8'));
  assert.equal(data.trials.length, 1); assert.equal(data.trials[0].metadata.startCharacter, '수나');
  assert.equal(summarize(store).trials[0].recordingActual, 'ON');
  assert.deepEqual(summarize(store).trials[0].actions.map(row => row.outcome), ['success', 'failure', 'success']);
  assert.equal(JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, '')).ProfileSnapshot.StartCharacter, '아리아');
  correctCharacterFixture(path, '다른 캐릭터');
  assert.equal(importTrial(store, path).corrected, true);
  assert.equal(validateStore(store).valid, true);
  const untouchedStore = file('fresh/records.json', { schemaVersion: 1, trials: [], batches: [] });
  importTrial(untouchedStore, path);
  assert.equal(validateStore(untouchedStore).valid, true);
});

test('character correction rejects unrelated raw edits, wrong offset and broken hash chain', t => {
  for (const kind of ['unrelated', 'offset', 'hash']) {
    const { file, raw, store } = fixture(t);
    const path = file('trial.json', raw({ TrialId: 'character', StartCharacter: '아리아', ActionIds: ['a', 'b', 'c'] }));
    file('trial.json.result.json', resultHistory(path, 'character', '111')); importTrial(store, path);
    const sidecar = correctCharacterFixture(path, '수나');
    if (kind === 'unrelated') writeFileSync(path, readFileSync(path, 'utf8').replace('3150', '3350'));
    if (kind === 'offset') sidecar.startCharacterCorrections[0].offset += 1;
    if (kind === 'hash') sidecar.startCharacterCorrections[0].beforeRawSha256 = '0'.repeat(64);
    writeFileSync(`${path}.result.json`, JSON.stringify(sidecar));
    const beforeStore = readFileSync(store);
    assert.throws(() => importTrial(store, path)); assert.deepEqual(readFileSync(store), beforeStore);
  }
});

test('unfinished character transaction blocks import without changing the store', t => {
  const { file, raw, store } = fixture(t);
  const path = file('trial.json', raw({ TrialId: 'pending-correction' }));
  importTrial(store, path); const previous = readFileSync(store);
  file('trial.json.start-character.txn.json', {});
  assert.throws(() => importTrial(store, path), { code: 'TRIAL_CORRECTION_PENDING' });
  assert.deepEqual(readFileSync(store), previous);
});

test('OBS ledger uses actual RecordingId filename and refreshes stop paths without changing raw or user evidence', t => {
  const { root, file, raw, store } = fixture(t);
  const id = 'obs-0123456789abcdef0123456789abcdef';
  const path = file('logs/raw.json', raw({ TrialId: 'obs-trial', ActionIds: ['a', 'b', 'c'], Recording: 'ON',
    ObsLedgerDirectory: '../ledger', ObsObservations: [{ State: 'ON', RecordingId: id }, { State: 'ON', RecordingId: id }] }));
  const original = readFileSync(path);
  file(`ledger/${id}.json`, obsLedger(id));
  importTrial(store, path);
  let trial = summarize(store).trials[0];
  assert.equal(trial.obsRecordings.length, 1);
  assert.equal(trial.obsRecordings[0].ledgerPath, join(root, 'ledger', `${id}.json`));
  assert.equal(trial.obsRecordings[0].status, 'available');
  assert.deepEqual(trial.obsRecordings[0].ledger.outputPaths, []);
  assert.equal(trial.recordingActual, 'ON'); assert.equal(trial.recordingSource.kind, 'obs-websocket');
  assert.ok(trial.actions.every(row => row.outcome === 'unconfirmed'));
  file('logs/raw.json.result.json', resultHistory(path, 'obs-trial', '101'));
  const ended = obsLedger(id, { observedStoppedUtc: '2026-09-19T01:02:00.000Z', continuityLost: true,
    outputPaths: ['C:/Recordings/session-1.mkv', 'C:/Recordings/session-2.mkv'] });
  file(`ledger/${id}.json`, ended);
  assert.equal(validateStore(store).valid, true, 'late stop path is not raw metadata corruption');
  importTrial(store, path);
  trial = summarize(store).trials[0];
  assert.deepEqual(trial.obsRecordings[0].ledger, ended);
  assert.equal(trial.recordingActual, 'ON'); assert.equal(trial.recordingSource.kind, 'obs-websocket');
  assert.deepEqual(trial.videos, []); assert.equal(trial.revision, 1);
  assert.deepEqual(trial.actions.map(row => row.outcome), ['success', 'failure', 'success']);
  assert.deepEqual(readFileSync(path), original); assert.equal(validateStore(store).valid, true);
});

test('OBS absolute ledger directory supports missing, damaged and mismatched ledgers without blocking binary results', t => {
  const { root, file, raw, store } = fixture(t);
  const id = 'obs-absolute';
  const path = file('raw.json', raw({ TrialId: 'obs-trial', ActionIds: ['a', 'b', 'c'],
    ObsLedgerDirectory: join(root, 'ledger'), ObsObservations: [{ State: 'ON', RecordingId: id }] }));
  file('raw.json.result.json', resultHistory(path, 'obs-trial', '010'));
  importTrial(store, path);
  assert.equal(summarize(store).trials[0].obsRecordings[0].status, 'missing');
  for (const invalid of ['{ broken JSON', obsLedger('different-recording'), obsLedger(id, { outputPaths: 'not an array' })]) {
    file(`ledger/${id}.json`, invalid); importTrial(store, path);
    const trial = summarize(store).trials[0];
    assert.equal(trial.obsRecordings[0].status, 'invalid');
    assert.ok(trial.obsRecordings[0].detail); assert.equal(trial.revision, 1);
    assert.deepEqual(trial.actions.map(row => row.outcome), ['failure', 'success', 'failure']);
  }
  file(`ledger/${id}.json`, obsLedger(id)); importTrial(store, path);
  assert.equal(summarize(store).trials[0].obsRecordings[0].status, 'available');
  assert.equal(validateStore(store).valid, true);
});

test('OBS missing directory and invalid recording references are explicit and never guess ledger locations', t => {
  const { file, raw, store } = fixture(t);
  importTrial(store, file('raw.json', raw({ TrialId: 'obs-trial', ObsObservations: [
    { State: 'ON', RecordingId: 'obs-no-directory' }, { State: 'UNKNOWN', RecordingId: '../other' }, { State: 'OFF', RecordingId: null },
  ] })));
  const entries = summarize(store).trials[0].obsRecordings;
  assert.equal(entries.length, 2);
  assert.equal(entries[0].status, 'unavailable'); assert.equal(entries[0].ledgerPath, null);
  assert.equal(entries[1].status, 'invalid'); assert.equal(entries[1].ledgerPath, null);
});

test('OBS OFF plus binary UI sidecar satisfies OFF batch without manual recording annotation', t => {
  const { file, raw, store } = fixture(t);
  const path = file('raw.json', raw({ TrialId: 'off', ActionIds: ['a', 'b', 'c'], Recording: 'OFF',
    ObsObservations: [{ AtUtc: '/Date(1789780000000)/', State: 'OFF' }, { AtUtc: '/Date(1789780010000)/', State: 'OFF' }] }));
  const original = readFileSync(path);
  file('raw.json.result.json', resultHistory(path, 'off', '101')); importTrial(store, path);
  saveBatch(store, file('batch.json', { id: 'off-batch', question: 'Timing with recording OFF', decision: 'Compare timings',
    stopRule: 'Stop after this trial', reason: 'Test observed OFF', conditions: [{ id: 'off-condition', description: 'Recording OFF', preset: { recording: 'OFF' } }],
    trials: [{ trialId: 'off', conditionId: 'off-condition' }] }));
  const trial = summarize(store, 'off-batch').trials[0];
  assert.equal(trial.recordingActual, 'OFF');
  assert.deepEqual(trial.recordingSource, { kind: 'obs-websocket', reference: 'NoticeRecord.ObsObservations' });
  assert.deepEqual(trial.conditionMismatches, []); assert.equal(validateStore(store).valid, true);
  const revision = JSON.parse(readFileSync(store, 'utf8')).trials[0].revisions[0];
  assert.equal(revision.recordingExplicit, false); assert.equal(revision.recording.actual, 'unconfirmed');
  assert.deepEqual(readFileSync(path), original);
});

test('F8 recording stays frozen through later OBS changes, result correction and import', t => {
  const { file, raw, store } = fixture(t);
  for (const [index, [start, later, effective]] of [
    ['ON', 'OFF', 'ON'], ['OFF', 'ON', 'OFF'], ['UNKNOWN', 'ON', 'OFF'], ['PAUSED', 'ON', 'OFF'],
  ].entries()) {
    const path = file(`f8-${index}.json`, raw({ TrialId: `f8-${index}`, ActionIds: ['a', 'b', 'c'],
      Recording: effective, RecordingBasis: 'f8-snapshot-unknown-off', F8Utc: '2026-09-19T09:00:00Z',
      RecordingAtF8: { State: start, AtUtc: '2026-09-19T08:59:59Z' },
      ObsObservations: [{ State: start }, { State: later }] }));
    file(`f8-${index}.json.result.json`, resultHistory(path, `f8-${index}`, '111')); importTrial(store, path);
    let trial = summarize(store).trials.find(row => row.id === `f8-${index}`);
    assert.equal(trial.recordingActual, effective);
    assert.match(trial.recordingSource.reference, /F8/);
    file(`f8-${index}.json.result.json`, resultHistory(path, `f8-${index}`, '111', '101')); importTrial(store, path);
    trial = summarize(store).trials.find(row => row.id === `f8-${index}`);
    assert.equal(trial.recordingActual, effective);
    assert.equal(trial.recordingAtF8.State, start);
  }
  assert.equal(validateStore(store).valid, true);
});

test('invalid F8 recording snapshot or mismatched effective state is rejected', t => {
  const { file, raw, store } = fixture(t);
  const valid = { Recording: 'ON', RecordingBasis: 'f8-snapshot-unknown-off', F8Utc: '2026-09-19T09:00:00Z', RecordingAtF8: { State: 'ON', AtUtc: '2026-09-19T08:59:59Z' } };
  for (const [i, patch] of [{ Recording: 'OFF' }, { F8Utc: null }, { RecordingAtF8: null }, { RecordingAtF8: { State: 'bad' } }].entries())
    assert.throws(() => importTrial(store, file(`bad-f8-${i}.json`, raw({ ...valid, ...patch }))));
});

test('legacy OBS keeps MIXED, treats UNKNOWN as policy OFF and preserves unobserved legacy Recording', t => {
  const { file, raw, store } = fixture(t);
  for (const [index, [states, expected]] of [
    [['ON', 'ON'], 'ON'], [['OFF', 'OFF'], 'OFF'], [['ON', 'OFF'], 'MIXED'], [['PAUSED'], 'MIXED'],
    [['ON', 'UNKNOWN'], 'OFF'], [[], 'OFF'], [['invalid-state'], 'OFF'], [[null], 'OFF'],
  ].entries()) {
    const path = file(`${index}.json`, raw({ TrialId: `obs-${index}`, Recording: 'OFF', ObsObservations: states.map(State => State == null ? null : { State }) }));
    importTrial(store, path);
    const trial = summarize(store).trials.find(row => row.id === `obs-${index}`);
    assert.equal(trial.recordingActual, expected); assert.equal(trial.recordingSource.kind, index >= 4 ? 'user-policy' : 'obs-websocket');
  }
  for (const Recording of ['ON', 'OFF']) {
    importTrial(store, file(`legacy-${Recording}.json`, raw({ TrialId: `legacy-${Recording}`, Recording })));
    const trial = summarize(store).trials.find(row => row.id === `legacy-${Recording}`);
    assert.equal(trial.recordingActual, 'unconfirmed'); assert.equal(trial.recordingSource, null);
  }
});

test('manual recording overrides OBS and explicit unconfirmed remains through UI correction history', t => {
  const { file, raw, store } = fixture(t);
  const path = file('raw.json', raw({ TrialId: 'manual', ActionIds: ['a', 'b', 'c'], ObsObservations: [{ State: 'OFF' }] }));
  file('raw.json.result.json', resultHistory(path, 'manual', '101')); importTrial(store, path);
  annotateTrial(store, 'manual', file('manual-on.json', { reason: 'Participant corrected observed state', recording: { actual: 'ON', source: user } }));
  let trial = summarize(store).trials[0];
  assert.equal(trial.recordingActual, 'ON'); assert.deepEqual(trial.recordingSource, user);
  annotateTrial(store, 'manual', file('uncertain.json', { reason: 'Participant withdrew recording certainty', recording: { actual: 'unconfirmed' } }));
  file('raw.json.result.json', resultHistory(path, 'manual', '101', '110')); importTrial(store, path);
  trial = summarize(store).trials[0];
  assert.equal(trial.recordingActual, 'unconfirmed'); assert.equal(trial.recordingSource, null);
  assert.equal(trial.obsRecording.actual, 'OFF');
  assert.deepEqual(trial.actions.map(row => row.outcome), ['success', 'success', 'failure']);
  const history = JSON.parse(readFileSync(store, 'utf8')).trials[0].revisions;
  assert.deepEqual(history.map(row => row.recordingExplicit), [false, true, true, true]);
  assert.deepEqual(history.map(row => row.recording.actual), ['unconfirmed', 'ON', 'unconfirmed', 'unconfirmed']);
});

test('initial explicit unconfirmed is distinguishable from an omitted recording annotation', t => {
  const { file, raw, store } = fixture(t);
  const path = file('raw.json', raw({ TrialId: 'uncertain', ObsObservations: [{ State: 'ON' }] }));
  importTrial(store, path);
  annotateTrial(store, 'uncertain', file('uncertain.json', { reason: 'Recording is not confirmed', recording: { actual: 'unconfirmed' } }));
  assert.equal(summarize(store).trials[0].recordingActual, 'unconfirmed');
  annotateTrial(store, 'uncertain', file('action.json', { reason: 'First action reported', actions: [{ attack: 0, outcome: 'success', source: user }] }));
  assert.equal(summarize(store).trials[0].recordingActual, 'unconfirmed');
});

test('UI result sidecar imports full history, reapplies only new corrections and keeps raw bytes', t => {
  const { file, raw, store } = fixture(t);
  const path = file('raw.json', raw({ TrialId: 'ui', ActionIds: ['first', 'second', 'third'], Party: 'party A', StartCharacter: 'character B' }));
  const original = readFileSync(path);
  file('raw.json.result.json', resultHistory(path, 'ui', '101'));
  importTrial(store, path);
  let trial = summarize(store).trials[0];
  assert.deepEqual(trial.actions.map(row => row.outcome), ['success', 'failure', 'success']);
  assert.equal(trial.party, 'party A'); assert.equal(trial.startCharacter, 'character B');
  const sha = trial.raw.sha256;
  annotateTrial(store, 'ui', file('manual.json', { reason: 'Recording confirmed separately', recording: { actual: 'OFF', source: user } }));
  importTrial(store, path);
  assert.equal(summarize(store).trials[0].revision, 2);
  file('raw.json.result.json', resultHistory(path, 'ui', '101', '110'));
  importTrial(store, path);
  trial = summarize(store).trials[0];
  assert.equal(trial.revision, 3); assert.equal(trial.recordingActual, 'OFF');
  assert.deepEqual(trial.actions.map(row => row.outcome), ['success', 'success', 'failure']);
  assert.ok(trial.actions.every(row => row.source.kind === 'user-report'));
  assert.equal(trial.raw.sha256, sha); assert.deepEqual(readFileSync(path), original);
  assert.equal(validateStore(store).valid, true);
  const revisions = JSON.parse(readFileSync(store, 'utf8')).trials[0].revisions;
  assert.equal(revisions[0].actions[1].outcome, 'failure');
  importTrial(store, path); assert.equal(summarize(store).trials[0].revision, 3);
});

test('UI result sidecar rejects another trial, changed raw hash, malformed bits, observation and changed history', t => {
  const { file, raw, store } = fixture(t);
  const path = file('raw.json', raw({ TrialId: 'ui', ActionIds: ['a', 'b', 'c'] }));
  const good = resultHistory(path, 'ui', '101');
  for (const bad of [resultHistory(path, 'other', '101'), { ...good, rawSha256: 'wrong' }, resultHistory(path, 'ui', '10'), resultHistory(path, 'ui', '10x')]) {
    file('raw.json.result.json', bad);
    assert.throws(() => importTrial(store, path), { code: 'INVALID_DATA' });
    assert.equal(summarize(store).trials.length, 0);
  }
  file('raw.json.result.json', good); importTrial(store, path);
  file('raw.json.result.json', resultHistory(path, 'ui', '111'));
  assert.throws(() => importTrial(store, path), { code: 'INVALID_DATA' });
  assert.equal(summarize(store).trials[0].revision, 1);
  const observed = file('observe.json', raw({ TrialId: 'observe', Status: 'observed-no-input', ActionIds: ['a', 'b', 'c'] }));
  file('observe.json.result.json', resultHistory(observed, 'observe', '111'));
  assert.throws(() => importTrial(store, observed), { code: 'INVALID_DATA' });
});

test('UI results deleted raw is not restored and profile snapshot metadata is preserved', t => {
  const { file, raw, store } = fixture(t);
  const path = file('raw.json', raw({ TrialId: 'ui', ActionIds: ['a', 'b', 'c'], ProfileSnapshot: { Party: 'snapshot party', StartCharacter: 'snapshot character' } }));
  file('raw.json.result.json', resultHistory(path, 'ui', '010'));
  importTrial(store, path);
  assert.equal(summarize(store).trials[0].party, 'snapshot party');
  assert.equal(summarize(store).trials[0].startCharacter, 'snapshot character');
  rmSync(path);
  assert.throws(() => importTrial(store, path), { code: 'MISSING_FILE' });
  assert.deepEqual(validateStore(store).issues.map(issue => issue.code), ['MISSING_RAW']);
});

test('legacy completed/input insertion/raw GameOutcome do not imply any game success', t => {
  const { file, raw, store } = fixture(t);
  const path = file('raw.json', raw());
  const original = readFileSync(path);
  const result = importTrial(store, path);
  assert.match(result.id, /^legacy-[a-f0-9]{64}$/);
  const summary = summarize(store);
  assert.equal(summary.empiricalDecision, 'manual-review-required');
  assert.equal(summary.trials[0].recordingActual, 'unconfirmed');
  assert.equal(summary.trials[0].allActionsReportedSuccess, false);
  assert.deepEqual(summary.trials[0].actions.map(row => row.outcome), ['unconfirmed', 'unconfirmed', 'unconfirmed']);
  assert.equal(summary.trials[0].actions[1].path, 'upstream-unconfirmed');
  assert.equal(validateStore(store).valid, true);
  assert.deepEqual(readFileSync(path), original);
});

test('exact duplicate imports are idempotent; hash and TrialId conflicts are explicit', t => {
  const { file, raw, store } = fixture(t);
  const first = file('first.json', raw({ TrialId: 'stable-guid', ProfileId: 'vesper-02', ProfileHash: 'profile-hash',
    ActionIds: ['entry', 'support-1', 'support-2'], ActionKeys: ['RMB', 'SPACE', 'SPACE'] }));
  assert.deepEqual(importTrial(store, first), { id: 'stable-guid', duplicate: false });
  assert.deepEqual(importTrial(store, file('copy.json', readFileSync(first, 'utf8'))), { id: 'stable-guid', duplicate: true });
  assert.throws(() => importTrial(store, first, { trialId: 'other-id' }), { code: 'TRIAL_CONFLICT' });
  assert.throws(() => importTrial(store, file('changed.json', raw({ TrialId: 'stable-guid', Recording: 'ON' }))), { code: 'TRIAL_CONFLICT' });
  const summary = summarize(store);
  assert.equal(summary.trials.length, 1);
  assert.equal(summary.trials[0].profileId, 'vesper-02');
  assert.equal(summary.trials[0].actions[0].id, 'entry');
});

test('legacy duplicate bytes cannot acquire a second explicit trial ID', t => {
  const { file, raw, store } = fixture(t);
  const path = file('raw.json', raw());
  importTrial(store, path, { trialId: 't1' });
  assert.equal(importTrial(store, path).id, 't1');
  assert.throws(() => importTrial(store, path, { trialId: 't2' }), { code: 'TRIAL_CONFLICT' });
});

test('active raw logs are rejected before import and the same TrialId can be imported after completion', t => {
  const { file, raw, store } = fixture(t);
  for (const Status of ['armed', 'detected']) {
    const path = file('active.json', raw({ TrialId: 'active-trial', Status }));
    assert.throws(() => importTrial(store, path), { code: 'ACTIVE_TRIAL' });
    assert.equal(summarize(store).trials.length, 0);
  }
  assert.throws(() => importTrial(store, file('unknown.json', raw({ TrialId: 'unknown-trial', Status: 'pending' }))), { code: 'INVALID_DATA' });
  const finalPath = file('active.json', raw({ TrialId: 'active-trial', Status: 'submitted-not-game-verified' }));
  assert.deepEqual(importTrial(store, finalPath), { id: 'active-trial', duplicate: false });
  assert.equal(validateStore(store).valid, true);
  assert.equal(summarize(store).trials[0].allActionsReportedSuccess, false);
});

test('finalized failure and cancellation logs remain importable without success inference', t => {
  const { file, raw, store } = fixture(t);
  for (const Status of ['failed', 'cancelled', 'release-failed', 'observed-no-input']) {
    importTrial(store, file(`${Status}.json`, raw({ TrialId: Status, Status })));
  }
  assert.equal(summarize(store).trials.length, 4);
  assert.ok(summarize(store).trials.every(trial => !trial.allActionsReportedSuccess));
});

test('recording correction preserves logged OFF, reason, old annotation and raw bytes', t => {
  const { file, raw, store } = fixture(t);
  const path = file('raw.json', raw());
  const original = readFileSync(path);
  importTrial(store, path, { trialId: 't1' });
  annotateTrial(store, 't1', file('first-note.json', note()));
  const correction = file('correction.json', { reason: 'User corrected the actual recording state', recording: { actual: 'ON', source: user } });
  assert.equal(annotateTrial(store, 't1', correction).revision, 2);
  assert.equal(annotateTrial(store, 't1', correction).duplicate, true);
  const result = summarize(store).trials[0];
  assert.equal(result.recordingLogged, 'OFF'); assert.equal(result.recordingActual, 'ON'); assert.equal(result.recordingDiffers, true);
  assert.equal(result.actions[0].outcome, 'success'); assert.deepEqual(result.actions[0].source, user);
  const revisions = JSON.parse(readFileSync(store, 'utf8')).trials[0].revisions;
  assert.equal(revisions.length, 2); assert.equal(revisions[0].recording.actual, 'OFF');
  assert.match(revisions[1].reason, /corrected/);
  assert.deepEqual(readFileSync(path), original);
});

test('mixed outcomes preserve earlier evidence and label downstream altered path', t => {
  const { file, raw, store } = fixture(t);
  importTrial(store, file('raw.json', raw({ DelaysMs: [3100, 4400, 5900, 7600] })), { trialId: 't1' });
  annotateTrial(store, 't1', file('note.json', note({ actions: [
    { attack: 0, outcome: 'success', source: user },
    { attack: 1, outcome: 'failure', source: user },
    { attack: 2, outcome: 'success', source: user },
    { attack: 3, outcome: 'not-evaluable', source: user, note: 'sequence interrupted' },
  ] })));
  const trial = summarize(store).trials[0];
  assert.deepEqual(trial.actions.map(row => row.path), ['normal', 'normal', 'after-failure', 'after-failure']);
  assert.equal(trial.actions[2].outcome, 'success'); assert.equal(trial.allActionsReportedSuccess, false);
});

test('annotation rejects source-less success or invalid action; only explicit unconfirmed clears a result', t => {
  const { file, raw, store } = fixture(t);
  importTrial(store, file('raw.json', raw()), { trialId: 't1' });
  for (const actions of [[{ attack: 0, outcome: 'success' }], [{ attack: 4, outcome: 'success', source: user }],
    [{ attack: 0, outcome: 'success', source: user }, { attack: 0, outcome: 'failure', source: user }]]) {
    assert.throws(() => annotateTrial(store, 't1', file('bad.json', note({ actions }))), { code: 'INVALID_DATA' });
  }
  annotateTrial(store, 't1', file('good.json', note()));
  assert.throws(() => annotateTrial(store, 't1', file('empty.json', { reason: 'No actual update', actions: [] })), { code: 'INVALID_DATA' });
  assert.throws(() => annotateTrial(store, 't1', file('empty.json', { reason: 'No actual update' })), { code: 'INVALID_DATA' });
  annotateTrial(store, 't1', file('withdrawn.json', { reason: 'Wrong trial was labelled', actions: [{ attack: 0, outcome: 'unconfirmed' }] }));
  assert.deepEqual(summarize(store).trials[0].actions.map(row => row.outcome), ['unconfirmed', 'unconfirmed', 'unconfirmed']);
});

test('single-action correction preserves other results, sources, recording and video references', t => {
  const { file, raw, store } = fixture(t);
  importTrial(store, file('raw.json', raw()), { trialId: 't1' });
  file('media/clip.mp4', 'synthetic video fixture');
  const videoSource = { kind: 'video', reference: 'clip.mp4 at 3.15s' };
  annotateTrial(store, 't1', file('initial.json', note({ videos: ['media/clip.mp4'], actions: [
    { attack: 0, outcome: 'success', source: videoSource },
    { attack: 1, outcome: 'success', source: user },
    { attack: 2, outcome: 'success', source: user },
  ] })));
  annotateTrial(store, 't1', file('correction.json', { reason: 'Second action was a failure', actions: [{ attack: 1, outcome: 'failure', source: user }] }));
  const result = summarize(store).trials[0];
  assert.deepEqual(result.actions.map(row => row.outcome), ['success', 'failure', 'success']);
  assert.deepEqual(result.actions[0].source, videoSource);
  assert.deepEqual(result.actions[2].source, user);
  assert.equal(result.recordingActual, 'OFF');
  assert.deepEqual(result.videos, ['../media/clip.mp4']);
  annotateTrial(store, 't1', file('clear.json', { reason: 'Third action is not certain', actions: [{ attack: 2, outcome: 'unconfirmed' }] }));
  assert.deepEqual(summarize(store).trials[0].actions.map(row => row.outcome), ['success', 'failure', 'unconfirmed']);
  const revisions = JSON.parse(readFileSync(store, 'utf8')).trials[0].revisions;
  assert.equal(revisions.length, 3);
  assert.deepEqual(revisions[0].actions.map(row => row.outcome), ['success', 'success', 'success']);
  assert.equal(revisions[2].actions.length, 3);
});

test('initial partial annotation defaults omitted fields to unconfirmed or empty', t => {
  const { file, raw, store } = fixture(t);
  importTrial(store, file('raw.json', raw()), { trialId: 't1' });
  annotateTrial(store, 't1', file('initial.json', { reason: 'Only entry result known', actions: [{ attack: 0, outcome: 'success', source: user }] }));
  const result = summarize(store).trials[0];
  assert.equal(result.recordingActual, 'unconfirmed'); assert.deepEqual(result.videos, []);
  assert.deepEqual(result.actions.map(row => row.outcome), ['success', 'unconfirmed', 'unconfirmed']);
});

test('relative file references validate missing raw, changed raw hash and missing video', t => {
  const { file, raw, store } = fixture(t);
  const path = file('raw.json', raw());
  const video = file('media/clip.mp4', 'synthetic video fixture');
  importTrial(store, path, { trialId: 't1' });
  annotateTrial(store, 't1', file('note.json', note({ videos: ['media/clip.mp4'], actions: [{ attack: 0, outcome: 'success', source: { kind: 'video', reference: 'clip.mp4 at 3.15s' } }] })));
  const trial = summarize(store).trials[0];
  assert.equal(trial.raw.path, '../raw.json'); assert.equal(trial.videos[0], '../media/clip.mp4');
  assert.equal(validateStore(store).valid, true);
  writeFileSync(path, JSON.stringify(raw({ Recording: 'ON' })));
  assert.deepEqual(validateStore(store).issues.map(issue => issue.code), ['RAW_HASH_CHANGED']);
  rmSync(path); rmSync(video);
  assert.deepEqual(validateStore(store).issues.map(issue => issue.code), ['MISSING_RAW', 'MISSING_VIDEO']);
});

test('video evidence requires an existing file reference', t => {
  const { file, raw, store } = fixture(t);
  importTrial(store, file('raw.json', raw()), { trialId: 't1' });
  assert.throws(() => annotateTrial(store, 't1', file('bad.json', note({ videos: ['missing.mp4'] }))), { code: 'INVALID_DATA' });
  assert.throws(() => annotateTrial(store, 't1', file('bad.json', note({ actions: [{ attack: 0, outcome: 'success', source: { kind: 'video', reference: 'unlinked clip' } }] }))), { code: 'INVALID_DATA' });
});

test('batch history connects trial conditions without automatically passing an empirical gate', t => {
  const { file, raw, store } = fixture(t);
  const batch = { id: 'b1', question: 'Does entry timing affect the next action?', decision: 'Choose tested interval', stopRule: 'Stop if tested endpoints hold; isolate contradictions', reason: 'initial plan',
    conditions: [{ id: 'A', description: 'earlier entry, recording OFF', preset: { recording: 'OFF', delaysMs: [3150, 4450, 5950] } }], trials: [] };
  saveBatch(store, file('batch.json', batch));
  importTrial(store, file('raw.json', raw()), { trialId: 't1' });
  annotateTrial(store, 't1', file('note.json', note({ actions: [0, 1, 2].map(attack => ({ attack, outcome: 'success', source: user })) })));
  const next = { ...batch, reason: 'Attach completed attempt', trials: [{ trialId: 't1', conditionId: 'A' }] };
  assert.equal(saveBatch(store, file('batch.json', next)).revision, 2);
  const result = summarize(store, 'b1');
  assert.equal(result.trials[0].allActionsReportedSuccess, true);
  assert.deepEqual(result.trials[0].conditionMismatches, []);
  assert.equal(result.empiricalDecision, 'manual-review-required');
  assert.equal(validateStore(store).valid, true);
  annotateTrial(store, 't1', file('correction.json', note({ reason: 'Recording was actually ON', recording: { actual: 'ON', source: user } })));
  assert.deepEqual(summarize(store, 'b1').trials[0].conditionMismatches, ['recording']);
  assert.equal(validateStore(store).issues[0].code, 'CONDITION_MISMATCH');
});

test('CLI works outside repo and returns structured errors with failing exit status', t => {
  const { root, store } = fixture(t);
  const cli = fileURLToPath(new URL('./records.mjs', import.meta.url));
  const bad = spawnSync(process.execPath, [cli, 'import', '--store', store, '--raw', 'missing.json'], { cwd: root, encoding: 'utf8' });
  assert.equal(bad.status, 1); assert.equal(JSON.parse(bad.stdout).error.code, 'MISSING_FILE');
  const missing = spawnSync(process.execPath, [cli, 'validate', '--store', store], { cwd: root, encoding: 'utf8' });
  assert.equal(missing.status, 1); assert.equal(JSON.parse(missing.stdout).issues[0].code, 'MISSING_STORE');
  const help = spawnSync(process.execPath, [cli, 'help'], { cwd: root, encoding: 'utf8' });
  assert.equal(help.status, 0); assert.equal(JSON.parse(help.stdout).ok, true);
});
