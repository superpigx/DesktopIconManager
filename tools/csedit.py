"""按文件自身换行符做精确替换的小工具（避免 CRLF/LF 混用导致匹配失败）。

用法：
    raw, nl = read(path)        # 读并探测主换行符
    raw = replace(raw, old, new)# 替换（自动把 old/new 的 \n 转成 nl）
    write(path, raw)            # 写回（保持原换行符）

约定：old/new 用 '\n' 书写，函数内部自动转换。
匹配失败时抛异常并打印首个不一致位置，绝不静默跳过。
"""
import io
import os


def read(path, enc="utf-8-sig"):
    raw = open(path, "rb").read()
    crlf = raw.count(b"\r\n")
    lf = raw.count(b"\n") - crlf
    nl = "\r\n" if crlf >= lf else "\n"
    text = raw.decode(enc)
    if text.startswith("﻿"):
        text = text[1:]
    return text, nl


def write(path, text, enc="utf-8-sig"):
    data = text.encode(enc)
    open(path, "wb").write(data)
    return len(data)


def to_nl(s, nl):
    return s.replace("\r\n", "\n").replace("\r", "\n").replace("\n", nl)


def replace(text, old, new, nl="\n", count=1, label=""):
    old_n = to_nl(old, nl)
    new_n = to_nl(new, nl)
    if old_n not in text:
        # 打印首个不一致位置附近，便于定位
        head = old_n.split(nl)[0]
        idx = text.find(head)
        near = text[max(0, idx): idx + len(head) + 120] if idx >= 0 else "(首行都没找到)"
        raise AssertionError(
            "未命中%s\n  首行: %r\n  实际附近: %r" % (label, head, near))
    return text.replace(old_n, new_n, count)


def check(path):
    raw = open(path, "rb").read()
    crlf = raw.count(b"\r\n")
    lf = raw.count(b"\n") - crlf
    return crlf, lf
