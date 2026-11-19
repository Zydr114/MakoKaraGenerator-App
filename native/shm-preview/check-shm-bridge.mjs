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
        const name = `/dev/shm/mkg-shm-test-${crypto.randomBytes(8).toString('hex')}`;
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

        // 3. Non-tmpfs path (e.g. workspace .local file)
        {
            const localDir = path.resolve(import.meta.dirname, '../../.local/shm-bridge-test');
            fs.mkdirSync(localDir, { recursive: true, mode: 0o700 });
            const localPath = path.join(localDir, `non-tmpfs-${crypto.randomBytes(6).toString('hex')}.tmp`);
            fs.writeFileSync(localPath, Buffer.alloc(1024));
            fs.chmodSync(localPath, 0o600);
            try {
                assert.throws(() => {
                    addon.openReadOnly(localPath, 1024);
                }, /File is not on tmpfs/);
            } finally {
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
