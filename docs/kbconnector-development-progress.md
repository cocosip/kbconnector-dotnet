# KingbaseConnector Development Progress

Last updated: 2026-11-24. This is the single current implementation and acceptance tracker. Requirements are defined in the [feature matrix](kbconnector-feature-matrix.md); implementation workflow and completion rules are in the [development guide](kbconnector-development.md).

## 1. Current snapshot

DP01 engineering is implemented. DP03 implements the first configuration slice, offline server-requested PG password/MD5 authentication and an internal bounded startup coordinator; DP04 adds bounded PG frame I/O, selected frontend encoders and strict backend control/error parsers with deterministic stream tests. Explicit package consumers run on Windows x64 .NET 8/9/10. No network connection or live profile is accepted yet; query execution, complete login, ownership/synchronization and server discovery remain open. The user requires the separate KingbaseConnector.DependencyInjection deliverable; registration contracts are defined, pending usable Connection/DataSource ownership.

| Product feature status | Count |
| --- | ---: |
| Complete | 1 |
| Implemented, acceptance pending | 0 |
| In progress | 12 |
| Blocked | 0 |
| Not started | 98 |
| Deferred: later L scope and subsequent V8 work | 11 |
| Not applicable | 0 |
| Total matrix features | 122 |

Scope inventory: 85 V9 required (R), 26 V9 conditional (C), 7 later-scope (L), and 4 subsequent V8 features. Conditional features start as Not started; their applicability has not been decided. Missing environments are tracked below and do not turn them into Not applicable.

Documentation deliverables are tracked separately from product functionality:

- [x] Maintain the unified design and feature matrix in English, with official sources and explicit evidence limits.
- [x] Provide an implementation guide with dependencies, validation workflow, and completion rules.
- [x] Register all 122 feature IDs in the progress ledger with an initial truthful status.
- [x] Extend the default-PG design for exceptional explicit MySQL-only connections and built-in pooling; synchronize requirements and work packages. This is documentation completion only.
- [x] Define startup decision gates and the first usable PG increment in development guide section 3.1. Toolchain/API decisions and live discovery remain work-package tasks; no product acceptance is implied.

## 2. Phase progress

| Phase | Status | Exit requirement | Evidence |
| --- | --- | --- | --- |
| V9-0 | Not started: runtime discovery | Register actual target profiles; establish version/mode, directory, and routine protocol facts. Static reference evidence is already available. | No live profiles registered. |
| V9-1 | In progress | Engineering and the minimum connection/query/type/transaction/pool/metadata paths, including relevant failure behavior. | DP01 engineering and DP03/DP04 offline subsets; E01-E05. |
| V9-2 | Not started | Complete required functional coverage including the exceptional explicit MySQL backend; resolve and validate applicable conditional features. | None yet. |
| V9-3 | Not started | Version/deployment and exceptional MySQL-only acceptance, both-protocol pooling/faults/resources, package consumers, and the V9 release gate. | None yet. |
| V8-1 | Deferred until the V9 gate | Establish V8R6C9B14 profiles and implement verified differences in the shared core. | Static D8-91 evidence only. |
| V8-2 | Deferred until V8-1 | Cover other agreed V8 targets and regress accepted V9 behavior. | None yet. |
| EF follow-up | Deferred | A separate provider design, feature matrix, and version-specific implementation. | No EF product projects. |

Do not check a phase complete solely because its work-package list is checked. Apply the design's phase and release gates to the actual feature/profile records.

## 3. Work packages

Work-package checkboxes can be marked complete when the named subset and its exit condition are satisfied. A package that implements only part of a feature must identify that subset in its evidence entry; the full feature remains open in section 4 until all requirements pass. Dependencies below define development order, not additional scope or already implemented behavior.

