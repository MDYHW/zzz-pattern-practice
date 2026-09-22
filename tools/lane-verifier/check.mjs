import {spawnSync} from 'node:child_process';
import {readFileSync,mkdirSync} from 'node:fs';
import {resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import assert from 'node:assert/strict';
const root=fileURLToPath(new URL('../../',import.meta.url));
const report=resolve(root,'.local/lane-verifier-check');mkdirSync(report,{recursive:true});
function run(command,args){const result=spawnSync(command,args,{cwd:root,stdio:'inherit',windowsHide:true});if(result.error)throw result.error;if(result.status!==0)throw new Error(`${command} failed (${result.status})`);}
run('powershell.exe',['-NoProfile','-File','tools/lane-verifier/Build.ps1']);
const exe=resolve(root,'.local/lane-verifier/LaneVerifier.exe');
run(exe,['--self-test',report]);run(exe,['--smoke',report]);
for(const name of ['self-test.json','smoke.json']){const data=JSON.parse(readFileSync(resolve(report,name),'utf8'));assert.equal(data.passed,true);assert.equal(data.gameInputsSent,0);}
// Moving shared detector classes must preserve the original automatic harness.
const regression=resolve(root,'.local/lane-verifier-regression');
run('powershell.exe',['-NoProfile','-File','tools/experiment/Build.ps1','-OutputDirectory',regression]);
for(const id of ['vesper-execute-01','vesper-execute-02','girtablullu-stagnant-execute-01']){
 run(resolve(regression,'ControlExperiment.exe'),['--profile',resolve(root,`tools/experiment/profiles/${id}.json`),'--notice-test',resolve(report,`existing-${id}.json`)]);
}
console.log(JSON.stringify({passed:true,scope:'manual lane + existing shared detector regression; synthetic windows and fake input only',report}));
