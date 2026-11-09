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

from vroid_cli.session import BRIDGE_LOADED, OUTDATED_INTEROP

SOURCE = Path(__file__).resolve().parent.parent


def run_cli(
    tmp_path: Path,
    *args: str,
    env: dict[str, str] | None = None,
    cwd: Path | None = None,
    state: bool = True,
    stdin: str | None = None,
) -> subprocess.CompletedProcess[str]:
    environment = {k: v for k, v in os.environ.items() if k != "VROID_STATE_DIR"}
    # Keep the inherited path, which provides dependencies in the Nix build.
    environment["PYTHONPATH"] = os.pathsep.join(
        filter(None, [str(SOURCE), environment.get("PYTHONPATH")])
    )
    if state:
        environment["VROID_STATE_DIR"] = str(tmp_path / "state")
    return subprocess.run(
        [sys.executable, "-m", "vroid_cli", *args],
        cwd=cwd or tmp_path,
        env=environment | (env or {}),
        input=stdin,
        text=True,
        capture_output=True,
        timeout=20,
    )


def invoke(
    tmp_path: Path,
    method: str,
    /,
    env: dict[str, str] | None = None,
    cwd: Path | None = None,
    state: bool = True,
    **fields: Any,
) -> subprocess.CompletedProcess[str]:
    """Run one request against session `test`."""
    request = json.dumps({"method": method} | fields)
    return run_cli(tmp_path, "test", request, env=env, cwd=cwd, state=state)


