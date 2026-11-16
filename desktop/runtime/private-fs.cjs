'use strict';

const childProcess = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');

const FILE_PERSISTENT_ACLS = 0x00000008;

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
  // PowerShell 7's inherited module path can break Windows PowerShell 5.1.
  const childEnv = { ...process.env, ...extraEnv };
  for (const key of Object.keys(childEnv)) {
    if (key.toUpperCase() === 'PSMODULEPATH') delete childEnv[key];
  }
  const encoded = Buffer.from(`$ProgressPreference = 'SilentlyContinue'\n${script}`, 'utf16le').toString('base64');
  return childProcess.execFileSync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
    {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
      env: childEnv,
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

// Cache only exact directories after full verification. Ancestors never vouch
// for pre-existing descendants. Same-user ACL mutation after admission is outside
// this process-local cache trust boundary; no per-file ACL shell is required.
function readWindowsDirectoryAclSids(dirPath) {
  const psScript = [
    '$acl = Get-Acl -LiteralPath $env:MAKO_ACL_TARGET',
    '$items = @($acl.Access | ForEach-Object {',
    '  $sid = $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value',
    '  [PSCustomObject]@{ sid = $sid; type = $_.AccessControlType.ToString(); rights = [int]$_.FileSystemRights; inheritance = [int]$_.InheritanceFlags; propagation = [int]$_.PropagationFlags }',
    '})',
    'ConvertTo-Json -InputObject @{ protected = $acl.AreAccessRulesProtected; rules = $items } -Compress',
  ].join('\n');
  return JSON.parse(runEncodedPowerShell(psScript, { MAKO_ACL_TARGET: dirPath }));
}

function assertWindowsDirectoryAcl(acl, currentSid, label, resolved) {
  const rules = acl.rules;
  if (acl.protected !== true || !Array.isArray(rules) || rules.length !== 1 ||
      rules[0].sid !== currentSid || rules[0].type !== 'Allow' ||
      rules[0].rights !== 2032127 || rules[0].inheritance !== 3 || rules[0].propagation !== 0) {
    throw new Error(`${label} must be private (protected current-user-only inheritable DACL required): ${resolved}`);
  }
}

function setWindowsDirectoryAcl(dirPath, sid) {
  runEncodedPowerShell([
    "$ErrorActionPreference = 'Stop'",
    '$acl = Get-Acl -LiteralPath $env:MAKO_ACL_TARGET',
    '$rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))',
    '$safe = $acl.AreAccessRulesProtected -and $rules.Count -eq 1',
    'if ($safe) {',
    '  $rule = $rules[0]',
    "  $safe = $rule.IdentityReference.Value -eq $env:MAKO_ACL_SID -and $rule.AccessControlType.ToString() -eq 'Allow' -and [int]$rule.FileSystemRights -eq 2032127 -and [int]$rule.InheritanceFlags -eq 3 -and [int]$rule.PropagationFlags -eq 0",
    '}',
    'if (-not $safe) {',
    // Replace the complete access section: /grant:r leaves other explicit ACEs.
    '$acl.SetSecurityDescriptorSddlForm("D:P(A;OICI;FA;;;" + $env:MAKO_ACL_SID + ")", [System.Security.AccessControl.AccessControlSections]::Access)',
    'Set-Acl -LiteralPath $env:MAKO_ACL_TARGET -AclObject $acl',
    '}',
  ].join('\n'), { MAKO_ACL_TARGET: dirPath, MAKO_ACL_SID: sid });
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

  assertPersistentAclVolume(resolved, options);
  fs.mkdirSync(resolved, { recursive: true });
  const info = fs.lstatSync(resolved);
  if (info.isSymbolicLink() || !info.isDirectory()) {
    throw new Error(`${label} cannot be a symlink: ${resolved}`);
  }
  if (!isSamePath(fs.realpathSync(resolved), resolved)) {
    throw new Error(`${label} cannot be a symlink: ${resolved}`);
  }

  const cacheKey = normalizeCasePath(resolved);
  if (!protectedDirCache.has(cacheKey)) {
    setWindowsDirectoryAcl(resolved, getCurrentUserSid());
  }
  assertPrivateDir(resolved, options);
  protectedDirCache.add(cacheKey);
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
  if (verifiedDirCache.has(cacheKey)) return resolved;
  assertPersistentAclVolume(resolved, options);
  const acl = (options.readAclSids ?? readWindowsDirectoryAclSids)(resolved);
  assertWindowsDirectoryAcl(acl, getCurrentUserSid(), label, resolved);
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
  assertWindowsDirectoryAcl,
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
