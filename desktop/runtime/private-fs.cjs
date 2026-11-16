'use strict';

const childProcess = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');

const FILE_PERSISTENT_ACLS = 0x00000008;
const SYSTEM_SID = 'S-1-5-18';
const BUILTIN_ADMINS_SID = 'S-1-5-32-544';

let cachedCurrentUserSid = null;
const volumeInfoCache = new Map();
const protectedDirCache = new Set();
const verifiedDirCache = new Set();

function normalizeCasePath(targetPath) {
  const resolved = path.resolve(targetPath);
  return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
}

function isSamePath(left, right) {
  return normalizeCasePath(left) === normalizeCasePath(right);
}

function isInsideRoot(root, target) {
  const rel = path.relative(normalizeCasePath(root), normalizeCasePath(target));
  return rel !== '' && rel !== '..' && !rel.startsWith(`..${path.sep}`) && !path.isAbsolute(rel);
}

function defaultRuntimeDataRoot(env = process.env) {
  if (process.platform === 'win32') {
    if (env.MAKO_RUNTIME_HOME !== undefined && env.MAKO_RUNTIME_HOME !== '') {
      if (!path.isAbsolute(env.MAKO_RUNTIME_HOME)) {
        throw new Error(`MAKO_RUNTIME_HOME must be an absolute path: ${env.MAKO_RUNTIME_HOME}`);
      }
      return env.MAKO_RUNTIME_HOME;
    }
    if (env.LOCALAPPDATA && path.isAbsolute(env.LOCALAPPDATA)) {
      return env.LOCALAPPDATA;
    }
    if (env.XDG_DATA_HOME && path.isAbsolute(env.XDG_DATA_HOME)) {
      return env.XDG_DATA_HOME;
    }
    return path.join(os.homedir(), 'AppData', 'Local');
  }
  if (env.XDG_DATA_HOME && path.isAbsolute(env.XDG_DATA_HOME)) {
    return env.XDG_DATA_HOME;
  }
  return path.join(os.homedir(), '.local', 'share');
}

function defaultRuntimeDataDir(appName = 'makokara-generator', env = process.env) {
  return path.join(defaultRuntimeDataRoot(env), appName);
}

function runEncodedPowerShell(script, extraEnv = {}) {
  const encoded = Buffer.from(`$ProgressPreference = 'SilentlyContinue'\n${script}`, 'utf16le').toString('base64');
  return childProcess.execFileSync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
    {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
      env: { ...process.env, ...extraEnv },
    },
  ).trim();
}

function getCurrentUserSid() {
  if (process.platform !== 'win32') return null;
  if (cachedCurrentUserSid) return cachedCurrentUserSid;
  const output = childProcess.execFileSync('whoami.exe', ['/user', '/fo', 'csv', '/nh'], {
    encoding: 'utf8',
    windowsHide: true,
  }).trim();
  const match = output.match(/S-1-\d+(?:-\d+)+/);
  if (!match) {
    throw new Error(`Unable to determine current Windows user SID from whoami output: ${output}`);
  }
  cachedCurrentUserSid = match[0];
  return cachedCurrentUserSid;
}

function findDeepestExistingPath(targetPath) {
  let current = path.resolve(targetPath);
  for (;;) {
    if (fs.existsSync(current)) return current;
    const parent = path.dirname(current);
    if (parent === current) {
      throw new Error(`Target volume root does not exist: ${targetPath}`);
    }
    current = parent;
  }
}

function assertNoSymlinkAncestors(targetPath, label = 'Private path') {
  const resolved = path.resolve(targetPath);
  let existing;
  try {
    existing = findDeepestExistingPath(resolved);
  } catch {
    throw new Error(`${label} root does not exist: ${resolved}`);
  }
  let cursor = existing;
  for (;;) {
    const info = fs.lstatSync(cursor);
    if (info.isSymbolicLink()) {
      throw new Error(`${label} cannot be or traverse a symlink: ${cursor}`);
    }
    const parent = path.dirname(cursor);
    if (parent === cursor) break;
    cursor = parent;
  }
  const canonical = fs.realpathSync(existing);
  if (!isSamePath(canonical, existing)) {
    throw new Error(`${label} cannot be or traverse a symlink: ${existing}`);
  }
  return existing;
}