- [x] **DP01 - Engineering and package structure** (V9-1; G01, G02). SDK/solution/shared props/CPM/core and test projects build all five TFMs; offline checks, packaging and explicit asset consumers execute. Optional DI remains with the later DataSource lifetime contract; G02/G03/G08 are partial. Evidence: E01.
- [ ] **DP02 - Server profiles and bootstrap discovery** (V9-0; C03-C05, G08). Register authorized profiles and confirm raw/product version, mode, catalog, and routine output facts. Establish isolated official comparison entry points. Can proceed alongside DP01 when environments exist. Evidence: none.
- [ ] **DP03 - Connection, configuration, and authentication foundation** (V9-1; C01-C02, C06-C10, C16, C18-C20, G06, G11). In progress: first local configuration slice (E02), offline PG password/MD5 exchange (E04) and internal startup coordinator (E05); networking, credential sources, full session configuration, remaining algorithms and complete login remain open. Use DP02 evidence for identity-dependent behavior.
- [ ] **DP04 - Protocol state and failure handling** (V9-1; Q01, Q05, Q17-Q18, P06, G07). In progress: independent bounded framing/frontend/control-error-parser subset (E03). Session/query ownership, cancellation-channel closure, synchronization and uncertain outcomes remain pending transport/authentication/execution.
- [ ] **DP05 - Type system and basic codecs** (V9-1; T01-T03, T05, T07-T10, T28, T30). After bootstrap/catalog contracts, implement type lookup and the basic input/result paths, exact conversion, typed getters, NULL handling, and bounds. Later mode-specific coverage for these features remains part of DP08. Evidence: none.
- [ ] **DP06 - Commands, parameters, and Reader foundation** (V9-1/2; Q02-Q04, Q06, Q15-Q16, M01, M06). After DP04/DP05, deliver named/positional parameter planning, streaming/multiple results, Reader behavior, and foundational metadata. Evidence: none.
- [ ] **DP07 - Pooling and session cleanup** (V9-1/2/3; P01-P03). After DP04/DP06, implement the built-in shared pool manager: private/direct pool identity, bounded leases/waiters, PG reset, generations, clear/expiry/dispose, credential rotation and unused-registry reclamation, transaction-reservation ownership, and resource/cancellation races. Complete transaction-affine reacquisition with DP10; MySQL reset and cross-backend acceptance remain DP22; P01-P03 stay open until applicable coverage passes. Evidence: none.
- [ ] **DP08 - Complete type and mapping coverage** (V9-2; T01-T32). Extend DP05 with remaining mode-specific numeric/time types, provider values, complex types, explicit mapping, unknown types/reload, and applicable array/COPY/output paths. Require independent input/result/output evidence where applicable. Evidence: none.
- [ ] **DP09 - Routines, output parameters, and cursors** (V9-0/2; Q10-Q14, Q19, M05). Use DP02/DP05/DP06 and routine metadata to resolve overloads, generate calls, publish outputs atomically, own cursor resources, and derive parameters. Evidence: none.
- [ ] **DP10 - Transactions** (V9-1/2/3; X01-X05). Build local state and savepoints on DP04/DP06; then evaluate explicit/automatic enlistment, same-transaction pending-session reuse with DP07 (including sequential opens at pool size 1), completion races, promotion, backend-specific 2PC, and recovery with their own environment evidence. Evidence: none.
- [ ] **DP11 - Prepared statements and batch APIs** (V9-2; Q07-Q09). After parameter/execution and pool contracts, implement statement identity, physical-session ownership, cache limits/eviction, invalidation, and sequential batch partial-success reporting. Evidence: none.
- [ ] **DP12 - COPY, LOB, and notification APIs** (V9-2/3; B01-B06). Use established codecs, transactions, and session ownership; implement completion/cancellation/cleanup and Wait/event semantics for applicable profiles. Evidence: none.
- [ ] **DP13 - Schema and EF foundation** (V9-2/3; M01-M10). Complete catalog collections, restrictions, keys/constraints/indexes, generated/default metadata, routines/UDTs, Reader metadata, DataAdapter/CommandBuilder, and stable result/error contracts. Depends on the relevant DP05/DP06/DP09/DP10 paths. Evidence: none.
- [ ] **DP14 - Failover and liveness** (V9-3; P04-P05, C15). After initialization and pooling are reliable, validate new-connection endpoint selection, role facts, heterogeneous nodes, and separate SQL/TCP keepalive behavior. Evidence: none.
- [ ] **DP15 - Conditional authentication and transports** (V9-3; C11-C14, C17). Evaluate and implement applicable national-cryptography algorithms/TLS, integrated/client-certificate authentication, Unix sockets, and password-file behavior. Record each algorithm/platform decision separately. Evidence: none.
- [ ] **DP16 - Platform/package acceptance and V9 release** (V9-3; G01-G09, G11, all applicable R/C features). Run actual asset consumers, diagnostics/redaction, fault/resource coverage, and the target version/deployment/mode tests. Require DP20-DP22 native-endpoint and both-protocol pool results; check the design section 11.2 gate and resolve all conditional decisions. Evidence: none.
- [ ] **DP17 - V8R6C9B14 extension** (V8-1; V01-V02). After the V9 gate, register actual V8 modes and validate the same functional paths using only verified differences. Evidence: none.
- [ ] **DP18 - Additional V8 and V9 regression** (V8-2; V03-V04). After DP17, add agreed V8 builds and rerun the accepted V9 profiles. Evidence: none.
- [ ] **DP19 - Explicit follow-up proposals** (later scope; B07-B08, P07, G10, E01-E03). CDC, COPY convenience APIs, query multiplexing, trimming/AOT, and independent EF providers remain separate proposals. This package is outside the initial V9 gate. Evidence: none.

- [ ] **DP20 - Exceptional MySQL transport and session backend** (V9-2; C20-C21, C03-C07, C16, C18-C19, Q20 bootstrap subset). After DP01/DP03 foundations, implement explicit protocol/port validation, packets/capabilities, TLS/plugin authentication, and bounded COM_QUERY/text-result decoding for native Kingbase bootstrap using DP02/U07 evidence. This subset does not require DP21's complete typed command/result implementation. PG remains the normal default; do not copy its handshake messages. Evidence: none.
- [ ] **DP21 - MySQL execution, types, and ADO.NET surfaces** (V9-2; Q20, T33, Q02-Q11, Q15-Q19, X01-X03, M01-M10 and applicable T features). After DP20 and shared command/type contracts, implement prepared binding with session-aware lexical plans, text/binary/streaming results and applicable ENUM/SET, transaction/status/error semantics, native catalogs/schema/routines, and capability rejections for PG-only APIs. Verify supported subsets rather than assuming PG feature parity. Evidence: none.
- [ ] **DP22 - Both-protocol pool and exceptional endpoint acceptance** (V9-3; P01-P03, P06, C16, Q05, Q18, X04 where applicable, G06-G08; A16-A17). After DP07/DP20/DP21, validate per-backend reset or retirement, bounded cancellation, two-borrower state isolation, stress/clear/disposal/credential and registry-reclamation races, same-transaction reacquisition/completion on X04 profiles, and a MySQL-only endpoint with PG unavailable. Resolve U07 facts; retain profile-specific applicability and physical-reuse evidence. Evidence: none.

