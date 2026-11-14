#!/usr/bin/env python3
"""Retired M03 v1 file-marker + WaitHandle cancellation probe.

SDK v1 was removed in M14 Wave 1. SDK v2 prohibits script file I/O
(`System.IO.File`) and blocking synchronization primitives (`WaitHandle`),
making this file-marker probe inapplicable. HTTP seek generation cancellation
and Worker process reuse are covered by `probe.py --backend gpu` and the
backend host integration suite.
"""
import sys


def main():
    print(
        "cancel_probe.py is retired in M14: SDK v1 file-marker + WaitHandle "
        "probing was removed with SDK v1 (SDK v2 bans script I/O and blocking "
        "waits). Use probe.py --backend gpu and host integration tests instead.",
        file=sys.stderr,
    )
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
