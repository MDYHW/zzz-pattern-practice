import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync, renameSync, unlinkSync, openSync, closeSync, statSync } from 'node:fs';
import { dirname, resolve, relative, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';

const OUTCOMES = ['success', 'failure', 'unconfirmed', 'not-evaluable'];
const RECORDING = ['ON', 'OFF', 'unconfirmed'];
const FINAL_STATUSES = ['completed', 'submitted-not-game-verified', 'observed-no-input', 'failed', 'cancelled', 'release-failed'];
const schemaVersion = 1;
const defaultStore = resolve(dirname(fileURLToPath(import.meta.url)), '../../.local/experiments/records.json');

function fail(code, message) { throw Object.assign(new Error(message), { code }); }
function requireValue(condition, message) { if (!condition) fail('INVALID_DATA', message); }
function object(value, name) { requireValue(value && typeof value === 'object' && !Array.isArray(value), `${name} must be an object`); }
function nonempty(value, name) { requireValue(typeof value === 'string' && value.trim().length > 0, `${name} must be a nonempty string`); }
function keys(value, allowed, name) {
  object(value, name);
  for (const key of Object.keys(value)) requireValue(allowed.includes(key), `Unknown ${name} field: ${key}`);
}
function parseJson(bytes, path) {
  try { return JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, '')); }
  catch (error) { fail('INVALID_JSON', `${path}: ${error.message}`); }
}
function readBytes(path) {
  try { return readFileSync(path); }
  catch (error) { fail(error.code === 'ENOENT' ? 'MISSING_FILE' : 'INVALID_JSON', `${path}: ${error.message}`); }
}
function readJson(path) { return parseJson(readBytes(path), path); }
function hash(bytes) { return createHash('sha256').update(bytes).digest('hex'); }
function portable(base, file) {
  const result = relative(dirname(base), resolve(file));
  requireValue(!isAbsolute(result), 'Files must be on the same filesystem volume as the store');
  return result.replaceAll('\\', '/');
}
function fileAt(store, path) { return resolve(dirname(store), path); }
function load(store) {
  if (!existsSync(store)) return { schemaVersion, trials: [], batches: [] };
  const data = readJson(store);
  requireValue(data.schemaVersion === schemaVersion && Array.isArray(data.trials) && Array.isArray(data.batches), 'Unsupported records store');
  return data;
}
function mutate(store, update) {
  store = resolve(store);
  mkdirSync(dirname(store), { recursive: true });
  const lock = `${store}.lock`;
  let fd;
  try { fd = openSync(lock, 'wx'); }
  catch (error) { fail(error.code === 'EEXIST' ? 'STORE_LOCKED' : error.code, `Cannot lock ${store}: ${error.message}`); }
  const temp = `${store}.${process.pid}.tmp`;
  try {
    const data = load(store);
    const result = update(data);
    writeFileSync(temp, `${JSON.stringify(data, null, 2)}\n`, { flag: 'wx' });
    renameSync(temp, store);
    return result;
  } finally {
    if (existsSync(temp)) unlinkSync(temp);
    closeSync(fd);
    unlinkSync(lock);
  }
}

function obsRecordings(raw, rawPath) {
  const observations = Array.isArray(raw.ObsObservations) ? raw.ObsObservations : [];
  const ids = [...new Set(observations.map(row => row?.RecordingId).filter(id => id != null && id !== ''))];
  return ids.map(recordingId => {
    const entry = { recordingId, ledgerPath: null, status: 'unavailable', ledger: null };
    if (typeof recordingId !== 'string' || !/^[a-zA-Z0-9_-]+$/.test(recordingId))
      return { ...entry, status: 'invalid', detail: 'Invalid recording ID' };
    if (typeof raw.ObsLedgerDirectory !== 'string' || !raw.ObsLedgerDirectory.trim())
      return { ...entry, detail: 'Raw trial does not specify an OBS ledger directory' };
    entry.ledgerPath = resolve(dirname(rawPath), raw.ObsLedgerDirectory, `${recordingId}.json`);
    try {
      const ledger = readJson(entry.ledgerPath);
      object(ledger, 'OBS ledger');
      requireValue(ledger.recordingId === recordingId, 'OBS ledger recording ID differs from raw observation');
      nonempty(ledger.observedFromUtc, 'OBS ledger observedFromUtc');
      requireValue(typeof ledger.continuityLost === 'boolean', 'OBS ledger continuityLost must be boolean');
      requireValue(Array.isArray(ledger.outputPaths) && ledger.outputPaths.every(path => typeof path === 'string' && path.trim()), 'OBS ledger outputPaths must contain path strings');
      requireValue(Array.isArray(ledger.observations) && ledger.observations.every(row => row && typeof row === 'object' && typeof row.atUtc === 'string' && typeof row.state === 'string'), 'OBS ledger observations are invalid');
      for (const name of ['observedFromUtc', 'observedStartedUtc', 'observedStoppedUtc'])
        requireValue(ledger[name] == null || (typeof ledger[name] === 'string' && Number.isFinite(Date.parse(ledger[name]))), `Invalid OBS ledger ${name}`);
      return { ...entry, status: 'available', ledger };
    } catch (error) {
      return { ...entry, status: error.code === 'MISSING_FILE' ? 'missing' : 'invalid', detail: error.message };
    }
  });
}