Work-package IDs are stable, not execution order. There are 22 packages; DP20-DP22 are dependencies of the V9 release package DP16. Shared features retain one canonical ledger row even when multiple packages implement different subsets.

## 4. Feature ledger

Each matrix feature has exactly one row below. The matrix owns names, scope, and acceptance requirements; keep IDs synchronized when it changes. `[ ]` is open and `[x]` means **Complete** under development guide section 6. Use Not started, In progress, Implemented, Blocked, Complete, Deferred, or Not applicable for Status. Record an accepted profile/subset and evidence before checking a row.

SIM and LIVE are independent: Not run, Passed, Failed, or N/A with an explicit reason. A LIVE pass must name profiles in the evidence entry. N/A is not available merely because a server or test is missing. Deferred rows remain unchecked.

### Connections, identification, authentication, and transport

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [ ] | C01 | ConnectionStringBuilder / factory | In progress | Passed: E02 subset | Not run | E02: builder only; factory pending |
| [ ] | C02 | TCP, DNS, synchronous/asynchronous Open | Not started | Not run | Not run | - |
| [ ] | C03 | Full product version detection | Not started | Not run | Not run | - |
| [ ] | C04 | Automatic detection / explicit CompatibilityMode | Not started | Not run | Not run | - |
| [ ] | C05 | Full-mode / standalone edition compatibility | Not started | Not run | Not run | - |
| [ ] | C06 | Session encoding / parameter status | In progress | Passed: E03/E05 subsets | Not run | Strict UTF8 parsing/startup confirmation; lexical/codec updates pending |
| [ ] | C07 | TLS, CA, hostname validation | Not started | Not run | Not run | - |
| [ ] | C08 | Password / MD5 authentication | In progress | Passed: E04/E05 subsets | Not run | Offline exchange/startup coordinator; network/complete login pending |
| [ ] | C09 | SCRAM-SHA-256 | Not started | Not run | Not run | - |
| [ ] | C10 | Separate SM3 authentication | Not started | Not run | Not run | - |
| [ ] | C11 | SCRAM-SM3 / SM4 | Not started | Not run | Not run | - |
| [ ] | C12 | Chinese cryptographic-standard TLS | Not started | Not run | Not run | - |
| [ ] | C13 | GSS/SSPI, client certificates | Not started | Not run | Not run | - |
| [ ] | C14 | Unix domain socket | Not started | Not run | Not run | - |
| [ ] | C15 | Keepalive / broken-connection detection | Not started | Not run | Not run | - |
| [ ] | C16 | Credential sources and rotation | Not started | Not run | Not run | - |
| [ ] | C17 | Passfile / environment variables | Not started | Not run | Not run | - |
| [ ] | C18 | Startup / session configuration | In progress | Passed: E05 subset | Not run | Bounded internal user/database/UTF8 startup only; other settings/reset pending |
| [ ] | C19 | Connection state / ChangeDatabase | Not started | Not run | Not run | - |
| [ ] | C20 | Wire protocol selection / endpoint contract | In progress | Passed: E02 subset | Not run | E02: local validation only; backend/handshake/pools pending |
| [ ] | C21 | MySQL handshake / TLS / authentication | Not started | Not run | Not run | - |

### Commands, parameters, Reader, and routines

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [ ] | Q01 | Simple / Extended Query | In progress | Passed: E03 subset | Not run | E03: framing/Simple Query encoder only; execution/Extended Query pending |
| [ ] | Q02 | Nonquery / scalar / reader | Not started | Not run | Not run | - |
| [ ] | Q03 | Named and positional parameters | Not started | Not run | Not run | - |
| [ ] | Q04 | Parameter types, precision/scale, typed NULL | Not started | Not run | Not run | - |
| [ ] | Q05 | Synchronous/asynchronous execution, cancellation, and timeouts | Not started | Not run | Not run | - |
| [ ] | Q06 | Multiple results and statements | Not started | Not run | Not run | - |
| [ ] | Q07 | Explicit Prepare / Unprepare | Not started | Not run | Not run | - |
| [ ] | Q08 | Automatic preparation | Not started | Not run | Not run | - |
| [ ] | Q09 | Sequential batch / DbBatch | Not started | Not run | Not run | - |
| [ ] | Q10 | Function/procedure resolution and overloads | Not started | Not run | Not run | - |
| [ ] | Q11 | Input/Output/InputOutput/ReturnValue | Not started | Not run | Not run | - |
| [ ] | Q12 | Oracle package routines | Not started | Not run | Not run | - |
| [ ] | Q13 | refcursor / multiple cursors | Not started | Not run | Not run | - |
| [ ] | Q14 | SQL Server routine outputs / return codes | Not started | Not run | Not run | - |
| [ ] | Q15 | SequentialAccess / Stream / TextReader | Not started | Not run | Not run | - |
| [ ] | Q16 | CommandBehavior / reader contract | Not started | Not run | Not run | - |
| [ ] | Q17 | SQLSTATE / Notice / deterministic exceptions | In progress | Passed: E03 subset | Not run | E03: internal SQLSTATE/error fields only; public exceptions/notices/outcomes pending |
| [ ] | Q18 | Synchronization after Cancel / damaged connection handling | Not started | Not run | Not run | - |
| [ ] | Q19 | DeriveParameters | Not started | Not run | Not run | - |
| [ ] | Q20 | MySQL commands / prepared execution / results | Not started | Not run | Not run | - |

