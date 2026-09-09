#!/usr/bin/env python3
"""Serve a published SPA locally with index.html fallback for client routes."""

from __future__ import annotations

import argparse
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path, PurePosixPath
from urllib.parse import urlsplit


class SpaHandler(SimpleHTTPRequestHandler):
    def do_GET(self) -> None:
        request_path = urlsplit(self.path).path
        target = Path(self.translate_path(request_path))
        if not target.exists() and not PurePosixPath(request_path).suffix:
            self.path = "/index.html"
        super().do_GET()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("--port", type=int, default=8766)
    args = parser.parse_args()
    root = args.root.resolve(strict=True)
    handler = partial(SpaHandler, directory=str(root))
    with ThreadingHTTPServer(("127.0.0.1", args.port), handler) as server:
        print(f"Serving {root} at http://127.0.0.1:{args.port}/", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
