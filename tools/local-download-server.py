#!/usr/bin/env python3
"""
Local download server and release packager for LlmodelBox.
Packages built win-x64 binaries into a clean release directory with SHA-256
checksums and serves a lightweight local download website over HTTP.
"""

from __future__ import annotations

import argparse
import datetime
import functools
import hashlib
import html
import http.server
import json
import os
import platform
import shutil
import socket
import socketserver
import sys
import zipfile
from pathlib import Path


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def format_bytes(size: int) -> str:
    units = ["B", "KB", "MB", "GB"]
    value = float(size)
    for unit in units:
        if value < 1024.0 or unit == units[-1]:
            return f"{value:.2f} {unit}" if unit != "B" else f"{int(value)} B"
        value /= 1024.0
    return f"{size} B"


def detect_lan_ip() -> str:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.connect(("8.8.8.8", 80))
        return sock.getsockname()[0]
    except OSError:
        return "127.0.0.1"
    finally:
        sock.close()


def prepare_release(
    build_dir: Path,
    release_dir: Path,
    repo_root: Path,
    package_label: str,
) -> dict:
    exe_source = build_dir / "LlmodelBox.exe"
    if not exe_source.is_file():
        raise FileNotFoundError(f"Expected executable not found: {exe_source}")

    release_dir.mkdir(parents=True, exist_ok=True)

    exe_target = release_dir / "LlmodelBox.exe"
    if exe_source.resolve() != exe_target.resolve():
        shutil.copy2(exe_source, exe_target)

    zip_path = release_dir / "LlmodelBox-win-x64.zip"
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for item in sorted(build_dir.rglob("*")):
            if item.is_file() and item.suffix.lower() != ".pdb":
                rel = item.relative_to(build_dir)
                zf.write(item, arcname=str(rel).replace("\\", "/"))

        # Include clean runtime catalog & docs if present (never personal settings/chat/logs)
        for extra_name in ("models.json", "README.md"):
            extra = repo_root / extra_name
            if extra.is_file():
                zf.write(extra, arcname=extra_name)

    exe_sha = sha256_file(exe_target)
    zip_sha = sha256_file(zip_path)

    checksums_path = release_dir / "SHA256SUMS.txt"
    checksums_path.write_text(
        f"{exe_sha}  LlmodelBox.exe\n{zip_sha}  LlmodelBox-win-x64.zip\n",
        encoding="utf-8",
    )

    return {
        "package_label": package_label,
        "build_host": f"{platform.system()} ({platform.machine()})",
        "target_rid": "win-x64",
        "built_at": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC"),
        "exe_name": exe_target.name,
        "exe_size": exe_target.stat().st_size,
        "exe_size_human": format_bytes(exe_target.stat().st_size),
        "exe_sha256": exe_sha,
        "zip_name": zip_path.name,
        "zip_size": zip_path.stat().st_size,
        "zip_size_human": format_bytes(zip_path.stat().st_size),
        "zip_sha256": zip_sha,
    }


