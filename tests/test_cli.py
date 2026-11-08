"""Exercise the CLI and its file protocol without a running VRoid process."""

import fcntl
import json
import os
import subprocess
import sys
import threading
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest

SOURCE = Path(__file__).resolve().parent.parent


def invoke(
    tmp_path: Path,
    *args: str,
    env: dict[str, str] | None = None,
    cwd: Path | None = None,
    state: bool = True,
) -> subprocess.CompletedProcess[str]:
    environment = {k: v for k, v in os.environ.items() if k != "VROID_STATE_DIR"}
    environment["PYTHONPATH"] = str(SOURCE)
    if state:
        environment["VROID_STATE_DIR"] = str(tmp_path / "state")
    return subprocess.run(
        [sys.executable, "-m", "vroid_cli", "--session", "test", *args],
        cwd=cwd or tmp_path,
        env=environment | (env or {}),
        text=True,
        capture_output=True,
        timeout=20,
    )


def write_status(rpc: Path, **fields: Any) -> dict[str, Any]:
    status = {
        "boot": "test-boot",
        "protocol": 1,
        "heartbeat": time.time() * 1000,
        "main": True,
        "modified": False,
    } | fields
    temporary = rpc / "status.tmp"
    temporary.write_text(json.dumps(status))
    temporary.replace(rpc / "status.json")
    return status


PREFERENCES = "wine/drive_c/users/user/AppData/LocalLow/pixiv/VRoid Studio/preferences"


def bridge(tmp_path: Path, *, terms: bool = True, **fields: Any) -> Path:
    rpc = tmp_path / "state/test/rpc"
    (rpc / "results").mkdir(parents=True)
    write_status(rpc, **fields)
    preferences = rpc.parent / PREFERENCES
    preferences.mkdir(parents=True)
    accepted = "Yes" if terms else "No"
    (preferences / "editoroption.xml").write_text(
        f"\ufeff<EditorOption><Preference><AllAgreementsAreAccepted>{accepted}"
        "</AllAgreementsAreAccepted></Preference></EditorOption>",
        encoding="utf-8",
    )
    return rpc


def heartbeats(rpc: Path, seconds: float) -> threading.Thread:
    """Write fresh heartbeats like a running bridge."""

    def beat() -> None:
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            write_status(rpc)
            time.sleep(0.2)

    thread = threading.Thread(target=beat, daemon=True)
    thread.start()
    return thread


def fake_framewisp(tmp_path: Path, script: str) -> dict[str, str]:
    """Put a stand-in framewisp on PATH. `script` sees argv as `args`."""
    binary = tmp_path / "bin"
    binary.mkdir(exist_ok=True)
    program = binary / "framewisp"
    program.write_text(
        f"#!{sys.executable}\nimport json, sys, time\nfrom pathlib import Path\n"
        f"args = sys.argv[1:]\n{script}\n"
    )
    program.chmod(0o755)
    return {"PATH": f"{binary}{os.pathsep}{os.environ['PATH']}"}


def running_desktop(tmp_path: Path) -> dict[str, str]:
    desktop = tmp_path / "state/test/desktop"
    desktop.mkdir(parents=True, exist_ok=True)
    (desktop / "session.json").write_text("{}")
    log = tmp_path / "framewisp.log"
    return fake_framewisp(
        tmp_path,
        f"open({str(log)!r}, 'a').write(args[1] + '\\n')\n"
        "if args[1] == 'stop': Path(args[0], 'session.json').unlink()\n"
        "print(json.dumps({'app': {'running': True}}))",
    )


