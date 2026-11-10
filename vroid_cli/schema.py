"""Request and result models; the JSON Schema printed by --agent-skill comes from here."""

import json
from collections import Counter
from pathlib import Path
from typing import Annotated, Any, Literal, get_args

from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    PositiveFloat,
    SerializerFunctionWrapHandler,
    StringConstraints,
    TypeAdapter,
    ValidationError,
    ValidatorFunctionWrapHandler,
    WrapValidator,
    field_validator,
    model_serializer,
    model_validator,
)

Section = Literal["Face", "Hair", "Body", "Outfit"]
SECTIONS: tuple[Section, ...] = get_args(Section)

# As VRoid's editor shows it, on the same sRGB scale; there is no alpha.
Color = Annotated[
    str,
    StringConstraints(pattern=r"^#[0-9A-Fa-f]{6}$"),
    Field(description='a colour as "#RRGGBB"'),
]


def one_error(value: Any, handler: ValidatorFunctionWrapHandler) -> Any:
    # Without this, a bad value gets one error per union member.
    try:
        return handler(value)
    except ValidationError:
        if isinstance(value, str):
            raise ValueError(
                f'{value!r} is neither a number nor a colour as "#RRGGBB"'
            ) from None
        raise ValueError(
            "expected a finite number, an array of them for a repeated label, "
            'or a colour as "#RRGGBB"'
        ) from None


Value = Annotated[float | list[float] | Color, WrapValidator(one_error)]
# Marks an item that is not one of VRoid's built-in items.
CUSTOM = "custom"
ItemId = Annotated[str, StringConstraints(pattern=r"^[A-Za-z0-9_]+$")]


def no_default(schema: dict[str, Any]) -> None:
    # An omitted item is kept, while an explicit null takes it off.
    schema.pop("default", None)


class Model(BaseModel):
    # The defaults accept NaN, coerce "0.5" to 0.5, and drop misspelled fields.
    model_config = ConfigDict(
        strict=True, extra="forbid", allow_inf_nan=False, frozen=True
    )


class CategoryTemplate(BaseModel):
    """A category's item, numeric values, and colours, keyed by label."""

    model_config = ConfigDict(
        strict=True, extra="allow", allow_inf_nan=False, frozen=True
    )
    # Types the extra labels; pyright cannot see pydantic allows narrowing it.
    __pydantic_extra__: dict[str, Value] = Field(init=False)  # pyright: ignore[reportIncompatibleVariableOverride]  # fmt: skip
    item: ItemId | None = Field(
        None,
        description="the item to wear: a built-in item's identifier as `items` "
        'lists it, or "custom" for the item that is not built in that the '
        "category already wears; null takes off the item of a category that "
        "`items` reports as removable. Omit it to keep the current item.",
        json_schema_extra=no_default,
    )

    @model_serializer(mode="wrap")
    def omit_unset_item(self, handler: SerializerFunctionWrapHandler) -> Any:
        data = handler(self)
        if "item" not in self.model_fields_set:
            data.pop("item", None)
        return data


Template = Annotated[
    dict[str, CategoryTemplate],
    Field(
        description="category name -> label -> value: a number, or a colour "
        'as "#RRGGBB", and the optional "item". A numeric label that occurs '
        "more than once in a category takes an array of its values in app "
        "order. Categories share numeric parameters by label, so each numeric "
        "label may appear in only one category. Some colours are shared too, "
        "such as skin; set each in one place."
    ),
]


