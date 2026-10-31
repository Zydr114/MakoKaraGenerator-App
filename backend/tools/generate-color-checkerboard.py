#!/usr/bin/env python3
"""Generate the fixed RGB checkerboard fixture using only Python's standard library."""

from pathlib import Path
import struct
import zlib


WIDTH = 640
HEIGHT = 480
CELL_SIZE = 40
OUTPUT = Path(__file__).resolve().parents[1] / "example" / "color-checkerboard-640x480.png"


def png_chunk(kind: bytes, data: bytes) -> bytes:
    checksum = zlib.crc32(kind + data) & 0xFFFFFFFF
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", checksum)


def main() -> None:
    # Adapt the red/green coordinate colours and alternating blue channel from
    # ProjectRenderSessionTests.R8Public.Helpers.cs, R8WriteBackground.
    columns = WIDTH // CELL_SIZE
    rows = HEIGHT // CELL_SIZE
    scanlines = bytearray()
    for y in range(HEIGHT):
        row = y // CELL_SIZE
        scanlines.append(0)  # PNG filter: None.
        for x in range(WIDTH):
            column = x // CELL_SIZE
            red = 20 + 158 * column // (columns - 1)
            green = 30 + 176 * row // (rows - 1)
            blue = 70 if (column + row) % 2 == 0 else 210
            scanlines.extend((red, green, blue))

    header = struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 2, 0, 0, 0)
    png = b"\x89PNG\r\n\x1a\n"
    png += png_chunk(b"IHDR", header)
    png += png_chunk(b"IDAT", zlib.compress(scanlines, level=9))
    png += png_chunk(b"IEND", b"")
    OUTPUT.write_bytes(png)
    print(f"Generated {OUTPUT}: {WIDTH}x{HEIGHT}, RGB, {columns}x{rows} cells")


if __name__ == "__main__":
    main()
