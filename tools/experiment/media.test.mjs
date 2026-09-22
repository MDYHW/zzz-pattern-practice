import test from 'node:test';
import assert from 'node:assert/strict';
import { alignRecording, inspectMedia, alignmentReport, writeAlignmentReport } from './media.mjs';
import { readFileSync, writeFileSync, mkdtempSync, rmSync, linkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const record = { skill: 'synthetic', fps: 60, firstSourceFrame: 60, endSourceFrameExclusive: 300,
  patternOriginSourceFrame: 66, patternTimeZeroAtClipTime: .1, windowsMs: [[1000, 1400], [2000, 2500]],
  referenceMs: [1200, 2250], sourceOverlayDownFrames: [138, 201] };
test('moving only clip start translates every coordinate without changing widths and gaps', () => {
  const a=alignRecording(record,2), b=alignRecording({...record,firstSourceFrame:30,patternTimeZeroAtClipTime:.6},2);
  a.actions.forEach((x,i)=>{for(const field of ['start','end','reference','observed'])assert(Math.abs(b.actions[i][field]-x[field]-.5)<1e-8);assert.equal(x.widthMs,b.actions[i].widthMs)});
});
test('wrong origin, changed observation, missing rows and overlapping intervals cannot pass', () => {
  for(const patch of [{patternTimeZeroAtClipTime:0},{sourceOverlayDownFrames:[170,201]},{referenceMs:[1200]},
    {windowsMs:[[1000,2100],[2000,2500]]},{fps:0},{sourceOverlayDownFrames:[138,300]},{windowsMs:[[1000,1400],[3500,4500]],referenceMs:[1200,4000]}])
    assert.throws(()=>alignRecording({...record,...patch},2));
  assert.throws(()=>alignRecording({...record,sourceOverlayDownFrames:[141,201]},2));
  assert.equal(alignRecording({...record,sourceOverlayDownFrames:[141,201]}).toleranceFrames,null);
});
test('reviewed clips reproduce every current evidence entry within two frames', () => {
  const root=fileURLToPath(new URL('../../',import.meta.url));
  const evidence=JSON.parse(readFileSync(root+'docs/evidence/service-media-20260920.json','utf8'));
  const report=inspectMedia(evidence,root+'public',2);
  assert.equal(report.length,evidence.recordings.length);
  assert.equal(report.reduce((sum,r)=>sum+r.actions.length,0),evidence.recordings.reduce((sum,r)=>sum+r.windowsMs.length,0));
  assert.throws(()=>inspectMedia({recordings:[{...evidence.recordings[0],clipSha256:'0'.repeat(64)}]},root+'public',2));
  assert.match(alignmentReport([{...report[0],skill:'<script>bad</script>'}]),/&lt;script&gt;bad/);
});

test('report cannot overwrite evidence or video through alternate paths', () => {
  const dir=mkdtempSync(join(tmpdir(),'zzz-media-'));
  try {
    const evidence=join(dir,'evidence.json'), clip=join(dir,'clip.mp4'), alias=join(dir,'alias.html');
    writeFileSync(evidence,'original evidence');writeFileSync(clip,'original clip');
    const rows=[{...alignRecording(record),clipUrl:pathToFileURL(clip).href}];
    assert.throws(()=>writeAlignmentReport(evidence,evidence,rows),/cannot overwrite/);
    assert.throws(()=>writeAlignmentReport(clip,evidence,rows),/cannot overwrite/);
    linkSync(clip,alias);
    assert.throws(()=>writeAlignmentReport(alias,evidence,rows),/cannot overwrite/);
    if(process.platform==='win32') assert.throws(()=>writeAlignmentReport(evidence.toUpperCase(),evidence,rows),/cannot overwrite/);
    assert.equal(readFileSync(evidence,'utf8'),'original evidence');assert.equal(readFileSync(clip,'utf8'),'original clip');
    writeAlignmentReport(join(dir,'report.html'),evidence,rows);
    assert.match(readFileSync(join(dir,'report.html'),'utf8'),/data-time=/);
  } finally {rmSync(dir,{recursive:true,force:true})}
});
