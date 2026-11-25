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
change only session settings. Other modes' temporal
types, other builds/platforms and LIVE TLS remain pending.

PG TIMESTAMP/TIMESTAMPTZ cases compare literal expected server text with prepared
calendar/instant binding, typed NULL, Kind-aware DateTime inference and explicit
DateTimeOffset normalization. They exercise microseconds, BC leap days and era
transitions, CLR boundaries, beyond-CLR years, exercised lower/upper calendar
values and explicit infinities. Results retain Unspecified/UTC Kind and stable
default/provider types; non-CLR conversion throws without losing provider access.
ISO numeric output is checked under UTC, Asia/Shanghai and America/New_York,
including spring/fall DST transitions and both occurrences of a repeated clock
time. Africa/Monrovia in 1970 supplies IANA's known -00:44:30 historical offset;
accepted fixed one-hour zones exercise display across the BC/AD boundary. The
profile rejected a custom second-bearing TimeZone setting (22023), so the
historical zone is used to validate actual second-offset output. SQL/German styles
reject timestamp conversion; ISO recovery and same-backend DateStyle/TimeZone
pool reset succeed. Only session settings change; these cases establish no
binary temporal, other-mode timestamp or additional-platform acceptance.

PG INTERVAL cases cover all four output grammars through explicit and automatic
preparation, typed NULL and exact independent EXTRACT expectations for calendar
months/days and clock hours/minutes/microseconds. The observed default/verbose
status labels are kingbase/kingbase_verbose; postgres/postgres_verbose SET aliases
select those labels. Cover mixed signs, ago inversion, SQL's uniform day/time
sign, one day versus 24 clock hours, signed month extremes, the positive day
extreme, near-minimum negative days and large signed clock hours with microseconds.
TimeSpan inference, explicit TIME selection, invalid extra ticks and continued
execution are checked. Pool reuse confirms the same backend and restored
IntervalStyle after changing display style, DateStyle and TimeZone.

Boundary failures are separate acceptance facts: the tested server rejects full
Int64 clock values in text input with 22015 and emits noncanonical negative-first
minimum-day verbose text. Conversion rejects safely; ISO output recovers the
minimum-day value and the connection remains usable. These cases do not establish
binary/full-range interval, server arithmetic beyond the exercised clock envelope,
Oracle YM/DS, other builds/platforms or LIVE TLS acceptance. All changes are scoped
to session settings; configuration, credentials and raw results stay external.

Sequential PG batch cases use a session-owned temporary table under the writes
opt-in. With both ordinary and explicitly prepared execution, confirm the first
INSERT is acknowledged, a division-by-zero statement fails with 22012 and the
third INSERT is not executed. Outside a transaction the first write persists;
inside an explicit transaction it is removed only by caller-initiated rollback.
The same session remains usable after synchronized failure and rollback.

Three further temporary-table cases confirm that an explicitly assigned
transaction cannot silently enter autocommit after raw ROLLBACK, that an
acknowledged UPDATE RETURNING an unsupported array retains its affected count,
and that an unrepresentable first scalar stops before a later INSERT. Independent
same-session queries verify no later write occurred and the acknowledged write
persists. These do not accept arbitrary array decoding or uncertain LIVE outcomes.

Separate read-only batch cases exercise explicit and automatic preparation,
fresh per-statement parameters, typed text NULL, lossless interval results and
ordered NextResult access across repeated execution. These establish bounded PG
sequential behavior, without accepting non-PG batching, pipelining, streaming,
concurrent isolation, uncertain transport outcomes or real TLS. Offline peers
cover cancellation, total timeout, preparation-only failures, acknowledgment
retention and aggregate buffer/count limits; exact-archive probes remain offline.

PG XML cases are read-only apart from session settings. Explicit and automatic
preparation use actual XML parameters, fresh document/fragment/empty content and
a 90,000-character Unicode payload. An explicitly prepared observer checks the
exact target statement in pg_prepared_statements after every execution. Check XML
metadata, pg_typeof, independent XMLSERIALIZE text, GetChars and the buffered
TextReader lifetime. A UTF-16 XML
declaration sent over confirmed UTF8 verifies server-side serialization preserves
Unicode without changing client encoding. Typed NULL and invalid XML cases run
with and without explicit preparation under document policy, reject both
malformed XML and a well-formed multi-root fragment, then verify same-session recovery. No
external entity payload is sent to a real server. Offline codecs preserve a
literal external-entity document without parsing it. These cases do not establish
binary XML, network streaming, other-mode XML, COPY/arrays or routine outputs.
