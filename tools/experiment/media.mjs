import { readFileSync, writeFileSync, mkdirSync, existsSync, statSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';

const root = fileURLToPath(new URL('../../', import.meta.url));
const finite = (value, name) => { if (!Number.isFinite(value)) throw new Error(`${name}: finite number required`); return value; };
const frame = (value, name) => { if (!Number.isInteger(value) || value < 0) throw new Error(`${name}: nonnegative frame required`); return value; };

// Coordinates stay explicit: source frames -> clip seconds; experiment ms -> clip seconds.
export function alignRecording(record, toleranceFrames) {
  const fps = finite(record.fps, 'fps');
  if (fps <= 0) throw new Error('fps must be positive');
  const first = frame(record.firstSourceFrame, 'firstSourceFrame');
  const end = frame(record.endSourceFrameExclusive, 'endSourceFrameExclusive');
  const origin = frame(record.patternOriginSourceFrame, 'patternOriginSourceFrame');
  if (end <= first) throw new Error('trim end must be after start');
  const duration = (end - first) / fps, offset = (origin - first) / fps;
  if (record.patternTimeZeroAtClipTime !== undefined && Math.abs(finite(record.patternTimeZeroAtClipTime, 'patternTimeZeroAtClipTime') - offset) > 1e-8)
    throw new Error('declared clip origin disagrees with source frames');
  if (toleranceFrames !== undefined && (!Number.isFinite(toleranceFrames) || toleranceFrames < 0)) throw new Error('invalid overlay tolerance');
  const { windowsMs, referenceMs, sourceOverlayDownFrames } = record;
  if (!Array.isArray(windowsMs) || !windowsMs.length || !Array.isArray(referenceMs) || !Array.isArray(sourceOverlayDownFrames)
    || windowsMs.length !== referenceMs.length || windowsMs.length !== sourceOverlayDownFrames.length) throw new Error('windows/references/observations must have equal nonzero counts');
  let previous = -Infinity;
  const actions = windowsMs.map((window, i) => {
    if (!Array.isArray(window) || window.length !== 2) throw new Error(`action ${i}: expected [start,end]`);
    const startMs = finite(window[0], 'start'), endMs = finite(window[1], 'end'), reference = finite(referenceMs[i], 'reference');
    if (startMs < 0 || endMs <= startMs || startMs <= previous || reference < startMs || reference > endMs) throw new Error(`action ${i}: invalid, overlapping, or unordered interval/reference`);
    previous = endMs;
    const start = startMs / 1000 + offset, finish = endMs / 1000 + offset;
    const observedFrame = frame(sourceOverlayDownFrames[i], `action ${i} overlay frame`);
    const observed = (observedFrame - first) / fps, expected = reference / 1000 + offset;
    if (start < -1e-8 || finish > duration + 1e-8 || observedFrame < first || observedFrame >= end) throw new Error(`action ${i}: outside trimmed clip`);
    if (observed < start - 1e-8 || observed > finish + 1e-8) throw new Error(`action ${i}: observed input outside selected service interval`);
    const residualFrames = (observed - expected) * fps;
    if (toleranceFrames !== undefined && Math.abs(residualFrames) > toleranceFrames + 1e-8) throw new Error(`action ${i}: overlay residual exceeds ${toleranceFrames} frames`);
    return { index: i, startMs, endMs, start, end: finish, reference: expected, observed, residualFrames, widthMs: endMs - startMs };
  });
  return { skill: record.skill, clip: record.clip, duration, offset, toleranceFrames: toleranceFrames ?? null, actions };
}

export function inspectMedia(evidence, publicRoot, toleranceFrames) {
  if (!Array.isArray(evidence.recordings) || !evidence.recordings.length) throw new Error('recordings must be nonempty');
  return evidence.recordings.map(record => {
    if (typeof record.skill !== 'string' || !record.skill || typeof record.clip !== 'string' || !record.clip) throw new Error('skill and clip are required');
    const aligned = alignRecording(record, toleranceFrames);
    const path = resolve(publicRoot, record.clip);
    const digest = createHash('sha256').update(readFileSync(path)).digest('hex');
    if (digest !== record.clipSha256) throw new Error(`${record.skill}: clip hash differs from reviewed asset`);
    return { ...aligned, clipSha256: digest, clipUrl: pathToFileURL(path).href };
  });
}

const escape = value => String(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]));
export function alignmentReport(recordings) {
  const rows = recordings.map(r => `<section><h2>${escape(r.skill)}</h2><p>실험 원점 → 클립 ${r.offset.toFixed(6)}초 · 잔차 기준 ${r.toleranceFrames === null ? '미설정' : `${r.toleranceFrames}프레임`}</p><video controls preload="metadata" src="${escape(r.clipUrl)}"></video><table><thead><tr><th>동작</th><th>실험 구간 ms</th><th>클립 구간 초</th><th>관측 초</th><th>잔차 프레임</th></tr></thead><tbody>${r.actions.map(a => `<tr><th>${a.index}</th><td>${a.startMs}–${a.endMs}</td><td>${a.start.toFixed(3)}–${a.end.toFixed(3)}</td><td><button data-time="${a.observed}">${a.observed.toFixed(3)}</button></td><td>${a.residualFrames.toFixed(2)}</td></tr><tr><td colspan="5"><div class="axis"><span class="window" style="left:${a.start / r.duration * 100}%;width:${(a.end-a.start) / r.duration * 100}%"></span><i style="left:${a.reference / r.duration * 100}%" aria-label="기준 입력"></i><b style="left:${a.observed / r.duration * 100}%" aria-label="관측 입력"></b></div></td></tr>`).join('')}</tbody></table></section>`).join('');
  return `<!doctype html><html lang="ko"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>영상 정렬 검토</title><style>body{font:15px system-ui;background:#151617;color:#edf0e8;margin:24px auto;padding:0 16px;max-width:1000px}section{margin:32px 0}video{width:100%;max-height:500px}table{width:100%;border-collapse:collapse}td,th{padding:7px;text-align:left}button{font:inherit;padding:5px}i,b{position:absolute;top:0;bottom:0;width:2px;background:#d5f529}b{background:#43cbd5}.axis{height:20px;background:#333;position:relative}.window{position:absolute;height:100%;background:#687168}p{line-height:1.6}@media(max-width:600px){table{font-size:12px}td,th{padding:3px}}</style><h1>영상 정렬 검토</h1><p>회색: 선택 구간 · 라임: 기준 입력 · 청록: 영상 입력 표시. 관측 시각을 누르면 해당 장면으로 이동합니다. 시간 변환·파일 무결성 검사이며 실제 게임 성공률이나 허용 경계의 증명이 아닙니다.</p>${rows}<script>document.querySelectorAll('button[data-time]').forEach(button=>button.addEventListener('click',()=>{const video=button.closest('section').querySelector('video');video.pause();video.currentTime=Number(button.dataset.time)}));</script></html>`;
}

