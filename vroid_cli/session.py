"""Named sessions: state directories, controller lock, and app lifecycle."""

import fcntl
import json
import os
import re
import subprocess
import time
from collections.abc import Generator
from contextlib import contextmanager
from dataclasses import dataclass
from enum import Enum
from pathlib import Path
from typing import Any

from . import rpc

SESSION_NAME = re.compile(r"[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}")
# Must match the player size in scripts/vroid-studio.sh.
DISPLAY = ("2560", "1440")
# Mesa render devices; automatic NVIDIA selection stalled the compositor.
PREFERRED_GPU_VENDORS = {"0x1002", "0x8086"}
TERMS_ACCEPTED = "<AllAgreementsAreAccepted>Yes</AllAgreementsAreAccepted>"


class Desktop(Enum):
    STOPPED = "stopped"
    EXITED = "exited"  # VRoid exited; the runner is shutting down.
    RUNNING = "running"


@dataclass(frozen=True, kw_only=True)
class Session:
    name: str
    directory: Path

    @property
    def desktop(self) -> Path:
        return self.directory / "desktop"

    @property
    def rpc(self) -> Path:
        return self.directory / "rpc"


def state_root(cwd: Path) -> Path:
    if override := os.environ.get("VROID_STATE_DIR"):
        return Path(override).resolve()
    top = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"],
        cwd=cwd,
        capture_output=True,
        text=True,
        check=False,
    )
    base = Path(top.stdout.strip()) if top.returncode == 0 else cwd
    return (base / ".vroid").resolve()


def resolve(name: str, *, cwd: Path) -> Session:
    if not SESSION_NAME.fullmatch(name):
        raise ValueError("session names are 1-64 letters, digits, '_' or '-'")
    return Session(name=name, directory=state_root(cwd) / name)


def create_state(session: Session) -> None:
    (session.rpc / "results").mkdir(parents=True, exist_ok=True)
    # Each session ignores itself, whichever repository contains it (R-SES-3).
    ignore = session.directory / ".gitignore"
    if not ignore.exists():
        ignore.write_text("*\n")


@contextmanager
def controller(session: Session) -> Generator[None]:
    """Hold the session's controller lock; fail at once if another holds it (R-SES-1)."""
    if not session.directory.is_dir():
        raise RuntimeError(f"Session {session.name!r} does not exist; launch it first")
    with (session.directory / "command.lock").open("a") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError(
                f"Another command controls session {session.name!r}; "
                "wait for it to finish"
            ) from None
        yield


