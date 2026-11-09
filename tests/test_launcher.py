"""Exercise the in-desktop launcher with a stand-in Wine and store."""

import json
import os
import subprocess
import sys
from collections.abc import Iterator
from pathlib import Path, PureWindowsPath
from typing import Any

import pytest

LAUNCHER = Path(__file__).resolve().parent.parent / "scripts/vroid-studio.sh"
STORE = ["VROID_WINE", "VROID_APP", "VROID_INTEROP", "VROID_BEPINEX", "VROID_BRIDGE"]
PREFERENCES = (
    "wine/drive_c/users/user/AppData/LocalLow/pixiv/VRoid Studio"
    "/preferences/editoroption.xml"
)
# Records each call in SESSION/calls.jsonl; wineboot creates a user profile.
# Starting VRoid copies the preferences it would read to SESSION/started.xml.
WINE = f"""\
import json, os, shutil, sys
from pathlib import Path
session = Path(os.environ["VROID_SESSION_DIR"])
name = Path(sys.argv[0]).name
with open(session / "calls.jsonl", "a") as calls:
    calls.write(json.dumps({{"program": sys.argv[0], "args": sys.argv[1:],
                            "env": dict(os.environ)}}) + "\\n")
if name == "wineboot":
    Path(os.environ["WINEPREFIX"], "drive_c/users/user/AppData").mkdir(parents=True)
if name == "wine" and sys.argv[1].endswith("VRoidStudio.exe"):
    shutil.copyfile(session / {PREFERENCES!r}, session / "started.xml")
"""


@pytest.fixture
def writable(tmp_path: Path) -> Iterator[Path]:
    yield tmp_path
    for path in tmp_path.rglob("*"):
        if not path.is_symlink():
            path.chmod(path.stat().st_mode | 0o700)


def store(tmp_path: Path, version: str) -> dict[str, str]:
    """Create read-only stand-ins for the store paths the wrapper sets."""
    root = tmp_path / f"store-{version}"
    files = {
        "VROID_APP": ["VRoidStudio.exe", "winhttp.dll"],
        "VROID_INTEROP": ["interop/assembly-hash.txt", "unity-libs/UnityEngine.dll"],
        "VROID_BEPINEX": [
            "BepInEx/core/BepInEx.Unity.IL2CPP.dll",
            "dotnet/coreclr.dll",
        ],
        "VROID_BRIDGE": ["VroidBridge.dll"],
    }
    paths: dict[str, str] = {}
    for name, entries in files.items():
        path = root / name.removeprefix("VROID_").lower()
        for entry in entries:
            (path / entry).parent.mkdir(parents=True, exist_ok=True)
            (path / entry).write_text(version)
        paths[name] = str(path)
    wine = root / "wine/bin"
    wine.mkdir(parents=True)
    for program in ["wine", "wineboot", "wineserver"]:
        (wine / program).write_text(f"#!{sys.executable}\n{WINE}")
        (wine / program).chmod(0o755)
    paths["VROID_WINE"] = str(wine.parent)
    (root / "BepInEx.cfg").write_text(version)
    paths["VROID_BEPINEX_CONFIG"] = str(root / "BepInEx.cfg")
    for item in sorted(root.rglob("*"), reverse=True):
        item.chmod(0o555 if item.is_dir() or item.parent == wine else 0o444)
    return paths


def run(session: Path, paths: dict[str, str]) -> subprocess.CompletedProcess[str]:
    session.mkdir(exist_ok=True)
    (session / "calls.jsonl").unlink(missing_ok=True)
    return subprocess.run(
        ["bash", str(LAUNCHER)],
        env={k: v for k, v in os.environ.items() if not k.startswith("VROID_")}
        | paths
        | {"VROID_SESSION_DIR": str(session)},
        capture_output=True,
        text=True,
        timeout=20,
    )


def launch(session: Path, paths: dict[str, str]) -> list[dict[str, Any]]:
    result = run(session, paths)
    assert result.returncode == 0, result.stderr
    calls = (session / "calls.jsonl").read_text().splitlines()
    return [json.loads(call) for call in calls]


def unix(argument: str) -> Path:
    """The Unix path of a Wine `Z:` path."""
    assert argument.startswith("Z:"), argument
    return Path(PureWindowsPath(argument[2:]).as_posix())


def option(call: dict[str, Any], name: str) -> Path:
    return unix(call["args"][call["args"].index(name) + 1])


def test_new_session_runs_the_shared_install(writable: Path) -> None:
    # R-SES-9
    session = writable / "session"
    paths = store(writable, "1")
    boot, server, app = launch(session, paths)
    # Wine's Mono and Gecko setup must be off from the first call.
    assert Path(boot["program"]).name == "wineboot"
    assert "mscoree,mshtml=" in boot["env"]["WINEDLLOVERRIDES"]
    assert Path(server["program"]).name == "wineserver"
    assert app["program"] == f"{paths['VROID_WINE']}/bin/wine"
    assert app["args"][0] == f"{paths['VROID_APP']}/VRoidStudio.exe"
    assert "winhttp=n,b" in app["env"]["WINEDLLOVERRIDES"]
    assert "mscoree,mshtml=" in app["env"]["WINEDLLOVERRIDES"]
    root = session / "bepinex"
    target = option(app, "--doorstop-target-assembly")
    assert target == root / "core/BepInEx.Unity.IL2CPP.dll"
    assert target.read_text() == "1"
    assert option(app, "--unhollowed-path") == Path(paths["VROID_INTEROP"])
    assert unix(app["env"]["BEPINEX_PRELOADER_LOG"]) == root / "preloader.log"
    assert option(app, "-logFile") == session / "player.log"
    assert (root / "plugins/VroidBridge.dll").read_text() == "1"
    assert not (session / "wine/drive_c/VRoidStudio").exists()


