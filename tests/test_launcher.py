"""Exercise the in-desktop launcher with a stand-in Wine."""

import os
import subprocess
import sys
from collections.abc import Iterator
from pathlib import Path

import pytest

LAUNCHER = Path(__file__).resolve().parent.parent / "scripts/vroid-studio.sh"


@pytest.fixture
def writable(tmp_path: Path) -> Iterator[Path]:
    yield tmp_path
    for path in tmp_path.rglob("*"):
        path.chmod(path.stat().st_mode | 0o700)


def launch(
    tmp_path: Path, session: Path, bundle: Path
) -> subprocess.CompletedProcess[str]:
    binary = tmp_path / "bin"
    binary.mkdir(exist_ok=True)
    wine = binary / "wine"
    wine.write_text(f"#!{sys.executable}\n")
    wine.chmod(0o755)
    return subprocess.run(
        ["bash", str(LAUNCHER)],
        env=os.environ
        | {
            "PATH": f"{binary}{os.pathsep}{os.environ['PATH']}",
            "VROID_SESSION_DIR": str(session),
            "VROID_INSTALLER": "unused",
            "VROID_BRIDGE_BUNDLE": str(bundle),
        },
        capture_output=True,
        text=True,
        timeout=20,
    )


def store_bundle(path: Path, version: str) -> Path:
    """Create a bundle with the read-only modes of a Nix store path."""
    for directory in ["dotnet", "BepInEx/plugins"]:
        (path / directory).mkdir(parents=True)
    (path / "dotnet/runtime.dll").write_text(version)
    (path / "BepInEx/plugins/VroidBridge.dll").write_text(version)
    (path / "winhttp.dll").write_text(version)
    for item in sorted(path.rglob("*"), reverse=True):
        item.chmod(0o555 if item.is_dir() else 0o444)
    path.chmod(0o555)
    return path


@pytest.mark.parametrize("legacy", [False, True])
def test_bridge_upgrade_replaces_store_copies(writable: Path, legacy: bool) -> None:
    # R-SES-6
    session = writable / "session"
    app = session / "wine/drive_c/VRoidStudio"
    app.mkdir(parents=True)
    (app / "VRoidStudio.exe").touch()
    (session / "installed").touch()
    first = store_bundle(writable / "first", "1")
    second = store_bundle(writable / "second", "2")
    if legacy:
        # Earlier installs copied the store's read-only directory modes.
        subprocess.run(["cp", "-r", f"{first}/.", str(app)], check=True)
        (app / ".vroid-bridge-bundle").write_text(f"{first}\n")
    else:
        installed = launch(writable, session, first)
        assert installed.returncode == 0, installed.stderr
    upgraded = launch(writable, session, second)
    assert upgraded.returncode == 0, upgraded.stderr
    for name in [
        "dotnet/runtime.dll",
        "BepInEx/plugins/VroidBridge.dll",
        "winhttp.dll",
    ]:
        assert (app / name).read_text() == "2"
    assert (app / ".vroid-bridge-bundle").read_text() == f"{second}\n"
    assert (app / "dotnet").stat().st_mode & 0o200


def test_failed_copy_is_retried_on_the_next_launch(writable: Path) -> None:
    # R-SES-6
    session = writable / "session"
    app = session / "wine/drive_c/VRoidStudio"
    app.mkdir(parents=True)
    (app / "VRoidStudio.exe").touch()
    (session / "installed").touch()
    (app / ".vroid-bridge-bundle").write_text("previous\n")
    bundle = store_bundle(writable / "bundle", "1")
    (bundle / "winhttp.dll").chmod(0)
    result = launch(writable, session, bundle)
    assert result.returncode != 0
    assert "winhttp.dll" in result.stderr
    assert (app / ".vroid-bridge-bundle").read_text() == "previous\n"
    (bundle / "winhttp.dll").chmod(0o444)
    retried = launch(writable, session, bundle)
    assert retried.returncode == 0, retried.stderr
    assert (app / "winhttp.dll").read_text() == "1"
    assert (app / ".vroid-bridge-bundle").read_text() == f"{bundle}\n"


def test_incomplete_installation_is_not_repeated(writable: Path) -> None:
    # R-SES-5
    session = writable / "session"
    (session / "wine").mkdir(parents=True)
    result = launch(writable, session, store_bundle(writable / "bundle", "1"))
    assert result.returncode == 1
    assert "Incomplete installation" in result.stderr
    assert list((session / "wine").iterdir()) == []