function observedRecording(raw) {
  if (raw.RecordingBasis != null) {
    requireValue(raw.RecordingBasis === 'f8-snapshot-unknown-off', 'Unsupported recording basis');
    requireValue(typeof raw.F8Utc === 'string' && Number.isFinite(Date.parse(raw.F8Utc)), 'F8 recording requires its capture time');
    const observed = raw.RecordingAtF8?.State;
    requireValue(['ON', 'OFF', 'UNKNOWN', 'PAUSED'].includes(observed), 'F8 recording snapshot is invalid');
    const actual = observed === 'ON' ? 'ON' : 'OFF';
    requireValue(raw.Recording === actual, 'F8 recording differs from effective policy state');
    return { actual, source: { kind: observed === 'UNKNOWN' || observed === 'PAUSED' ? 'user-policy' : 'obs-websocket',
      reference: `NoticeRecord.RecordingAtF8 at ${raw.F8Utc}; f8-snapshot-unknown-off; observed ${observed}` } };
  }
  if (!Object.hasOwn(raw, 'ObsObservations')) return null;
  const rows = raw.ObsObservations;
  let actual = 'UNKNOWN';
  if (Array.isArray(rows) && rows.length > 0 && rows.every(row => row && ['ON', 'OFF', 'PAUSED'].includes(row.State))) {
    const states = new Set(rows.map(row => row.State));
    actual = states.has('PAUSED') || states.size > 1 ? 'MIXED' : rows[0].State;
  }
  if (actual === 'UNKNOWN') return { actual: 'OFF', source: { kind: 'user-policy', reference: 'Legacy NoticeRecord.ObsObservations UNKNOWN treated as OFF by user policy; no F8 snapshot' } };
  return { actual, source: { kind: 'obs-websocket', reference: 'NoticeRecord.ObsObservations' } };
}

function noticeOriginMs(raw) {
  const sample = raw.Samples?.[raw.FirstMatchIndex];
  if (Number.isFinite(sample?.CaptureEndMs)) return sample.CaptureEndMs;
  if (Array.isArray(raw.DueMs) && raw.DueMs.length && raw.DueMs.length === raw.DelaysMs.length) {
    const origins = raw.DueMs.map((due, index) => due - raw.DelaysMs[index]);
    if (origins.every(Number.isFinite) && origins.every(value => Math.abs(value - origins[0]) < 1e-6)) return origins[0];
  }
  return null;
}
function startInputMetadata(raw) {
  const planned = raw.ProfileSnapshot?.StartInputs ?? [];
  const inputs = raw.StartInputs ?? [];
  requireValue(Array.isArray(planned) && Array.isArray(inputs), 'StartInputs plan and events must be arrays');
  if (!planned.length) {
    requireValue(!inputs.length, 'StartInputs events require a plan');
    return null;
  }
  requireValue(raw.ProfileSnapshot.SchemaVersion === 3 && raw.ProfileSnapshot.StartAttack == null,
    'StartInputs require SchemaVersion 3 without StartAttack');
  requireValue(planned.length <= 16 && new Set(planned.map(row => row?.Id)).size === planned.length,
    'StartInputs require at most 16 unique rows');
  for (const row of planned) {
    requireValue(row && typeof row.Id === 'string' && row.Id.length && ['W', 'RMB'].includes(row.Key), 'Invalid StartInputs row');
    requireValue(Number.isFinite(row.AtMs) && row.AtMs >= 100 && row.AtMs % 50 === 0
      && Number.isFinite(row.HoldMs) && row.HoldMs >= 50 && row.HoldMs <= 2000 && row.HoldMs % 50 === 0,
    'Invalid StartInputs timing');
  }
  const virtual = ['observe', 'observe-only'].includes(raw.Mode);
  const completed = ['completed', 'submitted-not-game-verified', 'observed-no-input'].includes(raw.Status);
  const byRow = new Map();
  for (const event of inputs) {
    requireValue(event && Number.isInteger(event.Attack) && event.Attack >= 0 && event.Attack < planned.length,
      'Invalid StartInputs event row');
    const row = planned[event.Attack];
    requireValue(event.Key === row.Key && (virtual ? ['virtual-down', 'virtual-up'] : ['down', 'up']).includes(event.Action),
      'StartInputs event key or action differs from plan');
    requireValue([event.DueMs, event.BeginMs, event.EndMs].every(Number.isFinite)
      && event.DueMs <= event.BeginMs && event.BeginMs <= event.EndMs, 'Invalid StartInputs event timing');
    const prior = byRow.get(event.Attack) ?? [];
    const down = event.Action.endsWith('down');
    requireValue(down ? !prior.length && event.DueMs === row.AtMs
      : prior.length > 0 && prior[0].Action.endsWith('down')
        && (prior.length === 1 || (!completed && prior.at(-1).Inserted !== 1)),
    'StartInputs must record one down followed by release attempts');
    requireValue(down || event.DueMs >= (completed ? row.AtMs + row.HoldMs : prior.at(-1).EndMs),
      'StartInputs release precedes its hold or interrupted event');
    prior.push(event); byRow.set(event.Attack, prior);
  }
  if (completed) requireValue(planned.every((_, index) => byRow.get(index)?.length === 2),
    'Completed StartInputs require down and up events for each row');
  return { planned, inputs, timeOrigin: 'f8-monitor-start', outcome: 'unconfirmed' };
}

