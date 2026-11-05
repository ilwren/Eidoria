#!/usr/bin/env python3
"""Verify that the Android APK contains Android-compatible, 16 KiB-aligned ONNX Runtime ELF assets."""

from __future__ import annotations

import re
import subprocess
import sys
import zipfile
from pathlib import Path


GLIBC_DEPENDENCIES = re.compile(
    r"(?:libdl\.so\.2|librt\.so\.1|libpthread\.so\.0|libstdc\+\+\.so\.6|"
    r"libgcc_s\.so\.1|libc\.so\.6|ld-linux(?:-[^\s]+)?\.so\.[0-9]+)"
)


def readelf(*args: str, binary: Path) -> str:
    result = subprocess.run(
        ["readelf", *args, str(binary)], check=True, capture_output=True, text=True
    )
    return result.stdout


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: verify-android-onnx-apk.py <apk>", file=sys.stderr)
        return 2

    apk = Path(sys.argv[1])
    if not apk.is_file():
        print(f"APK not found: {apk}", file=sys.stderr)
        return 2

    with zipfile.ZipFile(apk) as archive:
        names = set(archive.namelist())
        expected = "lib/arm64-v8a/libonnxruntime.so"
        if expected not in names:
            print(f"APK does not contain Android arm64 ONNX Runtime asset: {expected}", file=sys.stderr)
            return 1

        libraries = sorted(
            name
            for name in names
            if name.startswith("lib/arm64-v8a/libonnxruntime") and name.endswith(".so")
        )
        for name in libraries:
            payload = archive.read(name)
            binary = Path("/tmp") / f"mdexplorer-{apk.stem}-{Path(name).name}"
            binary.write_bytes(payload)
            try:
                dependencies = readelf("-dW", binary=binary)
                if GLIBC_DEPENDENCIES.search(dependencies):
                    print(f"Linux/glibc ONNX binary was packaged for Android: {name}", file=sys.stderr)
                    return 1

                program_headers = readelf("-lW", binary=binary)
                alignments = []
                for line in program_headers.splitlines():
                    fields = line.split()
                    if fields and fields[0] == "LOAD":
                        try:
                            alignments.append(int(fields[-1], 16))
                        except ValueError:
                            pass
                if not alignments:
                    print(f"No ELF LOAD segments found in {name}", file=sys.stderr)
                    return 1
                if any(alignment < 16 * 1024 for alignment in alignments):
                    print(
                        f"ONNX library is not 16 KiB page-aligned: {name} "
                        f"(LOAD alignments: {[hex(value) for value in alignments]})",
                        file=sys.stderr,
                    )
                    return 1
            finally:
                binary.unlink(missing_ok=True)

    print(f"Android ONNX assets verified: {len(libraries)} arm64 library/libraries in {apk}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
