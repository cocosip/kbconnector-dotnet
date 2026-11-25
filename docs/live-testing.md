# Private live testing

The integration suite accepts process environment configuration only. Keep all
deployment addresses, ports, database/user names, passwords, service paths,
exact server build records and raw evidence outside the repository, including
ignored files. Do not put them in CI configuration or committed test fixtures.

Set `KINGBASE_TEST_LIVE=1` in the test process and provide these connection-string
variables from a private external configuration:

| Variable | Expected server compatibility mode |
| --- | --- |
| KINGBASE_TEST_POSTGRESQL | PostgreSql |
| KINGBASE_TEST_ORACLE | Oracle |
| KINGBASE_TEST_MYSQL | MySql |
| KINGBASE_TEST_SQLSERVER | SqlServer |

All four bootstrap cases use the PostgreSql wire protocol. A MySql compatibility
mode pass does not establish native MySQL protocol support. Set the transport
and TLS policy explicitly in each private connection string; there is no TLS
downgrade or endpoint fallback. Public execution cases currently require PG mode.

```powershell
# Load process environment from your private configuration first.
# Keep the output destination outside this repository.
dotnet test tests/KingbaseConnector.IntegrationTests -c Release
```

The suite uses Auto mode discovery and bounded Open/command/cleanup budgets.
With LIVE disabled it skips visibly. When enabled, missing/invalid required
configuration fails rather than skips. Select an individual test with the normal
`--filter` option when only its profile is available. A separate
`KINGBASE_TEST_WRITES=1` enables transaction tests that create a temporary table
on a dedicated unpooled connection. These tests commit/roll back only their own
temporary data; physical connection disposal removes the table. Ordinary tests
never enable either opt-in. Do not pass these variables to `eng/verify.ps1` or
configure them globally for routine CI.

LIVE failures report only the test/profile, exception type and SQLSTATE. They
omit raw messages, inner exceptions, assertion values and connection strings.
Do not attach private raw diagnostics to issues or commits. Record exact
profile/deployment identity and artifact evidence in the operator's private
record; the progress tracker contains only opaque profile IDs and sanitized
subcase results. This privacy rule takes precedence over publishing full profile
identity in the progress table.

Current cases cover four-mode bootstrap/catalog/scalar discovery and PG public
scalar parameters, typed NULL, server-error synchronization, internal named
statement execution/Close, public Prepare/Unprepare with value changes, shared
closure and logical reopen, same-physical-session pool setting restoration,
and local commit/rollback/savepoint requests at three isolation levels. The write
opt-in also enables prepared temporary-table schema invalidation (0A000), explicit
repreparation and failed-transaction Unprepare/rollback recovery. These cases
also exercise opt-in automatic preparation with server-side prepared-statement
counts, shared Unprepare, same-physical-session pool reset and fresh thresholds,
plus automatic schema-error and failed-transaction recovery under the write opt-in. First named-execution errors retain an attached handle; server counts confirm Unprepare releases it before a new threshold. They
do not prove concurrent isolation semantics, full dirty-session cleanup,
non-PG public APIs, native MySQL, TLS policy or release readiness.

PG DATE cases cover typed NULL, ISO display orders, actual date metadata and text
binding, finite CLR and larger years, BC leap days, calendar boundaries and both
infinities through KingbaseDate. They compare server-cast text with literal expected
values, exercise explicit preparation and DateOnly, reject unsupported display styles,
restore ISO and verify same-backend pool DateStyle reset. These cases alter only
session settings.

PG TIME/TIMETZ cases cover exact microseconds, typed NULL, 24:00:00, signed offsets
including seconds and +/-15:59:59, TimeSpan and modern TimeOnly input/access,
stable default/provider metadata and end-of-day TimeOnly overflow. Explicit and
automatic prepared execution compare server-cast text with independent literals
across accepted ISO/SQL/German DateStyles and UTC, Asia/Shanghai and America/New_York
session zones. The private profile rejects the Postgres display-style setting
with SQLSTATE 22023; it is excluded from these format scenarios. Separate server
casts reject +/-16-hour offsets and confirm synchronized error recovery. Invalid
client submicrosecond input rejects and subsequent execution succeeds. These cases
change only session settings. TIMESTAMP/TIMESTAMPTZ/INTERVAL, other modes' temporal
types, other builds/platforms and LIVE TLS remain pending.
