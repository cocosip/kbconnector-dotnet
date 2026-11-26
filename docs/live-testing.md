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
downgrade or endpoint fallback. Public execution cases include bounded four-mode PG-wire coverage; individual metadata cases retain their PG-only scope.

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


DataAdapter acceptance uses session-temporary PG tables for mapped DataSet Fill,
AddWithKey/FillSchema, paging, explicit INSERT/UPDATE/DELETE with Current/Original
and typed NULL parameters, and independent stored-value/count oracles. Stale
original values produce DBConcurrencyException and retain modified rows/errors;
a duplicate-key server error with ContinueUpdateOnError allows a later row after
synchronization. Typed row events skip or replace commands and report original
command identities/counts, including retained base event arguments. Original-NULL flags and explicit transaction rollback preserve pending DataRow changes when configured. Returned INSERT rows and accept-change policies are
checked independently. Read-only constant queries establish adapter-owned
connection opening/closing, including a query error. CommandBuilder and batching
have the separate acceptance scopes below; streaming and other public
compatibility modes remain open.


CommandBuilder acceptance covers generated quoted/aliased CRUD (including names
containing quotes, Unicode, backslashes, punctuation and case-distinct columns),
original NULL flags, composite keys, identity/generated exclusions and explicit
transaction rollback. Scalar type tests establish numeric/text/binary/UUID and
calendar/clock/interval/XML/JSON/JSONB bindings; TIMESTAMPTZ DataColumns use UTC
DateTimeMode, and JSONB expectations use canonical server text. XML/JSON are not
used in equality concurrency predicates. Further cases verify optimistic conflicts,
RefreshSchema after type/projection changes through BCL references, fallback
parameter aliases, attachment/disposal, unchanged caller-authored SQL after failed
generation, and rejection of ONLY/repeated/complex/no-key update projections.
Alternate index collations and nondefault opclasses cannot authorize equality-based
write keys. Unicode identifier generation also runs with standard_conforming_strings
off. All fixtures are temporary; the suite does not accept ONLY generation,
additional compatibility modes, platforms/builds, native MySQL or real TLS.

DataAdapter batching acceptance covers logical sizes 0/2/257, 300-row groups
crossing the 256-command physical boundary, byte/value snapshots, explicit and
generated mixed CRUD, original NULLs, explicit rollback and optimistic zero-row
conflicts. Duplicate errors at the first/middle/last position verify stop/continue
policies and that unsent writes are not retried. Connection/transaction/timeout
mismatches reject before writes. Row-event tests cover replacement, skipping,
reentrancy, missing/disposed commands and stopping with the next row's existing
error and parameter values intact. Unknown affected counts retain pending rows
and adapter-owned connection cleanup. Fixtures use temporary PG tables.

Timeout/disconnect/post-ack local failure, diagnostic-handler suppression or
replacement, and elapsed-deadline enforcement are separate loopback evidence.
Five exact-package consumers exercise batch snapshots and per-row outcomes
offline; LIVE uses source-built modern assets. These results do not establish
wire pipelining, automatic retries, atomic batches, other modes/platforms or
real-server TLS acceptance.

Four-mode public PG-wire execution acceptance uses Pooling=false and both
explicit mode selection and detected-mode connections. Scalar cases cover actual
catalog integer/text/NULL/binary/numeric bindings, explicit preparation with new
values, buffered Reader ownership, SchemaOnly and synchronized error recovery.
Mode-specific cases keep Oracle q strings, MySQL backtick/hash bodies and SQL
Server brackets out of parameter binding. Temporary-table batches independently
check stored updates and rollback; SQL Server additionally rolls back to a
savepoint through ROLLBACK TRANSACTION name and keeps its provider transaction.
Cached PG-generated writes reject after reopening the same connection in each
other mode, through direct, per-row and batched adapter paths, before changing
the target fixture.

Independent internal probes confirm all three non-PG string-escape settings and
the tested quote syntax. MySQL-mode division by zero returns NULL on this profile;
SQL Server count(*) reports Int32, and uncast string-literal output can have an
unsupported type in the E52 baseline. E54 now maps its proven native string type;
the lexer case retains its explicit pg_catalog.text cast. Exact-asset loopback
probes exercise explicit/automatic preparation in each non-PG mode. Non-PG schema/key metadata, full type
families, routines, deployment/platform parity and native MySQL remain open.

