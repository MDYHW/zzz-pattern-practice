import { spawnSync } from 'node:child_process';
import { readFileSync, readdirSync, mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root=fileURLToPath(new URL('../../',import.meta.url));
const output=resolve(root,'.local/experiment-check');
function run(command,args){
  const result=spawnSync(command,args,{cwd:root,stdio:'inherit',windowsHide:true});
  if(result.error)throw result.error;
  if(result.status!==0)throw new Error(`${command} failed (${result.status})`);
}
// Only bundled profiles enter this check; editable .local copies are experiment settings,
// not service defaults. Return every profile, including candidates without media.
export function profileChecks(profiles, recordings, baselineDecisions = new Map()) {
  assert(profiles.length, 'No bundled profiles found');
  assert(Array.isArray(recordings), 'Service recordings must be an array');
  const byId = new Map();
  for (const profile of profiles) {
    const id = profile.data.Id;
    assert(typeof id === 'string' && /^[a-z0-9][a-z0-9-]{0,79}$/.test(id), 'Profile needs a valid Id');
    assert(!byId.has(id), `Duplicate bundled profile Id: ${id}`);
    byId.set(id, profile);
  }
  for (const [id, profile] of byId) {
    const matches = recordings.filter(recording => recording.contentId === id);
    assert(matches.length <= 1, `Duplicate service recording contentId: ${id}`);
    if (matches.length) {
      const recording = matches[0];
      assert.equal(profile.data.BossId, recording.bossId, `${id}: boss mismatch`);
      assert.equal(profile.data.Skill, recording.skill, `${id}: skill mismatch`);
      assert.deepEqual(profile.data.Actions.map(a => [a.Timing.EarlyMs, a.Timing.LateMs]), recording.windowsMs,
        `${id}: profile must match adopted service ranges`);
    }
    if (baselineDecisions.has(id)) {
      assert.deepEqual(profile.data.Actions.map(a => a.Timing.BaselineMs), baselineDecisions.get(id),
        `${id}: profile must preserve adopted baseline settings`);
    }
  }
  return profiles;
}

function main() {
  mkdirSync(output,{recursive:true});
  run(process.execPath,['--test','tools/experiment/check.test.mjs','tools/experiment/records.test.mjs','tools/experiment/media.test.mjs']);
  if(process.platform!=='win32')throw new Error('Runner checks require Windows/.NET Framework; record and media tests ran separately.');
  run('powershell.exe',['-NoProfile','-File','tools/experiment/Build.ps1']);
  const evidence=JSON.parse(readFileSync(resolve(root,'docs/evidence/service-media-20260920.json'),'utf8'));
  const off01=JSON.parse(readFileSync(resolve(root,'docs/evidence/vesper-execute01-off-20260920.json'),'utf8'));
  const off02=JSON.parse(readFileSync(resolve(root,'docs/evidence/vesper-timing-origin-20260919.json'),'utf8')).lateEndpointClosure.decision;
  const girEvidence=JSON.parse(readFileSync(resolve(root,'docs/evidence/girtablullu-off-20260920.json'),'utf8'));
  const profileDirectory=resolve(root,'tools/experiment/profiles');
  const profiles=readdirSync(profileDirectory,{withFileTypes:true})
    .filter(entry=>entry.isFile() && entry.name.endsWith('.json'))
    .sort((a,b)=>a.name.localeCompare(b.name))
    .map(entry=>{
      const path=resolve(profileDirectory,entry.name);
      return {path,data:JSON.parse(readFileSync(path,'utf8').replace(/^\uFEFF/,''))};
    });
  // Baselines retain their own decision sources; media references are observations,
  // and later service endpoint extensions do not rewrite earlier baseline decisions.
  const off02Windows=[[3050,off02.entryLateMs],...off02.supportEarlyMs.map((early,i)=>[early,off02.supportLateMs[i]])];
  const checked=profileChecks(profiles,evidence.recordings,new Map([
    ['vesper-execute-01',off01.decision.baselineMs],
    ['vesper-execute-02',off02Windows.map(([early,late])=>early+Math.floor((late-early)/100)*50)],
    ['girtablullu-stagnant-execute-01',girEvidence.decision.baselineMs],
    ['kusarikku-execute-01',[4100,6850,7900,9650,10750]],
    ['mirage-archer-unit-attack-09',[4250,6400,8150,9550,11850]],
  ]));
  const gir=checked.find(profile=>profile.data.Id==='girtablullu-stagnant-execute-01');
  if(gir){
    assert.equal(gir.data.Party,girEvidence.party);
    assert.equal(gir.data.StartCharacter,girEvidence.startCharacter);
  }
  const exe=resolve(root,'.local/experiment-build/ControlExperiment.exe');
  // The synthetic common suite is independent of the selected boss. Run it once;
  // every profile still exercises its nine schedules and editor snapshots.
  for(const [index,{path,data}] of checked.entries()){
    run(exe,['--profile',path,index === 0 ? '--notice-test' : '--profile-check',resolve(output,`notice-${data.Id}.json`)]);
    run(exe,['--profile',path,'--ui-smoke',resolve(output,`ui-${data.Id}.png`)]);
  }
  run(process.execPath,['tools/experiment/media.mjs','--evidence','docs/evidence/service-media-20260920.json','--max-overlay-frames','2','--report',resolve(output,'service-alignment.html')]);
  console.log(JSON.stringify({ok:true,scope:'fake input, profile/evidence consistency, record integrity and video alignment; no live game input',output}));
}

if(process.argv[1] && resolve(process.argv[1])===fileURLToPath(import.meta.url)){
  try{main();}
  catch(error){console.error(JSON.stringify({ok:false,error:error.message}));process.exitCode=1;}
}
