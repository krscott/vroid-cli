"""Edit VRoid Studio characters through its internal API on a private desktop.

REQUEST is JSON text, @PATH to read it from a file, or - to read stdin. A
successful command prints one JSON value on stdout and exits 0. A failed
command prints its reason on stderr and exits nonzero (2 for usage errors).
`--agent-skill` (or `--help`) prints the guide and the request and result
JSON Schema.
"""

import errno
import json
import os
import sys
import uuid
from pathlib import Path
from typing import Any, NoReturn, cast

from pydantic import ValidationError

from . import rpc, schema
from . import session as sessions

USAGE = "usage: vroid-cli SESSION REQUEST\n       vroid-cli --agent-skill"


def usage(reason: str) -> NoReturn:
    print(f"vroid-cli: {reason}\n{USAGE}", file=sys.stderr)
    raise SystemExit(2)


def agent_skill() -> str:
    skill = os.environ.get("VROID_CLI_SKILL")
    if not skill:
        usage("VROID_CLI_SKILL is unset; run the vroid-cli command")
    return (
        f"{Path(skill).read_text().rstrip()}\n\n## JSON Schema\n\n"
        "Requests and results, generated from the validating models:\n\n"
        f"```json\n{json.dumps(schema.json_schema(), indent=2)}\n```\n"
    )


def read_request(argument: str) -> str:
    if argument == "-":
        return sys.stdin.read()
    if argument.startswith("@"):
        try:
            return Path(argument[1:]).read_text()
        except OSError as error:
            usage(f"cannot read {argument[1:]}: {error.strerror}")
    if argument.lstrip().startswith("{"):
        return argument
    usage("REQUEST must be a JSON object, @PATH, or -")


def bridge_args(request: schema.BridgeRequest) -> dict[str, Any]:
    args = request.model_dump(mode="json", exclude={"method"})
    if isinstance(request, schema.Open | schema.Save | schema.Import):
        args["path"] = rpc.wine_path(request.path)
    if isinstance(request, schema.Render):
        args["view"] = request.views
        if request.reference is not None:
            args["reference"] = rpc.wine_path(request.reference)
    return args


def with_path(result: Any, key: str, path: Path) -> Any:
    """Report the caller's path instead of the bridge's Wine path."""
    if not isinstance(result, dict):
        return result  # Result validation reports it.
    return cast(dict[str, Any], result) | {key: str(path)}


# os.link errors that mean the filesystem has no hard links.
NO_HARD_LINKS = {errno.EPERM, errno.EOPNOTSUPP, errno.ENOTSUP, errno.EXDEV}


def publish(
    session: sessions.Session,
    request: schema.Render | schema.Thumbnails | schema.Export,
) -> Any:
    """Have the bridge write a hidden file beside the destination, then link it.

    The link is atomic and never replaces an existing file (R-VIEW-3,
    R-EDIT-16). The hidden file is removed whatever happens; one left by a
    crash is named `.vroid-render-*.png` or `.vroid-export-*.vroidcustomitem`.
    """
    method = request.method
    kind, key = ("export", "exported") if method == "export" else ("render", "rendered")
    temporary = request.path.with_name(
        f".vroid-{kind}-{uuid.uuid4().hex}{request.path.suffix}"
    )
    args = bridge_args(request) | {"path": rpc.wine_path(temporary)}
    try:
        result = sessions.request(session, method=method, args=args)
        try:
            os.link(temporary, request.path)
        except FileExistsError:
            raise RuntimeError(
                f"refusing to overwrite {request.path}, created while drawing it"
            ) from None
        except OSError as error:
            hint = (
                ". Write to a filesystem that supports hard links"
                if error.errno in NO_HARD_LINKS
                else ""
            )
            raise RuntimeError(
                f"cannot link {temporary.name} to {request.path}: "
                f"{error.strerror}{hint}"
            ) from None
    finally:
        temporary.unlink(missing_ok=True)
    return with_path(result, key, request.path)


def run(session: sessions.Session, request: schema.Request) -> Any:
    match request:
        case schema.Launch():
            return sessions.launch(session, timeout_s=request.timeout)
        case schema.Status():
            return sessions.summary(session)
        case schema.Stop():
            return sessions.stop(session, force=request.force)
        case schema.Open() | schema.Save():
            result = sessions.request(
                session, method=request.method, args=bridge_args(request)
            )
            key = "opened" if isinstance(request, schema.Open) else "saved"
            return with_path(result, key, request.path)
        case schema.Render() | schema.Thumbnails() | schema.Export():
            return publish(session, request)
        case _:
            return sessions.request(
                session, method=request.method, args=bridge_args(request)
            )


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    if args and args[0] in {"-h", "--help", "--agent-skill"}:
        print(agent_skill(), end="")
        return 0
    if len(args) != 2:
        usage("expected SESSION and REQUEST")
    name, argument = args
    try:
        request = schema.parse(read_request(argument))
    except ValueError as error:
        usage(f"invalid request:\n{error}")
    try:
        session = sessions.resolve(name, cwd=Path.cwd())
    except ValueError as error:
        usage(str(error))
    try:
        raw = run(session, request)
    except (RuntimeError, OSError) as error:
        print(f"vroid-cli: {error}", file=sys.stderr)
        return 1
    adapter = schema.RESULTS[request.method]
    try:
        result = adapter.validate_python(raw)
    except ValidationError as error:
        undo = cast(dict[str, Any], raw).get("undo") if isinstance(raw, dict) else None
        print(
            f"vroid-cli: {request.method} ran, but its result does not match the "
            f"schema; inspect the session before another edit:\n"
            f"{schema.describe(error)}"
            + (
                f"\nIts undo file is {undo}; check it before applying it"
                if isinstance(undo, str)
                else ""
            ),
            file=sys.stderr,
        )
        return 1
    # Indented so saved templates diff line by line.
    print(adapter.dump_json(result, indent=2).decode())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
