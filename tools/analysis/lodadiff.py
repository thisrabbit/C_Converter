"""Compare loda records by object number between the 2024 file (_25) and the file CorelDRAW downgraded to 2019 (_21).

loda: u32 len, u32 n, u32 offOffsets, u32 offTypes, u32 objType,
      u32 offsets[n+1] (offset last = len), u32 types[n] (in ascending order, REVERSE item automatically with offsets: offsets[i] ↔ types[n-1-i]).

  python lodadiff.py samples/gen
"""
import collections
import difflib
import glob
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(__file__))
from cdrtree import load  # noqa: E402


def parse_loda(d):
    ln, n, oo, ot, typ = struct.unpack_from("<5I", d, 0)
    offs = struct.unpack_from(f"<{n + 1}I", d, oo)
    types = struct.unpack_from(f"<{n}I", d, ot)
    args = collections.OrderedDict()
    for i, t in enumerate(types):
        args[t] = d[offs[n - 1 - i]:offs[n - i]]
    return typ, args


def byte_ops(a, b):
    sm = difflib.SequenceMatcher(None, a, b, autojunk=False)
    out = []
    for op, i1, i2, j1, j2 in sm.get_opcodes():
        if op != "equal":
            out.append(f"{op}@{i1:#x}:{a[i1:i2].hex()}->{b[j1:j2].hex()}"[:120])
    return out


if __name__ == "__main__":
    root = sys.argv[1]
    summary = collections.Counter()
    for f25 in sorted(glob.glob(os.path.join(root, "*_25.cdr"))):
        f21 = f25[:-7] + "_21.cdr"
        if not os.path.exists(f21):
            continue
        a = {p: d for p, c, d in load(f25)}
        b = {p: d for p, c, d in load(f21)}
        name = os.path.basename(f25)[:-7]
        for p in b:
            if not p.endswith("]") or "/loda[" not in p or p not in a or a[p] == b[p]:
                continue
            ta, aa = parse_loda(a[p])
            tb, ab = parse_loda(b[p])
            added = [hex(t) for t in ab if t not in aa]
            removed = [hex(t) for t in aa if t not in ab]
            changed = {hex(t): byte_ops(aa[t], ab[t]) for t in ab if t in aa and aa[t] != ab[t]}
            print(f"{name:<18} obj={ta:<3} {p.split('/', 2)[-1][:50]:<50} +{added} -{removed}")
            for t, ops in changed.items():
                print(f"{'':>24}arg {t}: {len(aa[int(t, 16)])}->{len(ab[int(t, 16)])}  {ops[:4]}")
                summary[(ta, t)] += 1
            for t in added:
                summary[(ta, "+" + t, ab[int(t, 16)].hex())] += 1
    print("\nTotal valid:")
    for k, v in summary.most_common():
        print(f"  {v:4} x {k}")
