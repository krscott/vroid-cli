# Every command is one JSON request

`vroid-cli SESSION REQUEST` is the only form of a command. There are no
subcommands and no flags: every method, including `launch`, `status`, and
`stop`, is a JSON object. Agents drive vroid-cli, and its results were already
JSON, so requests and results share one schema, and agents build, save,
compare, and replay requests with ordinary JSON tools.

The session is an argument and not a field, so a saved request works in any
session.

## Consequences

`dump` prints an `apply` request, which is what makes its output a template.

The request and result models that validate a command also generate the schema
that `--agent-skill` prints, so the documented interface cannot drift from the
one the CLI accepts.
