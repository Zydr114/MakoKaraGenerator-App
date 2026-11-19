// Cross-platform exact process identity, tree enumeration, and resource sampling
// for acceptance scripts (Linux /proc + Windows Get-Process / Win32_Process).
import { execFile } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readdir, readFile } from 'node:fs/promises';
import { delimiter, isAbsolute, join } from 'node:path';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

function encodePowerShell(script) {
  return Buffer.from(script, 'utf16le').toString('base64');
}

async function runPowerShellJson(script) {
  const { stdout } = await execFileAsync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-EncodedCommand', encodePowerShell(script)],
    { encoding: 'utf8', maxBuffer: 4 * 1024 * 1024, windowsHide: true },
  );
  const trimmed = stdout.trim();
  return trimmed ? JSON.parse(trimmed) : null;
}

export function resolvePythonExecutable() {
  if (process.platform !== 'win32') return 'python3';
  for (const dir of String(process.env.PATH ?? '').split(delimiter)) {
    if (!dir || !isAbsolute(dir)) continue;
    if (dir.toLowerCase().includes(join('microsoft', 'windowsapps').toLowerCase())) continue;
    const candidate = join(dir, 'python.exe');
    if (existsSync(candidate)) return candidate;
  }
  return 'python';
}

export async function getProcessIdentity(pid) {
  if (!Number.isSafeInteger(pid) || pid < 1) return { pid, exited: true };
  if (process.platform === 'linux') {
    try {
      const stat = await readFile(`/proc/${pid}/stat`, 'utf8');
      const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ');
      return { pid, parentPid: Number(fields[1]), startTicks: fields[19], state: fields[0] };
    } catch (error) {
      if (error.code === 'ENOENT') return { pid, exited: true };
      throw error;
    }
  }
  if (process.platform === 'win32') {
    const script = [
      "$ProgressPreference = 'SilentlyContinue'",
      `$id = ${pid}`,
      'try {',
      '  $p = Get-Process -Id $id -ErrorAction Stop',
      '  $w = Get-CimInstance Win32_Process -Filter ("ProcessId = " + $id) -ErrorAction SilentlyContinue',
      '  @{',
      '    pid = [int]$id',
      '    parentPid = if ($w) { [int]$w.ParentProcessId } else { 0 }',
      '    startTicks = $p.StartTime.ToFileTimeUtc().ToString()',
      '    state = "R"',
      '  } | ConvertTo-Json -Compress',
      '} catch {',
      '  @{ pid = [int]$id; exited = $true } | ConvertTo-Json -Compress',
      '}',
    ].join('\n');
    const info = await runPowerShellJson(script);
    return info ?? { pid, exited: true };
  }
  throw new Error(`Unsupported platform for process identity: ${process.platform}`);
}

export async function getProcessIdentities(pids) {
  const unique = [...new Set(pids)].filter((pid) => Number.isSafeInteger(pid) && pid >= 1);
  if (unique.length === 0) return [];
  if (process.platform !== 'win32') {
    return Promise.all(unique.map((pid) => getProcessIdentity(pid)));
  }
  const script = [
    "$ProgressPreference = 'SilentlyContinue'",
    `$ids = @(${unique.join(',')})`,
    '$wmi = @{}',
    'foreach ($item in @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)) { $wmi[[int]$item.ProcessId] = [int]$item.ParentProcessId }',
    '$out = [System.Collections.Generic.List[object]]::new()',
    'foreach ($id in $ids) {',
    '  try {',
    '    $p = Get-Process -Id $id -ErrorAction Stop',
    '    $ppid = if ($wmi.ContainsKey($id)) { $wmi[$id] } else { 0 }',
    '    $out.Add(@{ pid = [int]$id; parentPid = $ppid; startTicks = $p.StartTime.ToFileTimeUtc().ToString(); state = "R" })',
    '  } catch {',
    '    $out.Add(@{ pid = [int]$id; exited = $true })',
    '  }',
    '}',
    'ConvertTo-Json -InputObject @($out) -Compress',
  ].join('\n');
  const list = await runPowerShellJson(script);
  return Array.isArray(list) ? list : list ? [list] : [];
}