def write_status(rpc: Path, **fields: Any) -> dict[str, Any]:
    status = {
        "boot": "test-boot",
        "protocol": 2,
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


APPLY = '{"method": "apply", "Body": {"Whole Body": {"Head Size": %s}}}'


def test_nonfinite_values_are_rejected_without_state(tmp_path: Path) -> None:
    # R-EDIT-2
    for value in ["NaN", "Infinity", "-Infinity", "1e999", "[0.5, NaN]"]:
        result = run_cli(tmp_path, "test", APPLY % value)
        assert result.returncode == 2
        assert "Body.Whole Body.Head Size" in result.stderr
        assert "finite" in result.stderr
    assert not (tmp_path / "state").exists()


@pytest.mark.parametrize(
    ("request_text", "reason"),
    [
        (APPLY % '"0.5"', "Body.Whole Body.Head Size: expected a finite number"),
        ('{"method": "launch", "timout": 5}', "timout: Extra inputs"),
        ('{"method": "stop", "force": 1}', "force: Input should be a valid boolean"),
        ('{"method": "Apply"}', "does not match any of the expected tags"),
        ('{"method": "apply", "Body": {}, "Body": {}}', "duplicate keys: Body"),
        (
            '{"method": "apply", "Face": {"A": {"x": 0}, "B": {"y": 0, "x": 1}}}',
            "Face/B/x is the same parameter as Face/A/x; set it once",
        ),
        ('{"method": "status"', "Invalid JSON"),
        # Python's or pydantic's recursion limit refuses it, depending on the host.
        ('{"method": "apply", "Face": ' + "[" * 100_000, "invalid request:"),
        ("status", "REQUEST must be a JSON object, @PATH, or -"),
    ],
)
def test_invalid_requests_are_rejected_without_state(
    tmp_path: Path, request_text: str, reason: str
) -> None:
    # R-CLI-3
    result = run_cli(tmp_path, "test", request_text)
    assert result.returncode == 2
    assert result.stdout == ""
    assert reason in result.stderr
    assert "Traceback" not in result.stderr
    assert "errors.pydantic.dev" not in result.stderr
    assert not (tmp_path / "state").exists()


def test_requests_are_read_from_files_and_stdin(tmp_path: Path) -> None:
    (tmp_path / "status.json").write_text('{"method": "status"}')
    for result in [
        run_cli(tmp_path, "test", "@status.json"),
        run_cli(tmp_path, "test", "-", stdin='{"method": "status"}'),
    ]:
        assert result.returncode == 0, result.stderr
        assert json.loads(result.stdout)["session"] == "test"
    missing = run_cli(tmp_path, "test", "@missing.json")
    assert missing.returncode == 2
    assert "cannot read missing.json" in missing.stderr


def test_agent_skill_includes_the_request_and_result_schema(tmp_path: Path) -> None:
    skill = SOURCE / ".agents/skills/vroid-cli/SKILL.md"
    for flag in ["--agent-skill", "--help"]:
        result = run_cli(tmp_path, flag, env={"VROID_CLI_SKILL": str(skill)})
        assert result.returncode == 0, result.stderr
        assert result.stdout.startswith(skill.read_text().rstrip())
        text = result.stdout.split("```json\n", 1)[1].rsplit("```", 1)[0]
        generated = json.loads(text)
        assert set(generated["results"]) >= {"dump", "apply", "launch"}
        assert "apply" in generated["request"]["discriminator"]["mapping"]


def test_existing_save_destination_is_preserved(tmp_path: Path) -> None:
    # R-EDIT-4
    rpc = bridge(tmp_path)
    project = tmp_path / "saved.vroid"
    project.write_bytes(b"deliverable")
    result = invoke(tmp_path, "save", path="saved.vroid")
    assert result.returncode == 2
    assert project.read_bytes() == b"deliverable"
    assert not (rpc / "request.json").exists()


def test_stale_bridge_receives_no_requests(tmp_path: Path) -> None:
    # R-RPC-4
    rpc = bridge(tmp_path, heartbeat=(time.time() - 30) * 1000)
    result = invoke(tmp_path, "categories", section="Face")
    assert result.returncode == 1
    assert "not responding" in result.stderr
    assert not (rpc / "request.json").exists()


def test_pending_request_is_never_replaced(tmp_path: Path) -> None:
    # R-RPC-3
    rpc = bridge(tmp_path)
    request = rpc / "request.json"
    request.write_text('{"unfinished":true}')
    result = invoke(tmp_path, "apply", Body={"Whole Body": {"Head Size": 0.5}})
    assert result.returncode == 1
    assert "pending" in result.stderr
    assert request.read_text() == '{"unfinished":true}'


@pytest.mark.parametrize(
    "body",
    [
        {"method": "categories", "section": "Face"},
        {"method": "launch", "timeout": 1},
        {"method": "stop"},
    ],
)
def test_competing_controller_is_rejected(tmp_path: Path, body: dict[str, Any]) -> None:
    # R-SES-1
    rpc = bridge(tmp_path)
    env = fake_framewisp(tmp_path, "raise SystemExit('framewisp must not run')")
    with (rpc.parent / "command.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        result = run_cli(tmp_path, "test", json.dumps(body), env=env)
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
    result = invoke(tmp_path, "launch", timeout=8, env=env)
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
    launched = invoke(tmp_path, "launch", timeout=8, env=env)
    beating.join()
    assert launched.returncode == 1
    assert "never accepts them for you" in launched.stderr
    assert "stop" not in (tmp_path / "framewisp.log").read_text().split()
    edited = invoke(tmp_path, "categories", section="Body", env=env)
    assert edited.returncode == 1
    assert "terms" in edited.stderr
    assert not (rpc / "request.json").exists()
    status = json.loads(invoke(tmp_path, "status", env=env).stdout)
    assert status["app_running"] is True
    assert (status["terms_accepted"], status["ready"]) == (False, False)


def test_launch_reports_why_the_app_exited(tmp_path: Path) -> None:
    # R-SES-12
    env = fake_framewisp(
        tmp_path,
        "Path(args[0]).mkdir(parents=True)\n"
        "Path(args[0], 'app.log').write_text('wine: cannot find VRoidStudio.exe\\n')\n"
        "Path(args[0], '../bepinex').mkdir()\n"
        "Path(args[0], '../bepinex/LogOutput.log').write_text('[Fatal] no core\\n')\n"
        "Path(args[0], 'app-exit.json').write_text('{}')\n"
        "sys.exit(1)",
    )
    result = invoke(tmp_path, "launch", timeout=8, env=env)
    assert result.returncode == 1
    assert "wine: cannot find VRoidStudio.exe" in result.stderr
    assert "[Fatal] no core" in result.stderr


@pytest.mark.parametrize(
    "log, shown",
    [
        ("[Error  :   BepInEx] plugin failed\n", "plugin failed"),
        (f"[Warning:InteropManager] {OUTDATED_INTEROP}.\n", "do not match its VRoid"),
    ],
)
def test_launch_timeout_explains_the_bepinex_log(
    tmp_path: Path, log: str, shown: str
) -> None:
    # R-SES-12
    target = tmp_path / "state/test/bepinex/LogOutput.log"
    env = fake_framewisp(
        tmp_path,
        f"target = Path({str(target)!r})\n"
        "target.parent.mkdir(parents=True, exist_ok=True)\n"
        f"target.write_text({log!r})\n"
        "time.sleep(4)",
    )
    result = invoke(tmp_path, "launch", timeout=2, env=env)
    assert result.returncode == 1
    assert "not ready after 2s" in result.stderr
    assert shown in result.stderr


def test_launch_fails_at_once_when_bepinex_fails_to_start(tmp_path: Path) -> None:
    # R-SES-12: BepInEx then shows a dialog and VRoid never becomes ready.
    preloader = tmp_path / "state/test/bepinex/preloader.log"
    env = fake_framewisp(
        tmp_path,
        f"preloader = Path({str(preloader)!r})\n"
        "preloader.parent.mkdir(parents=True, exist_ok=True)\n"
        "preloader.write_text('UnauthorizedAccessException\\n')\n"
        "time.sleep(10)",
    )
    started = time.monotonic()
    result = invoke(tmp_path, "launch", timeout=30, env=env)
    assert time.monotonic() - started < 8
    assert result.returncode == 1
    assert "BepInEx failed to start" in result.stderr
    assert "UnauthorizedAccessException" in result.stderr


def test_resumed_launch_reports_the_running_apps_bepinex_failure(
    tmp_path: Path,
) -> None:
    # R-SES-12: a launch after a timeout still explains the same app's failure.
    rpc = bridge(tmp_path, heartbeat=0)
    preloader = rpc.parent / "bepinex/preloader.log"
    preloader.parent.mkdir()
    preloader.write_text("UnauthorizedAccessException\n")
    old = time.time() - 600
    os.utime(preloader, (old, old))
    result = invoke(tmp_path, "launch", timeout=30, env=running_desktop(tmp_path))
    assert result.returncode == 1
    assert "BepInEx failed to start" in result.stderr
    assert "UnauthorizedAccessException" in result.stderr
    assert preloader.exists()


def test_launch_ignores_bepinex_logs_from_an_earlier_run(tmp_path: Path) -> None:
    rpc = bridge(tmp_path, boot="old-boot")
    root = rpc.parent / "bepinex"
    root.mkdir()
    (root / "preloader.log").write_text("earlier failure\n")
    (root / "LogOutput.log").write_text(
        f"[Warning:InteropManager] {OUTDATED_INTEROP}.\n"
        f"[Info   :VroidBridge] {BRIDGE_LOADED.format(boot='new-boot')}\n"
    )
    marker = tmp_path / "started"
    env = fake_framewisp(tmp_path, f"Path({str(marker)!r}).touch()\ntime.sleep(5)")

    def new_instance() -> None:
        deadline = time.monotonic() + 5
        while not marker.exists() and time.monotonic() < deadline:
            time.sleep(0.01)
        write_status(rpc, boot="new-boot")

    thread = threading.Thread(target=new_instance)
    thread.start()
    result = invoke(tmp_path, "launch", timeout=8, env=env)
    thread.join()
    assert result.returncode == 0, result.stderr
    assert not (root / "preloader.log").exists()
    assert not (root / "LogOutput.log").exists()


@pytest.mark.parametrize("logged_boot", ["test-boot", "old-boot"])
def test_launch_refuses_outdated_interop_from_this_boot_only(
    tmp_path: Path, logged_boot: str
) -> None:
    # The CLI recognizes this boot's BepInEx log by the bridge's load message.
    plugin = (SOURCE / "plugin/Plugin.cs").read_text()
    assert BRIDGE_LOADED.format(boot="{rpc.Boot}") in plugin
    rpc = bridge(tmp_path)
    (rpc.parent / "bepinex").mkdir()
    (rpc.parent / "bepinex/LogOutput.log").write_text(
        f"[Warning:InteropManager] {OUTDATED_INTEROP}. To disable this message, "
        "create file X with the following contents: Y\n"
        f"[Info   :VroidBridge] {BRIDGE_LOADED.format(boot=logged_boot)}\n"
    )
    env = running_desktop(tmp_path)
    beating = heartbeats(rpc, 3)
    result = invoke(tmp_path, "launch", timeout=8, env=env)
    beating.join()
    if logged_boot == "test-boot":
        assert result.returncode == 1
        assert "do not match its VRoid" in result.stderr
    else:
        assert result.returncode == 0, result.stderr


def test_launch_reports_a_desktop_that_failed_before_vroid(tmp_path: Path) -> None:
    desktop = tmp_path / "state/test/desktop"
    desktop.mkdir(parents=True)
    (desktop / "app.log").write_text("previous run\n")
    (desktop / "app-exit.json").write_text("{}")
    old = time.time() - 60
    os.utime(desktop / "app-exit.json", (old, old))
    env = fake_framewisp(tmp_path, "print('sway exited', file=sys.stderr)\nsys.exit(1)")
    result = invoke(tmp_path, "launch", timeout=8, env=env)
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
    assert '"force": true' in refused.stderr
    assert "stop" not in (tmp_path / "framewisp.log").read_text().split()
    forced = invoke(tmp_path, "stop", force=True, env=env)
    assert forced.returncode == 0, forced.stderr
    assert json.loads(forced.stdout)["stopped"] is True
    assert (tmp_path / "framewisp.log").read_text().split()[-1] == "stop"


@pytest.mark.parametrize("ok", [True, False])
def test_request_roundtrip(tmp_path: Path, ok: bool) -> None:
    # R-CLI-1, R-CLI-2, R-RPC-5, R-RPC-6
    rpc = bridge(tmp_path)
    template = {"Whole Body": {"Head Size": -0.25}}
    undo = {"method": "apply", "Face": {}, "Body": {"Whole Body": {"Head Size": 0}}}
    peer, received = answer(
        rpc,
        lambda _: {
            "ok": ok,
            "result": {"changed": 1, "undo": undo} if ok else None,
            "error": None if ok else "engine refused",
        },
    )
    result = invoke(tmp_path, "apply", Body=template)
    peer.join()
    assert len(received) == 1
    request = received[0]
    assert request["boot"] == "test-boot"
    assert request["method"] == "apply"
    assert (request["Face"], request["Body"]) == ({}, template)
    assert (rpc / "results" / f"{request['id']}.json").exists()
    if ok:
        assert result.returncode == 0
        assert json.loads(result.stdout) == {"changed": 1, "undo": undo}
        assert result.stderr == ""
    else:
        assert result.returncode == 1
        assert result.stdout == ""
        assert "engine refused" in result.stderr


def test_dump_output_is_valid_apply_input(tmp_path: Path) -> None:
    rpc = bridge(tmp_path)
    dump = {
        "method": "apply",
        "Face": {"Face Sets": {"Eye Size X": 0.1, "Nose Size": [0.25, -0.5]}},
        "Body": {"Whole Body": {"Head Size": 0.125}},
    }
    peer, received = answer(
        rpc,
        lambda request: {
            "ok": True,
            "result": (
                dump if request["method"] == "dump" else {"changed": 0, "undo": dump}
            ),
            "error": None,
        },
    )
    dumped = invoke(tmp_path, "dump")
    assert dumped.returncode == 0, dumped.stderr
    assert json.loads(dumped.stdout) == dump
    (tmp_path / "template.json").write_text(dumped.stdout)
    applied = run_cli(tmp_path, "test", "@template.json")
    peer.join()
    assert applied.returncode == 0, applied.stderr
    assert received[1] | {"id": "", "boot": "", "timeout_ms": 0} == dump | {
        "id": "",
        "boot": "",
        "timeout_ms": 0,
    }


def test_a_result_that_breaks_the_schema_is_an_error(tmp_path: Path) -> None:
    rpc = bridge(tmp_path)
    peer, _ = answer(
        rpc,
        lambda _: {"ok": True, "result": {"saved": "x", "bytes": "1"}, "error": None},
    )
    (tmp_path / "saved.vroid").unlink(missing_ok=True)
    result = invoke(tmp_path, "save", path="saved.vroid")
    peer.join()
    assert result.returncode == 1
    assert result.stdout == ""
    assert "does not match the schema" in result.stderr
    assert "bytes: Input should be a valid integer" in result.stderr


def test_command_returns_after_the_bridge_removes_its_request(tmp_path: Path) -> None:
    # R-RPC-3
    rpc = bridge(tmp_path)
    peer, _ = answer(
        rpc, lambda _: {"ok": True, "result": [], "error": None}, removal_delay=1
    )
    result = invoke(tmp_path, "categories", section="Body")
    assert result.returncode == 0, result.stderr
    assert not (rpc / "request.json").exists()
    peer.join()


def test_paths_are_sent_as_wine_paths(tmp_path: Path) -> None:
    rpc = bridge(tmp_path)
    (tmp_path / "source.vroid").write_bytes(b"project")
    peer, received = answer(
        rpc, lambda r: {"ok": True, "result": {"opened": r["path"]}, "error": None}
    )
    result = invoke(tmp_path, "open", path="source.vroid")
    peer.join()
    assert result.returncode == 0, result.stderr
    assert received[0]["path"] == f"Z:{tmp_path / 'source.vroid'}"
    assert json.loads(result.stdout) == {"opened": str(tmp_path / "source.vroid")}


def test_status_of_a_new_session_is_one_json_value(tmp_path: Path) -> None:
    # R-CLI-1
    result = invoke(tmp_path, "status")
    assert result.returncode == 0, result.stderr
    status = json.loads(result.stdout)
    assert status["app_running"] is False and status["ready"] is False