def render_index_html(meta: dict) -> bytes:
    esc = html.escape
    page = f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>LlmodelBox // Local Release Portal</title>
  <style>
    :root {{
      --bg: #0b0e14;
      --card: #121722;
      --border: #232b3b;
      --text: #e6edf3;
      --muted: #8b949e;
      --accent: #3b82f6;
      --accent-hover: #2563eb;
      --surface: #0f141d;
      --mono: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
      --sans: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
    }}
    * {{ box-sizing: border-box; }}
    body {{
      margin: 0;
      min-height: 100vh;
      background: radial-gradient(circle at top, #131b2b 0%, var(--bg) 55%);
      color: var(--text);
      font-family: var(--sans);
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 24px;
    }}
    .wrap {{
      width: 100%;
      max-width: 760px;
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: 14px;
      padding: 28px;
      box-shadow: 0 24px 60px rgba(0, 0, 0, 0.45);
    }}
    .badge {{
      display: inline-block;
      font-family: var(--mono);
      font-size: 12px;
      color: #93c5fd;
      background: rgba(59, 130, 246, 0.12);
      border: 1px solid rgba(59, 130, 246, 0.3);
      border-radius: 999px;
      padding: 4px 10px;
      margin-bottom: 12px;
    }}
    h1 {{
      margin: 0 0 8px 0;
      font-size: 26px;
      letter-spacing: -0.02em;
    }}
    .subtitle {{
      margin: 0 0 22px 0;
      color: var(--muted);
      font-size: 14px;
      line-height: 1.5;
    }}
    .meta-grid {{
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
      gap: 12px;
      margin-bottom: 22px;
    }}
    .meta-box {{
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 10px;
      padding: 12px 14px;
    }}
    .meta-label {{
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      color: var(--muted);
      margin-bottom: 4px;
    }}
    .meta-value {{
      font-family: var(--mono);
      font-size: 13px;
      color: var(--text);
      word-break: break-word;
    }}
    .downloads {{
      display: grid;
      gap: 12px;
      margin-bottom: 20px;
    }}
    .dl-card {{
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 14px;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 10px;
      padding: 16px;
    }}
    .dl-title {{
      font-family: var(--mono);
      font-size: 15px;
      font-weight: 600;
      margin-bottom: 4px;
    }}
    .dl-info {{
      font-size: 12px;
      color: var(--muted);
      font-family: var(--mono);
      word-break: break-all;
    }}
    .btn {{
      display: inline-block;
      text-decoration: none;
      font-size: 13px;
      font-weight: 600;
      padding: 10px 16px;
      border-radius: 8px;
      background: var(--accent);
      color: #ffffff;
      white-space: nowrap;
    }}
    .btn:hover {{ background: var(--accent-hover); }}
    .btn-secondary {{
      background: #1f2937;
      border: 1px solid var(--border);
      color: var(--text);
    }}
    .btn-secondary:hover {{ background: #293548; }}
    .footer {{
      display: flex;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: 10px;
      border-top: 1px solid var(--border);
      padding-top: 14px;
      font-size: 12px;
      color: var(--muted);
    }}
    .footer a {{
      color: #93c5fd;
      text-decoration: none;
      font-family: var(--mono);
    }}
    .footer a:hover {{ text-decoration: underline; }}
  </style>
</head>
<body>
  <main class="wrap">
    <div class="badge">LOCAL ARTIFACT SERVER // WIN-X64</div>
    <h1>LlmodelBox Release Download</h1>
    <p class="subtitle">
      Built and staged locally for Windows (x64). Download the standalone executable directly or grab the full release archive.
    </p>

    <section class="meta-grid">
      <div class="meta-box">
        <div class="meta-label">Packaging</div>
        <div class="meta-value">{esc(meta["package_label"])}</div>
      </div>
      <div class="meta-box">
        <div class="meta-label">Build Host</div>
        <div class="meta-value">{esc(meta["build_host"])}</div>
      </div>
      <div class="meta-box">
        <div class="meta-label">Target</div>
        <div class="meta-value">{esc(meta["target_rid"])}</div>
      </div>
      <div class="meta-box">
        <div class="meta-label">Built At</div>
        <div class="meta-value">{esc(meta["built_at"])}</div>
      </div>
    </section>

    <section class="downloads">
      <div class="dl-card">
        <div>
          <div class="dl-title">{esc(meta["exe_name"])}</div>
          <div class="dl-info">Size: {esc(meta["exe_size_human"])} | SHA-256: {esc(meta["exe_sha256"][:24])}...</div>
        </div>
        <a class="btn" href="/download/LlmodelBox.exe">Download .exe</a>
      </div>

      <div class="dl-card">
        <div>
          <div class="dl-title">{esc(meta["zip_name"])}</div>
          <div class="dl-info">Size: {esc(meta["zip_size_human"])} | SHA-256: {esc(meta["zip_sha256"][:24])}...</div>
        </div>
        <a class="btn btn-secondary" href="/download/LlmodelBox-win-x64.zip">Download .zip</a>
      </div>
    </section>

    <footer class="footer">
      <span>Press Ctrl+C in the builder terminal to stop this server.</span>
      <span><a href="/download/SHA256SUMS.txt">SHA256SUMS.txt</a> &middot; <a href="/health">/health</a></span>
    </footer>
  </main>
</body>
</html>
"""
    return page.encode("utf-8")


class ReleaseHandler(http.server.BaseHTTPRequestHandler):
    def __init__(self, *args, release_dir: Path, meta: dict, **kwargs):
        self.release_dir = release_dir
        self.meta = meta
        super().__init__(*args, **kwargs)

    def log_message(self, format: str, *args) -> None:
        sys.stdout.write(f"[http] {self.address_string()} - {format % args}\n")
        sys.stdout.flush()

    def _send_bytes(self, status: int, content_type: str, body: bytes, send_body: bool = True) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if send_body:
            self.wfile.write(body)

    def _handle_request(self, send_body: bool) -> None:
        path = self.path.split("?", 1)[0]
        if path in ("/", "/index.html"):
            self._send_bytes(200, "text/html; charset=utf-8", render_index_html(self.meta), send_body)
            return

        if path == "/health":
            body = json.dumps(self.meta, indent=2).encode("utf-8")
            self._send_bytes(200, "application/json; charset=utf-8", body, send_body)
            return

        allowed_files = {
            "/download/LlmodelBox.exe": ("LlmodelBox.exe", "application/vnd.microsoft.portable-executable"),
            "/download/LlmodelBox-win-x64.zip": ("LlmodelBox-win-x64.zip", "application/zip"),
            "/download/SHA256SUMS.txt": ("SHA256SUMS.txt", "text/plain; charset=utf-8"),
        }

        if path in allowed_files:
            filename, mime = allowed_files[path]
            file_path = self.release_dir / filename
            if not file_path.is_file():
                self.send_error(404, "Artifact not found")
                return

            size = file_path.stat().st_size
            self.send_response(200)
            self.send_header("Content-Type", mime)
            self.send_header("Content-Length", str(size))
            self.send_header("Content-Disposition", f'attachment; filename="{filename}"')
            self.end_headers()
            if send_body:
                with file_path.open("rb") as fh:
                    shutil.copyfileobj(fh, self.wfile, length=1024 * 256)
            return

        self.send_error(404, "Not found")

    def do_GET(self) -> None:
        self._handle_request(send_body=True)

    def do_HEAD(self) -> None:
        self._handle_request(send_body=False)


class ReusableTCPServer(socketserver.ThreadingMixIn, socketserver.TCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main() -> int:
    parser = argparse.ArgumentParser(description="Package and serve LlmodelBox release artifacts locally.")
    parser.add_argument("--build-dir", required=True, help="Directory containing the built LlmodelBox.exe")
    parser.add_argument("--release-dir", required=True, help="Directory to stage release artifacts")
    parser.add_argument("--repo-root", default=".", help="Repository root directory")
    parser.add_argument("--package-label", default="win-x64 Release", help="Human-readable packaging label")
    parser.add_argument("--host", default="0.0.0.0", help="Bind address (default: 0.0.0.0)")
    parser.add_argument("--port", type=int, default=8080, help="HTTP port (default: 8080)")
    parser.add_argument("--prepare-only", action="store_true", help="Only create zip and checksums, do not start server")
    args = parser.parse_args()

    build_dir = Path(args.build_dir).resolve()
    release_dir = Path(args.release_dir).resolve()
    repo_root = Path(args.repo_root).resolve()

    meta = prepare_release(build_dir, release_dir, repo_root, args.package_label)
    print(f"Prepared release in: {release_dir}")
    print(f"  - {meta['exe_name']} ({meta['exe_size_human']}) SHA-256: {meta['exe_sha256']}")
    print(f"  - {meta['zip_name']} ({meta['zip_size_human']}) SHA-256: {meta['zip_sha256']}")

    if args.prepare_only:
        return 0

    handler_cls = functools.partial(ReleaseHandler, release_dir=release_dir, meta=meta)
    lan_ip = detect_lan_ip()
    with ReusableTCPServer((args.host, args.port), handler_cls) as httpd:
        print("")
        print("Local Download Website is running:")
        print(f"  Local:   http://localhost:{args.port}/")
        if lan_ip != "127.0.0.1":
            print(f"  Network: http://{lan_ip}:{args.port}/")
        print("Press Ctrl+C to stop.")
        print("")
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nServer stopped.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