class Accessory(BaseModel):
    """One worn accessory: its item and its colours, keyed by label."""

    model_config = ConfigDict(
        strict=True, extra="allow", allow_inf_nan=False, frozen=True
    )
    # Types the extra labels; pyright cannot see pydantic allows narrowing it.
    __pydantic_extra__: dict[str, Color] = Field(init=False)  # pyright: ignore[reportIncompatibleVariableOverride]  # fmt: skip
    item: ItemId = Field(
        description="a built-in accessory's identifier as `accessories` lists it, "
        'or "custom" for a worn accessory that is not built in'
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
    """List a section's categories. Outfit categories hold only items."""

    method: Literal["categories"]
    section: Section


class Parameters(Model):
    """List a category's numeric parameters and colours."""

    method: Literal["parameters"]
    section: Section
    category: str


class Items(Model):
    """List a category's built-in items and the item it wears."""

    method: Literal["items"]
    section: Section
    category: str


class Accessories(Model):
    """List the worn accessories and the built-in ones that can be added."""

    method: Literal["accessories"]


class Dump(Model):
    """Return every item, accessory, numeric value, and colour as an apply request."""

    method: Literal["dump"]


class Apply(Model):
    """Select items and accessories, and set numeric values and colours.

    Every name, item, bound, and colour is checked before anything changes.
    Items and accessories change first, then values and colours.
    """

    method: Literal["apply"]
    Face: Template = Field(default_factory=dict)
    Hair: Template = Field(default_factory=dict)
    Body: Template = Field(default_factory=dict)
    Outfit: Template = Field(default_factory=dict)
    Accessories: list[Accessory] | None = Field(
        None,
        description="every accessory to wear, in any order; apply adds and "
        "removes accessories to match, so list the worn ones to keep them. Added "
        "accessories take VRoid's default placement, and a removed one loses its "
        "placement. Omit it to keep the current ones.",
    )
    discard_custom: bool = Field(
        False,
        description="allow replacing or removing items and accessories that are "
        "not built in; the result's `discarded` names them, and `undo` cannot "
        "restore them. Save a copy of the project first.",
    )

    @model_serializer(mode="wrap")
    def omit_defaults(self, handler: SerializerFunctionWrapHandler) -> Any:
        # Dumped and undo templates leave accessories alone unless they list
        # them, and never discard anything.
        data = handler(self)
        if self.Accessories is None:
            data.pop("Accessories", None)
        if not self.discard_custom:
            data.pop("discard_custom", None)
        return data

    @model_validator(mode="after")
    def each_parameter_once(self) -> "Apply":
        # The same numeric label in two categories of a section is one
        # parameter; the later value would silently win. Colour labels such as
        # "Color" repeat across unrelated colours; the bridge checks those.
        for section in SECTIONS:
            first: dict[str, str] = {}
            categories: dict[str, CategoryTemplate] = getattr(self, section)
            for category, values in categories.items():
                extra: dict[str, Any] = values.model_extra or {}
                for label, value in extra.items():
                    if isinstance(value, str):
                        continue
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
    character state at one size share their scale and ground line; items that
    make the character taller or wider change them.
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
    | Items
    | Accessories
    | Dump
    | Apply
    | Save
    | Render,
    Field(discriminator="method"),
]
BridgeRequest = (
    Open | Categories | Parameters | Items | Accessories | Dump | Apply | Save | Render
)
REQUEST: TypeAdapter[Request] = TypeAdapter(Request)


class SessionStatus(Model):
    session: str
    directory: str
    desktop: str
    app_running: bool
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


class ColorParameter(Model):
    label: str
    value: Color


class ItemList(Model):
    selected: str | None = Field(
        description='the worn built-in item, "custom" for an item that is not '
        "built in, or null"
    )
    removable: bool = Field(description="whether the item can be taken off")
    set: bool = Field(
        description="selecting one of these items selects items in other "
        "categories, so `dump` lists those instead"
    )
    items: list[str] = Field(description="built-in item identifiers in app order")


class AvailableAccessory(Model):
    item: str
    name: str


class AccessoryList(Model):
    worn: list[Accessory] = Field(description="in the order `dump` lists them")
    available: list[AvailableAccessory] = Field(
        description="built-in accessories, including ones VRoid's editor does not "
        "offer, such as animal ears and tails"
    )


class Applied(Model):
    changed: int = Field(
        description="number of items, accessories, and values that changed. Each "
        "element of a repeated label's array counts separately; values already "
        "equal to the request, and VRoid's own changes to other categories, "
        "don't count."
    )
    discarded: list[str] = Field(
        description="non-built-in items and accessories this request replaced or "
        "removed, which `undo` cannot restore"
    )

    undo: str = Field(
        description="path of an apply request, written before the first change, "
        "that restores the previous items, accessories, and values, except what "
        "`discarded` names; apply it with `@PATH`. Undoing an item change in a "
        "section that still wears an item that is not built in needs "
        "discard_custom."
    )


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
    "parameters": TypeAdapter(list[Parameter | ColorParameter]),
    "items": TypeAdapter(ItemList),
    "accessories": TypeAdapter(AccessoryList),
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