function auxiliaryInputMetadata(raw) {
  const planned = raw.ProfileSnapshot?.AuxiliaryInputs;
  const inputs = raw.AuxiliaryInputs ?? [];
  if (planned == null) {
    requireValue(Array.isArray(inputs) && inputs.length === 0, 'AuxiliaryInputs events require a ProfileSnapshot.AuxiliaryInputs plan');
    return null;
  }
  requireValue(Array.isArray(planned), 'ProfileSnapshot.AuxiliaryInputs must be an array');
  requireValue(Array.isArray(inputs), 'AuxiliaryInputs must be an array');
  if (!planned.length) {
    requireValue(inputs.length === 0, 'AuxiliaryInputs events require a nonempty plan');
    return null;
  }
  requireValue([2, 3].includes(raw.ProfileSnapshot.SchemaVersion), 'AuxiliaryInputs require ProfileSnapshot SchemaVersion 2 or 3');
  requireValue(planned.length <= 32, 'ProfileSnapshot.AuxiliaryInputs must contain at most 32 rows');
  const ids = new Set();
  for (const [index, row] of planned.entries()) {
    keys(row, ['Id', 'Label', 'Key', 'AtMs', 'HoldMs', 'Enabled'], `auxiliary input plan row ${index}`);
    nonempty(row.Id, `auxiliary input plan row ${index}.Id`);
    nonempty(row.Label, `auxiliary input plan row ${index}.Label`);
    requireValue(!ids.has(row.Id), 'Auxiliary input plan IDs must be unique'); ids.add(row.Id);
    requireValue(['Space', 'RMB', 'LMB'].includes(row.Key), `Invalid auxiliary input key at row ${index}`);
    requireValue(Number.isFinite(row.AtMs) && row.AtMs >= 100 && row.AtMs % 50 === 0,
      `Invalid auxiliary input AtMs at row ${index}`);
    requireValue(Number.isFinite(row.HoldMs) && row.HoldMs >= 50 && row.HoldMs % 50 === 0 && row.AtMs + row.HoldMs <= 120000,
      `Invalid auxiliary input HoldMs at row ${index}`);
    requireValue(typeof row.Enabled === 'boolean', `Invalid auxiliary input Enabled at row ${index}`);
  }
  const origin = inputs.length ? noticeOriginMs(raw) : null;
  requireValue(!inputs.length || origin != null, 'AuxiliaryInputs timing requires a recorded notice origin');
  const byRow = new Map();
  const virtual = ['observe', 'observe-only'].includes(raw.Mode);
  for (const [index, event] of inputs.entries()) {
    keys(event, ['Action', 'Key', 'Attack', 'DueMs', 'BeginMs', 'EndMs', 'Inserted', 'Win32Error'], `auxiliary input event ${index}`);
    requireValue(Number.isInteger(event.Attack) && event.Attack >= 0 && event.Attack < planned.length, `Invalid auxiliary input source row at event ${index}`);
    const row = planned[event.Attack];
    requireValue(row.Enabled, `Auxiliary input event ${index} refers to a disabled row`);
    requireValue(event.Key === row.Key, `Auxiliary input event ${index} key differs from its plan row`);
    const allowedActions = virtual ? ['virtual-down', 'virtual-up'] : ['down', 'up'];
    requireValue(allowedActions.includes(event.Action), `Invalid auxiliary input action at event ${index}`);
    for (const name of ['DueMs', 'BeginMs', 'EndMs']) requireValue(Number.isFinite(event[name]), `Invalid auxiliary input ${name} at event ${index}`);
    requireValue(event.DueMs <= event.BeginMs && event.BeginMs <= event.EndMs, `Auxiliary input event ${index} has invalid due/begin/end order`);
    requireValue(Number.isInteger(event.Inserted) && Number.isInteger(event.Win32Error), `Invalid auxiliary input submission result at event ${index}`);
    const direction = event.Action.endsWith('down') ? 'down' : 'up';
    const rows = byRow.get(event.Attack) ?? [];
    const interruptedRelease = direction === 'up' && !['completed', 'submitted-not-game-verified', 'observed-no-input'].includes(raw.Status);
    const sequenceMatches = rows.length === 0 ? direction === 'down'
      : rows.length === 1 ? rows[0].direction === 'down' && direction === 'up'
      : interruptedRelease && !virtual && rows.length === 2 && rows[1].direction === 'up' && rows[1].event.Inserted !== 1 && direction === 'up';
    requireValue(sequenceMatches, `Auxiliary input row ${event.Attack} must record one down followed by release attempts`);
    const down = rows[0]?.event;
    const earliestRelease = down ? down.EndMs + row.HoldMs : NaN;
    const timingMatches = direction === 'down'
      ? Math.abs(event.DueMs - (origin + row.AtMs)) < 1e-6
      : interruptedRelease ? event.DueMs >= (rows.at(-1)?.event.EndMs ?? down.DueMs)
        : event.DueMs >= earliestRelease;
    requireValue(timingMatches,
      `Auxiliary input event ${index} timing differs from its plan row`);
    rows.push({ direction, event }); byRow.set(event.Attack, rows);
  }
  if (['completed', 'submitted-not-game-verified', 'observed-no-input'].includes(raw.Status)) {
    for (const [index, row] of planned.entries())
      requireValue(!row.Enabled || byRow.get(index)?.length === 2, `Completed auxiliary input row ${index} requires down and up events`);
  }
  return { planned, inputs, outcome: 'unconfirmed' };
}

