'use strict';

const fs = require('node:fs');
const path = require('node:path');
const privateFs = require('./private-fs.cjs');
const {
  compatibleProductVersion,
  validateDesktopDeployment,
  validateDesktopDeploymentAsync,
} = require('./backend-host.cjs');

function packagedEntry(resources, relative, directory = false) {
  const target = path.join(resources, relative);
  const info = fs.lstatSync(target, { throwIfNoEntry: false });
  if (!(directory ? info?.isDirectory() : info?.isFile()) || !privateFs.isSamePath(fs.realpathSync(target), target)) {
    throw new Error(`Missing or linked Windows Portable payload: ${relative}`);
  }
  return target;
}

function finalizeWindowsPortableResources(resources, frontendDist, backendDist, validated) {
  const { deployment, gpuManifest, verifiedDeployment } = validated;
  if (!compatibleProductVersion(deployment.version)) {
    throw new Error(`Incompatible Windows Portable backend version: ${deployment.version}`);
  }
  const shmAddonSha256 = gpuManifest.files['desktop/shm_bridge.node'];
  if (gpuManifest.platform !== 'win-x64' || gpuManifest.defaultTransport !== 'shm' || !shmAddonSha256) {
    throw new Error('Missing matched Windows x64 SHM bridge in desktop deployment');
  }
  const shmAddon = packagedEntry(backendDist, 'desktop/shm_bridge.node');
  const mediaTools = packagedEntry(resources, 'media-tools', true);
  const ffmpeg = packagedEntry(mediaTools, 'ffmpeg.exe');
  const ffprobe = packagedEntry(mediaTools, 'ffprobe.exe');
  return { frontendDist, backendDist, shmAddon, shmAddonSha256, ffmpeg, ffprobe, verifiedDeployment };
}

function resolveWindowsPortableResources(resourcesDir) {
  const resources = fs.realpathSync(resourcesDir);
  const frontendDist = packagedEntry(resources, 'frontend-dist', true);
  packagedEntry(frontendDist, 'index.html');
  const backendDist = packagedEntry(resources, 'backend-dist', true);
  const validated = validateDesktopDeployment(backendDist);
  return finalizeWindowsPortableResources(resources, frontendDist, backendDist, validated);
}

async function resolveWindowsPortableResourcesAsync(resourcesDir, options = {}) {
  const resources = fs.realpathSync(resourcesDir);
  const frontendDist = packagedEntry(resources, 'frontend-dist', true);
  packagedEntry(frontendDist, 'index.html');
  const backendDist = packagedEntry(resources, 'backend-dist', true);
  const validated = await validateDesktopDeploymentAsync(backendDist, '45.2.1', options);
  return finalizeWindowsPortableResources(resources, frontendDist, backendDist, validated);
}

function ensureWindowsPortableTempDir(sessionPaths) {
  return privateFs.ensurePrivateDir(path.join(sessionPaths.base, 'temp'), { label: 'Standalone temp directory' });
}

function windowsPortableBackendOptions(resources, sessionPaths) {
  const temp = ensureWindowsPortableTempDir(sessionPaths);
  return {
    ffmpeg: resources.ffmpeg,
    ffprobe: resources.ffprobe,
    requireMediaTools: true,
    cwd: temp,
    env: { TEMP: temp, TMP: temp },
  };
}

module.exports = {
  ensureWindowsPortableTempDir,
  resolveWindowsPortableResources,
  resolveWindowsPortableResourcesAsync,
  windowsPortableBackendOptions,
};
