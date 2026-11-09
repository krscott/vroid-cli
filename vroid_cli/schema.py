"""Request and result models; the JSON Schema printed by --agent-skill comes from here."""

import json
from pathlib import Path
from typing import Annotated, Any, Literal

from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    PositiveFloat,
    TypeAdapter,
    ValidationError,
    field_validator,
)

Section = Literal["Face", "Body"]
# A label that occurs more than once in a category maps to its values in app order.
Value = float | list[float]
Template = dict[str, dict[str, Value]]


class Model(BaseModel):
    # The defaults accept NaN, coerce "0.5" to 0.5, and drop misspelled fields.
    model_config = ConfigDict(
        strict=True, extra="forbid", allow_inf_nan=False, frozen=True
    )


def project_path(value: Path) -> Path:
    path = (Path.cwd() / value).resolve()
    if path.suffix.lower() != ".vroid":
        raise ValueError("project paths must end in .vroid")
    return path


class Launch(Model):
    """Start the app, or wait until it is ready."""

    method: Literal["launch"]
    timeout: PositiveFloat = Field(600.0, description="seconds to wait for readiness")


class Status(Model):
    """Report the session's state."""

    method: Literal["status"]


class Stop(Model):
    """Stop the app and its private desktop."""

    method: Literal["stop"]
    force: bool = Field(
        False, description="stop despite unsaved changes or a pending request"
    )


class Open(Model):
    """Open an existing .vroid project."""

    method: Literal["open"]
    path: Path

    @field_validator("path")
    @classmethod
    def existing(cls, value: Path) -> Path:
        path = project_path(value)
        if not path.is_file():
            raise ValueError(f"{path} is not an existing file")
        return path


class Categories(Model):
    """List a section's categories."""

    method: Literal["categories"]
    section: Section


class Parameters(Model):
    """List a category's numeric parameters."""

    method: Literal["parameters"]
    section: Section
    category: str


class Dump(Model):
    """Return every Face and Body numeric value as an apply request."""

    method: Literal["dump"]


class Apply(Model):
    """Set numeric values, keyed by category and parameter label.

    Every name and bound is checked before any value changes.
    """

    method: Literal["apply"]
    Face: Template = Field(default_factory=dict[str, dict[str, Value]])
    Body: Template = Field(default_factory=dict[str, dict[str, Value]])


class Save(Model):
    """Save the project to a new .vroid path in an existing directory."""

    method: Literal["save"]
    path: Path

    @field_validator("path")
    @classmethod
    def new(cls, value: Path) -> Path:
        path = project_path(value)
        if path.exists() or path.is_symlink():
            raise ValueError(f"refusing to overwrite {path}")  # R-EDIT-4
        if not path.parent.is_dir():
            raise ValueError(f"{path.parent} is not a directory")
        return path


Request = Annotated[
    Launch | Status | Stop | Open | Categories | Parameters | Dump | Apply | Save,
    Field(discriminator="method"),
]
BridgeRequest = Open | Categories | Parameters | Dump | Apply | Save
REQUEST: TypeAdapter[Request] = TypeAdapter(Request)


class SessionStatus(Model):
    session: str
    directory: str
    desktop: str
    app_running: bool
    terms_accepted: bool
    ready: bool
    pending_request: bool
    bridge: dict[str, Any] | None = Field(description="the bridge's last status")


class Stopped(Model):
    session: str
    stopped: bool


class Opened(Model):
    opened: str


class Parameter(Model):
    label: str
    value: float
    min: float
    max: float


class Applied(Model):
    changed: int = Field(description="number of values that changed")
    undo: Apply = Field(description="restores the previous values")


class Saved(Model):
    saved: str
    bytes: int


RESULTS: dict[str, TypeAdapter[Any]] = {
    "launch": TypeAdapter(SessionStatus),
    "status": TypeAdapter(SessionStatus),
    "stop": TypeAdapter(Stopped),
    "open": TypeAdapter(Opened),
    "categories": TypeAdapter(list[str]),
    "parameters": TypeAdapter(list[Parameter]),
    "dump": TypeAdapter(Apply),
    "apply": TypeAdapter(Applied),
    "save": TypeAdapter(Saved),
}


def parse(text: str) -> Request:
    """Validate a request. Raises ValueError naming each bad field."""

    def unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        keys = [key for key, _ in pairs]
        if duplicates := sorted({key for key in keys if keys.count(key) > 1}):
            raise ValueError(f"duplicate keys: {', '.join(duplicates)}")
        return dict(pairs)

    try:
        # pydantic keeps the last of duplicate keys; refuse them instead.
        json.loads(text, object_pairs_hook=unique)
    except json.JSONDecodeError:
        pass  # pydantic reports invalid JSON below.
    try:
        return REQUEST.validate_json(text)
    except ValidationError as error:
        raise ValueError(describe(error)) from None


def describe(error: ValidationError) -> str:
    """One line per error: the field's path, without the method tag, and the reason."""
    lines: list[str] = []
    for item in error.errors(include_url=False):
        field = ".".join(map(str, item["loc"][1:])) or "request"
        lines.append(f"{field}: {item['msg'].removeprefix('Value error, ')}")
    return "\n".join(lines)


def json_schema() -> dict[str, Any]:
    return {
        "request": REQUEST.json_schema(),
        "results": {
            method: adapter.json_schema() for method, adapter in RESULTS.items()
        },
    }
