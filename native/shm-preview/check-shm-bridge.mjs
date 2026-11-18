import test from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import crypto from 'node:crypto';
import path from 'node:path';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const addon = require('./build/shm_bridge.node');

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