def answer(
    rpc: Path,
    reply: Callable[[dict[str, Any]], dict[str, Any]],
    *,
    removal_delay: float = 0,
) -> tuple[threading.Thread, list[dict[str, Any]]]:
    """Act as the bridge for the next requests until none arrives for 2s."""
    received: list[dict[str, Any]] = []

    def peer() -> None:
        request = rpc / "request.json"
        deadline = time.monotonic() + 2
        while time.monotonic() < deadline:
            if request.exists():
                packet = json.loads(request.read_text())
                received.append(packet)
                response = rpc / "results" / f"{packet['id']}.json"
                response.with_suffix(".tmp").write_text(json.dumps(reply(packet)))
                response.with_suffix(".tmp").replace(response)
                time.sleep(removal_delay)
                request.unlink()
                deadline = time.monotonic() + 2
            time.sleep(0.01)

    thread = threading.Thread(target=peer, daemon=True)
    thread.start()
    return thread, received


def test_nonfinite_values_are_rejected_without_state(tmp_path: Path) -> None:
    # R-EDIT-2
    for value in ["nan", "inf", "-inf"]:
        result = invoke(tmp_path, "set", "Body", "Whole Body", "Head Size", "--", value)
        assert result.returncode == 2
        assert "finite" in result.stderr
    assert not (tmp_path / "state").exists()


def test_existing_save_destination_is_preserved(tmp_path: Path) -> None:
    # R-EDIT-4
    rpc = bridge(tmp_path)
    project = tmp_path / "saved.vroid"
    project.write_bytes(b"deliverable")
    result = invoke(tmp_path, "save", "saved.vroid")
    assert result.returncode == 2
    assert project.read_bytes() == b"deliverable"
    assert not (rpc / "request.json").exists()


def test_stale_bridge_receives_no_requests(tmp_path: Path) -> None:
    # R-RPC-4
    rpc = bridge(tmp_path, heartbeat=(time.time() - 30) * 1000)
    result = invoke(tmp_path, "categories", "Face")
    assert result.returncode == 1
    assert "not responding" in result.stderr
    assert not (rpc / "request.json").exists()


def test_pending_request_is_never_replaced(tmp_path: Path) -> None:
    # R-RPC-3
    rpc = bridge(tmp_path)
    request = rpc / "request.json"
    request.write_text('{"unfinished":true}')
    result = invoke(tmp_path, "set", "Body", "Whole Body", "Head Size", "0.5")
    assert result.returncode == 1
    assert "pending" in result.stderr
    assert request.read_text() == '{"unfinished":true}'


@pytest.mark.parametrize(
    "command",
    [
        ["categories", "Face"],
        ["launch", "--timeout", "1"],
        ["stop"],
    ],
)
def test_competing_controller_is_rejected(tmp_path: Path, command: list[str]) -> None:
    # R-SES-1
    rpc = bridge(tmp_path)
    env = fake_framewisp(tmp_path, "raise SystemExit('framewisp must not run')")
    with (rpc.parent / "command.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        result = invoke(tmp_path, *command, env=env)
    assert result.returncode == 1
    assert "Another command controls" in result.stderr
    assert not (rpc / "request.json").exists()
    assert not (rpc.parent / "runner.log").exists()


def test_launch_ignores_a_heartbeat_from_before_launch(tmp_path: Path) -> None:
    # R-SES-2
    rpc = bridge(tmp_path, boot="old-boot")
    marker = tmp_path / "started"
    env = fake_framewisp(
        tmp_path, f"Path({str(marker)!r}).write_text(' '.join(args))\ntime.sleep(5)"
    )

    def new_instance() -> None:
        deadline = time.monotonic() + 5
        while not marker.exists() and time.monotonic() < deadline:
            time.sleep(0.01)
        time.sleep(0.5)
        write_status(rpc, boot="new-boot")

    thread = threading.Thread(target=new_instance)
    thread.start()
    result = invoke(tmp_path, "launch", "--timeout", "8", env=env)
    thread.join()
    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout)["bridge"]["boot"] == "new-boot"
    assert marker.read_text().endswith(
        "run --x11 --width 2560 --height 1440 -- vroid-studio"
    )