### Transactions

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [ ] | X01 | Local Begin/Commit/Rollback | Not started | Not run | Not run | - |
| [ ] | X02 | Isolation levels and read-only semantics | Not started | Not run | Not run | - |
| [ ] | X03 | Savepoint / RollbackTo / Release | Not started | Not run | Not run | - |
| [ ] | X04 | Single-resource TransactionScope | Not started | Not run | Not run | - |
| [ ] | X05 | Distributed / two-phase transactions | Not started | Not run | Not run | - |

### Types and mapping

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [ ] | T01 | Type catalog / OID resolution | Not started | Not run | Not run | - |
| [ ] | T02 | bool / BIT / bit strings | Not started | Not run | Not run | - |
| [ ] | T03 | Signed integers | Not started | Not run | Not run | - |
| [ ] | T04 | Unsigned integers | Not started | Not run | Not run | - |
| [ ] | T05 | NUMERIC/NUMBER/DECIMAL | Not started | Not run | Not run | - |
| [ ] | T06 | SQL Server NUMERIC/MONEY | Not started | Not run | Not run | - |
| [ ] | T07 | float/double / special values | Not started | Not run | Not run | - |
| [ ] | T08 | char/varchar/text/nchar/nvarchar | Not started | Not run | Not run | - |
| [ ] | T09 | bytea/binary/varbinary/RAW | Not started | Not run | Not run | - |
| [ ] | T10 | PG DATE/TIME/TIMESTAMP/TZ | Not started | Not run | Not run | - |
| [ ] | T11 | Oracle DATE | Not started | Not run | Not run | - |
| [ ] | T12 | SQL Server DATETIME/DATETIME2 | Not started | Not run | Not run | - |
| [ ] | T13 | MySQL DATE/DATETIME/TIMESTAMP | Not started | Not run | Not run | - |
| [ ] | T14 | MySQL TIME | Not started | Not run | Not run | - |
| [ ] | T15 | INTERVAL / YM / DS | Not started | Not run | Not run | - |
| [ ] | T16 | YEAR | Not started | Not run | Not run | - |
| [ ] | T17 | int16 / wide integers | Not started | Not run | Not run | - |
| [ ] | T18 | UUID | Not started | Not run | Not run | - |
| [ ] | T19 | JSON / JSONB | Not started | Not run | Not run | - |
| [ ] | T20 | XML | Not started | Not run | Not run | - |
| [ ] | T21 | Arrays | Not started | Not run | Not run | - |
| [ ] | T22 | range | Not started | Not run | Not run | - |
| [ ] | T23 | enum / domain / MySQL SET | Not started | Not run | Not run | - |
| [ ] | T24 | composite / record | Not started | Not run | Not run | - |
| [ ] | T25 | Network addresses | Not started | Not run | Not run | - |
| [ ] | T26 | Geometry / full-text search / hstore and others | Not started | Not run | Not run | - |
| [ ] | T27 | Unknown types and catalog reload | Not started | Not run | Not run | - |
| [ ] | T28 | GetFieldValue / typed getters | Not started | Not run | Not run | - |
| [ ] | T29 | PG MONEY | Not started | Not run | Not run | - |
| [ ] | T30 | Catalog / identifier types | Not started | Not run | Not run | - |
| [ ] | T31 | SQL Server SMALLDATETIME/DATETIMEOFFSET/TIME | Not started | Not run | Not run | - |
| [ ] | T32 | Custom mappings / name translation | Not started | Not run | Not run | - |
| [ ] | T33 | MySQL wire types / Kingbase catalog identity | Not started | Not run | Not run | - |

### COPY, large objects, and notifications

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [ ] | B01 | Text COPY import/export | Not started | Not run | Not run | - |
| [ ] | B02 | Binary COPY import/export | Not started | Not run | Not run | - |
| [ ] | B03 | Raw COPY stream | Not started | Not run | Not run | - |
| [ ] | B04 | LargeObjectManager / OID LOB | Not started | Not run | Not run | - |
| [ ] | B05 | BLOB/CLOB/NCLOB binding | Not started | Not run | Not run | - |
| [ ] | B06 | LISTEN/NOTIFY / Wait | Not started | Not run | Not run | - |
| [ ] | B07 | Logical replication / CDC API | Deferred | Not run | Not run | - |
| [ ] | B08 | DataTable/DataSet COPY convenience API | Deferred | Not run | Not run | - |

