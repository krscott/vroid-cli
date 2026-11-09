from collections.abc import Iterator
from pathlib import Path

import pytest


@pytest.fixture
def writable(tmp_path: Path) -> Iterator[Path]:
    """A temporary directory whose read-only contents are cleaned up."""
    yield tmp_path
    for path in tmp_path.rglob("*"):
        if not path.is_symlink():
            path.chmod(path.stat().st_mode | 0o700)