Ordinary PG-wire pooling now has a separate four-mode acceptance scope. Direct
Auto-mode pools and private explicit-mode DataSources run at max size 1 with
configured application name/search path/time zone, including quote/backslash and
Unicode startup values. They assert the initial configured values, then compare backend PID and baseline settings across
leases after changing common session/transaction defaults, creating a temporary
table, preparing a statement, LISTEN and an advisory lock, and leaving an explicit
transaction failed. The next borrower must see the same PID, restored settings,
no temp table/prepared/listening/lock state and invalidated old transaction/plan
ownership. Oracle/MySQL also change a supported sql_mode flag and verify restore.
Production verifies the captured optional sql_mode value, retaining NULL versus empty. Unknown modes, mode-case normalization, initial/report drift, optional-setting mismatches and idle validation are exercised separately
with loopback peers; each exact package asset runs all four mode pool probes with
bound startup restoration and changed catalog OIDs.

These tests use temporary or session-scoped state. The probed MySQL variable
assignment syntax rejects with 42601; no variable cleanup acceptance is inferred.
The profiles normalize non-ASCII application-name bytes to question marks; the exact-value fixture uses ASCII application names and a Unicode search path. Non-PG metadata/types/routines, package state, full resource/load/platform and
native MySQL acceptance remain open. Real TLS remains user-deferred.

Native scalar acceptance adds 23 source-built LIVE cases. All four modes exercise
signed128 high-bit/extreme BigInteger values and full-range unsigned logical
bindings, explicit preparation, typed NULL, overflow rejection and subsequent
reuse. Six native integer/BIT cases cover Oracle/MySQL signed tiny integers,
SQL Server unsigned tiny integers and Boolean BIT, and MySQL uint4/uint8,
including values above the signed range. Eight write-opt-in cases create only
temporary sys.varcharbyte/bpcharbyte columns in non-PG modes and SQL Server
nchar/nvarchar columns, comparing exact Unicode/quote/backslash content,
fixed padding, NULL and buffered character access. Independent probes establish
bpcharbyte byte padding and nchar Unicode-scalar padding. One SQL Server case
reads an ordinary uncast string literal. All private probe output stays external.

Exact-asset loopback probes separately cover the ten appended selectors, actual
native OIDs changing across pooled leases, stable CLR metadata, checked unsigned
reads from widened signed types and invalid selector/overflow rejection before
application frames. They do not establish exact-package LIVE. Optional-catalog
denial, untrusted aliases, mode drift, duplicate/contradictory OIDs, fatal errors
and cancellation have separate simulated evidence. Binary formats, native
MySQL wire, generic bit strings, complete type/profile/platform coverage and
non-PG schema/key/routine parity remain open.

PG bit-string acceptance adds eight source-built cases across all four modes.
Prepared bit/varbit parameters exercise leading zeros, empty and non-byte-aligned
lengths, high bits, typed NULL, provider/default/typed getters, client-bound
rejection and subsequent reuse. Four write-opt-in cases create only temporary
bit(5)/varbit(9) columns, compare independent expected values and declared
bit-count metadata, and require SQLSTATE 22026 for short/long fixed values and
22001 for an oversized varying value. The supplied Oracle profile normalizes
empty input to NULL; the other profiles preserve empty bit strings. Independent
read-only probes characterize that difference before changing test expectations.
MySQL sys.bit emits byte data rather than the PG 0/1 text representation and
remains unimplemented. SQL Server Boolean BIT stays separate. Exact-asset
loopback probes cover both appended selectors, Varbit inference, NULL, fresh
pooled OIDs and queued adapter BitArray mutation; they do not imply exact-package
LIVE or native MySQL bit-string acceptance.

Native temporal acceptance adds 22 source-built cases. Eleven prepared scalar
cases bind actual qualified Oracle/SQL Server/MySQL temporal and YEAR types,
compare exact default CLR values and typed NULL, including SQL Server seven-digit
fractions and MySQL negative durations beyond 24 hours. Three write-opt-in cases
create only temporary native columns with explicit precision, compare independent
expected values, verify SQL Server SMALLDATETIME rounds 30 seconds upward, and
preserve NULL. Four UTC/non-UTC/DST wall-clock cases confirm Unspecified values
are not shifted by client machine timezone or a fixed eight-hour rule; UTC Kind
input rejects before I/O and the next valid command succeeds.