### Pooling, high availability, and resources

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [ ] | P01 | Pool identity / capacity / waiting | Not started | Not run | Not run | - |
| [ ] | P02 | Session reset and dirty-connection disposal | Not started | Not run | Not run | - |
| [ ] | P03 | Lifetime / pool clearing / generations | Not started | Not run | Not run | - |
| [ ] | P04 | Multiple hosts / failover | Not started | Not run | Not run | - |
| [ ] | P05 | Primary/read-only targets and selection policy | Not started | Not run | Not run | - |
| [ ] | P06 | One active operation per connection | Not started | Not run | Not run | - |
| [ ] | P07 | Modern query multiplexing | Deferred | Not run | Not run | - |

### Schema and EF foundation contracts

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [ ] | M01 | Standard GetSchema collections | Not started | Not run | Not run | - |
| [ ] | M02 | Tables/Views/Schemas/Columns | Not started | Not run | Not run | - |
| [ ] | M03 | PK/FK/Indexes/IndexColumns | Not started | Not run | Not run | - |
| [ ] | M04 | Sequences/identity/generated | Not started | Not run | Not run | - |
| [ ] | M05 | Procedures/Functions/Parameters/UDT | Not started | Not run | Not run | - |
| [ ] | M06 | GetColumnSchema/GetSchemaTable | Not started | Not run | Not run | - |
| [ ] | M07 | Restrictions / quoting / permissions | Not started | Not run | Not run | - |
| [ ] | M08 | DataAdapter/CommandBuilder | Not started | Not run | Not run | - |
| [ ] | M09 | Result and error contracts required by EF | Not started | Not run | Not run | - |
| [ ] | M10 | Databases/Users/Constraints and collection aliases | Not started | Not run | Not run | - |

### EF follow-up

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [ ] | E01 | Separate EF Core 8/9/10 providers | Deferred | Not run | Not run | - |
| [ ] | E02 | Offline dialect profiles / model caching | Deferred | Not run | Not run | - |
| [ ] | E03 | LINQ/updates/migrations/reverse engineering | Deferred | Not run | Not run | - |

### Engineering, platforms, diagnostics, and release

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [x] | G01 | slnx / CPM / unified versions | Complete | N/A: engineering | N/A: engineering | E01 |
| [ ] | G02 | Single core package / optional DI package | In progress | Not run | Not run | E01: core structure; user-required separate DI delivery pending Connection/DataSource |
| [ ] | G03 | Package consumption across TFMs | In progress | Passed: E02 subset | Not run | E01/E02: explicit loading/configuration only |
| [ ] | G04 | Windows/Linux x64 | Not started | Not run | Not run | - |
| [ ] | G05 | ARM64 / Framework hosts | Not started | Not run | Not run | - |
| [ ] | G06 | Logging / Activity / Meter | Not started | Not run | Not run | - |
| [ ] | G07 | Security / failure / resource stress | In progress | Passed: E03 subset | Not run | E03: bounded frame/fault/stream retirement cases only |
| [ ] | G08 | Differential / live test framework | In progress | Not run | Not run | E01: offline/live separation and fail-closed opt-in only |
| [ ] | G09 | NuGet / provenance / licensing / compatibility notes | Not started | Not run | Not run | - |
| [ ] | G10 | Trimming / NativeAOT | Deferred | Not run | Not run | - |
| [ ] | G11 | Configuration and default-value contract | In progress | Passed: E02 subset | Not run | E02: settled initial configuration slice; later settings/rotation pending |

### V8 extension

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [ ] | V01 | V8R6C9B14 identity / mode / catalog | Deferred | Not run | Not run | - |
| [ ] | V02 | All V8 type / authentication / routine paths | Deferred | Not run | Not run | - |
| [ ] | V03 | Earlier V8 subversions | Deferred | Not run | Not run | - |
| [ ] | V04 | V9 regression after V8 extension | Deferred | Not run | Not run | - |

## 5. Profiles and unresolved dependencies

No live profile is registered. Candidate profile IDs in the matrix describe targets, not actual accepted environments. Register the complete matrix section 3.2 identity before adding any LIVE pass:

| profileId / parentCandidate | Product/startup versions and query source | Protocol/endpoint/mode policy/source/settings | Auth/TLS/reset/cancel/topology/platform | Driver artifact | Acceptance/cleanup evidence |
| --- | --- | --- | --- | --- | --- |
| None registered | - | - | - | - | No live acceptance. |

The unresolved design items below are dependencies to resolve; they do not mean every independent implementation task is blocked.

| Design item | Current dependency | Affected work | Next action |
| --- | --- | --- | --- |
| U01 | No registered authorized real-server profiles; on 2026-11-24 the user confirmed no test environment is currently available and requested continued offline development. | DP02; server-dependent acceptance across DP03-DP16 and DP20-DP22. | Continue independent offline units; register available V9 environments without storing credentials when provided. |
| U02 | Full product version query/grammar and reliable deployment identification remain unconfirmed. | C03-C05, DP02/DP03. | Establish bounded read-only queries and retain the actual returned strings. |
| U03 | Routine output/return-code formats and any need for protocol extensions remain unconfirmed. | Q10-Q14, DP09. | Compare actual routine result/protocol behavior per mode. |
| U04 | V8 mode/deployment existence and additional V8 target builds remain unconfirmed. | V01-V03, DP17/DP18. | Register V8R6C9B14 facts during the V8 stage; agree additional builds before expanding scope. |
| U05 | Conditional cryptography, integrated authentication, transaction affinity/promotion, and backend-specific transaction recovery environments are not registered. | C11-C13, X04-X05, DP10/DP15. | Register the required server/platform/security configuration and establish applicability separately, including PG preparation versus native MySQL XA and recovery. |
| U06 | Public package name, license, and publication configuration are not finalized. | G09, DP16. | Resolve before public packaging/release and retain dependency/code provenance. |
| U07 | Native MySQL endpoint bootstrap/authentication/catalog/reset/cancellation facts and an actual MySQL-only profile are not established. | C20-C21, Q20, T33, P01-P03, DP20-DP22. | Register selected protocol/actual endpoint with PG unavailable; verify Kingbase identity, mode provenance, auth/TLS, prepared/text types, reset effects, cancellation/routing, and metadata per native profile. |