export async function getProcessMetrics(pid) {
  if (!Number.isSafeInteger(pid) || pid < 1) return { pid, exited: true };
  if (process.platform === 'linux') {
    try {
      const [status, fds] = await Promise.all([
        readFile(`/proc/${pid}/status`, 'utf8'),
        readdir(`/proc/${pid}/fd`),
      ]);
      const readKiB = (key) => {
        const match = status.match(new RegExp(`^${key}:\\s+(\\d+)\\s+kB$`, 'm'));
        return match ? Number(match[1]) : null;
      };
      return { pid, vmRssKiB: readKiB('VmRSS'), vmSizeKiB: readKiB('VmSize'), fdCount: fds.length };
    } catch (error) {
      if (error.code === 'ENOENT') return { pid, exited: true };
      throw error;
    }
  }
  if (process.platform === 'win32') {
    const script = [
      "$ProgressPreference = 'SilentlyContinue'",
      `$id = ${pid}`,
      'try {',
      '  $p = Get-Process -Id $id -ErrorAction Stop',
      '  @{',
      '    pid = [int]$id',
      '    handleCount = [int]$p.HandleCount',
      '    workingSetBytes = [int64]$p.WorkingSet64',
      '    privateBytes = [int64]$p.PrivateMemorySize64',
      '    workingSetKiB = [int64][math]::Round($p.WorkingSet64 / 1024)',
      '    privateKiB = [int64][math]::Round($p.PrivateMemorySize64 / 1024)',
      '  } | ConvertTo-Json -Compress',
      '} catch {',
      '  @{ pid = [int]$id; exited = $true } | ConvertTo-Json -Compress',
      '}',
    ].join('\n');
    const info = await runPowerShellJson(script);
    return info ?? { pid, exited: true };
  }
  throw new Error(`Unsupported platform for process metrics: ${process.platform}`);
}

export async function getProcessTree(rootPid) {
  if (!Number.isSafeInteger(rootPid) || rootPid < 1) return [];
  if (process.platform === 'linux') {
    const entries = await readdir('/proc');
    const all = (
      await Promise.all(entries.filter((x) => /^\d+$/.test(x)).map((x) => getProcessIdentity(Number(x))))
    ).filter((x) => !x.exited);
    const result = all.filter((x) => x.pid === rootPid);
    for (let i = 0; i < result.length; i++) {
      result.push(...all.filter((x) => x.parentPid === result[i].pid && !result.some((y) => y.pid === x.pid)));
    }
    return result;
  }
  if (process.platform === 'win32') {
    const script = [
      "$ProgressPreference = 'SilentlyContinue'",
      `$rootId = ${rootPid}`,
      '$all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)',
      '$byParent = @{}',
      '$byId = @{}',
      'foreach ($item in $all) {',
      '  $id = [int]$item.ProcessId',
      '  $ppid = [int]$item.ParentProcessId',
      '  $byId[$id] = $ppid',
      '  if (-not $byParent.ContainsKey($ppid)) { $byParent[$ppid] = [System.Collections.Generic.List[int]]::new() }',
      '  $byParent[$ppid].Add($id)',
      '}',
      '$result = [System.Collections.Generic.List[object]]::new()',
      'if ($byId.ContainsKey($rootId)) {',
      '  $queue = [System.Collections.Generic.Queue[int]]::new()',
      '  $seen = [System.Collections.Generic.HashSet[int]]::new()',
      '  $queue.Enqueue($rootId); [void]$seen.Add($rootId)',
      '  while ($queue.Count -gt 0) {',
      '    $cur = $queue.Dequeue()',
      '    try {',
      '      $p = Get-Process -Id $cur -ErrorAction Stop',
      '      $result.Add(@{ pid = $cur; parentPid = $byId[$cur]; startTicks = $p.StartTime.ToFileTimeUtc().ToString(); state = "R" })',
      '    } catch {}',
      '    if ($byParent.ContainsKey($cur)) {',
      '      foreach ($child in $byParent[$cur]) {',
      '        if ($seen.Add($child)) { $queue.Enqueue($child) }',
      '      }',
      '    }',
      '  }',
      '}',
      'ConvertTo-Json -InputObject @($result) -Compress',
    ].join('\n');
    const list = await runPowerShellJson(script);
    return Array.isArray(list) ? list : list ? [list] : [];
  }
  throw new Error(`Unsupported platform for process tree: ${process.platform}`);
}

export async function waitOwnedExit(identity, timeoutMs = 15_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const now = await getProcessIdentity(identity.pid);
    if (now.exited || now.startTicks !== identity.startTicks) return true;
    await new Promise((done) => setTimeout(done, 40));
  }
  return false;
}

export function stopLauncherGracefully(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  if (process.platform === 'win32' && child.stdin && !child.stdin.destroyed) {
    try {
      child.stdin.write('SHUTDOWN\n');
      child.stdin.end();
      return;
    } catch {
      // fall through
    }
  }
  try {
    child.kill('SIGTERM');
  } catch {
    // ignore already exited
  }
}