MySQL boundary tests exercise ±838:59:59, signed microseconds, client overflow
rejection, and synchronized SQLSTATE 22008/23514 for server-rejected zero/invalid
dates and YEAR zero. Three year 10000 cases prove default DateTime overflow leaves
lossless provider reads available for DATE/DATETIME/TIMESTAMP. Zero-value provider
parsing and simulated rows are separate evidence; real-server zero acceptance
remains open. Independent read-only probes confirm native type layouts/I/O/trust,
Oracle DATE truncation and BC/infinity, SQL Server precision/ranges and DateStyle
output, and MySQL precision/zero/range behavior. Raw profiles and probe output stay
outside the repository. SQL Server invalid-literal Simple Query was observed to
emit Error/CommandComplete/Ready; the current strict parser retires that uncertain
path rather than assuming complete error synchronization acceptance.

Five exact-asset loopback consumers separately exercise native selectors,
provider/default CLR agreement, new provider inference/typed getters, NULL and
precision rejection, and fresh native plus domain-base OIDs across pool leases.
They do not establish exact-package LIVE, generic domains, DATETIMEOFFSET, binary
formats, routine outputs/COPY or full server/platform acceptance. Real TLS remains
user-deferred; offline TLS tests are retained.

Notification acceptance adds 20 cases across the four PG-wire modes. Two isolated
connections verify actual sender PID, Unicode/quote/backslash payload and timeout
followed by same-session query reuse. Transaction tests prove rollback does not
publish, commit delivers, and no callback is dispatched while no explicit read
operation runs. Handlers reject same-connection commands/Wait; waits inside a
transaction reject before I/O. Cancellation/zero-poll cases preserve the backend
PID and subsequently receive an empty NOTIFY payload through synchronous Wait.
Handler exceptions propagate after a complete message while a later command
still succeeds. Max-size-1 private pools compare backend PIDs, queue a notification
before closing the first borrower, verify no old handler/message/subscription
survives cleanup, then register a new subscription and receive only its message.
Raw sender/session identities and original results remain outside the repository.

Loopback regressions separately cover partial headers/bodies across timeout and
cancellation, late-frame retention, both directions of resumed byte-budget changes,
malformed/EOF/flood behavior, callback reentrancy and result/error precedence,
BEGIN cleanup success/failure, KeyInfo enrichment, acknowledged batch counts and
stale-generation exception isolation. Each exact package asset runs the same public
four-mode event/Wait/cancellation/error/pool-ownership probe. These are simulated
package-consumer checks, not exact-package LIVE or cross-platform certification.
There is no automatic replay/resubscription or background dispatch; ordinary query
cancellation and graceful Cancel-channel acceptance remain separate. Native MySQL
wire and real TLS are not accepted by these tests.

Notice acceptance adds 12 cases across the same four PG-wire modes. Isolated
anonymous blocks raise a fixed Unicode WARNING with independently expected
SQLSTATE/detail/hint. Prepared execution delivers exactly one notice per call;
handlers reject same-connection command/Wait reentry. A throwing handler leaves
the synchronized session usable. A following deliberate server error takes
precedence over that handler. The supplied SQL Server profile emits an extra
CommandComplete after Error in this case (independently captured extended-query
sequence 12nNECZ); tests explicitly require strict retirement and reopen, rather
than claiming synchronized SQLSTATE recovery for that tail. Other modes verify
the expected 22012 and same-session reuse. Two successive max-size-1 private-pool
borrowers verify same PID and delivery only to the current logical handler.
No permanent routines or objects are created. Raw profiles, frames and original
results remain outside the repository.

Loopback tests and every exact package asset separately exercise command and idle
notices, structured/immutable fields, safe default strings, handler failure/reentry,
first-failure ordering and startup/reset/borrower sink isolation. Notice alone does
not complete Wait successfully; its handler failure exits promptly after a complete
frame without retiring a healthy idle session. Additional source regressions cover
malformed fields/UTF8, identity drift, schema row-validation precedence, scalar
overflow consistency, BEGIN cleanup, confirmed rollback and batch acknowledgment.
Broader profile/platform, native MySQL wire, exact-package LIVE and real TLS remain
separate acceptance gaps.