## 6. Validation and completion evidence

This section records implementation and acceptance evidence linked from the ledger/package rows. Keep test artifacts and temporary investigation output outside this document.

Use the following fields for each entry:

| Field | Required content |
| --- | --- |
| Feature / work package | Canonical feature IDs, package ID, and exact sub-items covered. |
| State and date | Current status, completion date when applicable, and the declared acceptance boundary. |
| Source / artifact | Commit or stable source/artifact identity; implementation and test paths. Use package version/hash when validating a package. |
| Commands and results | Exact relevant commands, pass/fail/skip outcomes, and test/subcase IDs. |
| SIM | Executed simulation cases and limitations, or an evidence-based N/A reason. |
| LIVE | profileId and per-subcase results, or the unresolved requirement/inapplicability reason. |
| Comparison | Official DLL full hash and independent specification/observations if a comparison informed the result. |
| Failure/limits | Client defect, confirmed server limit, permissions/configuration, missing environment, or unknown; link defects and explain the evidence. |
| Resource/cleanup | Connection/transaction/Reader/COPY ownership and test data cleanup results where relevant. |
| Follow-up | Remaining requirements and blockers; leave the parent unchecked if any applicable requirement remains. |

When a unit is finished, update its row, package, phase, and section 1 counts in the same change. Preserve earlier evidence when reopening a regression or expanding the acceptance set. A passing local test, a matching official result, or a completed documentation task alone must not create a LIVE pass.

### E01 - DP01 engineering foundation (2026-11-24)

- SDK policy correction (2026-11-24): at the user's request, removed `global.json` and the SDK pin from the active setup/design instructions. Use an installed SDK supporting net10.0 and slnx. The original artifact and SDK below remain historical validation evidence; they are not a version requirement.
- Version policy correction (2026-11-24): at the user's request, the shared product version is now `0.1.0` without a prerelease suffix. The earlier `0.1.0-dev` artifacts below remain historical evidence only. Version syntax does not change the early-development or unaccepted-LIVE status.
- Policy correction validation: `./eng/verify.ps1` passed without an SDK pin: 273 offline passes, 3 live-gate skips, zero build warnings/errors and five explicit package consumers checking the standard version. SDK 10.0.403 was selected by the local environment. Local `KingbaseConnector.0.1.0.nupkg` SHA-256: `2D6952F76DB66C07643D308D7F3D849BEA2D8CA1D00D9DDD87FB38E8C0D206D6`.
- Scope/state: G01 Complete; G02 single-core project subset, G03 asset-loading subset and G08 offline/live-gate subset In progress. DI lifetime, functional package APIs, differential comparison and server acceptance remain open.
- Source/artifact: `global.json`, shared props, `KingbaseConnector.slnx`, `src/KingbaseConnector`, `tests/*`, `eng/verify.ps1`. Validated local package `KingbaseConnector.0.1.0-dev.nupkg`, SHA-256 `9C190B16AA3C14FE7E04F5EF44FB46E4DD4A4F1DAED8C15D1298BC97F4FBD468`; name/version are provisional under U06 and are not published.
- Commands/results: `./eng/verify.ps1` restores/builds all five TFMs with zero warnings/errors, runs 2 engineering tests on each of net8.0/net9.0/net10.0 (6 passed), visibly skips the live gate once on each host (3 skipped), packs, and loads all five explicitly selected assets. The empty protocol suite has no SIM result. SDK 10.0.403; Windows x64 runtimes 8.0.33/9.0.22/10.0.14; Standard assets run on .NET 8.
- Reproduction/negative gate: the same entry point passed from `artifacts/clean-checkout`, a snapshot containing only tracked/non-ignored source files with no pre-existing build outputs or official binaries. In a child PowerShell process, `$env:KINGBASE_TEST_LIVE='1'; dotnet test tests/KingbaseConnector.IntegrationTests -c Release -f net10.0 --no-build --no-restore` failed as intended with the unregistered-profile diagnostic (1 failed, no network activity); the variable was confined to that process. This is a gate check, not a failed database acceptance case.
- SIM/LIVE: N/A for G01 because it is exclusively build/structure work. No simulated or live database acceptance is claimed for any other feature; no profile is registered.
- Dependencies/provenance: no explicit runtime dependency or official DLL/EF/external database-driver reference in the core. Test-only licenses checked in restored nuspecs: Microsoft.NET.Test.Sdk 18.0.1 (MIT), xunit 2.9.3 and xunit.runner.visualstudio 3.1.5 (Apache-2.0). Exact versions and initial implicit Standard build graph are documented in the development guide. Implementation is independent.
- Resource/limits/follow-up: fresh consumer cache/output per run prevents stale provisional packages from passing. No database resources are opened. Linux, ARM64, Framework hosts, connection functionality, DI, license/publication and functional package-consumer contracts remain pending.