function getWindowsVolumeInfo(targetPath) {
  if (process.platform !== 'win32') {
    return { volumeRoot: '/', fileSystemFlags: FILE_PERSISTENT_ACLS, fileSystemName: 'posix' };
  }
  const existing = findDeepestExistingPath(targetPath);
  const parsedRoot = path.parse(existing).root;
  const cacheKey = parsedRoot.toUpperCase();
  if (volumeInfoCache.has(cacheKey)) {
    return volumeInfoCache.get(cacheKey);
  }
  const psScript = [
    'Add-Type -TypeDefinition @"',
    'using System;',
    'using System.Runtime.InteropServices;',
    'using System.Text;',
    'public static class MakoWin32Volume {',
    '  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]',
    '  public static extern bool GetVolumePathNameW(string lpszFileName, StringBuilder lpszVolumePathName, uint cchBufferLength);',
    '  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]',
    '  public static extern bool GetVolumeInformationW(string lpRootPathName, IntPtr lpVolumeNameBuffer, uint nVolumeNameSize, IntPtr lpVolumeSerialNumber, IntPtr lpMaximumComponentLength, out uint lpFileSystemFlags, StringBuilder lpFileSystemNameBuffer, uint nFileSystemNameSize);',
    '}',
    '"@',
    '$target = $env:MAKO_VOL_TARGET',
    '$vol = New-Object System.Text.StringBuilder 1024',
    'if (-not [MakoWin32Volume]::GetVolumePathNameW($target, $vol, 1024)) {',
    '  throw [System.ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())',
    '}',
    '[uint32]$flags = 0',
    '$fsName = New-Object System.Text.StringBuilder 256',
    'if (-not [MakoWin32Volume]::GetVolumeInformationW($vol.ToString(), [IntPtr]::Zero, 0, [IntPtr]::Zero, [IntPtr]::Zero, [ref]$flags, $fsName, 256)) {',
    '  throw [System.ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())',
    '}',
    'Write-Output ($vol.ToString() + "|" + $flags + "|" + $fsName.ToString())',
  ].join('\n');
  const raw = runEncodedPowerShell(psScript, { MAKO_VOL_TARGET: existing });
  const [volumeRoot, flagsText, fileSystemName] = raw.split('|');
  const fileSystemFlags = Number(flagsText);
  if (!volumeRoot || !Number.isFinite(fileSystemFlags)) {
    throw new Error(`Unable to inspect volume capabilities for ${existing}: ${raw}`);
  }
  const result = { volumeRoot, fileSystemFlags, fileSystemName: fileSystemName || '' };
  if (isSamePath(volumeRoot, parsedRoot)) {
    volumeInfoCache.set(cacheKey, result);
  }
  return result;
}

function assertPersistentAclVolume(targetPath, options = {}) {
  if (process.platform !== 'win32') return null;
  const getter = options.getVolumeInfo ?? getWindowsVolumeInfo;
  const info = getter(targetPath);
  if ((info.fileSystemFlags & FILE_PERSISTENT_ACLS) === 0) {
    throw new Error(
      `Private runtime directory requires NTFS persistent ACLs (FILE_PERSISTENT_ACLS 0x00000008 missing on ${info.volumeRoot}, fs=${info.fileSystemName || 'unknown'}): ${targetPath}`,
    );
  }
  return info;
}

function isProtectedAncestorCached(resolved) {
  const key = normalizeCasePath(resolved);
  if (protectedDirCache.has(key)) return true;
  for (const root of protectedDirCache) {
    if (isInsideRoot(root, resolved)) return true;
  }
  return false;
}

function readWindowsDirectoryAclSids(dirPath) {
  const psScript = [
    '$acl = Get-Acl -LiteralPath $env:MAKO_ACL_TARGET',
    '$items = @($acl.Access | ForEach-Object {',
    '  $sid = $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value',
    '  [PSCustomObject]@{ sid = $sid; type = $_.AccessControlType.ToString() }',
    '})',
    'ConvertTo-Json -InputObject $items -Compress',
  ].join('\n');
  const raw = runEncodedPowerShell(psScript, { MAKO_ACL_TARGET: dirPath });
  if (!raw) return [];
  const parsed = JSON.parse(raw);
  return Array.isArray(parsed) ? parsed : [parsed];
}