// Compare filesystem identity as well as spelling (Windows casing, links).
export function writeAlignmentReport(outputPath, evidencePath, recordings) {
  const output = resolve(outputPath);
  const canonical = path => process.platform === 'win32' ? resolve(path).toLowerCase() : resolve(path);
  const outputStat = existsSync(output) ? statSync(output, { bigint: true }) : null;
  for (const input of [evidencePath, ...recordings.map(r => fileURLToPath(r.clipUrl))]) {
    const inputStat = statSync(input, { bigint: true });
    if (canonical(output) === canonical(input) || (outputStat && outputStat.dev === inputStat.dev && outputStat.ino === inputStat.ino))
      throw new Error('report cannot overwrite evidence or clip');
  }
  mkdirSync(dirname(output), { recursive: true });
  writeFileSync(output, alignmentReport(recordings));
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    if (args.length === 1 && args[0] === '--help') {
      console.log('node tools/experiment/media.mjs --evidence <json> [--public <asset-root>] [--max-overlay-frames <n>] [--report <html>]');
    } else {
      const flags = {};
      for (let i = 0; i < args.length; i += 2) {
        if (!['--evidence', '--public', '--max-overlay-frames', '--report'].includes(args[i]) || args[i + 1] === undefined || args[i] in flags) throw new Error('unknown, duplicate, or missing option; use --help');
        flags[args[i]] = args[i + 1];
      }
      if (!flags['--evidence']) throw new Error('--evidence is required');
      const evidencePath = resolve(flags['--evidence']);
      const evidence = JSON.parse(readFileSync(evidencePath, 'utf8').replace(/^\uFEFF/, ''));
      const tolerance = flags['--max-overlay-frames'] === undefined ? undefined : Number(flags['--max-overlay-frames']);
      const recordings = inspectMedia(evidence, resolve(flags['--public'] ?? resolve(root, 'public')), tolerance);
      if (flags['--report']) {
        writeAlignmentReport(flags['--report'], evidencePath, recordings);
      }
      console.log(JSON.stringify({ ok: true, scope: 'clip alignment and reviewed asset integrity; game outcome not evaluated', recordings }, null, 2));
    }
  } catch (error) { console.error(JSON.stringify({ ok: false, error: error.message })); process.exitCode = 1; }
}
