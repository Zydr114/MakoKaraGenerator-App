import _winapi
import ctypes
import hashlib
import msvcrt
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tempfile
from ctypes import wintypes
import zlib


class BasicLimits(ctypes.Structure):
    _fields_ = [
        ("process_time", ctypes.c_longlong), ("job_time", ctypes.c_longlong),
        ("flags", wintypes.DWORD), ("min_working_set", ctypes.c_size_t),
        ("max_working_set", ctypes.c_size_t), ("active_processes", wintypes.DWORD),
        ("affinity", ctypes.c_size_t), ("priority", wintypes.DWORD),
        ("scheduling", wintypes.DWORD),
    ]


class ExtendedLimits(ctypes.Structure):
    _fields_ = [
        ("basic", BasicLimits), ("io_counters", ctypes.c_uint64 * 6),
        ("process_memory", ctypes.c_size_t), ("job_memory", ctypes.c_size_t),
        ("peak_process_memory", ctypes.c_size_t), ("peak_job_memory", ctypes.c_size_t),
    ]


kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
kernel.CreateJobObjectW.restype = wintypes.HANDLE
kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
kernel.ResumeThread.argtypes = [wintypes.HANDLE]
kernel.ResumeThread.restype = wintypes.DWORD


def check_win(condition):
    if not condition:
        raise ctypes.WinError(ctypes.get_last_error())


def run(helper, args, cwd, ack=b"", metadata_valid=True):
    handles = set()

    def pipe():
        pair = _winapi.CreatePipe(None, 65536)
        handles.update(pair)
        return pair

    def close(handle):
        _winapi.CloseHandle(handle)
        handles.remove(handle)

    def stream(handle, mode):
        fd = msvcrt.open_osfhandle(handle, os.O_BINARY | (os.O_RDONLY if mode == "rb" else os.O_WRONLY))
        handles.remove(handle)
        return os.fdopen(fd, mode)

    job = kernel.CreateJobObjectW(None, None)
    check_win(job)
    try:
        limits = ExtendedLimits()
        limits.basic.flags = 0x100 | 0x200 | 0x2000
        limits.process_memory = limits.job_memory = 128 * 1024 * 1024
        check_win(kernel.SetInformationJobObject(job, 9, ctypes.byref(limits), ctypes.sizeof(limits)))
        input_read, input_write = pipe()
        output_read, output_write = pipe()
        error_read, error_write = pipe()
        metadata_read, metadata_write = pipe()
        child_handles = [input_read, output_write, error_write, metadata_write]
        for handle in child_handles:
            os.set_handle_inheritable(handle, True)
        startup = subprocess.STARTUPINFO(
            dwFlags=subprocess.STARTF_USESTDHANDLES,
            hStdInput=input_read, hStdOutput=output_write, hStdError=error_write,
            lpAttributeList={"handle_list": child_handles},
        )
        env = dict(os.environ, PATH=str(cwd), MAKO_MEDIA_METADATA_HANDLE=str(metadata_write) if metadata_valid else "9" * 20)
        cmd = subprocess.list2cmdline([str(helper), *map(str, args)])
        process, thread, _, _ = _winapi.CreateProcess(
            str(helper), cmd, None, None, True, 4 | subprocess.CREATE_NO_WINDOW, env, str(cwd), startup,
        )
        handles.update([process, thread])
        try:
            check_win(kernel.AssignProcessToJobObject(job, process))
        except OSError:
            _winapi.TerminateProcess(process, 65)
            raise
        check_win(kernel.ResumeThread(thread) == 1)
        for handle in child_handles:
            close(handle)
        with stream(input_write, "wb") as input_pipe:
            input_pipe.write(ack)
        assert _winapi.WaitForSingleObject(process, 15000) == 0, "helper did not exit"
        with stream(output_read, "rb") as output, stream(error_read, "rb") as error, stream(metadata_read, "rb") as metadata:
            return _winapi.GetExitCodeProcess(process), output.read(), metadata.read(), error.read()
    finally:
        _winapi.CloseHandle(job)
        for handle in handles:
            _winapi.CloseHandle(handle)


