"""Edit VRoid Studio characters through its internal API on a private desktop.

REQUEST is JSON text, @PATH to read it from a file, or - to read stdin. A
successful command prints one JSON value on stdout and exits 0. A failed
command prints its reason on stderr and exits nonzero (2 for usage errors).
`--agent-skill` (or `--help`) prints the guide and the request and result
JSON Schema.
"""

import json
import os
import sys
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
        usage("VROID_CLI_SKILL is unset; run the packaged vroid-cli")
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
    if isinstance(request, schema.Open | schema.Save):
        args["path"] = rpc.wine_path(request.path)
    return args


def run(session: sessions.Session, request: schema.Request) -> Any:
    match request:
        case schema.Launch():
            return sessions.launch(session, timeout_s=request.timeout)
        case schema.Status():
            return sessions.summary(session)
        case schema.Stop():
            return sessions.stop(session, force=request.force)
        case schema.Copy():
            source = sessions.resolve(request.source, cwd=Path.cwd())
            return sessions.copy(session, source=source)
        case schema.Open() | schema.Save():
            result: Any = sessions.request(
                session, method=request.method, args=bridge_args(request)
            )
            # The bridge reports the Wine path; report the caller's path instead.
            if not isinstance(result, dict):
                return result  # Result validation reports it.
            paths = cast(dict[str, Any], result)
            paths["opened" if isinstance(request, schema.Open) else "saved"] = str(
                request.path
            )
            return paths
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
        print(
            f"vroid-cli: {request.method} ran, but its result does not match the "
            f"schema; inspect the session before another edit:\n"
            f"{schema.describe(error)}",
            file=sys.stderr,
        )
        return 1
    # Indented so saved templates diff line by line.
    print(adapter.dump_json(result, indent=2).decode())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
