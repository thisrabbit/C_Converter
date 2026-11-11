"""Create file "CorelDRAW newer version" simulated from one file 2024 (for testing experimental mode only).

  python simulate_newer.py <input_2024.cdr> <out.cdr> [--vrsn 2600] [--extra-key]

Change form type, fver, vrsn, and CoreVersion; --extra-key inserts an unusual JSON key into document.cdss.
This is not a real 2025 file; it only checks the experimental-mode code path.
"""
import argparse
import struct
import zipfile


def patch_root(root, vrsn):
    b = bytearray(root)
    b[11] = ord("R") + (vrsn // 100 - 25)  # 2500 = 'R' → 2600 = 'S'
    pos, end = 12, len(b)

    def walk(pos, end):
        while pos + 8 <= end:
            cc = bytes(b[pos:pos + 4])
            size = struct.unpack_from("<I", b, pos + 4)[0]
            ds = pos + 8
            if cc == b"LIST":
                walk(ds + 4, ds + size)
            elif cc == b"fver":
                struct.pack_into("<H", b, ds + 8, vrsn)
                b[ds + 8 + 6] = vrsn // 100
            elif cc == b"vrsn":
                struct.pack_into("<H", b, ds + 8, vrsn)
            pos = ds + size + (size & 1)

    walk(pos, end)
    return bytes(b)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--vrsn", type=int, default=2600)
    ap.add_argument("--extra-key", action="store_true")
    a = ap.parse_args()
    zin = zipfile.ZipFile(a.src)
    with zipfile.ZipFile(a.dst, "w") as zout:
        for info in zin.infolist():
            data = zin.read(info.filename)
            if info.filename == "content/root.dat":
                data = patch_root(data, a.vrsn)
            elif info.filename == "META-INF/metadata.xml":
                data = data.replace(b"<cdr:CoreVersion>2500</cdr:CoreVersion>", f"<cdr:CoreVersion>{a.vrsn}</cdr:CoreVersion>".encode())
            elif info.filename == "styles/document.cdss" and a.extra_key:
                data = data.replace(b'"version": 2500,', f'"version": {a.vrsn},\n  "newFeature{a.vrsn}": "1",'.encode(), 1)
            zout.writestr(info, data, compress_type=zipfile.ZIP_STORED if info.filename == "mimetype" else zipfile.ZIP_DEFLATED)
    print("created", a.dst)
