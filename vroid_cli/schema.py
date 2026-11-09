"""Request and result models; the JSON Schema printed by --agent-skill comes from here."""

import json
from collections import Counter
from pathlib import Path
from typing import Annotated, Any, Literal

from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    PositiveFloat,
    TypeAdapter,
    ValidationError,
    ValidatorFunctionWrapHandler,
    WrapValidator,
    field_validator,
    model_validator,
)

Section = Literal["Face", "Body"]


def one_error(value: Any, handler: ValidatorFunctionWrapHandler) -> Any:
    # Without this, a bad value gets one error per union member.
    try:
        return handler(value)
    except ValidationError:
        raise ValueError(
            "expected a finite number, or an array of them for a repeated label"
        ) from None


Value = Annotated[float | list[float], WrapValidator(one_error)]
Template = Annotated[
    dict[str, dict[str, Value]],
    Field(
        description="category name -> parameter label -> value. A label that "
        "occurs more than once in a category takes an array of its values in "
        "app order. Categories share parameters by label, so each label may "
        "appear in only one category."
    ),
]


class Model(BaseModel):
    # The defaults accept NaN, coerce "0.5" to 0.5, and drop misspelled fields.
    model_config = ConfigDict(
        strict=True, extra="forbid", allow_inf_nan=False, frozen=True
    )


def resolved(value: Path, suffix: str, kind: str) -> Path:
    path = (Path.cwd() / value).resolve()
    if path.suffix.lower() != suffix:
        raise ValueError(f"{kind} paths must end in {suffix}")
    return path


def project_path(value: Path) -> Path:
    return resolved(value, ".vroid", "project")


def new_file(path: Path) -> Path:
    """Refuse an existing destination or a missing directory (R-EDIT-4, R-VIEW-3)."""
    if path.exists() or path.is_symlink():
        raise ValueError(f"refusing to overwrite {path}")
    if not path.parent.is_dir():
        raise ValueError(f"{path.parent} is not a directory")
    return path


class Launch(Model):
    """Start the app, or wait until it is ready."""

    method: Literal["launch"]
    timeout: PositiveFloat = Field(120.0, description="seconds to wait for readiness")


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
    Face: Template = Field(default_factory=dict)
    Body: Template = Field(default_factory=dict)

    @model_validator(mode="after")
    def each_parameter_once(self) -> "Apply":
        # The same label in two categories of a section is one parameter; the
        # later value would silently win.
        for section, template in [("Face", self.Face), ("Body", self.Body)]:
            first: dict[str, str] = {}
            for category, values in template.items():
                for label in values:
                    if (other := first.setdefault(label, category)) != category:
                        raise ValueError(
                            f"{section}/{category}/{label} is the same parameter "
                            f"as {section}/{other}/{label}; set it once"
                        )
        return self


class Save(Model):
    """Save the project to a new .vroid path in an existing directory."""

    method: Literal["save"]
    path: Path

    @field_validator("path")
    @classmethod
    def new(cls, value: Path) -> Path:
        return new_file(project_path(value))


ImageSize = Annotated[int, Field(ge=16, le=4096, description="pixels")]


class Render(Model):
    """Render the open character to a new PNG in an existing directory.

    The image is orthographic on a plain background. Body views of one
    character at one size share their scale and ground line.
    """

    method: Literal["render"]
    path: Path
    view: Literal["front", "three_quarter", "side", "back"] = Field(
        description="three_quarter and side show the character's left side"
    )
    frame: Literal["body", "head"] = Field(
        "body",
        description="body fits the whole character; head fits everything "
        "above the neck, so a front head view is a face close-up",
    )
    width: ImageSize = 1024
    height: ImageSize = 1024

    @field_validator("path")
    @classmethod
    def new(cls, value: Path) -> Path:
        return new_file(resolved(value, ".png", "image"))


Request = Annotated[
    Launch
    | Status
    | Stop
    | Open
    | Categories
    | Parameters
    | Dump
    | Apply
    | Save
    | Render,
    Field(discriminator="method"),
]
BridgeRequest = Open | Categories | Parameters | Dump | Apply | Save | Render
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
    changed: int = Field(
        description="number of values that changed. Each element of a repeated "
        "label's array counts separately; values already equal to the request "
        "don't count."
    )
    undo: Apply = Field(description="restores the previous values")


class Saved(Model):
    saved: str
    bytes: int


class Rendered(Model):
    rendered: str
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
    "render": TypeAdapter(Rendered),
}


def parse(text: str) -> Request:
    """Validate a request. Raises ValueError naming each bad field."""

    def unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        value = dict(pairs)
        if len(value) < len(pairs):
            counts = Counter(key for key, _ in pairs)
            duplicates = sorted(key for key, count in counts.items() if count > 1)
            raise ValueError(f"duplicate keys: {', '.join(duplicates)}")
        return value

    try:
        # pydantic keeps the last of duplicate keys; refuse them instead.
        json.loads(text, object_pairs_hook=unique)
    except json.JSONDecodeError:
        pass  # pydantic reports invalid JSON below.
    except RecursionError:
        raise ValueError("request is nested too deeply") from None
    try:
        return REQUEST.validate_json(text)
    except ValidationError as error:
        raise ValueError(describe(error, tagged=True)) from None


def describe(error: ValidationError, *, tagged: bool = False) -> str:
    """One line per error: the field's path and the reason.

    `tagged` marks a request error, whose path starts with the method tag.
    """
    lines: list[str] = []
    for item in error.errors(include_url=False):
        field = ".".join(map(str, item["loc"][tagged:])) or (
            "request" if tagged else "result"
        )
        lines.append(f"{field}: {item['msg'].removeprefix('Value error, ')}")
    return "\n".join(lines)


def json_schema() -> dict[str, Any]:
    return {
        "request": REQUEST.json_schema(),
        "results": {
            method: adapter.json_schema() for method, adapter in RESULTS.items()
        },
    }