### E02 - DP03 first configuration slice (2026-11-24)

- Scope/state: C01 builder subset, C20 configuration validation subset, G11 initial settings contract and G03 configuration-consumer subset implemented. Parent features remain In progress: factory registration, transport/handshake/pool identity, settings application, credential callbacks/rotation and later configuration remain pending.
- Source/artifact: `KingbaseConnectionStringBuilder.cs`, `KingbaseConnectionOptions.cs`, `ConnectionStringBuilderTests.cs` and package consumer `Program.cs`. Validated local `KingbaseConnector.0.1.0-dev.nupkg` SHA-256 `FDDDD141B01D4BA000602E02FEA9E36CC87618C16A3F6124BE373922402E0AD0`.
- Contract: design section 6.5 records exact canonical keywords/properties, aliases, defaults, units, zero/negative behavior, required fields, BCL last-write-wins duplicate handling, enum/value validation, password round-tripping/redaction and cross-setting conflicts. Explicit MySQL port and Auto/MySql mode restriction are local checks only. TLS defaults are configuration, not an implemented handshake.
- Commands/results: the new tests first failed due to absent public types; initial implementation exposed the BCL's string-storage behavior and was corrected in the shared getter. `dotnet test tests/KingbaseConnector.UnitTests -c Release --no-restore --disable-build-servers` and `./eng/verify.ps1` passed with 55 unit cases per net8.0/net9.0/net10.0 (165 passes), 3 live-gate skips, all five product builds without warnings/errors, and public configuration consumer checks on all five explicit package assets.
- SIM/LIVE: passed offline configuration cases only (quoted Unicode/passwords, duplicates/aliases through the BCL base type, unknown/invalid/undefined/fractional input, required fields, pool bounds, null/remove/default behavior, no input-value/inner-exception leaks, NUL rejection and MySQL constraints). No framing/transport/server test was run, and no LIVE profile is accepted.
- Provenance/resources/follow-up: independently implemented using BCL facilities with no new dependencies; no database resources opened. Factory registration waits for usable Connection/Command APIs; transport/authentication/bootstrap and full G11 settings application remain open. The API is the same on all five assets.
- Code review: independent read-only review found no actionable defects; additional probes confirmed IDictionary.Add alias canonicalization and rollback after invalid inherited ConnectionString assignment. All initial configuration tests and package checks passed before commit.

### E03 - DP04 bounded PG framing and control parsers (2026-11-24)

- Scope/state: independent Q01 frame/Simple Query encoding, C06 UTF8/ParameterStatus parsing, Q17 internal error fields and G07 bounded-I/O/fault subsets implemented. Parent features and DP04 remain In progress. No query state machine, connection authentication, network transport or server capability is claimed.
- Source/artifact: `src/KingbaseConnector/Protocol/{PgWire,PgBinary,PgFrontendMessages,PgBackendMessages}.cs`, `tests/KingbaseConnector.ProtocolTests/PgProtocolTests.cs`. Local package `KingbaseConnector.0.1.0-dev.nupkg`, SHA-256 `F953FCE2A011F7C1E373C3D8B7EA5A0E365CED7547ACAF150E67D1C828BEE4F5`.
- Contract/provenance: standard PG v3.0 message layouts from W21, independently implemented with BCL facilities and no new dependencies. The design records the 64 MiB materialization bound, stream ownership, conservative retirement and unsupported streaming/state-machine scope. Official DLL source was not copied or executed.
- Commands/results: protocol tests first failed because the implementation did not exist. `dotnet test tests/KingbaseConnector.ProtocolTests -c Release --no-restore --disable-build-servers` and `./eng/verify.ps1` passed: 36 protocol and 55 unit tests on each net8.0/net9.0/net10.0 host (273 passes), 3 visible live-gate skips, all five product builds without warnings/errors, and five explicit public-configuration package consumers. Internal protocol code is built for both Standard assets; its runtime SIM cases executed on modern assets only.
- SIM: one-byte fragmentation and consecutive frames, every truncated header/payload boundary, negative/undersized/oversized lengths rejected before payload reads, empty/exact-limit frames, pre-cancel versus pending-read cancellation, concurrent read/write rejection, partial-write failure and one-time retirement, exact frontend length/order/UTF8 encoding, invalid Unicode/NUL, transaction status, parameter/key/error fields, duplicate/trailing/missing fields and safe error text. An invalid-Unicode test constructs its surrogate at runtime because attribute serialization replaces that input.
- LIVE/resource/follow-up: no real profile or network connection, no accepted LIVE result. Owned streams are closed on failure/disposal and never reused. Async I/O requires a cancellation-aware stream; transport deadlines/abort, TLS/authentication, query dispatch/synchronization, the bounded PG cancellation-channel closure barrier, public exception execution phases, notices and large-row/COPY streaming remain open.
- Code review: independent read-only review found no actionable defects in the bounded foundation. Cancellation-during-write and concurrent-dispose dedicated cases are not yet part of this subset; they remain in the transport/session resource work. No real network behavior was inferred from the stream tests.

