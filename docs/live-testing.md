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

The PG standard-schema cases read all five common collections through synchronous
and asynchronous APIs. An independent system-keyword query checks the exact
ReservedWords result; an actual regtype/OID query checks DataTypes identity.
Metadata retrieval inside a local transaction preserves its owner and allows
rollback. A separate pool case verifies the same backend is reused after close,
mutated result tables are not cached, and an active Reader excludes metadata
calls. No persistent objects/data are changed. Offline peers exercise invalid
names/restrictions, field structures, optional catalog/layout filtering, UInt32
OIDs, invariant culture, malformed/NULL/duplicate keywords, permission errors,
timeout/cancellation/close and the existing over-budget retirement behavior.

The PG object-schema cases verify Schemas/Tables/Views/Columns against actual
catalog results through synchronous/asynchronous APIs, including exact case,
empty filters and literal wildcard/quote inputs. Write-opt-in cases create only
session-local temporary tables/views with quoted Unicode names. They check
varchar byte/character lengths, numeric precision/scale, timestamp precision,
defaults/nullability, column order, fresh tables and transaction ownership.
A separate write-opt-in case uses transaction-local `pg_monitor` role selection
to confirm a test-owned temporary table and its columns are hidden when SELECT
is denied; SQLSTATE 42501 and rollback restore normal metadata access. This
profile-specific case requires the built-in role and authorized role selection;
it changes no persistent objects, grants, roles or server settings. Other
permission models and compatibility modes require separate acceptance.

Character octet length is compared with an independent parameterized catalog
query for the actual temporary column. UTF8 client encoding does not establish
the server encoding or a fixed bytes-per-character multiplier.

Relational schema cases opt into temporary writes. They compare composite
primary/unique keys and reordered FK pairs, duplicate FK names on distinct
tables, standalone referenced unique indexes, quoted Unicode/wildcard names,
partial/expression/INCLUDE/btree/hash indexes and independent catalog oracles.
Transaction ownership and local-role visibility/42501/rollback are checked
for all six collections. Two same-role connections independently check that
information_schema.tables and Tables hide the other session's temporary table;
all six relational collections must hide it while its owner retains visibility.
No persistent objects, grants or server settings are
changed; temporary objects disappear when each dedicated connection closes.

Sequence/generation cases also use temporary objects. They check signed bigint
bounds, negative increments, smallint/integer precision, cache/cycle options,
identity-owned sequence inventory, exact quoted/literal restrictions and
unchanged sequence last_value/is_called after metadata reads. Column cases
separate ordinary defaults and nextval from ALWAYS/BY DEFAULT identity and
generated expressions, compare an independent pg_attrdef expression oracle,
and verify transactional DDL rollback. Restricted-role and same-role
cross-session cases cover sequence privilege and temporary-schema visibility.

General table-constraint cases use temporary tables and transaction rollback.
They cover CHECK dependencies, constant CHECK with no column rows, NO INHERIT,
unvalidated constraints, composite PK/UNIQUE/FK, deferred FK and exclusion
expression slots with repeated ordinary columns. Independent catalog queries
check row counts and definitions; literal expectations check kinds and column
positions. Schemata/PrimaryKey aliases return canonical table shapes and rows.
Restricted-role, rollback-safe column-only grants and two-session temporary
visibility cases exercise the owning-table visibility contract. Definitions
are never executed, and no persistent grants or objects remain.

Database/user directory cases compare all returned fields with independent
public-catalog queries, including signed connection limits and separate
database availability/CONNECT flags. Exact current names, omitted/null/empty
and literal missing filters, fresh tables and discoverable canonical aliases
are checked through sync/async APIs. A transaction-local restricted role can
read the public directory while authentication-catalog access is denied;
rollback restores the original role and usable connection. No databases,
roles, grants or settings are created or changed persistently. Credentials,
deployment identities and raw catalog/assertion output remain external.