def test_upgrade_switches_store_paths(writable: Path) -> None:
    # R-SES-9
    session = writable / "session"
    launch(session, store(writable, "1"))
    config = session / "bepinex/config/BepInEx.cfg"
    config.write_text("rewritten by BepInEx")
    calls = launch(session, store(writable, "2"))
    # The prefix exists now, so only the app runs.
    assert [Path(call["program"]).name for call in calls] == ["wine"]
    root = session / "bepinex"
    for path in ["core/BepInEx.Unity.IL2CPP.dll", "plugins/VroidBridge.dll"]:
        assert (root / path).read_text() == "2"
    # BepInEx rewrites its config, so it must be a writable file, not a link.
    assert not config.is_symlink()
    assert config.read_text() == "2"
    assert config.stat().st_mode & 0o200


def test_installed_session_uses_the_shared_install(writable: Path) -> None:
    # R-SES-9
    session = writable / "session"
    old = session / "wine/drive_c/VRoidStudio"
    for path in ["VRoidStudio.exe", "BepInEx/interop/old.dll", "winhttp.dll"]:
        (old / path).parent.mkdir(parents=True, exist_ok=True)
        (old / path).write_text("old")
    (session / "wine/drive_c/users/user/AppData").mkdir(parents=True)
    (session / "installed").touch()
    before = sorted((p, p.read_bytes()) for p in old.rglob("*") if p.is_file())
    paths = store(writable, "1")
    (app,) = launch(session, paths)
    assert app["args"][0] == f"{paths['VROID_APP']}/VRoidStudio.exe"
    assert option(app, "--unhollowed-path") == Path(paths["VROID_INTEROP"])
    after = sorted((p, p.read_bytes()) for p in old.rglob("*") if p.is_file())
    assert after == before


def test_app_environment_names_its_store_paths(writable: Path) -> None:
    # R-SES-10: Nix keeps store paths named in a running process's
    # environment, so every store path the app reads must be named there.
    session = writable / "session"
    paths = store(writable, "1")
    *_, app = launch(session, paths)
    named = [Path(app["env"][name]) for name in STORE]
    assert named == [Path(paths[name]) for name in STORE]
    read = [Path(app["program"])]
    read += [Path(a) for a in app["args"] if a.startswith("/")]
    read += [unix(a) for a in app["args"] if a.startswith("Z:")]
    read += [p.resolve() for p in (session / "bepinex").iterdir() if p.is_symlink()]
    used: set[Path] = set()
    for path in read:
        if path.is_relative_to(writable / "store-1"):
            (prefix,) = [prefix for prefix in named if path.is_relative_to(prefix)]
            used.add(prefix)
    assert used == set(named)


def test_launcher_accepts_terms_before_vroid_starts(writable: Path) -> None:
    # R-SES-8
    session = writable / "session"
    launch(session, store(writable, "1"))
    started = (session / "started.xml").read_bytes()
    # VRoid's own encoding: UTF-8 with a byte order mark.
    assert started.startswith(b'\xef\xbb\xbf<?xml version="1.0" encoding="utf-8"?>')
    for element in [
        b"<AllAgreementsAreAccepted>Yes</AllAgreementsAreAccepted>",
        b"<UpdatedPrivacyPolicyAreAccepted>Yes</UpdatedPrivacyPolicyAreAccepted>",
        b"<tutorialIsRead>true</tutorialIsRead>",
    ]:
        assert element in started


def saved_preferences(session: Path, accepted: str) -> tuple[Path, bytes]:
    """Write preferences like VRoid's, with other keys and CRLF line endings."""
    preferences = session / PREFERENCES
    preferences.parent.mkdir(parents=True)
    content = (
        '\ufeff<?xml version="1.0" encoding="utf-8"?>\r\n<EditorOption>\r\n'
        "  <Preference>\r\n    <LanguageMode>Ja</LanguageMode>\r\n"
        f"    <AllAgreementsAreAccepted>{accepted}</AllAgreementsAreAccepted>\r\n"
        "    <UpdatedPrivacyPolicyAreAccepted>Yes</UpdatedPrivacyPolicyAreAccepted>\r\n"
        "    <tutorialIsRead>false</tutorialIsRead>\r\n"
        "  </Preference>\r\n</EditorOption>\r\n"
    ).encode()
    preferences.write_bytes(content)
    return preferences, content


def test_launcher_leaves_accepted_preferences_unchanged(writable: Path) -> None:
    # R-SES-8
    session = writable / "session"
    preferences, content = saved_preferences(session, "Yes")
    modified = preferences.stat().st_mtime_ns
    launch(session, store(writable, "1"))
    assert (session / "started.xml").read_bytes() == content
    assert preferences.stat().st_mtime_ns == modified


def test_launcher_refuses_preferences_without_accepted_terms(writable: Path) -> None:
    # R-SES-8
    session = writable / "session"
    preferences, content = saved_preferences(session, "No")
    result = run(session, store(writable, "1"))
    assert result.returncode == 1
    assert f"{preferences} do not record its accepted terms" in result.stderr
    assert "Delete that file" in result.stderr
    assert preferences.read_bytes() == content
    assert not (session / "started.xml").exists()
