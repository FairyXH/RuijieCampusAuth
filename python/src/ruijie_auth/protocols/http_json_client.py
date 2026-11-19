"""HTTP JSON client (mirrors Core.Protocols.HttpJsonClient, stdlib only)."""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from dataclasses import dataclass


@dataclass(frozen=True)
class HttpJsonResponse:
    ok: bool
    status: int
    url: str
    body: str
    json: dict | list | None


def _send(request: urllib.request.Request) -> HttpJsonResponse:
    url = request.full_url
    # Mirrors HttpClientHandler.UseProxy = false.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=10) as response:
            body = response.read().decode("utf-8", errors="replace")
            status = response.status
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        return HttpJsonResponse(False, exc.code, url, body, _try_json(body))
    except Exception as exc:
        return HttpJsonResponse(False, 0, url, str(exc), None)
    return HttpJsonResponse(200 <= status < 300, status, url, body, _try_json(body))


def _try_json(body: str) -> dict | list | None:
    if not body.strip():
        return None
    try:
        return json.loads(body)
    except json.JSONDecodeError:
        return None


def get(url: str) -> HttpJsonResponse:
    request = urllib.request.Request(url)
    request.add_header("Accept", "application/json")
    request.add_header("User-Agent", "HttpCall")
    return _send(request)


def post_json(url: str, body: dict | list) -> HttpJsonResponse:
    payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(url, data=payload, method="POST")
    request.add_header("Accept", "application/json")
    request.add_header("User-Agent", "HttpCall")
    request.add_header("Content-Type", "application/json")
    return _send(request)