COPY acceptance adds 28 cases across the four PG-wire modes. Text fixtures verify
empty export, UTF8, NULL, backslash/tab escapes, custom CSV delimiters, quotes and
embedded newlines. Binary fixtures preserve the independently observed KBCOPY
signature, empty export, actual server rows/NULL and same-profile raw import/export.
No typed binary row codec or cross-version restore is implied. Successful counts
require COPY completion plus ReadyForQuery. Temporary fixtures distinguish MySQL's
observed invalid-integer-to-zero conversion from the other modes' 22P02 rejection;
all four separately reject a violated CHECK with 23514 and reuse the same PID.

Text cancellation flushes a pending row, sends CopyFail, and confirms statement
atomicity both outside and inside an explicit transaction. An outer write made
before COPY disappears only after explicit Rollback, and the handle remains
associated until then; a rollback attempt during COPY rejects locally. Independent
raw probes show 57014 and Ready=E for outer-transaction cancellation in all modes.
An unfinished export destroys the first pooled physical session, and a subsequent
max-size-1 borrower has a different PID and executes normally. An approximately 3MiB import with
1500 session-local trigger notices runs with CommandTimeout=0 and confirms receive
progress during writes, exact row count and later query reuse. Fixtures and trigger
functions are confined to the session's temporary namespace. Profiles, original
results, signatures/catalog/frame probes and deployment identities stay outside
the repository.

Offline regressions separately verify COPY data beyond the ordinary 1MiB buffering
limit, fragmented/truncated data, invalid formats/counts/lengths, lexer endpoint
preflight, strict UTF8/BOM data, independent cleanup deadlines, callback precedence,
error-before-Z ordering, early server cancellation, known receive-fault propagation,
rejected concurrent completion/cancellation/disposal and stale-owner isolation.
A controlled duplex stream blocks each data write until its server Notice has
been consumed; disabling the receive path makes this test fail. Every exact package
asset runs the four-mode text/raw stream, completion, cancellation/callback and
same-socket two-borrower reset probe. These package checks use loopback servers,
while LIVE remains source-built on the three modern Windows runtimes. Typed binary
COPY, native MySQL wire, broader profiles/platforms, exact-package LIVE and real TLS
remain separate work; real TLS is user-deferred and offline TLS tests stay required.

OID large-object acceptance covers create/open/read/write/length/64-bit seek and
truncate/close/unlink in each PG-wire mode. Multi-MiB fixtures exercise bounded
byte-array chunks and cursor restoration; a seek beyond 2GiB does not write or
allocate a 2GiB object. Savepoint fixtures distinguish surviving parents, reverted
children, released nested scopes and immediate server descriptor reuse. Unlink
invalidates only matching OID handles; access restrictions and raw rollback
ownership are checked independently. Committed-content tests delete their object
in a finally block using a separate connection and independent cleanup deadline.
Other fixtures roll back their objects. Pool fixtures verify the same backend PID,
rollback of the previous borrower's object, and stale disposal while the new
borrower owns a reused descriptor.

Offline faults cover exact result metadata/counts, partial writes, interrupted
reads, operation/cleanup deadlines, callbacks and primary SQLSTATE precedence,
failed-transaction deferred close, public/raw savepoint state and descriptor reuse.
Each exact package asset runs a four-mode same-socket two-borrower probe with high
UInt32 OIDs, multi-chunk bytes, 64-bit positions and explicit cleanup. These are
simulated package-consumer checks; LIVE remains source-built. Memory bounds cover
the byte-array stream methods, not inherited BCL Span/Memory staging. Broader
privilege/profile/platform and exact-package LIVE acceptance remains pending;
B05 ordinary BLOB/CLOB/NCLOB binding and typed binary COPY remain separate work.

E61 ordinary LOB cases separately cover Oracle and MySQL PG-wire domains. Temporary
columns are created and populated inside an explicit transaction and rolled back.
Prepared byte-array/string inputs exercise binary zeros/backslashes/non-ASCII bytes,
Unicode supplementary characters, newlines and quotes; buffered GetBytes/GetChars
verify lengths, offsets and partial copies, and buffered Stream/TextReader views
verify full values. Qualified DataTypeName and typed NULL reuse the same prepared
statement. Empty BLOB remains non-NULL; empty CLOB/NCLOB becomes NULL on Oracle and
remains empty on MySQL. Separate PG/SQL Server cases verify local rejection of
unavailable LOB selectors without bytea/OID fallback or loss of ordinary execution.