### E04 - DP03 offline PG password/MD5 authentication (2026-11-24)

- Scope/state: C08 In progress; implemented standard server-requested password/MD5 challenge processing and exact PasswordMessage encoding. This is an internal exchange handler, not a complete Connection.Open/login, TLS or Kingbase acceptance path. G02's separate DI deliverable is explicitly required by the user; its registration contracts are defined in design section 5.1 and remain pending usable Connection/DataSource ownership.
- Source/artifact: `Protocol/PgPasswordAuthentication.cs`, `PgFrontendMessages.Password`, `PgPasswordAuthenticationTests.cs`. Local `KingbaseConnector.0.1.0.nupkg` SHA-256 `89B82D3BDFAD315D01F809FF21D4834B847349D50F735DF1F2F7F50B7D4955D7`.
- Contract/provenance: W21/W22 standard PG layouts and flow, independent BCL implementation with no new dependency or official DLL execution. Python hashlib produced independent fixed ASCII/Unicode/empty-password vectors using raw four-byte salts and lowercase hex. No proactive credential transmission or unsupported-algorithm fallback.
- Commands/results: tests against temporary unimplemented signatures failed in 27 new cases before implementation; an erroneous test row treating the valid password-response/AuthenticationOk sequence as invalid was removed after comparing the failure with W22. `./eng/verify.ps1` then passed: 62 protocol and 55 unit cases per net8.0/net9.0/net10.0 (351 passes), 3 visible live-gate skips, all five product builds without warnings/errors and five explicit configuration/version package consumers. Standard assets are compiled; internal authentication runtime cases use modern assets only.
- SIM: exact UTF8 cleartext/empty-password packets, three independent MD5 vectors, AuthenticationOk with no challenge, exactly one response followed by AuthenticationOk, duplicate/post-completion challenges, unsupported algorithms, incorrect message codes, truncated/trailing payloads, invalid Unicode/NUL and safe diagnostics, terminal failed exchanges and unmodified raw input salt.
- LIVE/resources/follow-up: no registered server profile, network connection or accepted LIVE result. Temporary credential/hash buffers are cleared in finally; generated frames belong to the caller and must be cleared after successful or failed writes. Existing immutable credential strings cannot be erased. Complete transport/TLS/login, SCRAM, SM3, credential callbacks/rotation and server-dependent acceptance remain open.
- Code review: independent read-only review found no actionable defects, independently recomputed all three MD5 vectors, passed the 26 authentication cases on all three modern hosts and built the five product TFMs without warnings/errors.

### E05 - DP03/DP04 internal bounded PG startup exchange (2026-11-24)

- Scope/state: C06/C08/C18 and DP03/DP04 remain In progress. The internal coordinator sends StartupMessage, processes supported authentication, parameters/notices/key/error messages and returns only after an idle ReadyForQuery plus confirmed UTF8 and exactly one backend key. It does not select a transport, secure a socket, establish Kingbase identity or implement public Open.
- Source/artifact: `Protocol/PgStartup.cs`, `PgStartupTests.cs`, the per-read payload limit in `PgWire` and its framing boundary tests. Local `KingbaseConnector.0.1.0.nupkg` SHA-256 `FC3518A79602D5C189D9DECE765DC5240871E946EC3F2F155187C266E8FC8DC2`.
- Contract/provenance: independently implements the W21/W22 PG startup phase using existing framing/authentication primitives and no additional dependencies. Bounds the exchange to 256 backend frames and 1 MiB aggregate payload. Latest repeated ParameterStatus values replace previous ones. Structured notices are parsed/discarded in this internal subset; public notice events remain open.
- Commands/results: 11 new tests failed against temporary unimplemented signatures before implementation. After the review correction below, `./eng/verify.ps1` passed: 78 protocol plus 55 unit cases on each net8.0/net9.0/net10.0 (399 passes), 3 visible live-gate skips, five product TFMs without warnings/errors and five explicit configuration/version package consumers. Internal startup runtime SIM uses modern assets; Standard assets compile only for this subset.
- SIM: one-byte-fragmented trust/password/MD5 startup, literal outgoing bytes, repeated parameters, notices at either phase, server errors before/after authentication, phase/order/key/encoding/transaction errors, unsupported algorithms with no password response, message/payload limits, pre-cancellation versus pending-read cancellation, password-buffer clearing on success/write failure and one-time stream retirement.
- LIVE/resources/follow-up: no real profile, socket or accepted LIVE result. The caller owns the successful wire; all failed exchanges after I/O retire it, preserving the original error. Returned password frames are cleared after the write even if it throws. Remaining transport/TLS, complete algorithms, session settings, public connection/exception/notice contracts, identity/catalog initialization and pool/reset/DI lifecycle remain pending.
- Code review: independent review identified that the first payload-budget check happened after frame materialization. A header-only oversized-frame test reproduced the stall. Framing now checks each read against the remaining startup payload allowance before allocation/body reads; exact/zero/negative limits and sync/async body-unread cases are covered. Independent re-review found no further actionable issues; full verification passed after this fix.