function rawMetadata(raw, rawPath) {
  object(raw, 'NoticeRecord');
  nonempty(raw.Version, 'Version');
  nonempty(raw.Skill, 'Skill');
  nonempty(raw.Status, 'Status');
  requireValue(Array.isArray(raw.Inputs), 'Inputs must be an array');
  requireValue(Array.isArray(raw.DelaysMs) && raw.DelaysMs.length > 0 && raw.DelaysMs.every(v => Number.isFinite(v) && v >= 0), 'DelaysMs must contain planned nonnegative timings');
  for (const name of ['ActionIds', 'ActionKeys']) {
    if (raw[name] !== undefined) requireValue(Array.isArray(raw[name]) && raw[name].length === raw.DelaysMs.length && raw[name].every(v => typeof v === 'string' && v.length), `${name} must match DelaysMs`);
  }
  if (raw.ActionIds) requireValue(new Set(raw.ActionIds).size === raw.ActionIds.length, 'ActionIds must be unique');
  if (raw.TrialId !== undefined) nonempty(raw.TrialId, 'TrialId');
  const auxiliaryInputs = auxiliaryInputMetadata(raw);
  const startInputs = startInputMetadata(raw);
  return {
    version: raw.Version, skill: raw.Skill, profileId: raw.ProfileId ?? null, bossId: raw.BossId ?? null,
    profileHash: raw.ProfileHash ?? null, startedUtc: raw.StartedUtc ?? null,
    status: raw.Status, mode: raw.Mode ?? null, pattern: raw.Pattern ?? null,
    conditions: raw.Conditions ?? null, recordingLogged: raw.Recording ?? 'unspecified',
    party: raw.Party ?? raw.ProfileSnapshot?.Party ?? null,
    startCharacter: raw.StartCharacter ?? raw.ProfileSnapshot?.StartCharacter ?? null,
    ...(raw.ProfileSnapshot?.StartAttack != null
      ? { startAttack: { planned: raw.ProfileSnapshot.StartAttack, inputs: raw.StartAttackInputs ?? [], outcome: 'unconfirmed' } } : {}),
    ...(Array.isArray(raw.ProfileSnapshot?.Preparation) && raw.ProfileSnapshot.Preparation.length
      ? { preparation: { planned: raw.ProfileSnapshot.Preparation, inputs: raw.PreparationInputs ?? [], outcome: 'unconfirmed' } } : {}),
    ...(auxiliaryInputs ? { auxiliaryInputs } : {}),
    ...(startInputs ? { startInputs } : {}),
    obsObservations: raw.ObsObservations ?? [], obsLedgerDirectory: raw.ObsLedgerDirectory ?? null,
    obsRecordings: obsRecordings(raw, rawPath),
    obsRecording: observedRecording(raw),
    ...(raw.RecordingBasis == null ? {} : { recordingBasis: raw.RecordingBasis, recordingAtF8: raw.RecordingAtF8, f8Utc: raw.F8Utc }),
    // Submission status and raw GameOutcome deliberately never become game results.
    actions: raw.DelaysMs.map((delayMs, attack) => ({ attack, id: raw.ActionIds?.[attack] ?? `attack-${attack}`,
      key: raw.ActionKeys?.[attack] ?? raw.Inputs.find(input => input.Attack === attack)?.Key ?? null, delayMs })),
  };
}

export function importTrial(store, rawPath, { trialId } = {}) {
  store = resolve(store); rawPath = resolve(rawPath);
  if (existsSync(`${rawPath}.start-character.txn.json`)) fail('TRIAL_CORRECTION_PENDING', 'Finish starting-character correction in the experiment app before importing');
  const bytes = readBytes(rawPath);
  const raw = parseJson(bytes, rawPath);
  const metadata = rawMetadata(raw, rawPath);
  if (['armed', 'detected'].includes(raw.Status)) fail('ACTIVE_TRIAL', 'The raw trial is still active. Import only after the runner has saved its final status.');
  requireValue(FINAL_STATUSES.includes(raw.Status), `Unknown final trial status: ${raw.Status}`);
  const sha256 = hash(bytes);
  if (trialId !== undefined) nonempty(trialId, 'trialId');
  if (raw.TrialId && trialId && raw.TrialId !== trialId) fail('TRIAL_CONFLICT', 'Explicit trial ID differs from raw TrialId');
  const id = raw.TrialId ?? trialId ?? `legacy-${sha256}`;
  const sidecarPath = `${rawPath}.result.json`;
  const sidecar = existsSync(sidecarPath) ? readJson(sidecarPath) : null;
  const correctedHashes = validateCharacterCorrections(sidecar, bytes);
  return mutate(store, data => {
    const duplicate = data.trials.find(trial => trial.raw.sha256 === sha256);
    const existing = data.trials.find(trial => trial.id === id);
    if (existing && existing.raw.sha256 !== sha256) {
      if (!correctedHashes.includes(existing.raw.sha256)) fail('TRIAL_CONFLICT', `Trial ${id} already refers to different raw bytes`);
      existing.raw = { path: portable(store, rawPath), sha256 };
      existing.metadata = metadata;
      importResults(sidecar, existing, raw, store, sidecarPath);
      return { id, duplicate: true, corrected: true };
    }
    if (duplicate) {
      if ((trialId || raw.TrialId) && duplicate.id !== id) fail('TRIAL_CONFLICT', `Raw bytes already imported as ${duplicate.id}`);
      duplicate.metadata = metadata;
      if (sidecar) importResults(sidecar, duplicate, raw, store, sidecarPath);
      return { id: duplicate.id, duplicate: true };
    }
    const trial = { id, importedAt: new Date().toISOString(), raw: { path: portable(store, rawPath), sha256 }, metadata, revisions: [] };
    if (sidecar) importResults(sidecar, trial, raw, store, sidecarPath);
    data.trials.push(trial);
    return { id, duplicate: false };
  });
}