Independent external characterization establishes actual domain bases and trusted
I/O shapes, legacy blobout escaping and mode-specific literal/bound empty behavior.
Offline regressions reject unproven domain identities, preserve same-name namespace
isolation and bound repeated partial-read allocations with per-row/column private
decoded buffers. Exact-asset native probes exercise typed bytes/Unicode/NULL and
legacy BLOB output across physical catalog reloads. These are bounded ordinary
values at E61: subsequent E62/E64 add network SequentialAccess and Stream/TextReader input; full LOB bounds,
native MySQL, broader profiles/platforms and exact-package LIVE remain pending.


E62 public streaming cases use SequentialAccess through the public command/Reader,
not reflected internal protocol access. Four-mode rollback-isolated temporary
columns grow beyond buffered limits; Oracle/MySQL use actual CLOB/BLOB/NCLOB,
and the other modes use PG text/bytea. Readers verify bounded character/binary
chunks, Unicode/NULL, forward partial offsets, explicit scalar materialization,
unnamed/automatic/explicit preparation and completion. Separate cases verify
normal early-close batch effects inside an explicit rolled-back transaction,
same-backend pool reuse with stale child disposal, and canceled active leases
being retired before the next borrower. Raw source-built LIVE records stay in
the authorized external evidence directory.

The exact-asset pool consumer probe additionally verifies streaming metadata
before row bodies, large text/bytea fields, explicit/automatic preparation,
partial early close and fresh OIDs over two borrowers in all four simulated
modes. Simulated package consumers remain separate from source-built LIVE;
these checks do not establish exact-package LIVE or other platform acceptance.
E64 adds Stream/TextReader parameter input; strong SequentialAccess+KeyInfo remains
open; real-server TLS is still user-deferred, with offline TLS tests retained.

Text COPY lifetime regressions exercise buffered import and export after Close,
Begin-token cancellation and the total command deadline in all four PG-wire
modes. They use dedicated connections and only a temporary import table. Cached
characters and unflushed writes must reject with cancellation/timeout rather
than surviving the revoked operation. Loopback tests separately cover normal
acknowledged EOF, asynchronous overloads and repeated disposal; exact-package
probes exercise cached export rejection after Close.

E64 source-built LIVE adds twenty cases: twelve four-mode temporary-table
round trips for unnamed, explicit and automatic preparation; four prepared
Oracle/MySQL BLOB/CLOB/NCLOB domain cases using provider or qualified-name
selectors; and four-mode local NUL rejection followed by successful session
reuse. Nonseekable binary and Unicode inputs exceed the buffered exchange
limit, with SequentialAccess verifying the result. Prepare/SchemaOnly leave
input read counts at zero; source streams/readers remain caller-owned.
Loopback cases additionally cover ignored read cancellation, deadlines, Close,
split surrogates, Size, frame boundaries, spool disposal, queued-adapter
rejection, unsent batches and execution-write failure accounting. Exact-asset
native scalar probes add ordinary LOB streaming values and large base-type
inputs across two pooled borrowers in all four simulated modes. Real TLS,
exact-package LIVE and broader platform/profile acceptance remain separate.

E65 adds twenty source-built typed binary COPY cases across the four PG-wire
modes: core twelve-type import/export with independent ordinary text Reader
verification; server-generated binary exports independent of the importer;
incomplete import cancellation with and without an enclosing transaction; and
copies exceeding 1MiB cumulatively under two same-PID pooled borrowers, including
stale exporter disposal during the next import. NULL, empty values, integer
extrema, IEEE values, Unicode, bytea and UUID use explicit base column selectors.
Four additional WHERE-filtered imports, for twenty-four total, verify that fewer inserted rows are
reported successfully and ordinary Reader access reuses the connection afterward.
Fixture discovery found unavailable decode overloads in Oracle/MySQL, an
unsupported numeric-to-oid cast in SQL Server and different MySQL bpchar padding.
The corrected fixtures use NULL bytea (nonempty/empty remain independently
covered), text-to-oid and mode-specific server padding; these are SQL fixture
assumptions, not accepted driver defects. Preserve earlier failed run records
in the private evidence directory alongside the final passing runs.

