import test from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import crypto from 'node:crypto';
import path from 'node:path';
import readline from 'node:readline';
import { execFileSync, spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { readPeExportsAndImports } from './build.mjs';

const require = createRequire(import.meta.url);
const addonPath = path.resolve(import.meta.dirname, 'build', 'shm_bridge.node');
const addon = require(addonPath);

if (process.platform === 'linux') {
    function createTestShmFile(size = 4096, mode = 0o600) {
        const name = `/dev/shm/mkg-shm-${crypto.randomBytes(16).toString('hex')}`;
        const fd = fs.openSync(name, 'w', mode);
        fs.ftruncateSync(fd, size);
        return { name, fd, size };
    }

    test('openReadOnly succeeds on valid 0600 tmpfs file and immediately unlinks', () => {
        const { name, fd, size } = createTestShmFile(4096, 0o600);
        assert.strictEqual(fs.existsSync(name), true);

        const mapping = addon.openReadOnly(name, size);
        assert.strictEqual(mapping.byteLength, size);
        assert.strictEqual(mapping.isClosed(), false);

        // Immediately unlinked from /dev/shm
        assert.strictEqual(fs.existsSync(name), false);

        // Clean up fd
        fs.closeSync(fd);
        mapping.close();
        assert.strictEqual(mapping.isClosed(), true);
    });

    test('Live writes through creator fd are accurately read by readSlot across 3 slots', () => {
        const { name, fd, size } = createTestShmFile(3072, 0o600);
        const mapping = addon.openReadOnly(name, size);

        const slotSize = 1024;
        const buf1 = Buffer.allocUnsafeSlow(slotSize);
        const buf2 = Buffer.allocUnsafeSlow(slotSize);
        const buf3 = Buffer.allocUnsafeSlow(slotSize);

        // Write slot 0
        const data0 = crypto.randomBytes(slotSize);
        fs.writeSync(fd, data0, 0, slotSize, 0);
        mapping.readSlot(0, slotSize, buf1);
        assert.deepStrictEqual(buf1, data0);

        // Write slot 1
        const data1 = crypto.randomBytes(slotSize);
        fs.writeSync(fd, data1, 0, slotSize, slotSize);
        addon.readSlot(mapping, slotSize, slotSize, buf2); // Test module-level readSlot
        assert.deepStrictEqual(buf2, data1);

        // Write slot 2
        const data2 = crypto.randomBytes(slotSize);
        fs.writeSync(fd, data2, 0, slotSize, slotSize * 2);
        mapping.readSlot(slotSize * 2, slotSize, buf3);
        assert.deepStrictEqual(buf3, data2);

        fs.closeSync(fd);
        addon.close(mapping); // Test module-level close
        assert.strictEqual(mapping.isClosed(), true);
    });

    test('Second openReadOnly on the same path fails (already unlinked)', () => {
        const { name, fd, size } = createTestShmFile(1024, 0o600);
        const mapping = addon.openReadOnly(name, size);

        assert.throws(() => {
            addon.openReadOnly(name, size);
        }, /Failed to open shm file/);

        fs.closeSync(fd);
        mapping.close();
    });

    test('Rejects 0644 permissions, wrong expectedBytes, non-tmpfs path, out-of-bounds, mismatched buffer length, and readSlot after close', () => {
        // 1. Wrong permissions (0644)
        {
            const { name, fd, size } = createTestShmFile(1024, 0o644);
            assert.throws(() => {
                addon.openReadOnly(name, size);
            }, /Invalid permissions/);
            fs.closeSync(fd);
            try { fs.unlinkSync(name); } catch {}
        }

        // 2. Wrong expectedBytes
        {
            const { name, fd, size } = createTestShmFile(1024, 0o600);
            assert.throws(() => {
                addon.openReadOnly(name, 2048);
            }, /File size mismatch/);
            fs.closeSync(fd);
            try { fs.unlinkSync(name); } catch {}
        }

        // 3. Non-tmpfs path (e.g. workspace .local file, even when XDG_RUNTIME_DIR points to it)
        {
            const localDir = path.resolve(import.meta.dirname, '../../.local/shm-bridge-test');
            fs.mkdirSync(localDir, { recursive: true, mode: 0o700 });
            const localPath = path.join(localDir, `mkg-shm-${crypto.randomBytes(16).toString('hex')}`);
            fs.writeFileSync(localPath, Buffer.alloc(1024));
            fs.chmodSync(localPath, 0o600);
            const prevXdg = process.env.XDG_RUNTIME_DIR;
            try {
                // Without XDG_RUNTIME_DIR pointing to localDir, directory whitelist rejects it first.
                assert.throws(() => {
                    addon.openReadOnly(localPath, 1024);
                }, /directory must be \/dev\/shm or XDG_RUNTIME_DIR/);

                // Even if XDG_RUNTIME_DIR points to a non-tmpfs directory, fstatfs rejects it.
                process.env.XDG_RUNTIME_DIR = localDir;
                assert.throws(() => {
                    addon.openReadOnly(localPath, 1024);
                }, /File is not on tmpfs/);
            } finally {
                if (prevXdg === undefined) delete process.env.XDG_RUNTIME_DIR;
                else process.env.XDG_RUNTIME_DIR = prevXdg;
                try { fs.unlinkSync(localPath); } catch {}
            }
        }

        // Valid setup for remaining checks
        const { name, fd, size } = createTestShmFile(1024, 0o600);
        const mapping = addon.openReadOnly(name, size);
        fs.closeSync(fd);

        const targetBuf = Buffer.allocUnsafeSlow(512);

        // 4. Out-of-bounds (offset + length > size)
        assert.throws(() => {
            mapping.readSlot(800, 512, targetBuf);
        }, /Out of bounds read/);

        // 5. Mismatched targetBuffer length
        assert.throws(() => {
            mapping.readSlot(0, 1024, targetBuf);
        }, /targetBuffer length must match exactFrameBytes/);

        // 6. readSlot after close()
        mapping.close();
        assert.strictEqual(mapping.isClosed(), true);

        assert.throws(() => {
            mapping.readSlot(0, 512, targetBuf);
        }, /Mapping is closed/);
    });

    test('close is idempotent when called multiple times', () => {
        const { name, fd, size } = createTestShmFile(1024, 0o600);
        const mapping = addon.openReadOnly(name, size);
        fs.closeSync(fd);

        assert.strictEqual(mapping.isClosed(), false);
        mapping.close();
        assert.strictEqual(mapping.isClosed(), true);
        // Call again should not throw
        mapping.close();
        addon.close(mapping);
        assert.strictEqual(mapping.isClosed(), true);
    });

    test('Rejects Linux path outside whitelist, invalid tokens, traversal, and symlinks; accepts normalized XDG_RUNTIME_DIR', () => {
        const validToken32 = crypto.randomBytes(16).toString('hex');

        // 1. /tmp tmpfs path outside whitelist
        {
            const tmpPath = `/tmp/mkg-shm-${validToken32}`;
            const fd = fs.openSync(tmpPath, 'w', 0o600);
            fs.ftruncateSync(fd, 1024);
            try {
                assert.throws(() => {
                    addon.openReadOnly(tmpPath, 1024);
                }, /directory must be \/dev\/shm or XDG_RUNTIME_DIR/);
            } finally {
                fs.closeSync(fd);
                try { fs.unlinkSync(tmpPath); } catch {}
            }
        }

        // 2. Non-whitelisted basename inside /dev/shm
        {
            const otherPath = `/dev/shm/other-${crypto.randomBytes(8).toString('hex')}`;
            const fd = fs.openSync(otherPath, 'w', 0o600);
            fs.ftruncateSync(fd, 1024);
            try {
                assert.throws(() => {
                    addon.openReadOnly('/dev/shm/other', 1024);
                }, /mkg-shm-<24\.\.64 lower-hex>/);
                assert.throws(() => {
                    addon.openReadOnly(otherPath, 1024);
                }, /mkg-shm-<24\.\.64 lower-hex>/);
            } finally {
                fs.closeSync(fd);
                try { fs.unlinkSync(otherPath); } catch {}
            }
        }

        // 3. Invalid token lengths, uppercase, non-hex, prefix variants
        for (const badBase of [
            `mkg-shm-test-${crypto.randomBytes(8).toString('hex')}`,
            `mkg-shm-${'a'.repeat(23)}`,
            `mkg-shm-${'a'.repeat(65)}`,
            `mkg-shm-${'A'.repeat(32)}`,
            `mkg-shm-${'g'.repeat(32)}`,
            'mkg-shm-',
        ]) {
            assert.throws(() => {
                addon.openReadOnly(`/dev/shm/${badBase}`, 1024);
            }, /mkg-shm-<24\.\.64 lower-hex>/);
        }

        // 4. Traversal, double slashes, subdirectories, missing parent
        assert.throws(() => {
            addon.openReadOnly(`/dev/shm/../dev/shm/mkg-shm-${validToken32}`, 1024);
        }, /cannot contain '\.\.'/);
        assert.throws(() => {
            addon.openReadOnly(`/dev/shm//mkg-shm-${validToken32}`, 1024);
        }, /directory must be \/dev\/shm or XDG_RUNTIME_DIR/);
        assert.throws(() => {
            addon.openReadOnly(`/dev/shm/subdir/mkg-shm-${validToken32}`, 1024);
        }, /directory must be \/dev\/shm or XDG_RUNTIME_DIR/);
        assert.throws(() => {
            addon.openReadOnly(`mkg-shm-${validToken32}`, 1024);
        }, /directory must be \/dev\/shm or XDG_RUNTIME_DIR/);
        assert.throws(() => {
            addon.openReadOnly(`/mkg-shm-${validToken32}`, 1024);
        }, /directory must be \/dev\/shm or XDG_RUNTIME_DIR/);

        // 5. Symlink inside /dev/shm must be rejected by O_NOFOLLOW without unlinking target
        {
            const targetName = `/dev/shm/mkg-shm-${crypto.randomBytes(16).toString('hex')}`;
            const linkName = `/dev/shm/mkg-shm-${crypto.randomBytes(16).toString('hex')}`;
            const fd = fs.openSync(targetName, 'w', 0o600);
            fs.ftruncateSync(fd, 1024);
            fs.symlinkSync(targetName, linkName);
            try {
                assert.throws(() => {
                    addon.openReadOnly(linkName, 1024);
                }, /Failed to open shm file/);
                assert.strictEqual(fs.existsSync(targetName), true);
            } finally {
                fs.closeSync(fd);
                try { fs.unlinkSync(linkName); } catch {}
                try { fs.unlinkSync(targetName); } catch {}
            }
        }

        // 6. XDG_RUNTIME_DIR trailing-slash normalization + 24/64 hex bounds + invalid XDG_RUNTIME_DIR values
        {
            const xdgDir = `/dev/shm/mkg-xdg-test-${crypto.randomBytes(8).toString('hex')}`;
            fs.mkdirSync(xdgDir, { mode: 0o700 });
            const prevXdg = process.env.XDG_RUNTIME_DIR;
            try {
                process.env.XDG_RUNTIME_DIR = `${xdgDir}///`;
                for (const token of ['0'.repeat(24), 'f'.repeat(64)]) {
                    const shmPath = `${xdgDir}/mkg-shm-${token}`;
                    const fd = fs.openSync(shmPath, 'w', 0o600);
                    fs.ftruncateSync(fd, 1024);
                    const mapping = addon.openReadOnly(shmPath, 1024);
                    assert.strictEqual(mapping.byteLength, 1024);
                    assert.strictEqual(fs.existsSync(shmPath), false);
                    fs.closeSync(fd);
                    mapping.close();
                }

                // Relative XDG_RUNTIME_DIR must not be accepted
                process.env.XDG_RUNTIME_DIR = 'relative/dir';
                assert.throws(() => {
                    addon.openReadOnly(`relative/dir/mkg-shm-${validToken32}`, 1024);
                }, /directory must be \/dev\/shm or XDG_RUNTIME_DIR/);

                // Root '/' XDG_RUNTIME_DIR must not be accepted
                process.env.XDG_RUNTIME_DIR = '/';
                assert.throws(() => {
                    addon.openReadOnly(`/mkg-shm-${validToken32}`, 1024);
                }, /directory must be \/dev\/shm or XDG_RUNTIME_DIR/);
            } finally {
                if (prevXdg === undefined) delete process.env.XDG_RUNTIME_DIR;
                else process.env.XDG_RUNTIME_DIR = prevXdg;
                try { fs.rmSync(xdgDir, { recursive: true, force: true }); } catch {}
            }
        }
    });
} else if (process.platform === 'win32') {
    function startWindowsSectionHelper(sectionName, byteLength) {
        const psScript = `
$ProgressPreference = 'SilentlyContinue'
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class MakoShmTestWin32 {
    [DllImport("kernel32.dll", EntryPoint = "CreateFileMappingW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr sa, uint flProtect, uint maxHigh, uint maxLow, string lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr MapViewOfFile(IntPtr hMap, uint access, uint offHigh, uint offLow, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UnmapViewOfFile(IntPtr addr);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);
}
"@
$name = $env:MAKO_TEST_SHM_NAME
$len = [uint64]$env:MAKO_TEST_SHM_LEN
$h = [MakoShmTestWin32]::CreateFileMappingW([IntPtr](-1), [IntPtr]::Zero, 0x08000004, [uint32]($len -shr 32), [uint32]($len -band 0xffffffff), $name)
if ($h -eq [IntPtr]::Zero) { throw "CreateFileMappingW failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
$ptr = [MakoShmTestWin32]::MapViewOfFile($h, 0x0006, 0, 0, [UIntPtr]$len)
if ($ptr -eq [IntPtr]::Zero) { [void][MakoShmTestWin32]::CloseHandle($h); throw "MapViewOfFile failed" }
[Console]::Out.WriteLine("READY")
[Console]::Out.Flush()
try {
    while ($null -ne ($line = [Console]::In.ReadLine())) {
        if ($line -eq "QUIT") { break }
        $parts = $line.Split(' ', 2)
        $offset = [int]$parts[0]
        $bytes = [Convert]::FromBase64String($parts[1])
        [Runtime.InteropServices.Marshal]::Copy($bytes, 0, [IntPtr]($ptr.ToInt64() + $offset), $bytes.Length)
        [Threading.Thread]::MemoryBarrier()
        [Console]::Out.WriteLine("OK")
        [Console]::Out.Flush()
    }
} finally {
    [void][MakoShmTestWin32]::UnmapViewOfFile($ptr)
    [void][MakoShmTestWin32]::CloseHandle($h)
}
`;
        const childEnv = { ...process.env, MAKO_TEST_SHM_NAME: sectionName, MAKO_TEST_SHM_LEN: String(byteLength) };
        for (const key of Object.keys(childEnv)) {
            if (key.toUpperCase() === 'PSMODULEPATH') delete childEnv[key];
        }
        const encoded = Buffer.from(psScript, 'utf16le').toString('base64');
        const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded], {
            env: childEnv,
            stdio: ['pipe', 'pipe', 'pipe'],
            windowsHide: true,
        });
        let stderr = '';
        child.stderr.on('data', (chunk) => { stderr += chunk; });
        const rl = readline.createInterface({ input: child.stdout });
        const queue = [];
        let waiter = null;
        rl.on('line', (line) => {
            if (waiter) {
                const w = waiter;
                waiter = null;
                w.resolve(line);
            } else {
                queue.push(line);
            }
        });
        const nextLine = () => {
            if (queue.length > 0) return Promise.resolve(queue.shift());
            return new Promise((resolve, reject) => {
                const timer = setTimeout(() => {
                    waiter = null;
                    reject(new Error(`PowerShell SHM helper timed out: ${stderr}`));
                }, 15000);
                waiter = {
                    resolve: (line) => {
                        clearTimeout(timer);
                        resolve(line);
                    },
                };
                child.once('exit', (code) => {
                    if (waiter) {
                        clearTimeout(timer);
                        waiter = null;
                        reject(new Error(`PowerShell SHM helper exited early (${code}): ${stderr}`));
                    }
                });
            });
        };
        return {
            async ready() {
                const line = await nextLine();
                assert.strictEqual(line, 'READY');
            },
            async writeSlot(offset, buffer) {
                child.stdin.write(`${offset} ${buffer.toString('base64')}\n`);
                const ack = await nextLine();
                assert.strictEqual(ack, 'OK');
            },
            async close() {
                if (child.exitCode === null) {
                    try { child.stdin.write('QUIT\n'); child.stdin.end(); } catch {}
                    await new Promise((resolve) => child.once('exit', resolve));
                }
                rl.close();
            },
        };
    }

    test('Windows PE32+ exports napi_register_module_v1 with zero static node.exe/electron.exe import', () => {
        const { exports, imports } = readPeExportsAndImports(addonPath);
        assert.strictEqual(exports.includes('napi_register_module_v1'), true);
        assert.strictEqual(imports.some((dll) => /^(node|electron)\.(exe|dll)$/i.test(dll)), false);
    });

    test('Windows shm_bridge.node loads dynamically in both node.exe and electron.exe without node.lib', () => {
        assert.strictEqual(typeof addon.openReadOnly, 'function');
        assert.strictEqual(typeof addon.readSlot, 'function');
        assert.strictEqual(typeof addon.close, 'function');

        const desktopRequire = createRequire(path.resolve(import.meta.dirname, '../../desktop/package.json'));
        const electronExe = desktopRequire('electron');
        const probeCode = `
            const m = require(${JSON.stringify(addonPath)});
            let errMsg = '';
            try { m.openReadOnly('Global\\\\forbidden', 4096); } catch (e) { errMsg = String(e.message || e); }
            console.log(JSON.stringify({
                names: Object.getOwnPropertyNames(m).sort(),
                types: [typeof m.openReadOnly, typeof m.readSlot, typeof m.close],
                errMsg,
            }));
        `;
        const out = execFileSync(
            electronExe,
            ['-e', probeCode],
            { env: { ...process.env, ELECTRON_RUN_AS_NODE: '1' }, encoding: 'utf8', windowsHide: true },
        ).trim();
        const parsed = JSON.parse(out);
        assert.deepStrictEqual(parsed.names, ['close', 'openReadOnly', 'readSlot']);
        assert.deepStrictEqual(parsed.types, ['function', 'function', 'function']);
        assert.match(parsed.errMsg, /Local\\mkg-shm-/);
    });

    test('Windows Local\\mkg-shm-* live 3-slot writes, bounds, validation, and paired UnmapViewOfFile+CloseHandle', async () => {
        const token = crypto.randomBytes(16).toString('hex');
        const name = `Local\\mkg-shm-${token}`;
        const slotSize = 4096;
        const totalSize = slotSize * 3;
        const helper = startWindowsSectionHelper(name, totalSize);
        await helper.ready();

        try {
            // Reject invalid names and mismatched page-aligned section size
            assert.throws(() => addon.openReadOnly(`Global\\mkg-shm-${token}`, totalSize), /Local\\mkg-shm-/);
            assert.throws(() => addon.openReadOnly('Local\\other-object', totalSize), /Local\\mkg-shm-/);
            assert.throws(() => addon.openReadOnly(name, totalSize + 4096), /Section size mismatch/);
            assert.throws(() => addon.openReadOnly(name, 4096), /Section size mismatch/);

            const mapping = addon.openReadOnly(name, totalSize);
            assert.strictEqual(mapping.byteLength, totalSize);
            assert.strictEqual(mapping.isClosed(), false);

            const buf0 = Buffer.allocUnsafeSlow(slotSize);
            const buf1 = Buffer.allocUnsafeSlow(slotSize);
            const buf2 = Buffer.allocUnsafeSlow(slotSize);

            const data0 = crypto.randomBytes(slotSize);
            await helper.writeSlot(0, data0);
            mapping.readSlot(0, slotSize, buf0);
            assert.deepStrictEqual(buf0, data0);

            const data1 = crypto.randomBytes(slotSize);
            await helper.writeSlot(slotSize, data1);
            addon.readSlot(mapping, slotSize, slotSize, buf1);
            assert.deepStrictEqual(buf1, data1);

            const data2 = crypto.randomBytes(slotSize);
            await helper.writeSlot(slotSize * 2, data2);
            mapping.readSlot(slotSize * 2, slotSize, buf2);
            assert.deepStrictEqual(buf2, data2);

            // Bounds & length errors
            assert.throws(() => mapping.readSlot(totalSize - 1024, slotSize, buf0), /Out of bounds read/);
            assert.throws(() => mapping.readSlot(0, slotSize * 2, buf0), /targetBuffer length must match exactFrameBytes/);

            // Close creator first; consumer mapping remains readable until closed, then section disappears completely.
            await helper.close();
            const stillReadable = Buffer.allocUnsafeSlow(slotSize);
            mapping.readSlot(0, slotSize, stillReadable);
            assert.deepStrictEqual(stillReadable, data0);

            // Idempotent paired close releases both UnmapViewOfFile and CloseHandle
            mapping.close();
            addon.close(mapping);
            assert.strictEqual(mapping.isClosed(), true);
            assert.throws(() => mapping.readSlot(0, slotSize, buf0), /Mapping is closed/);

            // Kernel destroys the Local\ section once both creator and consumer handles are released
            assert.throws(() => addon.openReadOnly(name, totalSize), /Failed to open shm section/);
        } finally {
            await helper.close();
        }
    });
}