function validateCharacterCorrections(sidecar, bytes) {
  if (sidecar?.startCharacterCorrections == null) return [];
  requireValue(Array.isArray(sidecar.startCharacterCorrections), 'Invalid starting-character corrections');
  let text = bytes.toString('utf8');
  const previousHashes = [];
  for (const correction of [...sidecar.startCharacterCorrections].reverse()) {
    keys(correction, ['at', 'beforeRawSha256', 'afterRawSha256', 'before', 'after', 'offset', 'beforeJson', 'afterJson'], 'starting-character correction');
    nonempty(correction.at, 'correction time');
    requireValue(Number.isFinite(Date.parse(correction.at)), 'Invalid correction time');
    nonempty(correction.before, 'previous character'); nonempty(correction.after, 'corrected character');
    requireValue(typeof correction.beforeJson === 'string' && typeof correction.afterJson === 'string', 'Correction must contain original JSON tokens');
    requireValue(Number.isInteger(correction.offset) && correction.offset >= 0 &&
      text.slice(correction.offset, correction.offset + correction.afterJson.length) === correction.afterJson, 'Correction token position differs');
    requireValue(hash(Buffer.from(text)) === correction.afterRawSha256, 'Corrected raw hash differs');
    const after = parseJson(Buffer.from(text), 'corrected raw');
    const oldText = text.slice(0, correction.offset) + correction.beforeJson + text.slice(correction.offset + correction.afterJson.length);
    requireValue(hash(Buffer.from(oldText)) === correction.beforeRawSha256, 'Original raw hash cannot be reconstructed');
    const before = parseJson(Buffer.from(oldText), 'original raw');
    requireValue(before.StartCharacter === correction.before && after.StartCharacter === correction.after, 'Correction character differs');
    delete before.StartCharacter; delete after.StartCharacter;
    requireValue(JSON.stringify(before) === JSON.stringify(after), 'Starting-character correction changed unrelated fields');
    previousHashes.push(correction.beforeRawSha256); text = oldText;
  }
  return previousHashes;
}

function importResults(input, trial, raw, store, inputPath) {
  keys(input, ['schemaVersion', 'trialId', 'rawSha256', 'revisions', 'startCharacterCorrections'], 'result sidecar');
  requireValue(input.schemaVersion === 1, 'Unsupported result sidecar version');
  requireValue(input.trialId === trial.id && input.rawSha256 === trial.raw.sha256, 'Result sidecar refers to another trial or different raw bytes');
  requireValue(raw.Status !== 'observed-no-input' && !['observe', 'observe-only'].includes(raw.Mode), 'No-input observation cannot have binary action results');
  requireValue(Array.isArray(raw.ActionIds) && raw.ActionIds.length > 0, 'Result sidecar requires recorded ActionIds');
  requireValue(Array.isArray(input.revisions) && input.revisions.length > 0, 'Result sidecar requires revision history');
  const imported = trial.resultRevisionHashes ?? [];
  requireValue(input.revisions.length >= imported.length, 'Result sidecar history was truncated');
  for (const [index, revision] of input.revisions.entries()) {
    keys(revision, ['at', 'bits', 'reason', 'actions', 'preparationNote', 'preparationSource'], 'result revision');
    preparationReport(revision, trial);
    nonempty(revision.at, 'result revision at');
    requireValue(Number.isFinite(Date.parse(revision.at)), 'Result revision at must be a timestamp');
    requireValue(typeof revision.bits === 'string' && revision.bits.length === raw.ActionIds.length && /^[01]+$/.test(revision.bits), 'Result bits must be exactly one 0 or 1 per recorded action');
    requireValue(Array.isArray(revision.actions) && revision.actions.length === raw.ActionIds.length, 'Result revision must report every action');
    for (const [attack, row] of revision.actions.entries()) {
      requireValue(row?.attack === attack && row.outcome === (revision.bits[attack] === '1' ? 'success' : 'failure') && row.source?.kind === 'user-report', 'Result actions must match bits and cite user-report');
    }
    const fingerprint = hash(Buffer.from(JSON.stringify(revision)));
    if (index < imported.length) {
      requireValue(imported[index] === fingerprint, 'Previously imported result history was changed');
      continue;
    }
    const next = annotation({ reason: revision.reason, actions: revision.actions,
      ...(revision.preparationNote === undefined ? {} : { preparationNote: revision.preparationNote, preparationSource: revision.preparationSource }) }, trial, store, inputPath);
    trial.revisions.push({ at: revision.at, revision: trial.revisions.length + 1, ...next });
    imported.push(fingerprint);
  }
  trial.resultRevisionHashes = imported;
}