function ensurePrivateDir(dirPath, options = {}) {
  const label = options.label ?? 'Private directory';
  const resolved = path.resolve(dirPath);
  assertNoSymlinkAncestors(resolved, label);

  if (process.platform !== 'win32') {
    fs.mkdirSync(resolved, { recursive: true, mode: 0o700 });
    const info = fs.lstatSync(resolved);
    if (info.isSymbolicLink() || !info.isDirectory()) {
      throw new Error(`${label} cannot be a symlink: ${resolved}`);
    }
    if (info.uid !== process.getuid()) {
      throw new Error(`${label} must be owned by current user: ${resolved}`);
    }
    if ((info.mode & 0o777) !== 0o700) {
      if (options.strictExistingMode) {
        throw new Error(`${label} must have mode 0700: ${resolved}`);
      }
      fs.chmodSync(resolved, 0o700);
    }
    return resolved;
  }

  if (isProtectedAncestorCached(resolved) && fs.existsSync(resolved)) {
    const info = fs.lstatSync(resolved);
    if (info.isSymbolicLink() || !info.isDirectory()) {
      throw new Error(`${label} cannot be a symlink: ${resolved}`);
    }
    if (!isSamePath(fs.realpathSync(resolved), resolved)) {
      throw new Error(`${label} cannot be a symlink: ${resolved}`);
    }
    return resolved;
  }

  assertPersistentAclVolume(resolved, options);
  fs.mkdirSync(resolved, { recursive: true });
  const info = fs.lstatSync(resolved);
  if (info.isSymbolicLink() || !info.isDirectory()) {
    throw new Error(`${label} cannot be a symlink: ${resolved}`);
  }
  if (!isSamePath(fs.realpathSync(resolved), resolved)) {
    throw new Error(`${label} cannot be a symlink: ${resolved}`);
  }

  if (!isProtectedAncestorCached(resolved)) {
    const sid = getCurrentUserSid();
    childProcess.execFileSync(
      'icacls.exe',
      [resolved, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`],
      { encoding: 'utf8', windowsHide: true },
    );
    const cacheKey = normalizeCasePath(resolved);
    protectedDirCache.add(cacheKey);
    verifiedDirCache.add(cacheKey);
  }
  return resolved;
}

function assertPrivateDir(dirPath, options = {}) {
  const label = options.label ?? 'Private directory';
  const resolved = path.resolve(dirPath);
  const info = fs.lstatSync(resolved);
  if (info.isSymbolicLink() || !info.isDirectory()) {
    throw new Error(`${label} must be private and cannot be a symlink: ${resolved}`);
  }
  if (!isSamePath(fs.realpathSync(resolved), resolved)) {
    throw new Error(`${label} must be private and cannot be a symlink: ${resolved}`);
  }

  if (process.platform !== 'win32') {
    if (info.uid !== process.getuid() || (info.mode & 0o777) !== 0o700) {
      throw new Error(`${label} must be private: ${resolved}`);
    }
    return resolved;
  }

  const cacheKey = normalizeCasePath(resolved);
  if (verifiedDirCache.has(cacheKey) || isProtectedAncestorCached(resolved)) {
    return resolved;
  }

  assertPersistentAclVolume(resolved, options);
  const currentSid = getCurrentUserSid();
  const allowedSids = new Set(
    options.strictOwnerOnly ? [currentSid] : [currentSid, SYSTEM_SID, BUILTIN_ADMINS_SID],
  );
  const rules = (options.readAclSids ?? readWindowsDirectoryAclSids)(resolved);
  let ownerAllowed = false;
  for (const rule of rules) {
    if (rule.type === 'Allow') {
      if (!allowedSids.has(rule.sid)) {
        throw new Error(`${label} must be private (unexpected Allow ACE for ${rule.sid}): ${resolved}`);
      }
      if (rule.sid === currentSid) {
        ownerAllowed = true;
      }
    }
  }
  if (!ownerAllowed) {
    throw new Error(`${label} must be private (missing Allow ACE for current user): ${resolved}`);
  }
  verifiedDirCache.add(cacheKey);
  return resolved;
}

function assertPrivateFile(filePath, options = {}) {
  const label = options.label ?? 'Private file';
  const expectedMode = options.mode ?? 0o600;
  const resolved = path.resolve(filePath);
  const info = fs.lstatSync(resolved);
  if (info.isSymbolicLink() || !info.isFile()) {
    throw new Error(`${label} must be private and cannot be a symlink: ${resolved}`);
  }
  assertNoSymlinkAncestors(path.dirname(resolved), label);
  if (process.platform !== 'win32') {
    if (info.uid !== process.getuid() || (info.mode & 0o777) !== expectedMode) {
      throw new Error(
        `${label} must be private (mode ${expectedMode.toString(8)}, actual ${(info.mode & 0o777).toString(8)}): ${resolved}`,
      );
    }
    return resolved;
  }
  assertPrivateDir(path.dirname(resolved), { ...options, label });
  return resolved;
}

async function writePrivateJsonAtomic(filePath, info, options = {}) {
  const resolved = path.resolve(filePath);
  ensurePrivateDir(path.dirname(resolved), options);
  const temp = `${resolved}.${process.pid}.${crypto.randomUUID()}.tmp`;
  const handle = await fsp.open(temp, 'w', 0o600);
  try {
    await handle.writeFile(JSON.stringify(info, null, 2) + '\n');
  } finally {
    await handle.close();
  }
  await fsp.rename(temp, resolved);
}

function clearCachesForTesting() {
  volumeInfoCache.clear();
  protectedDirCache.clear();
  verifiedDirCache.clear();
}

module.exports = {
  FILE_PERSISTENT_ACLS,
  assertNoSymlinkAncestors,
  assertPersistentAclVolume,
  assertPrivateDir,
  assertPrivateFile,
  clearCachesForTesting,
  defaultRuntimeDataDir,
  defaultRuntimeDataRoot,
  ensurePrivateDir,
  getCurrentUserSid,
  getWindowsVolumeInfo,
  isInsideRoot,
  isSamePath,
  normalizeCasePath,
  writePrivateJsonAtomic,
};