def desktop_state(session: Session) -> Desktop:
    if not (session.desktop / "session.json").exists():
        if (session.desktop / ".headless-owner.json").exists():
            raise RuntimeError(abandoned(session))
        return Desktop.STOPPED
    # Framewisp records the app's exit; the runner then shuts itself down.
    if (session.desktop / "app-exit.json").exists():
        return Desktop.EXITED
    result = subprocess.run(
        ["framewisp", str(session.desktop), "status"],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode:
        if (session.desktop / "app-exit.json").exists():
            return Desktop.EXITED
        raise RuntimeError(f"{result.stderr.strip()}\n{abandoned(session)}")
    status: dict[str, Any] = json.loads(result.stdout)
    return Desktop.RUNNING if status["app"]["running"] else Desktop.EXITED


def abandoned(session: Session) -> str:
    return (
        f"The private desktop was not shut down cleanly. Inspect "
        f"{session.directory / 'runner.log'}, then run: "
        f"framewisp {session.desktop} recover"
    )


def tail(path: Path, lines: int = 10) -> str:
    try:
        return "\n".join(path.read_text(errors="replace").splitlines()[-lines:])
    except OSError:
        return ""


def exited(session: Session, *, since_ms: float = 0) -> str:
    """Explain why the private desktop or VRoid stopped (R-SES-5)."""
    # app.log belongs to the last run that started VRoid, which is this run
    # only if Framewisp recorded VRoid's exit during it.
    record = session.desktop / "app-exit.json"
    if record.exists() and record.stat().st_mtime * 1000 >= since_ms:
        log = session.desktop / "app.log"
        return f"VRoid exited. Last lines of {log}:\n{tail(log)}"
    log = session.directory / "runner.log"
    return f"The private desktop exited. Last lines of {log}:\n{tail(log)}"


def graphics_environment(
    *, drm: Path = Path("/sys/class/drm"), devices: Path = Path("/dev/dri")
) -> dict[str, str]:
    environment = dict(os.environ)
    if environment.get("WLR_RENDER_DRM_DEVICE"):
        return environment
    for node in sorted(drm.glob("renderD*")):
        vendor = node / "device/vendor"
        device = devices / node.name
        if vendor.is_file() and vendor.read_text().strip() in PREFERRED_GPU_VENDORS:
            if device.exists():
                environment["WLR_RENDER_DRM_DEVICE"] = str(device)
                break
    return environment


def terms_accepted(session: Session) -> bool:
    """Whether the user accepted VRoid's first-run terms in this session (R-SES-8)."""
    users = session.directory / "wine/drive_c/users"
    return any(
        TERMS_ACCEPTED in path.read_text(encoding="utf-8-sig", errors="replace")
        for path in users.glob(
            "*/AppData/LocalLow/pixiv/VRoid Studio/preferences/editoroption.xml"
        )
    )


def terms_pending(session: Session) -> str:
    return (
        "VRoid is waiting for its first-run welcome and terms dialogs. vroid-cli "
        "never accepts them for you. Have the user read and accept them on the "
        f"private desktop: framewisp {session.desktop} screenshot PNG, then "
        f"framewisp {session.desktop} click X Y. Then run launch again"
    )


def incompatible() -> str:
    return (
        "The session runs a bridge from an incompatible vroid-cli build. Use that "
        "build to save, or stop and launch again"
    )


def summary(session: Session) -> dict[str, Any]:
    exists = session.directory.is_dir()
    desktop = desktop_state(session) if exists else Desktop.STOPPED
    bridge = rpc.read_status(session.rpc)
    accepted = terms_accepted(session)
    return {
        "session": session.name,
        "directory": str(session.directory),
        "desktop": str(session.desktop),
        "app_running": desktop is Desktop.RUNNING,
        "terms_accepted": accepted,
        "ready": desktop is Desktop.RUNNING
        and accepted
        and bridge is not None
        and bridge.ready(),
        "pending_request": (session.rpc / "request.json").exists(),
        "bridge": bridge.raw if bridge else None,
    }


def launch(session: Session, *, timeout_s: float) -> dict[str, Any]:
    started_ms = rpc.now_ms()
    create_state(session)
    with controller(session):
        runner: subprocess.Popen[bytes] | None = None
        desktop = desktop_state(session)
        if desktop is Desktop.EXITED:
            raise RuntimeError(
                f"{exited(session)}\nWait for the desktop to close, then launch "
                "again; if this persists, run stop"
            )
        if desktop is Desktop.STOPPED:
            with (session.directory / "runner.log").open("ab") as log:
                runner = subprocess.Popen(
                    [
                        "framewisp",
                        str(session.desktop),
                        "run",
                        "--x11",
                        "--width",
                        DISPLAY[0],
                        "--height",
                        DISPLAY[1],
                        "--",
                        "vroid-studio",
                    ],
                    stdin=subprocess.DEVNULL,
                    stdout=log,
                    stderr=log,
                    start_new_session=True,
                    env=graphics_environment()
                    | {"VROID_SESSION_DIR": str(session.directory)},
                )
        deadline = time.monotonic() + timeout_s
        next_check = time.monotonic() + 5
        while True:
            status = rpc.read_status(session.rpc)
            # Only a heartbeat written after this launch began counts (R-SES-2).
            if status and status.fresh(since_ms=started_ms) and status.main:
                if not status.compatible:
                    raise RuntimeError(incompatible())
                if not terms_accepted(session):
                    raise RuntimeError(terms_pending(session))
                return summary(session)
            if runner is not None and runner.poll() is not None:
                raise RuntimeError(exited(session, since_ms=started_ms))
            if runner is None and time.monotonic() >= next_check:
                next_check = time.monotonic() + 5
                if desktop_state(session) is not Desktop.RUNNING:
                    raise RuntimeError(exited(session))
            if time.monotonic() >= deadline:
                raise RuntimeError(
                    f"VRoid is not ready after {timeout_s:.0f}s and keeps starting. "
                    "A first launch installs VRoid and generates bindings. "
                    "Run launch again to keep waiting; inspect "
                    f"{session.directory / 'player.log'} and "
                    f"{session.directory / 'wine/drive_c/VRoidStudio/BepInEx/LogOutput.log'}"
                )
            time.sleep(0.2)


def stop(session: Session, *, force: bool) -> dict[str, Any]:
    if not session.directory.is_dir():
        return {"session": session.name, "stopped": False}
    with controller(session):
        desktop = desktop_state(session)
        if desktop is Desktop.STOPPED:
            return {"session": session.name, "stopped": False}
        if desktop is Desktop.RUNNING:
            # The bridge writes its status before removing a finished request,
            # so a status read after this check is current.
            pending = (session.rpc / "request.json").exists()
            status = rpc.read_status(session.rpc)
            if not force and (pending or (status is not None and status.modified)):
                raise RuntimeError(
                    "The app has unsaved changes or a pending request. "
                    'Save or wait first, or send {"method": "stop", "force": true} '
                    "to discard them"
                )
            result = subprocess.run(
                ["framewisp", str(session.desktop), "stop"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                text=True,
                check=False,
            )
            if result.returncode and desktop_state(session) is Desktop.RUNNING:
                raise RuntimeError(f"{result.stderr.strip()}\n{abandoned(session)}")
        # The runner shuts itself down after the app exits.
        deadline = time.monotonic() + 60
        while (session.desktop / "session.json").exists():
            if time.monotonic() >= deadline:
                raise RuntimeError(abandoned(session))
            time.sleep(0.2)
        return {"session": session.name, "stopped": True}


def request(session: Session, *, method: str, args: dict[str, Any]) -> Any:
    with controller(session):
        if not terms_accepted(session):
            raise RuntimeError(terms_pending(session))
        status = rpc.read_status(session.rpc)
        if status is None or not status.fresh():
            raise RuntimeError(
                f"Session {session.name!r} is not running or not responding; "
                f"check status and {session.directory / 'player.log'}"
            )
        if not status.compatible:
            raise RuntimeError(incompatible())
        return rpc.submit(session.rpc, boot=status.boot, method=method, args=args)
