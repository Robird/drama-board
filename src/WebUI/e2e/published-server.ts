import { spawn } from 'node:child_process';
import { mkdtemp, rmdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const repoRoot = fileURLToPath(new URL('../../../', import.meta.url));

// Start the actual apphost from an empty external CWD. Port 0 lets Kestrel reserve
// a free port without a check-then-bind race or connecting to an existing server.
export async function startPublishedServer() {
  const cwd = await mkdtemp(join(tmpdir(), 'dramaboard-e2e-'));
  const executable = resolve(repoRoot, 'artifacts/server', process.platform === 'win32' ? 'DramaBoard.Server.exe' : 'DramaBoard.Server');
  const child = spawn(executable, ['--urls', 'http://127.0.0.1:0'], {
    cwd, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production' },
  });
  let log = '';
  let exited = false;
  const exit = new Promise<void>(resolveExit => { child.once('exit', () => { exited = true; resolveExit(); }); });
  child.stdout.on('data', data => { log += data.toString(); });
  child.stderr.on('data', data => { log += data.toString(); });
  async function stop() {
    if (!exited && child.pid) {
      child.kill('SIGTERM');
      const deadline = setTimeout(() => { if (!exited) child.kill('SIGKILL'); }, 5000);
      try { await exit; } finally { clearTimeout(deadline); }
    }
    await rmdir(cwd);
  }
  try {
    const url = await new Promise<string>((resolveUrl, reject) => {
      const timeout = setTimeout(() => reject(new Error(`Published server startup timed out.\n${log}`)), 15_000);
      function ready(data: Buffer) {
        const match = /Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/.exec(log + data.toString());
        if (match) { clearTimeout(timeout); child.stdout.off('data', ready); resolveUrl(match[1]); }
      }
      child.stdout.on('data', ready);
      child.once('error', error => { clearTimeout(timeout); reject(error); });
      child.once('exit', code => { clearTimeout(timeout); reject(new Error(`Published server exited (${code}).\n${log}`)); });
    });
    return { url, cwd, stop, getLog: () => log };
  } catch (error) {
    await stop();
    throw error;
  }
}