Routine metadata cases use session-owned temporary functions/procedures and
an aggregate, with quoted Unicode/literal names, overloads, unnamed/defaulted
arguments, IN/OUT/INOUT, VARIADIC arrays, TABLE outputs and zero arguments.
Independent pg_proc facts verify identities, input/default counts, return-set
flags and declared slot counts/order. Return modifiers unavailable from the
server remain null; procedure return/strictness facts are null. Aggregates
and the built-in window function are excluded. Sync/async restrictions,
specific identities, freshness and transactional rollback are checked.
A transaction-local restricted role sees EXECUTE-granted routines but cannot
see their owner-only default text; revoked procedures/arguments are hidden.
A second same-role session can see committed raw pg_proc rows from the first
session's temporary namespace while all three metadata collections exclude
them. Fixtures leave no persistent objects/grants/settings, do not invoke
routines, and raw LIVE output/configuration remain outside the repository.

UDT metadata cases create session-owned temporary enums, numeric/text domains
and standalone composites. Independent catalog queries check type/base/owner
identity, domain defaults and constraints; BEFORE/AFTER insertions preserve
numeric enum order, including empty and Unicode labels. Composite attributes
retain dropped-slot gaps and actual array/domain/nested member identities.
Domain nullability, numeric/text limits and collation are compared with server
facts. A transaction-local restricted role sees USAGE-granted types and domain
constraint flags, with owner-only definitions hidden; revoked types and their
attributes are excluded. A second session sees committed raw temporary type
rows while all four collections exclude the other session's temporary schema.
Temporary session cleanup or rollback removes all fixtures and local grants.
No value codecs, CLR mappings, anonymous record or other-mode acceptance is
established. Deployment configuration and raw results remain external.

Reader declared-facet cases use only constant expressions and session-owned
connections. Prepared and ordinary execution check VARCHAR/CHAR character
lengths, NUMERIC precision/scale up to precision 1000, unconstrained types,
Unicode aliases, typed NULL and empty results. Literal expected modifiers and
facets are checked against both BCL schema APIs without per-row catalog queries.
The currently supplied PG profile rejects negative scale and scale greater than
precision with 22023; these decoder boundaries have SIM coverage only. Base
names, keys and nullability require the separate KeyInfo contract below. The user temporarily deferred LIVE
TLS because the supplied endpoints do not support it; offline TLS remains required.

SchemaOnly cases create session-owned temporary tables/sequences and verify
that ordinary and explicitly prepared descriptions do not execute INSERT,
UPDATE, DELETE, TRUNCATE, CREATE TABLE, nextval or COMMIT. Independent count,
stored-value, sequence-is_called and to_regclass checks establish absence of
effects; rollback after a described COMMIT preserves the original value.
Prepared sequential batches expose all descriptions with NotExecuted statuses,
then execute normally when explicitly requested. Missing-column errors recover
on the same session. All fixtures disappear with session cleanup; other modes
and complete Reader metadata acceptance remain open.

KeyInfo acceptance covers direct table keys/nullability, identity/generated fields,
composite and nullable/deferred/INCLUDE/repeated-slot indexes, prepared commands,
SchemaOnly and batches. Owned serial defaults, removed defaults, noncanonical
currval and a same-named application nextval function/type verify conservative
identity handling. Restricted column grants use rollback. Inherited tables,
joins, views and computed-field SRFs (including ORDER BY aliases/arithmetic)
retain unknown result guarantees. Two sessions establish Repeatable Read or
Serializable snapshots before a column rename and verify that stale base names
are suppressed. These two-session and computed-function cases create unique
synthetic schemas and drop them in finally; remaining fixtures are temporary or
transactionally rolled back. Catalog drift between phases is also simulated in
protocol tests; no unconditional concurrent-DDL consistency claim is made.
Exact-package consumers exercise KeyInfo offline on all five assets; LIVE uses
source-built modern assets. Other modes/platforms and full M06/Q16 stay open.
