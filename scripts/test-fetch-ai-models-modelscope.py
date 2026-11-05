#!/usr/bin/env python3
"""Offline regression test for the ModelScope downloader using a local fake API."""
from __future__ import annotations

import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
from pathlib import Path
import tempfile
import threading
from urllib.parse import parse_qs, urlparse

SCRIPT = Path(__file__).with_name("fetch-ai-models-modelscope.py")
spec = importlib.util.spec_from_file_location("modelscope_downloader", SCRIPT)
assert spec and spec.loader
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

models = module.read_manifest()
repo_to_files = {repo: files for repo, files in models.values()}
file_bytes = {
    (repo, relative_path): (f"fixture:{repo}:{relative_path}".encode("utf-8") * 64)
    for repo, paths in repo_to_files.items()
    for relative_path in paths
}
model_id_by_repo = {repo: model_id for model_id, (repo, _) in models.items()}
revisions = {
    (repo, relative_path): module.MODELSCOPE_REVISIONS[model_id_by_repo[repo]]
    for repo, relative_path in file_bytes
}


class FakeModelScopeHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802 - stdlib handler protocol
        parsed = urlparse(self.path)
        prefix = "/api/v1/models/"
        if not parsed.path.startswith(prefix):
            self.send_error(404)
            return
        suffix = parsed.path[len(prefix):]
        if suffix.endswith("/repo/files"):
            repo = suffix[: -len("/repo/files")].strip("/")
            query = parse_qs(parsed.query)
            expected_revision = module.MODELSCOPE_REVISIONS[model_id_by_repo[repo]]
            if query.get("Revision", [""])[0] != expected_revision:
                self.send_error(400, "unexpected model revision")
                return
            entries = []
            for candidate_repo, path in file_bytes:
                if candidate_repo != repo:
                    continue
                content = file_bytes[(candidate_repo, path)]
                entries.append({
                    "Path": path,
                    "Type": "blob",
                    "Revision": revisions[(candidate_repo, path)],
                    "Size": len(content),
                    "Sha256": hashlib.sha256(content).hexdigest(),
                })
            self._write_json({"Code": 200, "Data": {"Files": entries}})
            return
        if suffix.endswith("/repo"):
            repo = suffix[: -len("/repo")].strip("/")
            query = parse_qs(parsed.query)
            relative_path = query.get("FilePath", [""])[0]
            content = file_bytes.get((repo, relative_path))
            expected_revision = revisions.get((repo, relative_path))
            if content is None or query.get("Revision", [""])[0] != expected_revision:
                self.send_error(404)
                return
            self.send_response(200)
            self.send_header("Content-Length", str(len(content)))
            self.end_headers()
            self.wfile.write(content)
            return
        self.send_error(404)

    def _write_json(self, value: dict) -> None:
        payload = json.dumps(value).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, _format: str, *_args: object) -> None:
        pass


server = ThreadingHTTPServer(("127.0.0.1", 0), FakeModelScopeHandler)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()
try:
    with tempfile.TemporaryDirectory(prefix="mdexplorer-modelscope-test-") as temp:
        destination = Path(temp) / "models"
        endpoint = f"http://127.0.0.1:{server.server_port}"
        module.fetch_modelscope(destination, endpoint)
        expected_files = sum(len(paths) for _, paths in models.values())
        checksum_lines = (destination / "checksums.txt").read_text(encoding="ascii").splitlines()
        assert len(checksum_lines) == expected_files, (len(checksum_lines), expected_files)
        for (repo, relative_path), content in file_bytes.items():
            model_id = next(model_id for model_id, (source, _) in models.items() if source == repo)
            installed = destination / model_id / relative_path
            assert installed.read_bytes() == content, installed
            expected_hash = hashlib.sha256(content).hexdigest()
            assert f"{expected_hash}  {model_id}/{relative_path}" in checksum_lines
finally:
    server.shutdown()
    server.server_close()
    thread.join(timeout=2)

print("ModelScope downloader offline regression passed (manifest paths, revision, size, SHA-256)")
