#!/usr/bin/env python3
"""Builds a Windows .ico from the macOS iconset PNGs (PNG-compressed entries)."""
import struct, sys, pathlib

src = pathlib.Path(sys.argv[1])
dst = pathlib.Path(sys.argv[2])
sizes = [(16, "icon_16x16.png"), (32, "icon_32x32.png"), (64, "icon_32x32@2x.png"),
         (128, "icon_128x128.png"), (256, "icon_256x256.png")]
entries = [(s, (src / f).read_bytes()) for s, f in sizes]

header = struct.pack("<HHH", 0, 1, len(entries))
offset = 6 + 16 * len(entries)
dir_entries, blobs = b"", b""
for size, blob in entries:
    dim = 0 if size >= 256 else size
    dir_entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
    blobs += blob
    offset += len(blob)
dst.write_bytes(header + dir_entries + blobs)
print(f"{dst}: {len(entries)} images, {dst.stat().st_size} bytes")
