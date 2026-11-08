"""Edit VRoid Studio characters through its internal API on a private desktop.

A successful command prints one JSON value on stdout and exits 0. A failed
command prints its reason on stderr and exits nonzero (2 for usage errors).
"""

import argparse
import json
import math
import os
import sys
from pathlib import Path
from typing import Any

from . import rpc
from . import session as sessions

SECTIONS = ["Face", "Body"]


def parser() -> argparse.ArgumentParser:
    root = argparse.ArgumentParser(
        prog="vroid-cli",
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    root.add_argument("--session", help="session name; use one per agent")
    root.add_argument(
        "--agent-skill", action="store_true", help="print the agent guide and exit"
    )
    commands = root.add_subparsers(dest="command", metavar="COMMAND")

    launch = commands.add_parser("launch", help="start the app or wait until ready")
    launch.add_argument(
        "--timeout",
        type=float,
        default=600.0,
        help="seconds to wait for readiness (default: 600)",
    )
    commands.add_parser("status", help="report the session's state")
    stop = commands.add_parser("stop", help="stop the app and its private desktop")
    stop.add_argument(
        "--force",
        action="store_true",
        help="stop despite unsaved changes or a running request",
    )
    commands.add_parser("open", help="open an existing .vroid project").add_argument(
        "path", type=Path
    )
    commands.add_parser("categories", help="list a section's categories").add_argument(
        "section", choices=SECTIONS
    )
    parameters = commands.add_parser(
        "parameters", help="list a category's numeric parameters"
    )
    parameters.add_argument("section", choices=SECTIONS)
    parameters.add_argument("category")
    edit = commands.add_parser("set", help="change one numeric parameter")
    edit.add_argument("section", choices=SECTIONS)
    edit.add_argument("category")
    edit.add_argument("parameter")
    edit.add_argument("value", type=float)
    commands.add_parser("save", help="save the project to a new path").add_argument(
        "path", type=Path
    )
    return root


def request_body(args: argparse.Namespace, *, cwd: Path) -> dict[str, Any] | str:
    """Return the request body, or a usage error, before any state is touched."""
    if args.command in {"open", "save"}:
        path: Path = (cwd / args.path).resolve()
        if path.suffix.lower() != ".vroid":
            return "project paths must end in .vroid"
        if args.command == "open" and not path.is_file():
            return f"{path} is not an existing file"
        if args.command == "save":
            if path.exists() or path.is_symlink():
                return f"refusing to overwrite {path}"  # R-EDIT-4
            if not path.parent.is_dir():
                return f"{path.parent} is not a directory"
        return {"path": rpc.wine_path(path)}
    if args.command == "set" and not math.isfinite(args.value):
        return "value must be a finite number"  # R-EDIT-2
    names = {"categories": [], "parameters": ["category"]}.get(
        args.command, ["category", "parameter", "value"]
    )
    return {"section": args.section} | {name: getattr(args, name) for name in names}


def main() -> int:
    cli = parser()
    args = cli.parse_args()
    if args.agent_skill:
        print(Path(os.environ["VROID_CLI_SKILL"]).read_text(), end="")
        return 0
    if args.command is None:
        cli.error("a command is required")
    if args.session is None:
        cli.error("--session NAME is required")
    cwd = Path.cwd()
    try:
        session = sessions.resolve(args.session, cwd=cwd)
    except ValueError as error:
        cli.error(str(error))
    result: Any
    try:
        if args.command == "launch":
            result = sessions.launch(session, timeout_s=args.timeout)
        elif args.command == "status":
            result = sessions.summary(session)
        elif args.command == "stop":
            result = sessions.stop(session, force=args.force)
        else:
            body = request_body(args, cwd=cwd)
            if isinstance(body, str):
                cli.error(body)
            result = sessions.request(session, method=args.command, args=body)
    except RuntimeError as error:
        print(f"vroid-cli: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