def test_commands_wait_for_the_user_to_accept_terms(tmp_path: Path) -> None:
    # R-SES-8
    rpc = bridge(tmp_path, terms=False)
    env = running_desktop(tmp_path)
    beating = heartbeats(rpc, 3)
    launched = invoke(tmp_path, "launch", "--timeout", "8", env=env)
    beating.join()
    assert launched.returncode == 1
    assert "never accepts them for you" in launched.stderr
    assert "stop" not in (tmp_path / "framewisp.log").read_text().split()
    edited = invoke(tmp_path, "categories", "Body", env=env)
    assert edited.returncode == 1
    assert "terms" in edited.stderr
    assert not (rpc / "request.json").exists()
    status = json.loads(invoke(tmp_path, "status", env=env).stdout)
    assert status["app_running"] is True
    assert (status["terms_accepted"], status["ready"]) == (False, False)


def test_launch_reports_why_the_app_exited(tmp_path: Path) -> None:
    # R-SES-5
    env = fake_framewisp(
        tmp_path,
        "Path(args[0]).mkdir(parents=True)\n"
        "Path(args[0], 'app.log').write_text('Incomplete installation in wine\\n')\n"
        "Path(args[0], 'app-exit.json').write_text('{}')\n"
        "sys.exit(1)",
    )
    result = invoke(tmp_path, "launch", "--timeout", "8", env=env)
    assert result.returncode == 1
    assert "Incomplete installation in wine" in result.stderr


def test_launch_reports_a_desktop_that_failed_before_vroid(tmp_path: Path) -> None:
    desktop = tmp_path / "state/test/desktop"
    desktop.mkdir(parents=True)
    (desktop / "app.log").write_text("previous run\n")
    (desktop / "app-exit.json").write_text("{}")
    old = time.time() - 60
    os.utime(desktop / "app-exit.json", (old, old))
    env = fake_framewisp(tmp_path, "print('sway exited', file=sys.stderr)\nsys.exit(1)")
    result = invoke(tmp_path, "launch", "--timeout", "8", env=env)
    assert result.returncode == 1
    assert "sway exited" in result.stderr
    assert "previous run" not in result.stderr


def test_exited_app_blocks_launch_and_stop_waits_for_the_desktop(
    tmp_path: Path,
) -> None:
    bridge(tmp_path, modified=True)
    desktop = tmp_path / "state/test/desktop"
    desktop.mkdir(parents=True)
    (desktop / "session.json").write_text("{}")
    (desktop / "app-exit.json").write_text("{}")
    env = fake_framewisp(tmp_path, "raise SystemExit('framewisp must not run')")
    launched = invoke(tmp_path, "launch", env=env)
    assert launched.returncode == 1
    assert "Wait for the desktop to close" in launched.stderr

    def close() -> None:
        time.sleep(0.5)
        (desktop / "session.json").unlink()

    closing = threading.Thread(target=close)
    closing.start()
    stopped = invoke(tmp_path, "stop", env=env)
    assert not (desktop / "session.json").exists()
    closing.join()
    assert stopped.returncode == 0, stopped.stderr
    assert json.loads(stopped.stdout)["stopped"] is True


def test_abandoned_desktop_asks_for_recovery(tmp_path: Path) -> None:
    desktop = tmp_path / "state/test/desktop"
    desktop.mkdir(parents=True)
    (desktop / ".headless-owner.json").write_text("{}")
    (desktop / "app-exit.json").write_text("{}")
    for command in ["status", "launch", "stop"]:
        result = invoke(tmp_path, command)
        assert result.returncode == 1
        assert "recover" in result.stderr


def test_state_ignores_itself_inside_another_repository(tmp_path: Path) -> None:
    # R-SES-3
    repository = tmp_path / "repo"
    (repository / "sub").mkdir(parents=True)
    subprocess.run(["git", "init", "-q", str(repository)], check=True)
    env = fake_framewisp(tmp_path, "sys.exit(1)")
    result = invoke(tmp_path, "launch", env=env, cwd=repository / "sub", state=False)
    assert result.returncode == 1
    assert (repository / ".vroid/test/runner.log").exists()
    untracked = subprocess.run(
        ["git", "status", "--porcelain", "--untracked-files=all"],
        cwd=repository,
        capture_output=True,
        text=True,
        check=True,
    )
    assert untracked.stdout == ""


