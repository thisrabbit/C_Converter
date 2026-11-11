"""Compare results opened with CorelDRAW 2019 (report.json + PNG from verify-2019.ps1):
file created by C_Converter create (<name>_25_2019) with file Corel saved automatically with Save As 2019 (<name>_21).

  python compare_render.py <verification folder>
"""
import json
import os
import sys

import numpy as np
from PIL import Image


def strip(o):
    """Ignore unstable fields between two saves (for example, auto-generated object names)."""
    if isinstance(o, dict):
        return {k: strip(v) for k, v in o.items() if k not in ("file", "seconds", "name")}
    if isinstance(o, list):
        return [strip(v) for v in o]
    return o


def first_diff(a, b, path=""):
    if type(a) is not type(b):
        return f"{path}: {a!r} ≠ {b!r}"
    if isinstance(a, dict):
        for k in sorted(set(a) | set(b)):
            if k not in a or k not in b:
                return f"{path}/{k}: only in {'Corel' if k in b else 'C_Converter'}"
            d = first_diff(a[k], b[k], f"{path}/{k}")
            if d:
                return d
    elif isinstance(a, list):
        if len(a) != len(b):
            return f"{path}: {len(a)} elements ≠ {len(b)}"
        for i, (x, y) in enumerate(zip(a, b)):
            d = first_diff(x, y, f"{path}[{i}]")
            if d:
                return d
    elif a != b:
        return f"{path}: C_Converter={a!r} Corel={b!r}"
    return None


def pixel_diff(p1, p2):
    if not (os.path.exists(p1) and os.path.exists(p2)):
        return None
    a = np.asarray(Image.open(p1).convert("RGB"), dtype=np.int16)
    b = np.asarray(Image.open(p2).convert("RGB"), dtype=np.int16)
    if a.shape != b.shape:
        return f"image size {a.shape} ≠ {b.shape}"
    d = np.abs(a - b).max(axis=2)
    bad = int((d > 8).sum())
    return None if bad == 0 else f"{bad} differing pixels (maximum channel difference {int(d.max())})"


if __name__ == "__main__":
    root = sys.argv[1]
    report = {e["file"]: e for e in json.load(open(os.path.join(root, "report.json"), encoding="utf-8-sig"))}
    ok = total = 0
    for name in sorted(n[: -len("_21")] for n in report if n.endswith("_21")):
        ours, corel = report.get(name + "_25_2019"), report[name + "_21"]
        if ours is None:
            continue
        total += 1
        problems = []
        if not ours["opened"]:
            problems.append("CorelDRAW 2019 FAILED TO OPEN: " + ours.get("error", ""))
        else:
            d = first_diff(strip(ours), strip(corel))
            if d:
                problems.append("structure: " + d)
            for page in corel.get("pages", []):
                i = page["index"]
                pd = pixel_diff(os.path.join(root, f"{name}_25_2019.p{i}.png"), os.path.join(root, f"{name}_21.p{i}.png"))
                if pd:
                    problems.append(f"page {i}: {pd}")
        if problems:
            print(f"DIFFERENT   {name}")
            for p in problems:
                print("        - " + p)
        else:
            ok += 1
            print(f"MATCH  {name}")
    print(f"\n{ok}/{total} identical when opened in CorelDRAW 2019 (structure + shape image)")