function source(value, name) {
  keys(value, ['kind', 'reference'], name);
  requireValue(['user-report', 'video'].includes(value.kind), `${name}.kind must be user-report or video`);
  nonempty(value.reference, `${name}.reference`);
}
function hasManualRecording(trial) {
  const latest = trial.revisions.at(-1);
  if (typeof latest?.recordingExplicit === 'boolean') return latest.recordingExplicit;
  // Older snapshots had no explicitness flag. Retain evidenced manual reports,
  // including a later unconfirmed correction following an earlier report.
  return trial.revisions.some(row => row.recordingExplicit === true || (row.recording && (row.recording.actual !== 'unconfirmed' || row.recording.source)));
}
function preparationReport(input, trial) {
  if (input.preparationNote === undefined) {
    requireValue(input.preparationSource === undefined, 'Preparation source requires a note');
    return;
  }
  requireValue(typeof input.preparationNote === 'string' && input.preparationNote.length <= 500, 'Preparation note must be a string of at most 500 characters');
  requireValue(input.preparationNote.length === 0 || trial.metadata.preparation || trial.metadata.startInputs, 'Preparation note requires recorded preparation');
  source(input.preparationSource, 'preparationSource');
  requireValue(input.preparationSource.kind === 'user-report', 'Preparation note must cite user-report');
}
function annotation(input, trial, store, inputPath) {
  keys(input, ['reason', 'recording', 'videos', 'actions', 'preparationNote', 'preparationSource'], 'annotation');
  nonempty(input.reason, 'reason');
  requireValue(input.recording !== undefined || input.videos !== undefined || input.preparationNote !== undefined || (Array.isArray(input.actions) && input.actions.length > 0), 'Annotation requires at least one recording, videos, preparation note or action update');
  preparationReport(input, trial);
  const previous = trial.revisions.at(-1);
  const preparationNote = input.preparationNote === undefined ? previous?.preparationNote : input.preparationNote;
  const preparationSource = input.preparationNote === undefined ? previous?.preparationSource : input.preparationSource;
  if (input.videos !== undefined) requireValue(Array.isArray(input.videos), 'videos must be an array (empty explicitly clears references)');
  const videos = input.videos === undefined ? previous?.videos ?? [] : input.videos.map(path => {
    nonempty(path, 'video path');
    const full = resolve(dirname(inputPath), path);
    requireValue(existsSync(full) && statSync(full).isFile(), `Video file does not exist: ${full}`);
    return portable(store, full);
  });
  requireValue(new Set(videos).size === videos.length, 'Duplicate video reference');
  const recording = input.recording === undefined ? previous?.recording ?? { actual: 'unconfirmed' } : input.recording;
  keys(recording, ['actual', 'source'], 'recording');
  requireValue(RECORDING.includes(recording.actual), 'recording.actual must be ON, OFF or unconfirmed');
  if (recording.actual !== 'unconfirmed' || recording.source) source(recording.source, 'recording.source');
  if (input.actions !== undefined) requireValue(Array.isArray(input.actions), 'actions must be an array');
  const updates = input.actions ?? [];
  const seen = new Set();
  for (const row of updates) {
    keys(row, ['attack', 'outcome', 'source', 'note'], 'action');
    requireValue(Number.isInteger(row.attack) && row.attack >= 0 && row.attack < trial.metadata.actions.length && !seen.has(row.attack), 'attack must be a unique zero-based action index');
    seen.add(row.attack);
    requireValue(OUTCOMES.includes(row.outcome), `Invalid outcome for attack ${row.attack}`);
    if (row.outcome !== 'unconfirmed' || row.source) source(row.source, 'action.source');
    if (row.note !== undefined) nonempty(row.note, 'action.note');
  }
  const actions = trial.metadata.actions.map(({ attack }) => updates.find(row => row.attack === attack)
    ?? previous?.actions.find(row => row.attack === attack) ?? { attack, outcome: 'unconfirmed' });
  const sources = [recording.source, ...actions.map(row => row.source)];
  requireValue(!sources.some(value => value?.kind === 'video') || videos.length > 0, 'Video evidence requires at least one video file reference');
  return { reason: input.reason, recording, recordingExplicit: input.recording !== undefined || hasManualRecording(trial), videos, actions,
    ...(preparationNote === undefined ? {} : { preparationNote, preparationSource }) };
}

export function annotateTrial(store, trialId, inputPath) {
  store = resolve(store); inputPath = resolve(inputPath);
  const input = readJson(inputPath);
  return mutate(store, data => {
    const trial = data.trials.find(row => row.id === trialId);
    if (!trial) fail('UNKNOWN_TRIAL', `Unknown trial ${trialId}`);
    const next = annotation(input, trial, store, inputPath);
    const previous = trial.revisions.at(-1);
    const { at, revision, ...last } = previous ?? {};
    if (previous && JSON.stringify(last) === JSON.stringify(next)) return { id: trialId, revision, duplicate: true };
    trial.revisions.push({ at: new Date().toISOString(), revision: trial.revisions.length + 1, ...next });
    return { id: trialId, revision: trial.revisions.length, duplicate: false };
  });
}

