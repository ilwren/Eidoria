#!/usr/bin/env python3
"""Fetch the manifest's two text embedding packages from their ModelScope mirrors.

ModelScope uses a JSON file-list API rather than Hugging Face's /resolve/<sha>/ URLs.
Each selected file is downloaded at the per-file revision returned by that API and
SHA-256/size verified before it is atomically installed.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "scripts" / "ai-models.manifest"
MODEL_IDS = {"text-embed-zh", "text-embed-en"}
DEFAULT_ENDPOINT = "https://modelscope.cn"
# Immutable ModelScope revisions corresponding to the ONNX/Tokenizer snapshots validated in CI.
MODELSCOPE_REVISIONS = {
    "text-embed-zh": "08d7186b7de51be7c12444137221ad96825593d6",
    "text-embed-en": "e374d41ef0f72744177114e9980ea32ccc4f5ae6",
}
CHUNK_BYTES = 1024 * 1024


def read_manifest() -> dict[str, tuple[str, list[str]]]:
    models: dict[str, tuple[str, list[str]]] = {}
    files: dict[str, list[str]] = {}
    for line_number, raw in enumerate(MANIFEST.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        fields = line.split("|")
        if fields[0] == "model" and len(fields) == 11:
            model_id = fields[1]
            if model_id in MODEL_IDS:
                models[model_id] = (fields[6], [])
                files[model_id] = []
        elif fields[0] == "file" and len(fields) == 3 and fields[1] in MODEL_IDS:
            path = PurePosixPath(fields[2])
            if path.is_absolute() or ".." in path.parts:
                raise ValueError(f"Unsafe path in manifest line {line_number}: {fields[2]}")
            files[fields[1]].append(fields[2])

    result = {model_id: (repo, files[model_id]) for model_id, (repo, _) in models.items()}
    missing = MODEL_IDS - result.keys()
    if missing:
        raise ValueError(f"Missing required model definitions in manifest: {sorted(missing)}")
    for model_id, (_, paths) in result.items():
        if not paths:
            raise ValueError(f"No files listed for {model_id}")
    return result


def request_json(url: str) -> dict:
    request = Request(url, headers={"User-Agent": "MdExplorer-real-model-smoke/1.0"})
    with urlopen(request, timeout=45) as response:
        return json.load(response)


def download_file(url: str, destination: Path, expected_size: int, expected_sha256: str) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_name(destination.name + ".part")
    digest = hashlib.sha256()
    byte_count = 0
    request = Request(url, headers={"User-Agent": "MdExplorer-real-model-smoke/1.0"})
    try:
        with urlopen(request, timeout=90) as response, partial.open("wb") as output:
            while True:
                chunk = response.read(CHUNK_BYTES)
                if not chunk:
                    break
                output.write(chunk)
                digest.update(chunk)
                byte_count += len(chunk)
        actual_sha256 = digest.hexdigest()
        if expected_size <= 0 or byte_count != expected_size:
            raise ValueError(f"size mismatch: expected {expected_size}, received {byte_count}")
        if not re.fullmatch(r"[0-9a-fA-F]{64}", expected_sha256):
            raise ValueError("ModelScope API did not provide a valid SHA-256")
        if actual_sha256.lower() != expected_sha256.lower():
            raise ValueError(f"SHA-256 mismatch: expected {expected_sha256}, got {actual_sha256}")
        os.replace(partial, destination)
    except BaseException:
        partial.unlink(missing_ok=True)
        raise


def fetch_modelscope(destination: Path, endpoint: str) -> None:
    checksums: list[tuple[str, str]] = []
    models = read_manifest()
    for model_id, (repo, required_paths) in models.items():
        pinned_revision = MODELSCOPE_REVISIONS[model_id]
        list_url = f"{endpoint}/api/v1/models/{repo}/repo/files?Revision={pinned_revision}&Recursive=true"
        print(f"== ModelScope: {repo}@{pinned_revision}", flush=True)
        inventory = request_json(list_url)
        if inventory.get("Code") != 200:
            raise RuntimeError(f"ModelScope file-list API failed for {repo}: {inventory.get('Message', inventory)}")
        data = inventory.get("Data") or {}
        entries = {
            item.get("Path"): item
            for item in data.get("Files", [])
            if item.get("Type") == "blob" and item.get("Path")
        }

        for relative_path in required_paths:
            entry = entries.get(relative_path)
            if entry is None:
                raise FileNotFoundError(f"ModelScope mirror {repo} is missing {relative_path}")
            revision = entry.get("Revision")
            expected_size = int(entry.get("Size") or 0)
            expected_sha256 = str(entry.get("Sha256") or "")
            if revision != pinned_revision:
                raise ValueError(
                    f"ModelScope returned an unexpected revision for {repo}/{relative_path}: "
                    f"expected {pinned_revision}, got {revision!r}"
                )

            query = urlencode({"Revision": revision, "FilePath": relative_path})
            url = f"{endpoint}/api/v1/models/{repo}/repo?{query}"
            output_path = destination / model_id / relative_path
            print(f"   downloading {model_id}/{relative_path} ({expected_size} bytes)", flush=True)
            download_file(url, output_path, expected_size, expected_sha256)
            checksums.append((expected_sha256.lower(), f"{model_id}/{relative_path}"))

    checksum_file = destination / "checksums.txt"
    checksum_file.parent.mkdir(parents=True, exist_ok=True)
    temp_checksum_file = checksum_file.with_suffix(".txt.part")
    temp_checksum_file.write_text(
        "".join(f"{digest}  {relative_path}\n" for digest, relative_path in sorted(checksums, key=lambda item: item[1])),
        encoding="ascii",
    )
    os.replace(temp_checksum_file, checksum_file)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dir", required=True, type=Path, help="Model directory to populate")
    parser.add_argument("--endpoint", default=os.environ.get("MDEXPLORER_MODELSCOPE_ENDPOINT", DEFAULT_ENDPOINT))
    args = parser.parse_args()
    endpoint = args.endpoint.rstrip("/")
    try:
        fetch_modelscope(args.dir, endpoint)
    except (HTTPError, URLError, TimeoutError, OSError, ValueError, RuntimeError) as error:
        print(f"ModelScope download failed: {error}", file=sys.stderr, flush=True)
        return 2
    print(f"ModelScope files downloaded and SHA-256 checked into {args.dir}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
