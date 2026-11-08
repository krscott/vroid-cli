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
from pathlib import Path
from typing import Any

from . import rpc

SESSION_NAME = re.compile(r"[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}")
DISPLAY = ("2560", "1440")
# Mesa render devices; automatic NVIDIA selection stalled the compositor.
PREFERRED_GPU_VENDORS = {"0x1002", "0x8086"}


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
    root = session.directory.parent
    root.mkdir(parents=True, exist_ok=True)
    # Ignore the state root from inside, whichever repository contains it (R-SES-3).
    ignore = root / ".gitignore"
    if not ignore.exists():
        ignore.write_text("*\n")
    (session.rpc / "results").mkdir(parents=True, exist_ok=True)


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


def desktop_status(session: Session) -> dict[str, Any] | None:
    """Return Framewisp's status for a live runner, or None when none is running."""
    if not (session.desktop / "session.json").exists():
        if (session.desktop / ".headless-owner.json").exists():
            raise RuntimeError(abandoned(session))
        return None
    result = subprocess.run(
        ["framewisp", str(session.desktop), "status"],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode:
        raise RuntimeError(f"{result.stderr.strip()}\n{abandoned(session)}")
    status: dict[str, Any] = json.loads(result.stdout)
    return status


def abandoned(session: Session) -> str:
    return (
        f"The private desktop was not shut down cleanly. Inspect "
        f"{session.directory / 'runner.log'}, then run: "
        f"framewisp {session.desktop} recover"
    )


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
    preferences = session.directory / "wine/drive_c/users"
    for path in preferences.glob(
        "*/AppData/LocalLow/pixiv/VRoid Studio/preferences/editoroption.xml"
    ):
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        if "<AllAgreementsAreAccepted>Yes</AllAgreementsAreAccepted>" in text:
            return True
    return False


def terms_pending(session: Session) -> str:
    return (
        "VRoid is waiting for its first-run welcome and terms dialogs. vroid-cli "
        "never accepts them for you. Have the user read and accept them on the "
        f"private desktop: framewisp {session.desktop} screenshot PNG, then "
        f"framewisp {session.desktop} click X Y. Then run launch again"
    )


def summary(session: Session) -> dict[str, Any]:
    desktop = desktop_status(session) if session.directory.is_dir() else None
    bridge = rpc.read_status(session.rpc)
    running = desktop is not None and bool(desktop["app"]["running"])
    accepted = terms_accepted(session)
    return {
        "session": session.name,
        "directory": str(session.directory),
        "desktop": str(session.desktop),
        "app_running": running,
        "terms_accepted": accepted,
        "ready": running and accepted and bridge is not None and rpc.is_fresh(bridge),
        "pending_request": (session.rpc / "request.json").exists(),
        "bridge": bridge,
    }


def bundle() -> str:
    return os.environ["VROID_BRIDGE_BUNDLE"]


def launch(session: Session, *, timeout_s: float) -> dict[str, Any]:
    started_ms = rpc.now_ms()
    create_state(session)
    with controller(session):
        runner: subprocess.Popen[bytes] | None = None
        desktop = desktop_status(session)
        if desktop is None:
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
        elif not desktop["app"]["running"]:
            raise RuntimeError(
                f"VRoid exited; inspect {session.desktop / 'app.log'}, then stop "
                "the session before launching again"
            )
        deadline = time.monotonic() + timeout_s
        next_check = time.monotonic() + 5
        while True:
            status = rpc.read_status(session.rpc)
            # Only a heartbeat written after this launch began counts (R-SES-2).
            if status and status["main"] and rpc.is_fresh(status, since_ms=started_ms):
                if status["bundle"] != bundle():
                    raise RuntimeError(
                        "The session runs another vroid-cli build; "
                        "stop it and launch again"
                    )
                if not terms_accepted(session):
                    raise RuntimeError(terms_pending(session))
                return summary(session)
            if runner is not None and runner.poll() is not None:
                raise RuntimeError(
                    f"The private desktop exited; inspect {session.directory / 'runner.log'}"
                )
            if runner is None and time.monotonic() >= next_check:
                next_check = time.monotonic() + 5
                current = desktop_status(session)
                if current is None or not current["app"]["running"]:
                    raise RuntimeError(
                        f"VRoid exited; inspect {session.desktop / 'app.log'}"
                    )
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
        if desktop_status(session) is None:
            return {"session": session.name, "stopped": False}
        status = rpc.read_status(session.rpc)
        if not force and status and (status["modified"] or status["busy"]):
            raise RuntimeError(
                "The app last reported unsaved changes or a running request. "
                "Save first, or pass --force to discard them"
            )
        result = subprocess.run(
            ["framewisp", str(session.desktop), "stop"],
            stdout=subprocess.PIPE,
            text=True,
            check=False,
        )
        if result.returncode:
            raise RuntimeError(
                f"Could not stop the private desktop; {abandoned(session)}"
            )
        return {"session": session.name, "stopped": True}


def request(session: Session, *, method: str, args: dict[str, Any]) -> Any:
    with controller(session):
        if not terms_accepted(session):
            raise RuntimeError(terms_pending(session))
        status = rpc.read_status(session.rpc)
        if status is None or not rpc.is_fresh(status):
            raise RuntimeError(
                f"Session {session.name!r} is not running or not responding; "
                f"check status and {session.directory / 'player.log'}"
            )
        if status["bundle"] != bundle():
            raise RuntimeError(
                "The session runs another vroid-cli build; save, stop, and launch again"
            )
        return rpc.submit(session.rpc, boot=status["boot"], method=method, args=args)
