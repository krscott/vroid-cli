"""Session-local file protocol shared with the in-app bridge (plugin/Bridge.cs)."""

import json
import time
import uuid
from pathlib import Path
from typing import Any

# The bridge writes a heartbeat every second from Unity's main thread.
HEARTBEAT_STALE_MS = 5000
# The bridge abandons a request after this long...
ENGINE_TIMEOUT_MS = 90_000
# ...which leaves this margin before the CLI stops waiting for its result.
RESPONSE_MARGIN_S = 30.0


def now_ms() -> float:
    return time.time() * 1000


def write_json_atomic(path: Path, value: Any) -> None:
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value))
    temporary.replace(path)


def read_status(rpc: Path) -> dict[str, Any] | None:
    try:
        status: dict[str, Any] = json.loads((rpc / "status.json").read_text())
    except FileNotFoundError:
        return None
    return status


def is_fresh(status: dict[str, Any], *, since_ms: float | None = None) -> bool:
    heartbeat = float(status["heartbeat"])
    if since_ms is not None and heartbeat < since_ms:
        return False
    return now_ms() - heartbeat < HEARTBEAT_STALE_MS


def wine_path(path: Path) -> str:
    return "Z:" + str(path)


def submit(rpc: Path, *, boot: str, method: str, args: dict[str, Any]) -> Any:
    """Send one request and wait for its result. Never resends (R-RPC-5).

    The caller must hold the session's controller lock.
    """
    request_path = rpc / "request.json"
    if request_path.exists():
        raise RuntimeError(
            f"A request is still pending in {request_path}; wait for its result "
            f"in {rpc / 'results'} or restart the session to mark it unknown"
        )
    request = args | {
        "id": uuid.uuid4().hex,
        "boot": boot,
        "method": method,
        "timeout_ms": ENGINE_TIMEOUT_MS,
    }
    write_json_atomic(request_path, request)
    response = rpc / "results" / f"{request['id']}.json"
    wait_s = ENGINE_TIMEOUT_MS / 1000 + RESPONSE_MARGIN_S
    deadline = time.monotonic() + wait_s
    while not response.exists():
        if time.monotonic() > deadline:
            raise RuntimeError(
                f"No result after {wait_s:.0f}s; do not repeat the request. "
                f"Its result will appear at {response}"
            )
        time.sleep(0.05)
    payload: dict[str, Any] = json.loads(response.read_text())
    if not payload["ok"]:
        raise RuntimeError(f"VRoid refused {method}: {payload['error']}")
    return payload["result"]