Loopback tests independently validate literal network bytes, fragmented rows,
wrong catalog layouts, flags/extensions/lengths/trailers/counts, invalid UTF8 and
boolean values, exact row and column bounds, pre-dispatch rejection, concurrent
operations, callbacks and close/cancel/deadline revocation. Five exact-package
consumers add typed int4/text import/export with NULL/empty values over two
borrowers in all four simulated modes. Numeric/temporal/complex codecs, native
MySQL wire, exact-package LIVE, other profiles/platforms and real TLS remain
outside this increment's evidence. Offline TLS tests remain required.

E66 adds seven source-built scenarios in all four PG-wire modes (28 cases):
server-generated numeric/temporal COPY without using the importer; independent
server send-function hex literals including date/timestamp extrema; exact
numeric coefficient/scale limits; imported values checked through ordinary
provider text Readers and binary export; server-rejected numeric infinities
with continued connection use; binary intervals beyond the server text
formatter; and transactions/two same-PID pooled borrowers with stale disposal
and unsent precision errors. Actual integer_datetimes=on is required before
clock/timestamp/interval COPY. Numeric/DATE alone need no such status.

The tested four-mode servers reject numeric infinities: text casts report
22P02 and binary COPY reports22P03. SQL Server additionally rejects text NaN
and displays a binary-imported NaN as text zero; binary export retains NaN.
An interval with microseconds=Int64.MinValue+1 imports/exports exactly in
binary, but server text formatting reports22008. MySQL mode can omit the
offset from BC timestamptz text. Independent provider Reader verification
projects timestamptz AT TIME ZONE 'UTC' and compares its exact calendar;
the client never infers a missing offset. Initial failed fixtures and final
passing evidence stay together in the external private directory. These
observed server boundaries do not establish broader profile acceptance.

Loopback cases add literal numeric/temporal bytes, independent CLR calendar
comparisons, BC/epoch/range/precision/infinity boundaries, numeric resource
bounds/redundant zeros/malformed payloads and catalog/status preflight. Check
numeric Coefficient and Scale separately: numeric equality intentionally
compares equivalent values across scales. Five exact archive consumers add
seven-type rows, NULL, fragmented messages and two pooled borrowers in all
four simulated modes, including the Standard assets. Other base/native/domain/
complex codecs, field streaming, exact-package LIVE, native MySQL wire and
broader platforms/profiles remain open. Real TLS remains user-deferred;
offline TLS tests remain required.

## JSON and PG bit binary COPY (E67)

E67 adds five source-built scenarios in all four PG-wire modes (20 cases):

- Independent json_send/jsonb_send/bit_send/varbit_send hexadecimal literals and server-generated binary export establish JSONB version 1, network bit counts and MSB-first bit order.
- Temporary-table import, ordinary text Reader and binary export independently verify exact JSON versus server-normalized JSONB, duplicate keys, Unicode, fixed/varying/empty bits, leading zeros and SQL NULL.
- Invalid JSON/JSONB syntax is sent to the server, rejects with 22P02 and permits a subsequent command after terminal synchronization. The client owns UTF8/NUL/version validation and does not parse JSON syntax.
- A 1048576-bit varbit value imports and exports exactly, including first/last bits; an independent bit_length query confirms declared length.
- Explicit transaction rollback and two same-PID pooled borrowers verify ownership/reset and harmless stale importer disposal. SQL Server COUNT returns Int32 on the tested profile; other modes return Int64. The test compares the lossless numeric count without imposing a cross-mode CLR type.

Offline public/codec tests additionally reject unsupported versions, malformed Unicode/NUL, negative/extreme bit counts, wrong lengths, nonzero unused padding and valid oversized output before provider allocation. Invalid local rows stay unsent; output failures after a prior valid row retire the physical connection. Exact archive probes include all four new types, NULL, fragmented rows and two borrowers in four simulated modes. Preserve the existing 1MiB encoded row budget and separate 1048576-bit value bound. This accepts PG catalog base bit/varbit, not native MySQL BIT or SQL Server Boolean BIT. XML binary encoding declarations, other codecs and broader profile/platform acceptance remain open. Raw LIVE records and deployment/credential data stay outside Git; real-server TLS remains deferred and offline TLS active.
