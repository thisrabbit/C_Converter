"""Work specific classify integral nhanh (only use when research save) for .cdr ZIP format + root.dat.

Read tree RIFF in content/root.dat, go according to con pointer 16 byte sang content/data/*.dat,
return a list of chunks (path, FourCC, and content) for comparison between files.
"""
import struct
import sys
import zipfile


def load(path):
    z = zipfile.ZipFile(path)
    names = z.read("content/dataFileList.dat").decode().split("\n")
    files = [z.read("content/data/" + n.strip()) for n in names if n.strip()]
    root = z.read("content/root.dat")
    leaves = []

    def walk(buf, pos, end, prefix):
        counts = {}
        while pos + 8 <= end:
            cc = buf[pos:pos + 4].decode("latin1")
            size = struct.unpack_from("<I", buf, pos + 4)[0]
            ds, de = pos + 8, pos + 8 + size
            if cc in ("LIST", "RIFF"):
                lt = buf[ds:ds + 4].decode("latin1")
                key = f"{cc}:{lt}"
                counts[key] = counts.get(key, 0) + 1
                walk(buf, ds + 4, de, f"{prefix}/{lt.strip()}[{counts[key] - 1}]")
            else:
                counts[cc] = counts.get(cc, 0) + 1
                idx, ln, off, _ = struct.unpack_from("<IIII", buf, ds)
                if idx == 0xFFFFFFFF:
                    data = buf[ds + 8:ds + 8 + ln]
                else:
                    data = files[idx][off:off + ln]
                leaves.append((f"{prefix}/{cc.strip()}[{counts[cc] - 1}]", cc, data))
            pos = de + (size & 1)

    walk(root, 12, len(root), "")
    return leaves


def hexdump(b, width=16):
    out = []
    for i in range(0, len(b), width):
        chunk = b[i:i + width]
        out.append(f"  {i:04X}: {chunk.hex(' '):<{width * 3}} |{''.join(chr(c) if 32 <= c < 127 else '.' for c in chunk)}|")
    return "\n".join(out)


if __name__ == "__main__":
    for path, cc, data in load(sys.argv[1]):
        if len(sys.argv) > 2 and sys.argv[2] not in path:
            continue
        print(f"{path} ({len(data)} byte)")
        print(hexdump(data))
