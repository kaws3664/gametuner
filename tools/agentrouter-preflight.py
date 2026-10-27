#!/usr/bin/env python3
"""agentrouter 网关预检：找出会被 400 content-blocked 的内容。

agentrouter 只允许 CN/EN/FR/DE/RU 的正文；其他语言、以及高熵 base64/二进制块
都会被判为 "Unsupported language"。发请求前先跑这个。

用法:
    python tools/agentrouter-preflight.py <文件或目录> [...]
    python tools/agentrouter-preflight.py --text "要发的正文"

退出码 0 = 干净；1 = 有问题（下面列出行号与原因）。
"""
from __future__ import annotations

import argparse
import math
import pathlib
import re
import sys

# 会被网关判为"不支持语言"的书写系统（CJK 汉字本身允许，只拦假名/谚文等）
BAD_SCRIPTS = {
    "日文假名": re.compile(r"[\u3040-\u309f\u30a0-\u30ff\u31f0-\u31ff]"),
    "韩文谚文": re.compile(r"[\u1100-\u11ff\u3130-\u318f\uac00-\ud7af]"),
    "泰文": re.compile(r"[\u0e00-\u0e7f]"),
    "阿拉伯文": re.compile(r"[\u0600-\u06ff\u0750-\u077f]"),
    "希伯来文": re.compile(r"[\u0590-\u05ff]"),
    "印地文": re.compile(r"[\u0900-\u097f]"),
    "希腊文": re.compile(r"[\u0370-\u03ff]"),
    "泰米尔文": re.compile(r"[\u0b80-\u0bff]"),
    "西里尔文（俄文允许，保留提示）": re.compile(r"[\u0400-\u04ff]"),
}
# 允许的西里尔（俄文）不报错，这里只做提醒，所以单独拿出来
SOFT_SCRIPTS = {"西里尔文（俄文允许，保留提示）"}

B64_RUN = re.compile(r"[A-Za-z0-9+/=_-]{120,}")
SKIP_EXT = {".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".dll", ".exe", ".pdb",
            ".zip", ".7z", ".gz", ".nsp", ".nca", ".bin", ".pak", ".so", ".dylib"}
# 构建产物与依赖目录：agent 不会把这些贴进 prompt，扫了只会制造噪音
SKIP_DIRS = {"bin", "obj", "artifacts", "node_modules", ".git", ".vs", "__pycache__", ".venv"}


def entropy(s: str) -> float:
    if not s:
        return 0.0
    counts = {}
    for ch in s:
        counts[ch] = counts.get(ch, 0) + 1
    return -sum((c / len(s)) * math.log2(c / len(s)) for c in counts.values())


def scan_text(text: str, name: str):
    problems = []
    for lineno, line in enumerate(text.splitlines(), 1):
        for label, rx in BAD_SCRIPTS.items():
            if rx.search(line):
                problems.append((name, lineno, label, line.strip()[:80]))
        for m in B64_RUN.finditer(line):
            blob = m.group(0)
            # 高熵且无空白的超长串 ≈ base64/哈希/二进制，网关会当乱码拦下
            if entropy(blob) > 4.0 and not re.search(r"[\s]", blob):
                problems.append((name, lineno, "高熵长串(base64/hash)",
                                 blob[:60] + "..."))
                break
    return problems


def scan_path(path: pathlib.Path, problems):
    if path.is_dir():
        for child in sorted(path.rglob("*")):
            if child.is_dir() or any(part in SKIP_DIRS for part in child.parts):
                continue
            if child.is_file() and child.suffix.lower() not in SKIP_EXT:
                scan_path(child, problems)
        return
    if path.suffix.lower() in SKIP_EXT:
        problems.append((str(path), 0, "二进制文件（不要贴进 prompt）", ""))
        return
    try:
        text = path.read_text(encoding="utf-8", errors="strict")
    except UnicodeDecodeError:
        problems.append((str(path), 0, "无法按 UTF-8 读取（疑似二进制）", ""))
        return
    problems.extend(scan_text(text, str(path)))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("paths", nargs="*")
    ap.add_argument("--text", help="直接检查一段要发送的正文")
    ap.add_argument("--soft", action="store_true", help="只报告硬性问题（默认已如此）")
    args = ap.parse_args()

    problems = []
    if args.text:
        problems.extend(scan_text(args.text, "<text>"))
    for raw in args.paths:
        scan_path(pathlib.Path(raw), problems)

    hard = [p for p in problems if p[2] not in SOFT_SCRIPTS or not args.soft]
    if not hard:
        print("预检通过：没有网关会拦的语言或乱码块。")
        return 0

    print("预检发现会被 agentrouter 400 拦下的内容：")
    for name, lineno, label, sample in hard:
        where = f"{name}:{lineno}" if lineno else name
        print(f"  [{label}] {where}" + (f"  {sample}" if sample else ""))
    print("\n处理办法：把非 CN/EN/FR/DE/RU 的文本译成英文（或中文）再发；"
          "base64/二进制不要贴进 prompt，改用工具读取。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