def test_state_dir_hides_only_session_state(tmp_path: Path) -> None:
    # R-SES-3
    repository = tmp_path / "repo"
    subprocess.run(["git", "init", "-q", str(repository)], check=True)
    (repository / "important.py").write_text("")
    env = fake_framewisp(tmp_path, "sys.exit(1)") | {"VROID_STATE_DIR": str(repository)}
    result = invoke(tmp_path, "launch", env=env)
    assert result.returncode == 1
    assert (repository / "test/runner.log").exists()
    untracked = subprocess.run(
        ["git", "status", "--porcelain", "--untracked-files=all"],
        cwd=repository,
        capture_output=True,
        text=True,
        check=True,
    )
    assert untracked.stdout == "?? important.py\n"


@pytest.mark.parametrize("cause", ["modified", "pending"])
def test_stop_refuses_to_discard_work_unless_forced(tmp_path: Path, cause: str) -> None:
    # R-SES-4
    rpc = bridge(tmp_path, modified=cause == "modified")
    if cause == "pending":
        (rpc / "request.json").write_text("{}")
    env = running_desktop(tmp_path)
    refused = invoke(tmp_path, "stop", env=env)
    assert refused.returncode == 1
    assert "--force" in refused.stderr
    assert "stop" not in (tmp_path / "framewisp.log").read_text().split()
    forced = invoke(tmp_path, "stop", "--force", env=env)
    assert forced.returncode == 0, forced.stderr
    assert json.loads(forced.stdout)["stopped"] is True
    assert (tmp_path / "framewisp.log").read_text().split()[-1] == "stop"


@pytest.mark.parametrize("ok", [True, False])
def test_request_roundtrip(tmp_path: Path, ok: bool) -> None:
    # R-CLI-1, R-CLI-2, R-RPC-5, R-RPC-6
    rpc = bridge(tmp_path)
    peer, received = answer(
        rpc,
        lambda _: {
            "ok": ok,
            "result": {"value": -0.25} if ok else None,
            "error": None if ok else "engine refused",
        },
    )
    result = invoke(tmp_path, "set", "Body", "Whole Body", "Head Size", "-0.25")
    peer.join()
    assert len(received) == 1
    request = received[0]
    assert request["boot"] == "test-boot"
    assert (request["section"], request["category"], request["parameter"]) == (
        "Body",
        "Whole Body",
        "Head Size",
    )
    assert request["value"] == -0.25
    assert (rpc / "results" / f"{request['id']}.json").exists()
    if ok:
        assert result.returncode == 0
        assert json.loads(result.stdout) == {"value": -0.25}
        assert result.stderr == ""
    else:
        assert result.returncode == 1
        assert result.stdout == ""
        assert "engine refused" in result.stderr


def test_command_returns_after_the_bridge_removes_its_request(tmp_path: Path) -> None:
    # R-RPC-3
    rpc = bridge(tmp_path)
    peer, _ = answer(
        rpc, lambda _: {"ok": True, "result": [], "error": None}, removal_delay=1
    )
    result = invoke(tmp_path, "categories", "Body")
    assert result.returncode == 0, result.stderr
    assert not (rpc / "request.json").exists()
    peer.join()


def test_paths_are_sent_as_wine_paths(tmp_path: Path) -> None:
    rpc = bridge(tmp_path)
    (tmp_path / "source.vroid").write_bytes(b"project")
    peer, received = answer(rpc, lambda _: {"ok": True, "result": {}, "error": None})
    result = invoke(tmp_path, "open", "source.vroid")
    peer.join()
    assert result.returncode == 0, result.stderr
    assert received[0]["path"] == f"Z:{tmp_path / 'source.vroid'}"


def test_status_of_a_new_session_is_one_json_value(tmp_path: Path) -> None:
    # R-CLI-1
    result = invoke(tmp_path, "status")
    assert result.returncode == 0, result.stderr
    status = json.loads(result.stdout)
    assert status["app_running"] is False and status["ready"] is False
