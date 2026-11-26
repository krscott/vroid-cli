# The CLI and the bridge exchange JSON files

A request that the bridge carries out reaches it as a file in the session's
directory, and the bridge answers with a result file. Each file is written
whole and renamed into place. We kept files instead of a socket or a server
because they need no port allocation, no MCP dependency, and no setup specific
to one agent harness.

## Consequences

A result stays on disk after its command ends, so an agent can see whether an
edit or a save happened after a timeout or a crash (R-RPC-6). A request that an
app instance left unanswered is still there when the next one starts, which is
how it gets an unknown outcome instead of running again (R-RPC-2).
