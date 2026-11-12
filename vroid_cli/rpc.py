"""Session-local file protocol shared with the in-app bridge (plugin/Bridge.cs)."""

import json
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, cast

# Change together with Bridge.Protocol whenever the file protocol or the
# request methods change incompatibly.
PROTOCOL = 7
# The bridge writes a heartbeat about once a second from Unity's main thread.
HEARTBEAT_STALE_MS = 5000
# The bridge stops waiting on an operation after this long...
ENGINE_TIMEOUT_MS = 90_000
# ...which leaves this margin before the CLI stops waiting for its result.
RESPONSE_MARGIN_S = 30.0


class BridgeError(RuntimeError):
    """The bridge ran a request and reported `error`."""

    def __init__(self, method: str, error: str) -> None:
        super().__init__(f"{method} failed: {error}")
        self.error = error


@dataclass(frozen=True, kw_only=True)
class BridgeStatus:
    boot: str
    protocol: int
    heartbeat_ms: float
    main: bool
    modified: bool
    raw: dict[str, Any]

    def fresh(self, *, since_ms: float | None = None) -> bool:
        if since_ms is not None and self.heartbeat_ms < since_ms:
            return False
        return now_ms() - self.heartbeat_ms < HEARTBEAT_STALE_MS

    def ready(self, *, since_ms: float | None = None) -> bool:
        """Whether this bridge can take requests from this CLI."""
        return self.fresh(since_ms=since_ms) and self.main and self.compatible

    @property
    def compatible(self) -> bool:
        return self.protocol == PROTOCOL


def now_ms() -> float:
    return time.time() * 1000


def write_json_atomic(path: Path, value: Any) -> None:
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value))
    temporary.replace(path)


def read_status(rpc: Path) -> BridgeStatus | None:
    """Return the bridge's last status, or None if it is missing or unreadable."""
    try:
        raw = json.loads((rpc / "status.json").read_text())
        return BridgeStatus(
            boot=str(raw["boot"]),
            protocol=int(raw.get("protocol", 0)),
            heartbeat_ms=float(raw["heartbeat"]),
            main=bool(raw["main"]),
            modified=bool(raw["modified"]),
            raw=raw,
        )
    except (OSError, ValueError, KeyError, TypeError):
        return None


def wine_path(path: Path) -> str:
    return "Z:" + str(path)


def submit(rpc: Path, *, boot: str, method: str, args: dict[str, Any]) -> Any:
    """Send one request and wait for its result. Never resends (R-RPC-5).

    A result or error whose request wrote an undo file names it, and a
    result adds it as `undo` (R-EDIT-11). The caller must hold the session's
    controller lock.
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
    response = rpc / "results" / f"{request['id']}.json"
    undo = rpc / "undo" / f"{request['id']}.json"

    def restore(reason: str) -> str:
        if not undo.exists():
            return reason
        return f"{reason.rstrip('.')}. To undo any change, apply @{undo}"

    wait_s = ENGINE_TIMEOUT_MS / 1000 + RESPONSE_MARGIN_S
    deadline = time.monotonic() + wait_s

    def unfinished(why: str) -> RuntimeError:
        reason = (
            f"{why}; do not repeat the request. Its result will appear at "
            f"{response}; restarting the session marks it as having an unknown "
            "outcome"
        )
        if method == "apply" and not undo.exists():
            # The app may still write the undo file and change things later.
            reason += f"; if {undo} appears, apply it to undo any change"
        return RuntimeError(restore(reason))

    write_json_atomic(request_path, request)
    # The bridge writes the result before removing the request, so the next
    # command never finds this request still in place.
    try:
        while request_path.exists():
            if time.monotonic() > deadline:
                raise unfinished(f"No result after {wait_s:.0f}s")
            time.sleep(0.05)
    except KeyboardInterrupt:
        raise unfinished("Interrupted before the result") from None
    if not response.exists():
        raise RuntimeError(
            restore(f"The app discarded the request without a result; see {rpc}")
        )
    payload: dict[str, Any] = json.loads(response.read_text())
    if not payload["ok"]:
        raise BridgeError(method, restore(str(payload["error"])))
    result: Any = payload["result"]
    if undo.exists() and isinstance(result, dict):
        return cast(dict[str, Any], result) | {"undo": str(undo)}
    return result
