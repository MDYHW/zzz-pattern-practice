import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Keep the Python-only research tools independent of the app's npm dependencies.
const root = fileURLToPath(new URL('../', import.meta.url));
const [task, ...args] = process.argv.slice(2);
const tasks = {
  assets: ['tools/research/assets/extract_curve.py'],
  timing: ['tools/research/timing/compare.py'],
  windows: ['tools/research/timing/audit_windows.py'],
};
if (!(task in tasks) && task !== 'check') {
  console.error('Expected assets, timing, windows, or check.');
  process.exit(2);
}
const candidates = process.env.RESEARCH_PYTHON
  ? [process.env.RESEARCH_PYTHON]
  : ['python', 'python3', resolve(homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe')];
const python = candidates.find((candidate) => {
  const probeCode = `${task === 'assets' ? 'import cryptography; ' : ''}import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)`;
  const probe = spawnSync(candidate, ['-c', probeCode], { windowsHide: true });
  return probe.status === 0;
});
if (!python) {
  console.error(`Python 3.11+${task === 'assets' ? ' with cryptography' : ''} is required. Set RESEARCH_PYTHON to an existing interpreter with these dependencies.`);
  process.exit(2);
}
console.error(`Research Python: ${python}`);
const commands = task === 'check'
  ? ['assets', 'timing'].map((area) => ['-m', 'unittest', 'discover', '-s', `tools/research/${area}`, '-p', 'test_*.py'])
  : [[...tasks[task], ...args]];
for (const command of commands) {
  const script = command[0];
  if (script !== '-m' && !existsSync(resolve(root, script))) {
    console.error(`Missing research tool: ${script}`);
    process.exit(2);
  }
  const run = spawnSync(python, command, { cwd: root, stdio: 'inherit', windowsHide: true });
  if (run.error) console.error(run.error.message);
  if (run.status !== 0) process.exit(run.status ?? 1);
}
