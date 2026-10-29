#!/usr/bin/env python3
"""ui-lint.py — hard gates for the GameTuner/FrameSmith UI polish pass.

Rules enforced on src/GameTuner.App/MainWindow.xaml (and App.xaml where noted):
  1. No FontSize below 11 anywhere (App.xaml + MainWindow.xaml).
  2. Margin values in MainWindow.xaml must come from the 4/8 scale: {0,4,8,12,16,24,32}.
     Negative margins are banned everywhere.
  3. Padding values in MainWindow.xaml must come from {0,4,8,12,16,24,32} per component.
  4. CornerRadius in MainWindow.xaml may only be 8 (controls) or 12 (panels).
  5. No raw hex colors in MainWindow.xaml — colors must come via StaticResource
     from App.xaml (single source of truth).
  6. FontFamily in MainWindow.xaml must be a StaticResource reference.

Exit code 0 = clean, 1 = violations found (printed to stdout).
Usage: python tools/ui-lint.py
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
APP_XAML = ROOT / "src/GameTuner.App/App.xaml"
MAIN_XAML = ROOT / "src/GameTuner.App/MainWindow.xaml"

SCALE = {0, 4, 8, 12, 16, 24, 32}
RADII = {"8", "12"}

ATTR_RE = re.compile(r'([A-Za-z:]+)="([^"]*)"')


def iter_elements(xaml: str):
    """Yield (tag, attr_dict) for each self-closing or open element tag."""
    for m in re.finditer(r"<([A-Za-z][A-Za-z0-9.]*)\b([^>]*?)(/?)>", xaml, re.S):
        tag = m.group(1)
        attrs = dict(ATTR_RE.findall(m.group(2)))
        yield tag, attrs


def check_font_sizes(tag: str, attrs: dict, file: str, n: int, out: list) -> None:
    fs = attrs.get("FontSize")
    if fs is not None:
        try:
            v = float(fs)
        except ValueError:
            return
        if v < 11:
            out.append(f"{file}:{n}: FontSize={fs} below 11 on <{tag}>")


def check_thickness(attrs: dict, keys: tuple, file: str, n: int, out: list,
                    enforce_scale: bool, tag: str) -> None:
    for key in keys:
        val = attrs.get(key)
        if val is None:
            continue
        parts = val.replace(",", " ").split()
        try:
            nums = [float(p) for p in parts]
        except ValueError:
            continue  # resource references etc.
        if any(x < 0 for x in nums):
            out.append(f"{file}:{n}: negative {key}=\"{val}\" on <{tag}>")
        if enforce_scale and any(x not in SCALE for x in nums):
            out.append(f"{file}:{n}: {key}=\"{val}\" off the 4/8 scale {sorted(SCALE)} on <{tag}>")


def check_main(xaml: str, out: list) -> None:
    for m in re.finditer(r"<[A-Za-z][^>]*>", xaml, re.S):
        n = xaml.count("\n", 0, m.start()) + 1
        tag = re.match(r"<([A-Za-z][A-Za-z0-9.]*)", m.group(0)).group(1)
        attrs = dict(ATTR_RE.findall(m.group(0)))
        check_font_sizes(tag, attrs, "MainWindow.xaml", n, out)
        check_thickness(attrs, ("Margin", "Padding"), "MainWindow.xaml", n, out,
                        enforce_scale=True, tag=tag)
        cr = attrs.get("CornerRadius")
        if cr is not None and cr not in RADII:
            out.append(f"MainWindow.xaml:{n}: CornerRadius=\"{cr}\" not in {sorted(RADII)} on <{tag}>")
        hexes = re.findall(r"#[0-9A-Fa-f]{3,8}\b", m.group(0))
        for hx in hexes:
            out.append(f"MainWindow.xaml:{n}: raw hex color {hx} on <{tag}> — use StaticResource from App.xaml")
        ff = attrs.get("FontFamily")
        if ff is not None and "StaticResource" not in ff:
            out.append(f"MainWindow.xaml:{n}: literal FontFamily=\"{ff}\" — reference a font resource instead")


def check_app(xaml: str, out: list) -> None:
    for m in re.finditer(r"<[A-Za-z][^>]*>", xaml, re.S):
        n = xaml.count("\n", 0, m.start()) + 1
        tag = re.match(r"<([A-Za-z][A-Za-z0-9.]*)", m.group(0)).group(1)
        attrs = dict(ATTR_RE.findall(m.group(0)))
        check_font_sizes(tag, attrs, "App.xaml", n, out)
        check_thickness(attrs, ("Margin",), "App.xaml", n, out,
                        enforce_scale=False, tag=tag)


def main() -> int:
    out: list = []
    if APP_XAML.exists():
        check_app(APP_XAML.read_text(encoding="utf-8-sig"), out)
    if MAIN_XAML.exists():
        check_main(MAIN_XAML.read_text(encoding="utf-8-sig"), out)
    if out:
        print(f"ui-lint: {len(out)} violation(s)")
        for line in out:
            print("  " + line)
        return 1
    print("ui-lint: clean")
    return 0


if __name__ == "__main__":
    sys.exit(main())