function batchDefinition(input, data) {
  keys(input, ['id', 'question', 'decision', 'stopRule', 'conditions', 'trials', 'reason'], 'batch');
  for (const key of ['id', 'question', 'decision', 'stopRule', 'reason']) nonempty(input[key], key);
  requireValue(Array.isArray(input.conditions) && input.conditions.length > 0, 'conditions must be a nonempty array');
  const ids = new Set();
  for (const condition of input.conditions) {
    keys(condition, ['id', 'description', 'preset'], 'condition');
    nonempty(condition.id, 'condition.id'); nonempty(condition.description, 'condition.description');
    requireValue(!ids.has(condition.id), 'Duplicate condition ID'); ids.add(condition.id);
    if (condition.preset) {
      keys(condition.preset, ['profileId', 'pattern', 'delaysMs', 'recording'], 'preset');
      for (const key of ['profileId', 'pattern']) if (condition.preset[key] !== undefined) nonempty(condition.preset[key], `preset.${key}`);
      if (condition.preset.delaysMs !== undefined) requireValue(Array.isArray(condition.preset.delaysMs) && condition.preset.delaysMs.length > 0 && condition.preset.delaysMs.every(value => Number.isFinite(value) && value >= 0), 'preset.delaysMs must contain nonnegative numbers');
      if (condition.preset.recording !== undefined) requireValue(['ON', 'OFF'].includes(condition.preset.recording), 'preset.recording must be ON or OFF');
    }
  }
  requireValue(Array.isArray(input.trials), 'trials must be an array (empty is allowed before experiments)');
  const trials = new Set();
  for (const row of input.trials) {
    keys(row, ['trialId', 'conditionId'], 'batch trial');
    requireValue(data.trials.some(trial => trial.id === row.trialId), `Unknown trial ${row.trialId}`);
    requireValue(!trials.has(row.trialId), 'Duplicate trial in batch'); trials.add(row.trialId);
    requireValue(ids.has(row.conditionId), `Unknown condition ${row.conditionId}`);
  }
  return input;
}

export function saveBatch(store, inputPath) {
  const input = readJson(resolve(inputPath));
  return mutate(resolve(store), data => {
    const definition = batchDefinition(input, data);
    let batch = data.batches.find(row => row.id === definition.id);
    if (!batch) { batch = { id: definition.id, revisions: [] }; data.batches.push(batch); }
    const { at, revision, ...previous } = batch.revisions.at(-1) ?? {};
    if (JSON.stringify(previous) === JSON.stringify(definition)) return { id: batch.id, revision, duplicate: true };
    batch.revisions.push({ at: new Date().toISOString(), revision: batch.revisions.length + 1, ...definition });
    return { id: batch.id, revision: batch.revisions.length, duplicate: false };
  });
}

function trialSummary(trial) {
  const latest = trial.revisions.at(-1);
  const effectiveRecording = hasManualRecording(trial) ? latest.recording
    : trial.metadata.obsRecording ?? { actual: 'unconfirmed' };
  let earlierFailure = false;
  let earlierUnknown = false;
  const actions = trial.metadata.actions.map(action => {
    const result = latest?.actions.find(row => row.attack === action.attack) ?? { outcome: 'unconfirmed' };
    const path = earlierFailure ? 'after-failure' : earlierUnknown ? 'upstream-unconfirmed' : 'normal';
    if (result.outcome === 'failure') earlierFailure = true;
    else if (result.outcome !== 'success') earlierUnknown = true;
    return { ...action, ...result, path };
  });
  return { id: trial.id, ...trial.metadata, raw: trial.raw,
    ...(trial.metadata.preparation && latest?.preparationNote !== undefined
      ? { preparation: { ...trial.metadata.preparation, note: latest.preparationNote, source: latest.preparationSource } } : {}),
    ...(trial.metadata.startInputs && latest?.preparationNote !== undefined
      ? { startInputs: { ...trial.metadata.startInputs, note: latest.preparationNote, source: latest.preparationSource } } : {}),
    recordingActual: effectiveRecording.actual,
    recordingSource: effectiveRecording.source ?? null,
    recordingDiffers: ['ON', 'OFF'].includes(effectiveRecording.actual) ? effectiveRecording.actual !== trial.metadata.recordingLogged : null,
    videos: latest?.videos ?? [], revision: latest?.revision ?? 0, actions,
    allActionsReportedSuccess: actions.every(row => row.outcome === 'success'),
  };
}
function conditionIssues(trial, condition) {
  const expected = condition.preset ?? {};
  const actual = { profileId: trial.profileId, pattern: trial.pattern, delaysMs: trial.actions.map(row => row.delayMs), recording: trial.recordingActual };
  return Object.keys(expected).filter(key => JSON.stringify(expected[key]) !== JSON.stringify(actual[key]));
}
export function summarize(store, batchId) {
  const data = load(resolve(store));
  const batch = batchId ? data.batches.find(row => row.id === batchId)?.revisions.at(-1) : null;
  if (batchId && !batch) fail('UNKNOWN_BATCH', `Unknown batch ${batchId}`);
  const trials = data.trials.filter(trial => !batch || batch.trials.some(row => row.trialId === trial.id)).map(trial => {
    const summary = trialSummary(trial);
    if (!batch) return summary;
    const conditionId = batch.trials.find(row => row.trialId === trial.id).conditionId;
    return { ...summary, conditionId, conditionMismatches: conditionIssues(summary, batch.conditions.find(row => row.id === conditionId)) };
  });
  return { schemaVersion, batch, trials, empiricalDecision: 'manual-review-required' };
}

