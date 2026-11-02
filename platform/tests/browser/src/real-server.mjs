import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

const readyPrefix = 'QUESSHI_BROWSER_HOST_READY ';

// Starts the real platform server (tests/Quesshi.Browser.Host) on a throw-away PostgreSQL database. `emailCode`
// resolves the sign-in code the server logs for an address, so a test signs in the way a player does.
export async function startRealServer({ hostDll }) {
  const child = spawn('dotnet', [hostDll], {
    stdio: ['pipe', 'pipe', 'inherit'],
    env: { ...process.env, SECRET_KEY: 'browser-test-secret-key-0123456789abcdef', ASPNETCORE_ENVIRONMENT: 'Development' }
  });
  const codes = [];
  const waiters = [];
  let ready;
  let failed;
  let timer;
  const started = new Promise((resolve, reject) => { ready = resolve; failed = reject; });
  child.once('exit', code => failed(new Error(`The real server host exited with code ${code} before it was ready.`)));
  createInterface({ input: child.stdout }).on('line', line => {
    if (line.startsWith(readyPrefix)) return ready(JSON.parse(line.slice(readyPrefix.length)));
    const match = /Email sign-in code generated for (\S+): (\d+)/.exec(line.replaceAll('\\u0022', '"'));
    if (!match) return;
    const code = { email: match[1], code: match[2] };
    const waiter = waiters.findIndex(candidate => candidate.email === code.email);
    if (waiter >= 0) waiters.splice(waiter, 1)[0].resolve(code.code);
    else codes.push(code);
  });
  const timeout = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('The real server host did not become ready in time.')), 180000); });
  const info = await Promise.race([started, timeout]).catch(error => {
    child.kill();
    throw error;
  }).finally(() => clearTimeout(timer));
  return {
    ...info,
    emailCode(email) {
      const index = codes.findIndex(code => code.email === email);
      if (index >= 0) return Promise.resolve(codes.splice(index, 1)[0].code);
      return new Promise((resolve, reject) => {
        waiters.push({ email, resolve });
        setTimeout(() => reject(new Error(`No sign-in code was logged for ${email}.`)), 30000).unref();
      });
    },
    async close() {
      child.removeAllListeners('exit');
      if (child.exitCode !== null) return;
      const exited = new Promise(resolve => child.once('exit', resolve));
      child.stdin.end();
      const killer = setTimeout(() => child.kill(), 30000);
      await exited;
      clearTimeout(killer);
    }
  };
}