def records(raw):
    result = []
    offset = 0
    while offset < len(raw):
        magic, kind, size = struct.unpack_from("<8sII", raw, offset)
        assert magic == b"MKGPRE1\0" and size <= 16384
        offset += 16
        body = raw[offset:offset + size]
        assert len(body) == size
        result.append((kind, body))
        offset += size
    return result


def closure(wire):
    images = {}
    while wire and wire[0][0] == 4:
        _, body = wire.pop(0)
        size, digest, path_length = struct.unpack_from("<Q32sI", body)
        assert len(body) == 44 + path_length and 0 < path_length <= 4096
        text = body[44:].decode("utf-8")
        path = Path(text)
        assert path.is_absolute() and not text.startswith("\\\\?\\") and path not in images
        images[path] = (size, digest)
    assert images and wire and wire.pop(0) == (5, b"")
    return images


def png_chunk(kind, body):
    return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))


def test_wire(original, root):
    dlls = ("avutil-61.dll", "avcodec-63.dll", "avformat-63.dll")
    tool_dir = root / "\u539f\u751f \U0001f4fd"
    tool_dir.mkdir()
    helper = tool_dir / original.name
    shutil.copy2(original, helper)
    for dll in dlls:
        shutil.copy2(original.parent / dll, tool_dir / dll)

    exit_code, output, metadata, error = run(helper, ["--closure"], root, metadata_valid=False)
    assert exit_code == 0 and not metadata and not error, (exit_code, error)
    wire = records(output)
    images = closure(wire)
    assert not wire, "trailing stdout after closure terminator"
    assert {path.name for path in images if path.parent == tool_dir} == {helper.name, *dlls}
    system = Path(os.environ["SystemRoot"]) / "System32"
    assert any(path.parent == system for path in images)
    for path, (size, digest) in images.items():
        assert path.parent in (system, tool_dir), path
        assert path.stat().st_size == size, path
        with path.open("rb") as file:
            assert hashlib.file_digest(file, "sha256").digest() == digest, path

    pixels = b"\x00\x0a\x1a\x0d\x77\x09\x0a\x0d\x1a\xff\x01\x03"
    source = root / "\u6587\u4ef6 \U0001f5bc.png"
    png = b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", struct.pack(">IIBBBBB", 2, 2, 8, 2, 0, 0, 0))
    png += png_chunk(b"IDAT", zlib.compress(b"\0" + pixels[:6] + b"\0" + pixels[6:])) + png_chunk(b"IEND", b"")
    source.write_bytes(png)
    for mode, ack, raster in (("inspect", b"S", b""), ("decode", b"N", pixels)):
        args = [3, 0, mode, source, "mkg-libav-same-frame-v1"]
        exit_code, output, metadata, error = run(helper, args, root, ack)
        assert exit_code == 0 and output == raster and not error, (mode, exit_code, error)
        wire = records(metadata)
        for snapshot in range(3):
            actual = closure(wire)
            assert actual == images, (mode, snapshot, sorted(path.name for path in actual.keys() - images.keys()),
                                      sorted(path.name for path in images.keys() - actual.keys()))
        kind, frame = wire.pop(0)
        assert kind == 2 and len(frame) == 224
        assert struct.unpack_from("<III", frame, 52) == (2, 2, 1)
        assert frame[176:208] == hashlib.sha256(png).digest()
        assert closure(wire) == images
        assert wire == [(3, struct.pack("<QQII", 1, 1, mode == "decode", 0))]

    exit_code, output, metadata, error = run(helper, args, root, metadata_valid=False)
    assert exit_code == 65 and not output and not metadata and error == b"presentation-metadata-handle-invalid\n"

    (tool_dir / dlls[0]).rename(root / dlls[0])
    exit_code, output, metadata, error = run(helper, ["--closure"], root)
    assert exit_code == 65 and not output and not metadata and error == b"presentation-controlled-library-load-failed\n"
    print(f"Verified {len(images)} modules using only {system.name} and the Unicode deployment directory")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="mkg-wire-") as directory:
        test_wire(Path(sys.argv[1]), Path(directory).resolve())