export function validateStore(store) {
  store = resolve(store);
  if (!existsSync(store)) return { valid: false, issues: [{ code: 'MISSING_STORE', path: store }] };
  const data = load(store);
  const issues = [];
  const ids = new Set(), hashes = new Set();
  for (const trial of data.trials) {
    if (ids.has(trial.id) || hashes.has(trial.raw.sha256)) issues.push({ code: 'DUPLICATE_TRIAL', trialId: trial.id });
    ids.add(trial.id); hashes.add(trial.raw.sha256);
    const rawPath = fileAt(store, trial.raw.path);
    if (!existsSync(rawPath)) issues.push({ code: 'MISSING_RAW', trialId: trial.id, path: trial.raw.path });
    else if (hash(readFileSync(rawPath)) !== trial.raw.sha256) issues.push({ code: 'RAW_HASH_CHANGED', trialId: trial.id, path: trial.raw.path });
    else {
      const metadata = rawMetadata(readJson(rawPath), rawPath);
      // Ledger snapshots are refreshed on import; OBS can append its stop path after
      // a trial ended. Only fields derived from immutable raw bytes are compared here.
      if (Object.keys(trial.metadata).some(key => key !== 'obsRecordings' && JSON.stringify(metadata[key]) !== JSON.stringify(trial.metadata[key]))) issues.push({ code: 'METADATA_CHANGED', trialId: trial.id });
    }
    for (const video of trial.revisions.at(-1)?.videos ?? []) {
      if (!existsSync(fileAt(store, video))) issues.push({ code: 'MISSING_VIDEO', trialId: trial.id, path: video });
    }
  }
  for (const batch of data.batches) {
    const { at, revision, ...definition } = batch.revisions.at(-1);
    try { batchDefinition(definition, data); }
    catch (error) { issues.push({ code: 'INVALID_BATCH', batchId: batch.id, message: error.message }); continue; }
    for (const row of definition.trials) {
      const trial = trialSummary(data.trials.find(trial => trial.id === row.trialId));
      const mismatch = conditionIssues(trial, definition.conditions.find(condition => condition.id === row.conditionId));
      if (mismatch.length) issues.push({ code: 'CONDITION_MISMATCH', batchId: batch.id, trialId: row.trialId, fields: mismatch });
    }
  }
  return { valid: issues.length === 0, issues };
}

function cli(args) {
  const [command, ...rest] = args;
  if (!command || command === 'help' || command === '--help') return { commands: {
    import: '--raw <NoticeRecord.json> [--trial <id>]', annotate: '--trial <id> --file <annotation.json>',
    batch: '--file <batch.json>', summary: '[--batch <id>]', validate: '',
  }, common: '[--store <records.json>]', defaultStore, outcomes: OUTCOMES,
  annotation: { reason: 'initial report or correction explanation', recording: { actual: 'ON', source: { kind: 'user-report', reference: 'user confirmation' } }, videos: [], actions: [{ attack: 0, outcome: 'success', source: { kind: 'user-report', reference: 'user confirmation' } }] },
  preparationAnnotation: { reason: 'user reports an issue during preparation', preparationNote: 'Hit after the second preparation dodge', preparationSource: { kind: 'user-report', reference: 'user report for this trial' } },
  preparationNotes: ['Preparation notes are optional and require recorded preparation for nonempty text. Supply at most 500 characters and a preparationSource with kind user-report.', 'Omit preparationNote and preparationSource to preserve the prior report. An empty preparationNote explicitly clears the text and still requires a user-report source.', 'Blank or missing notes never establish preparation success; action outcomes remain separate.'],
  batch: { id: 'batch-01', question: 'What does this batch distinguish?', decision: 'Which product decision follows?', stopRule: 'When to stop or narrow investigation', reason: 'initial plan', conditions: [{ id: 'A', description: 'preset A', preset: { recording: 'OFF', delaysMs: [3100, 4400] } }], trials: [] },
  notes: ['Import finalized logs only: armed/detected are rejected because the runner still updates them.', 'Annotation is a partial update: omitted recording, videos and actions preserve prior values; initially they are unconfirmed or empty. Each revision stores the full resulting snapshot and reason.', 'Supply only changed action indices. An explicit outcome unconfirmed clears that action; videos: [] clears video references if no retained result cites video evidence. Reason and at least one update are required.', 'Video paths resolve relative to annotation JSON; stored paths resolve relative to the store.', 'Raw inputs/status never imply game success. validate checks record integrity, not empirical acceptance.'] };
  const options = {};
  for (let i = 0; i < rest.length; i += 2) {
    requireValue(rest[i].startsWith('--') && rest[i + 1] !== undefined && !rest[i + 1].startsWith('--'), 'Arguments must be --name value pairs');
    const key = rest[i].slice(2); requireValue(options[key] === undefined, `Duplicate option --${key}`); options[key] = rest[i + 1];
  }
  const allowed = { import: ['raw', 'trial'], annotate: ['trial', 'file'], batch: ['file'], summary: ['batch'], validate: [] };
  requireValue(Object.hasOwn(allowed, command), `Unknown command ${command}`);
  for (const key of Object.keys(options)) requireValue(key === 'store' || allowed[command].includes(key), `Unknown --${key} for ${command}`);
  const store = resolve(options.store ?? defaultStore);
  switch (command) {
    case 'import': nonempty(options.raw, '--raw'); return importTrial(store, options.raw, { trialId: options.trial });
    case 'annotate': nonempty(options.trial, '--trial'); nonempty(options.file, '--file'); return annotateTrial(store, options.trial, options.file);
    case 'batch': nonempty(options.file, '--file'); return saveBatch(store, options.file);
    case 'summary': return summarize(store, options.batch);
    case 'validate': { const result = validateStore(store); if (!result.valid) process.exitCode = 1; return result; }
  }
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { const result = cli(process.argv.slice(2)); console.log(JSON.stringify({ ok: process.exitCode !== 1, ...result })); }
  catch (error) { console.log(JSON.stringify({ ok: false, error: { code: error.code ?? 'UNEXPECTED_ERROR', message: error.message } })); process.exitCode = 1; }
}
