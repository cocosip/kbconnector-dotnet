# KingbaseConnector Development Progress

Last updated: 2026-11-24. This is the single current implementation and acceptance tracker. Requirements are defined in the [feature matrix](kbconnector-feature-matrix.md); implementation workflow and completion rules are in the [development guide](kbconnector-development.md).

## 1. Current snapshot

DP01 engineering is implemented. DP03 implements the first configuration slice, offline server-requested PG password/MD5 authentication, bounded startup and TCP/platform TLS; DP04 adds bounded PG framing/control/error primitives and an owned internal Simple/Extended text-query session. DP02/DP03 add a documented candidate version query, bounded full-version parsing and an offline identity/mode probe; DP05 adds a bounded required-built-in catalog loader using actual OIDs, initial scalar text codecs and exact Decimal/KingbaseNumeric values. DP06 adds single-statement PG named/native parameter planning with confirmed lexical state, public input parameters/collections and catalog-bound snapshots with exact constraints. An internal physical owner now composes initialization stages with one deadline, literal startup settings and failure cleanup against loopback peers. Explicit package consumers run on Windows x64 .NET 8/9/10. No real Kingbase server or live profile is accepted yet; public query/login APIs, cancellation-channel synchronization, catalog/runtime format acceptance and server acceptance remain open. The user requires the separate KingbaseConnector.DependencyInjection deliverable; registration contracts and per-TFM dependency ranges are defined, pending usable Connection/DataSource ownership.

| Product feature status | Count |
| --- | ---: |
| Complete | 1 |
| Implemented, acceptance pending | 0 |
| In progress | 32 |
| Blocked | 0 |
| Not started | 78 |
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
| V9-0 | In progress: offline probe; runtime discovery pending | Register actual target profiles; establish version/mode, directory, and routine protocol facts. | Documented candidate query/grammar and offline probe E08; no live profiles. |
| V9-1 | In progress | Engineering and the minimum connection/query/type/transaction/pool/metadata paths, including relevant failure behavior. | DP01 engineering and DP02-DP06 offline subsets; E01-E15. |
| V9-2 | Not started | Complete required functional coverage including the exceptional explicit MySQL backend; resolve and validate applicable conditional features. | None yet. |
| V9-3 | Not started | Version/deployment and exceptional MySQL-only acceptance, both-protocol pooling/faults/resources, package consumers, and the V9 release gate. | None yet. |
| V8-1 | Deferred until the V9 gate | Establish V8R6C9B14 profiles and implement verified differences in the shared core. | Static D8-91 evidence only. |
| V8-2 | Deferred until V8-1 | Cover other agreed V8 targets and regress accepted V9 behavior. | None yet. |
| EF follow-up | Deferred | A separate provider design, feature matrix, and version-specific implementation. | No EF product projects. |

Do not check a phase complete solely because its work-package list is checked. Apply the design's phase and release gates to the actual feature/profile records.

## 3. Work packages

Work-package checkboxes can be marked complete when the named subset and its exit condition are satisfied. A package that implements only part of a feature must identify that subset in its evidence entry; the full feature remains open in section 4 until all requirements pass. Dependencies below define development order, not additional scope or already implemented behavior.

- [x] **DP01 - Engineering and package structure** (V9-1; G01, G02). SDK/solution/shared props/CPM/core and test projects build all five TFMs; offline checks, packaging and explicit asset consumers execute. Optional DI remains with the later DataSource lifetime contract; G02/G03/G08 are partial. Evidence: E01.
- [ ] **DP02 - Server profiles and bootstrap discovery** (V9-0; C03-C05, G08). In progress: documented version query/grammar and offline version/mode probe E08. Register authorized profiles and confirm raw/product output, mode, catalog, editions and routine facts; establish isolated official comparison entry points. No live profile is accepted.
- [ ] **DP03 - Connection, configuration, and authentication foundation** (V9-1; C01-C02, C06-C10, C16, C18-C20, G06, G11). In progress: configuration (E02), offline password/MD5 (E04), startup (E05), loopback TCP/platform TLS (E06) and internal physical initialization composition/shared deadline/startup settings (E10). Public connection integration, pool-wait budgeting, custom CA policy, credential sources, complete session configuration, remaining algorithms and live login remain open. Use DP02 evidence for identity-dependent behavior.
- [ ] **DP04 - Protocol state and failure handling** (V9-1; Q01, Q05-Q06, Q17-Q18, P06, G07). In progress: bounded framing/frontend/control-error parsers (E03), owned internal Simple Query (E07) and catalog-bound text Extended Query (E13). Public execution/Reader ownership, complete extended capabilities, cancellation-channel closure and uncertain-outcome contracts remain pending.
- [ ] **DP05 - Type system and basic codecs** (V9-1; T01-T03, T05, T07-T10, T28, T30). In progress: required-built-in catalog/OID lookup E09, bounded scalar text conversion E11, exact Decimal/KingbaseNumeric text helper E12 and actual-catalog input resolution E15. Basic helpers use actual OIDs with stable CLR types and explicit NULL/unsupported behavior. Catalog/runtime format validation, temporal/binary codecs, session format facts, public execution/result paths and typed getters remain open. Later mode-specific coverage for these features remains part of DP08.
- [ ] **DP06 - Commands, parameters, and Reader foundation** (V9-1/2; Q02-Q04, Q06, Q15-Q16, M01, M06). In progress: internal ordered catalog-bound text values/typed NULL through Extended Query (E13); bounded PG named/native parameter planning (E14); public parameter/collection model and actual-catalog input snapshots with exact constraints (E15). Public commands, other-mode lexing/binding, streaming/multiple results, Reader behavior and foundational metadata remain pending.
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
| [ ] | C02 | TCP, DNS, synchronous/asynchronous Open | In progress | Passed: E06/E10 subsets | Not run | Internal physical initialization/shared deadline; public Open/pool wait/live acceptance pending |
| [ ] | C03 | Full product version detection | In progress | Passed: E08/E10 subsets | Not run | Bounded probe/parser and physical initialization; actual profiles/release adaptation pending |
| [ ] | C04 | Automatic detection / explicit CompatibilityMode | In progress | Passed: E08/E10 subsets | Not run | Offline configured/reported/effective mode/provenance and physical initialization; public/live integration pending |
| [ ] | C05 | Full-mode / standalone edition compatibility | Not started | Not run | Not run | - |
| [ ] | C06 | Session encoding / parameter status | In progress | Passed: E03/E05/E07 subsets | Not run | Strict UTF8/startup and bounded query parameter updates; typed/lexical consumers pending |
| [ ] | C07 | TLS, CA, hostname validation | In progress | Passed: E06 subset | Not run | Platform TLS/strict upgrade/untrusted rejection; custom CA and positive trust/name cases pending |
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
| [ ] | C18 | Startup / session configuration | In progress | Passed: E05/E10 subsets | Not run | Bounded startup incl literal application_name/search_path/TimeZone; effective format facts/reset pending |
| [ ] | C19 | Connection state / ChangeDatabase | Not started | Not run | Not run | - |
| [ ] | C20 | Wire protocol selection / endpoint contract | In progress | Passed: E02 subset | Not run | E02: local validation only; backend/handshake/pools pending |
| [ ] | C21 | MySQL handshake / TLS / authentication | Not started | Not run | Not run | - |

### Commands, parameters, Reader, and routines

| Done | ID | Feature | Status | SIM | LIVE | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| [ ] | Q01 | Simple / Extended Query | In progress | Passed: E03/E07/E13 subsets | Not run | Bounded internal Simple/Extended text exchanges; public/planning/complete extended capabilities pending |
| [ ] | Q02 | Nonquery / scalar / reader | Not started | Not run | Not run | - |
| [ ] | Q03 | Named and positional parameters | In progress | Passed: E14/E15 PG subset | Not run | Public parameter collection composes with immutable PG Bind plan; public execution/other modes pending |
| [ ] | Q04 | Parameter types, precision/scale, typed NULL | In progress | Passed: E13/E15 PG input subset | Not run | Public selectors, actual-OID resolution, typed NULL and exact size/precision/scale; public execution/outputs/other modes pending |
| [ ] | Q05 | Synchronous/asynchronous execution, cancellation, and timeouts | In progress | Passed: E07/E10/E13 subsets | Not run | Internal query cancellation/initialization deadline; public command timeout/cancel channel pending |
| [ ] | Q06 | Multiple results and statements | In progress | Passed: E07 subset | Not run | Bounded separate text results/tags; public NextResult/typed outputs pending |
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
| [ ] | Q17 | SQLSTATE / Notice / deterministic exceptions | In progress | Passed: E03/E07/E13 subsets | Not run | Safe internal errors/notices and Simple/Extended error draining; public exceptions/events/outcomes pending |
| [ ] | Q18 | Synchronization after Cancel / damaged connection handling | In progress | Passed: E07/E13 subsets | Not run | Ordinary errors drain to Ready; malformed/cancelled exchanges retire; cancel channel/pools pending |
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
| [ ] | T01 | Type catalog / OID resolution | In progress | Passed: E09/E11 subsets | Not run | Required built-in actual-OID maps and scalar text selection; full catalog/reload/live acceptance pending |
| [ ] | T02 | bool / BIT / bit strings | In progress | Passed: E11 bool subset | Not run | Canonical bool text/exact CLR input; BIT/bit strings/public paths pending |
| [ ] | T03 | Signed integers | In progress | Passed: E11 subset | Not run | Int16/Int32/Int64 scalar text boundaries; binding/public paths and other widths pending |
| [ ] | T04 | Unsigned integers | Not started | Not run | Not run | - |
| [ ] | T05 | NUMERIC/NUMBER/DECIMAL | In progress | Passed: E12/E15 PG text/value/input subsets | Not run | Exact Decimal/provider values and input precision/scale checks; public execution/outputs/other modes/binary pending |
| [ ] | T06 | SQL Server NUMERIC/MONEY | Not started | Not run | Not run | - |
| [ ] | T07 | float/double / special values | In progress | Passed: E11 text subset | Not run | Special values/negative zero/roundtrip/overflow/underflow; binary/public paths pending |
| [ ] | T08 | char/varchar/text/nchar/nvarchar | In progress | Passed: E11 subset | Not run | Strict bounded Unicode/padding-preserving text/varchar/bpchar; other families/public paths pending |
| [ ] | T09 | bytea/binary/varbinary/RAW | In progress | Passed: E11 bytea text subset | Not run | Canonical PG hex/escape decoding and hex encoding; other types/binary/public paths pending |
| [ ] | T10 | PG DATE/TIME/TIMESTAMP/TZ | Not started | Not run | Not run | - |
| [ ] | T11 | Oracle DATE | Not started | Not run | Not run | - |
| [ ] | T12 | SQL Server DATETIME/DATETIME2 | Not started | Not run | Not run | - |
| [ ] | T13 | MySQL DATE/DATETIME/TIMESTAMP | Not started | Not run | Not run | - |
| [ ] | T14 | MySQL TIME | Not started | Not run | Not run | - |
| [ ] | T15 | INTERVAL / YM / DS | Not started | Not run | Not run | - |
| [ ] | T16 | YEAR | Not started | Not run | Not run | - |
| [ ] | T17 | int16 / wide integers | Not started | Not run | Not run | - |
| [ ] | T18 | UUID | In progress | Passed: E11 text subset | Not run | Canonical Guid text/exact CLR input; binary/public paths pending |
| [ ] | T19 | JSON / JSONB | In progress | Passed: E11 text subset | Not run | Preserve raw server text as String; binary prefix/binding/public paths pending |
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
| [ ] | T30 | Catalog / identifier types | In progress | Passed: E11 oid text subset | Not run | Exact UInt32 scalar oid mapping; other identifier types/public paths pending |
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
| [ ] | P06 | One active operation per connection | In progress | Passed: E07 subset | Not run | Internal query exchange gate only; public Reader/COPY/connection ownership pending |
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
| [ ] | G07 | Security / failure / resource stress | In progress | Passed: E03/E05-E10 subsets | Not run | Bounded exchanges and composed initialization/deadline failures; full stress/resource acceptance pending |
| [ ] | G08 | Differential / live test framework | In progress | Not run | Not run | E01: offline/live separation and fail-closed opt-in only |
| [ ] | G09 | NuGet / provenance / licensing / compatibility notes | Not started | Not run | Not run | - |
| [ ] | G10 | Trimming / NativeAOT | Deferred | Not run | Not run | - |
| [ ] | G11 | Configuration and default-value contract | In progress | Passed: E02/E10 subsets | Not run | Local configuration, applied literal startup settings/initialization budget; effective server settings/rotation pending |

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
| U02 | Candidate full-version SQL/grammar have static W24/W25 references and offline E08 implementation; documented W26 catalog fields underpin offline E09, while actual query access/names, per-mode output and reliable deployment identification remain unconfirmed. | C03-C05/T01, DP02/DP03/DP05. | Run the bounded probes on registered profiles and retain exact returned strings/catalog/edition facts before identity/adaptation acceptance. |
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
- Dependency policy correction (2026-11-24): at the user's request, DI package minimums now follow the local obconnector reference: net8.0 `[8.0.0,)`, net9.0 `[9.0.0,)`, net10.0/Standard `[10.0.0,)`. Conditional central declarations cover DI abstractions and its test container. No new dependency reference was added to the core; actual DI package dependency groups and consumers remain pending its implementation.
- Dependency policy validation: `dotnet msbuild src/KingbaseConnector/KingbaseConnector.csproj -p:TargetFramework=<TFM> -getItem:PackageVersion` evaluated both DI declarations at the expected minimum for all five TFMs; `git diff --check` passed. Independent read-only review found no actionable issues or unintended core/test references. These checks validate conditional configuration, not a DI package or its runtime compatibility.
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

### E06 - DP03 TCP and platform TLS transport (2026-11-24)

- Scope/state: C02/C07 and G07 transport/resource subsets In progress; DP03 remains open. Internal synchronous/asynchronous TCP connection and strict PG SSLRequest upgrade are implemented. Public Open, a shared initialization deadline, startup integration and Kingbase identity are pending.
- Source/artifact: `Protocol/PgTransport.cs`, `PgTransportTests.cs` and design section 6.5. Local `KingbaseConnector.0.1.0.nupkg` SHA-256 `557020A3204520C0999FE3D59A23D48230EDBD8C55E47A04BF87019BFF0C90D4`.
- Contract/provenance: independently implements W21/W23 with BCL TcpClient/SslStream and no new dependencies. Disable sends no SSLRequest; Require encrypts without certificate validation; VerifyFull uses system chain/hostname validation. Both TLS modes require an S reply and protected TLS, with no plaintext fallback. Every asset explicitly selects OS TLS protocols with SslProtocols.None. Revocation checking is disabled in this subset.
- Commands/results: 14 new cases failed against temporary unimplemented signatures before implementation. `./eng/verify.ps1` passed after implementation and fixture correction: 93 protocol plus 55 unit cases on each net8.0/net9.0/net10.0 (444 passes), 3 visible live-gate skips, five product TFMs without warnings/errors and five explicit configuration/version package consumers. Windows x64 SDK 10.0.403; runtimes 8.0.33/9.0.22/10.0.14. Internal Standard transport is compiled, not directly exercised.
- SIM: 15 loopback transport cases per modern runtime cover sync/async plaintext and encrypted round trips, exact SSLRequest bytes, untrusted-certificate rejection before application data, N/invalid/truncated replies without fallback, cancellation during SSL negotiation/TLS handshake, pre-cancel and input validation, token lifetime after success, stream ownership and repeated disposal. IPv6 literal ::1 ran successfully on this host; unsupported IPv6 hosts visibly skip that case.
- Failure diagnosis: the first TLS fixture used an ephemeral server key rejected by Windows Schannel. The server error confirmed this fixture limitation; exporting/reloading its test-only PFX with UserKeySet corrected it without changing client validation or machine trust. Exported private-key bytes are cleared, certificates disposed, and peer failures awaited rather than hidden by client EOF.
- LIVE/resources/follow-up: no registered Kingbase profile or LIVE result. The transport owns its socket/stream; failures and in-flight cancellation close both, while later cancellation of the creation token leaves a successful transport open. Custom CA/client certificates, isolated hostname-mismatch and positive trusted-chain tests, revocation policy, Standard/older-host DNS cancellation latency, shared deadlines, identity, pooling and DI remain pending. No database objects or trust-store changes were made.
- Code review: independent read-only review found no actionable defects; its suggestion to use the explicit Standard TLS overload was applied and accepted in follow-up review, together with IPv6 coverage. Full verification passed after the final source/test changes.

### E07 - DP04 owned bounded PG text-query exchanges (2026-11-24)

- Scope/state: Q01/Q05/Q06/Q17/Q18 and C06/G07 internal subsets In progress. PgQuerySession owns an authenticated UTF8 wire and executes one bounded synchronous/asynchronous Simple Query exchange at a time. Public Command/Reader, Extended Query, typed codecs, cancellation-channel synchronization and pooling remain open.
- Source/artifact: `Protocol/PgQuerySession.cs`, `PgQuerySessionTests.cs` and design section 7. Local `KingbaseConnector.0.1.0.nupkg` SHA-256 `3A70E1BC6F291D122602A1D005D760AF125CD9485EE63A43A04B736C2461F150`.
- Contract/provenance: independently implements W21/W22 text RowDescription/DataRow, CommandComplete, EmptyQueryResponse and ReadyForQuery using existing BCL-only framing. Retains signed metadata values, raw unsigned OIDs, strict UTF8, NULL versus empty text, separate statement results and command tags. No static OID/type-catalog assumptions or identity queries. The exchange returns results only at ReadyForQuery; ordinary server errors discard results, drain to ReadyForQuery and throw safe SQLSTATE text before another query is allowed.
- Bounds: 1 MiB outbound UTF8 SQL including terminator; 1 MiB aggregate backend payload enforced from frame headers before body allocation; 4096 frames, 256 results, 1024 rows and 1024 columns per result. Session parameter retention is independently capped at 256 entries and 1 MiB UTF8 name/value bytes including terminators across repeated queries; replacing values releases previous allowance.
- Commands/results: 54 initial cases failed against temporary unimplemented signatures. Four additional empty-query phase cases and two retained-parameter cases reproduced missing checks before correction. `./eng/verify.ps1` passed after the final additions: 156 protocol plus 55 unit cases per net8.0/net9.0/net10.0 (633 passes), 3 visible live-gate skips, all five product TFMs without warnings/errors and five explicit configuration/version package consumers. Windows x64 SDK 10.0.403; Standard internal query paths compile, while query runtime cases execute on modern assets.
- SIM: 63 new cases per modern host cover one-byte fragmentation, unknown raw OIDs/signed metadata, Unicode/NULL/empty rows, zero-column/zero-row/nonquery/empty/multiple results, immutable returned data, repeated session parameters and notices, exact/over budgets, every truncated metadata/row payload, malformed counts/lengths/UTF8/trailing bytes/phases, unsupported binary/COPY/async messages, pre-cancel/local SQL validation, concurrent-exchange rejection, failed writes, truncated I/O, cancellation and disposal while reading. A TCP loopback case composes transport, trust startup and text query on the same socket and verifies peer EOF after session disposal.
- Error recovery/review: independent connected-source review found no actionable issues. A possible EmptyQueryResponse/ErrorResponse ordering concern was checked against W22's rule that errors/notices may interrupt any response phase; two explicit tests now verify errors after completed/empty results are drained and reported before reuse. This does not allow additional statement results after EmptyQueryResponse. A review reflection probe failed setup and contributed no behavioral evidence; acceptance relies on the executed owning tests.
- LIVE/resources/follow-up: no real Kingbase profile or LIVE result. Initial local validation, pre-cancellation and rejected concurrent calls preserve the session; failures after exchange start retire it unless a valid ReadyForQuery confirmed error synchronization. Socket/wire disposal is idempotent and blocked test reads terminate. The review's remaining boundaries are U01/U02 identity/mode/catalog, typed/public execution, COPY/streaming/cancellation channels/pooling, functional Connection/DataSource/DI and broader authentication/TLS/deployment/framework-host acceptance. Those remain pending, not judged complete by this module.

### E08 - DP02/DP03 offline Kingbase V9 identity and mode probe (2026-11-24)

- Scope/state: C03/C04 and DP02 remain In progress. The internal probe obtains a full product version separately from handshake server_version, accepts only branded major 9, and resolves configured/effective/reported compatibility modes with explicit provenance. No catalog, edition, deployment or release capability is inferred.
- Source/artifact: `KingbaseProductVersion.cs`, `Protocol/PgIdentityProbe.cs`, `ProductVersionTests.cs`, `PgIdentityProbeTests.cs` and the shared `QueryStream.cs` fixture extracted without behavior changes. Local `KingbaseConnector.0.1.0.nupkg` SHA-256 `0FE0A84969F39B9034F0942A90EE8D4B0F4862FF3696168D6C0CD7270051DD1F`.
- Contract/provenance: W24 documents `SELECT version();`; W25 defines the full product-token grammar `VxxxRxxxCxxx[Mxxx[customer]]Bxxxy[PSxxx]`. The independent bounded parser preserves raw text, token and each numeric/customer component. Auto uses current database_mode ParameterStatus or exactly `SHOW database_mode`; explicit configuration skips that extra query and rejects conflicting/unknown reported values. Each query must produce exactly one non-NULL text scalar and idle ReadyForQuery. Actual per-mode/version output remains U02.
- Commands/results: 54 initial cases failed against temporary unimplemented signatures. Final coverage adds 27 parser and 32 protocol cases per modern host; all 63 existing query cases passed after fixture extraction. `./eng/verify.ps1` passed: 188 protocol plus 82 unit cases per net8.0/net9.0/net10.0 (810 passes), 3 visible live-gate skips, five product TFMs without warnings/errors and five explicit configuration/version package consumers. Standard internal identity paths compile; runtime SIM uses modern assets.
- SIM: fixed-width ASCII grammar, optional customization/customer/patch fields, raw preservation, bounds/NUL/branding/invalid tokens; exact probe SQL, all four modes and case handling, raw startup/product-version separation, explicit/Auto provenance, status changes, conflict/unknown mode, malformed/non-scalar/non-idle responses, server errors, I/O failure, pre-cancel and in-flight cancellation. All begun probe failures retire the owned session; pre-I/O local rejection preserves it.
- Code review: independent connected-source review found no implementation blockers. Corrected W24's section citation to 2.4.2 against the cached official heading. The review did not claim live outputs or full-suite execution; the parent verified the completed full check and actual package hash.
- LIVE/resources/follow-up: no server profile, database objects or LIVE result. Exclusive initialization ownership is required. V8, catalog loading, per-release capabilities, deployment facts, functional Connection/DataSource/DI and concurrent public lifecycle remain open; no placeholder public Open or fallback query was added.

### E09 - DP05 required-built-in PG catalog metadata (2026-11-24)

- Scope/state: T01 and DP05 In progress. Added an internal bounded required-built-in catalog loader with immutable session-local maps keyed by actual unsigned OID and exact qualified name. This is metadata lookup, not codec or full catalog acceptance.
- Source/artifact: `Protocol/PgTypeCatalog.cs`, `PgTypeCatalogTests.cs` and shared internal mode parsing in `PgIdentityProbe.cs`. Local `KingbaseConnector.0.1.0.nupkg` SHA-256 `255FF21C38CC5B89F18EAF6929CE2252C4838DF534396BEA969E50CF72417CC6`.
- Contract/provenance: W26 independently documents sys_type/sys_namespace fields and relationships. The fixed read-only candidate query qualifies sys_catalog relations and selects only 21 named defined built-ins. Six required basic base types must exist; identifiers/qualified names must be unique and namespace IDs consistent. Preserve raw size, kind/category, base/element/array IDs and typmod, including references outside this subset. No global OID assumptions, supported-codec inference, fallback SQL, search_path-based lookup or writes.
- Commands/results: all 88 new catalog cases first failed against temporary unimplemented signatures, then passed on each modern host. `./eng/verify.ps1` completed successfully: 276 protocol plus 82 unit cases per net8.0/net9.0/net10.0 (1,074 passes), 3 visible live-gate skips, five product builds without warnings/errors and five explicit configuration/version package consumers. Internal Standard catalog paths compile; runtime SIM uses modern assets.
- SIM: changed/UInt32-boundary OIDs, exact SQL, immutable maps, distinct catalogs on separate sessions, raw metadata preservation, explicitly accepted true text spellings, NULLs/invalid numeric/character fields, undefined/unrequested/foreign-schema rows, duplicate/missing/non-base required types, inconsistent namespace IDs, extra/incorrect result shapes, non-idle results, final unknown/conflicting mode, server errors, truncated I/O, local validation/pre-cancel and cancellation while reading. Begun failures close the session; no fallback/replay or partial catalog escapes.
- Code review: independent connected-source review found no actionable defects or blocker. It confirmed field-level documentation and the explicit boundaries for pre-query/final mode consistency, exclusive initialization ownership, raw metadata without relationship closure and pending public APIs. Full verification and actual package hash were confirmed by the parent.
- LIVE/resources/follow-up: no real profile, database objects or LIVE result. Actual sys_catalog placement, names, boolean representations, permissions and query availability across V9 modes/builds remain U02. User-defined/array/domain/member loading, catalog reload/invalidation, codec/format selection, public Connection/DataSource/DI and broader platform acceptance remain open. Version/mode results alone do not establish this catalog subset on a real endpoint.

### E10 - DP03/DP04 bounded PG physical initialization (2026-11-24)

- Scope/state: C02/C03/C04/C18 and Q05 internal subsets remain In progress. An owned synchronous/asynchronous physical factory composes TCP/platform TLS, startup/authentication, identity/mode and required-built-in catalog loading on one connection. Public Connection/Open, pool wait, typed results and DI delivery remain pending.
- Source/artifact: `Protocol/PgPhysicalSession.cs`, shared startup/password validation in `PgFrontendMessages.cs`, `PgPasswordAuthentication.cs`, `PgStartup.cs`, and loopback `PgPhysicalSessionTests.cs`/`PgTestPeer.cs`. Local `KingbaseConnector.0.1.0.nupkg` SHA-256 `D497D7FB7207DC3E3E69CB188C07E58A14C49E4BD7FD5F6F085B5C3EECBD2C63`.
- Contract: immutable connection-string snapshot, complete startup encoding and credential-size validation before transport creation. Unsupported MySQL/custom-root paths fail locally. Nonempty application_name/search_path/TimeZone are literal startup fields. One monotonic timeout spans every initialization stage; zero is unlimited and int.MaxValue seconds is supported. Caller cancellation retains its token and takes precedence over a simultaneous deadline. Failure retires all created resources without retries or SQL replay; creation-token callbacks and timer are detached before success escapes.
- Commands/results: 17 initial factory cases failed against temporary unimplemented signatures. Final 22 physical cases plus 26 existing authentication cases passed on each modern host after both review regressions were reproduced and fixed. `./eng/verify.ps1` exited 0: 298 protocol plus 82 unit cases per net8.0/net9.0/net10.0 (1,140 passes), 3 visible live-gate skips, five product builds without warnings/errors and five explicit configuration/version package consumers. Full log: `artifacts/validation/dp03-physical-full-final.log`. Standard internal paths compile; runtime SIM uses modern assets.
- SIM: sync/async composition and ownership, raw startup/product-version separation, actual catalog OIDs, status/SHOW mode source, literal optional settings, zero/large/cumulative timeouts, cancellation/deadline during SSL negotiation/startup/version/mode/catalog, original-token propagation, post-success token cancellation, failures/EOF/no replay, pre-cancel and unsupported/invalid/oversized inputs before networking, idempotent disposal.
- Code review: independent connected-source review identified aggregate StartupMessage and password/MD5 input bounds previously deferred until after transport creation. Listener-based regression cases reproduced both defects; full startup encoding and shared password/MD5 length checks now run first. Targeted RED/GREEN evidence: `dp03-physical-aggregate-red.log`, `dp03-physical-authsize-red.log`, `dp03-physical-authsize-green.log` under `artifacts/validation/`. Follow-up review found no remaining actionable defects. An unread startup ErrorResponse fixture caused TCP reset and was corrected to terminate at the error; no production change was made for that fixture issue.
- LIVE/resources/follow-up: no real profile, database objects or LIVE result. Internal physical ownership is exercised with simulated peers only. Catalog SQL/runtime formats across real V9 profiles, Standard/older-host DNS cancellation latency, custom CA, remaining authentication, public lifecycle/query APIs, typed codecs, pooling and separate functional DI package remain open.

### E11 - DP05 catalog-bound scalar PG text codecs (2026-11-24)

- Scope/state: T01/T02/T03/T07/T08/T09/T18/T19/T30 internal subsets In progress. PgTextCodecs resolves 14 supported built-in names through actual session-local OIDs and validates scalar base metadata/size before exposing a stable CLR type or handling NULL. No binary layout, temporal/numeric support, public Reader or bound input acceptance is implied.
- Source/artifact: `Protocol/PgTextCodecs.cs`, `PgTextCodecsTests.cs`, shared `PgTestPeer.cs` catalog/typed-result fixture and design section 8.1. Local `KingbaseConnector.0.1.0.nupkg` SHA-256 `0F569862AF090DB2F74D745522B5FFDB2C9517FE6B381173A350E7E12DC44591`.
- Contract/provenance: independently implements the frozen scalar text contract using the BCL and static W27/W28 references. Mappings: bool, Int16/Int32/Int64, UInt32 oid, Single/Double, String text/varchar/bpchar/json/jsonb, Byte[] bytea and Guid uuid. SQL NULL becomes DBNull.Value; null/DBNull input remains NULL. Exact CLR input is required, with invariant numeric grammar, explicit finite overflow/nonzero underflow, canonical special values/negative zero and round-trip precision. String padding/JSON text are preserved; bytea reads canonical hex/escape output and writes lowercase hex. Value conversion does not concatenate SQL or select binary formats.
- Bounds/diagnostics: at most 1 MiB strict UTF8 per text value, checked before parsing/allocation amplification; hex input size checked before allocating output. Unknown/mismatched-layout/domain/array/unimplemented types reject even NULL. Conversion failures omit payloads and inner conversion errors. Raw text remains available through the existing query result.
- Commands/results: 76 new cases first failed behaviorally against temporary unimplemented signatures, then passed per modern runtime. A malformed UTF16 InlineData value was normalized in attribute serialization; the fixture now constructs that invalid input at runtime. No codec weakening was needed. `./eng/verify.ps1` exited 0: 374 protocol plus 82 unit cases per net8.0/net9.0/net10.0 (1,368 passes), 3 visible live-gate skips, five product builds without warnings/errors and five explicit configuration/version package consumers. Logs: `artifacts/validation/dp05-scalars-red.log`, `dp05-scalars-green.log`, `dp05-scalars-full.log`. Standard codecs compile; runtime SIM uses modern assets.
- SIM/review: shifted and UInt32.MaxValue OIDs, separate catalogs, malformed layouts, stable NULL mappings, exact integer limits, culture independence, floating special/subnormal/negative-zero/round-trip edges, malformed syntax/ranges, every byte value and escape form, strict UTF8/byte limits, safe exceptions and loaded-catalog/query-result composition. Independent connected-source review found no actionable defects and accepted the change. Canonical NaN text preserves NaN semantics, not arbitrary NaN payload bits.
- LIVE/follow-up: no real server profile, database objects or LIVE result. W27/W28 are static references only; actual text formats and catalog layouts across V9 modes/builds remain U02. Numeric/provider precision, temporal/session facts, binary codecs, bound parameters/outputs/COPY, public getters, pooling, functional Connection/DataSource/DI and older-host execution remain pending.

### E12 - DP05 exact decimal and high-precision numeric text (2026-11-24)

- Scope/state: T05 internal text/value subset In progress. Public immutable KingbaseNumeric preserves a BigInteger coefficient and nonnegative decimal scale, explicit NaN/positive/negative infinity, invariant parsing/formatting and numerical equality across scales. PgTextCodecs keeps Decimal as the stable default field type for actual catalog numeric OIDs and offers explicit provider-specific access. No bound parameters, public Reader or binary numeric path is claimed.
- Source/artifact: `KingbaseNumeric.cs`, numeric additions to `Protocol/PgTextCodecs.cs`, `NumericTests.cs`, `PgTextCodecsTests.cs`, public numeric checks in `ConsumerTests/Program.cs`, README example and design section 8.2. Final local `KingbaseConnector.0.1.0.nupkg` SHA-256 `C4CF35C46E885EFE37D39B197F21DB42B0E8A432FDEFAC65EB0A2B9204C9458D`.
- Contract: default struct is finite zero; preserve finite coefficient/scale and exact numeric text, with explicit zero semantics for negative zero. Parse ASCII fixed/exponent syntax and canonical specials; bounded exponent expansion and significant coefficient precision. MaxDigits/MaxScale 16384 are independent client resource limits, not server maxima or complete numeric coverage. Exact ToDecimal uses only zero-digit reductions to fit 96 bits/scale 0..28 and rejects every nonzero discarded digit, special value, magnitude overflow and underflow. FromDecimal preserves coefficient/scale. Numeric protocol text input accepts only Decimal or explicit KingbaseNumeric; NULL is DBNull in both result mappings. No SQL literals/interpolation or row-dependent default CLR types.
- Commands/results: all 56 new value tests failed against temporary unimplemented signatures; numeric codec additions and changed NULL coverage produced 9 expected failures while 75 existing scalar cases passed. GREEN then passed 56 value and 84 scalar cases per modern host. `./eng/verify.ps1` exited 0: 382 protocol plus 138 unit cases per net8.0/net9.0/net10.0 (1,560 passes), 3 visible live-gate skips, five product builds without warnings/errors and five fresh-cache package consumers. Logs: `artifacts/validation/dp05-numeric-red.log`, `dp05-numeric-green.log`, `dp05-numeric-full.log`. After README/XML-summary-only edits, repacked all five TFMs and reran five fresh-cache configuration/numeric consumers successfully; final package evidence is `dp05-numeric-package-final.log` and the hash above.
- SIM/review: exact decimal endpoints/scale/trailing-zero reductions, explicit failures on values decimal.Parse could round, tiny nonzero values, thousand-digit values, coefficient/scale limits and exponent amplification, invalid grammar/Unicode, safe errors, culture independence, special states, numerical equality/hash/default value, actual catalog numeric selection, stable default/provider CLR types and NULLs. Actual Standard assets execute public numeric consumer checks on .NET 8. Independent connected-source review found no actionable runtime correctness defects; its stale numeric-pending documentation finding was corrected. The reviewer did not rerun suites or certify LIVE results.
- LIVE/follow-up: no server profile, database objects or LIVE result. Binary numeric/NUMBER across other modes, precision/scale/typmod binding, public Reader and parameter/output paths, arrays/COPY, full server numeric range, older hosts and actual V9 formats remain pending. This bounded helper does not close T05 or DP05. Temporal/session facts, pooling and functional Connection/DataSource/DI remain separate work.

### E13 - DP04/DP06 bounded catalog-bound PG Extended Query (2026-11-24)

- Scope/state: Q01/Q04/Q05/Q17/Q18 internal protocol subsets remain In progress. An ordered parameter snapshot and actual session catalog produce unnamed Parse/Bind/DescribePortal/Execute/Sync frames; Simple and Extended Query share exchange ownership, text results, error draining and retirement. Callers must supply an already validated native positional SQL/parameter plan. SQL lexing, placeholder counts, named rewriting and public Command binding remain DP06 work.
- Source/artifact: `Protocol/PgExtendedQuery.cs`, `PgQuerySession.cs`, `PgExtendedQueryTests.cs`, shared `QueryStream.cs`/`PgTestPeer.cs`, and design section 7. Local `KingbaseConnector.0.1.0.nupkg` SHA-256 `FE4473452E1D3358F33E6C64C2C70522F4D5A1A6F5B53E0F169E483043C09AB7`.
- Contract: maximum 1024 parameters and 1 MiB aggregate outgoing frames; all SQL/catalog/layout/exact CLR/typed NULL encoding checks finish before I/O. Values travel only in text Bind fields. Actual unsigned OIDs retain their wire bits. Require ParseComplete, BindComplete, RowDescription/NoData, exactly one completion, then ReadyForQuery. Ordinary server errors drain through Ready before a safe exception and session reuse; malformed, interrupted or partially written exchanges retire without replay. Every outgoing frame is cleared after use and on failure, including Simple Query SQL buffers.
- Commands/results: 34 initial cases failed behaviorally against temporary unimplemented signatures. Final 36 new cases plus 63 existing Simple Query cases passed on each modern host. `./eng/verify.ps1` exited 0: 418 protocol plus 138 unit cases per net8.0/net9.0/net10.0 (1,668 passes), 3 visible live-gate skips, five product builds without warnings/errors and five fresh-cache configuration/numeric package consumers. Logs: `artifacts/validation/dp04-extended-red.log`, `dp04-extended-green.log`, `dp04-extended-full.log`. Standard internal paths compile; runtime protocol SIM uses modern assets.
- SIM/review: exact five-frame bytes, Unicode/injection-looking text confined to Bind, numeric/bytea/typed NULL, shifted/UInt32.MaxValue OIDs, exact aggregate boundary, control ordering/duplicates, notices/status, server errors at each phase, cancellation/concurrency, local failure without I/O, partial Parse/Bind writes, buffer clearing, incomplete error drain and Simple/Extended reuse. A TCP loopback peer composes startup/version/catalog and Extended Query on one socket with disposal EOF. Independent connected-source review found no actionable defects; its fresh Release net10.0 targeted run passed 99/99 without skips. A stale Debug assembly matched no tests and is excluded from evidence.
- LIVE/resources/follow-up: no real profile, database objects or LIVE result. W21/W22 and simulated peers establish only this offline protocol contract. Actual V9 format/catalog behavior remains U02. Public Connection/Command/Reader, SQL planning, streaming, Prepare/cache, cancellation channel, transactions/pooling and functional DataSource/DI remain pending. Unnamed statements/portals have physical-session/transaction lifetime; no named prepared cache is introduced.

### E14 - DP06 bounded single-statement PG parameter planning (2026-11-24)

- Scope/state: Q03 internal PG subset In progress. PgParameterPlan preserves native positions or rewrites named markers into Bind slots and returns immutable original collection indexes. Requires PostgreSql compatibility mode and confirmed standard_conforming_strings for each call; no inferred default/cache. Public parameters/commands and other modes remain open.
- Source/artifact: `Protocol/PgParameterPlan.cs`, `PgParameterPlanTests.cs`, design section 7.2 and W29. Final local `KingbaseConnector.0.1.0.nupkg` SHA-256 `BDAC541C9C54737ECF5B343927D72C8CBB04062F9D5FE2974A38B24B42A47383`.
- Contract: at most 1 MiB strict UTF8 SQL including terminator before and after rewriting, at most 1024 parameters with bounded snapshot enumeration/recheck, one statement and optional trailing semicolon/comments. Native positions require every index 1 through exact supplied count, without leading zeros, invalid suffixes or named mixing. ASCII named identifiers up to 128 characters match unique normalized collection names case-insensitively, reuse occurrence slots, and reject unused/missing/invalid names. Preserve quoted bodies/identifiers, ordinary and explicit escapes, newline continuation, dollar delimiters/identifier dollars, comments and supported operator boundaries. Do not read values or mutate caller collections; diagnostics omit SQL and names. This is binding-boundary validation, not full SQL grammar.
- Commands/results: 57 initial cases failed against temporary unimplemented signatures. Final 65 targeted cases passed on each net8.0/net9.0/net10.0 host. `./eng/verify.ps1` exited 0: 483 protocol plus 138 unit cases per modern runtime (1,863 passes), 3 visible live-gate skips, five product builds without warnings/errors and five fresh-cache configuration/numeric package consumers. Logs: `artifacts/validation/dp06-plan-red.log`, `dp06-plan-green-final.log`, `dp06-plan-full-final.log`. Standard internal paths compile; protocol runtime SIM uses modern assets.
- SIM/review: literal named/native SQL and exact Bind index/value order with actual catalog OIDs; repeated names and collection snapshot immutability; all supported quote/comment forms, E-string continuation, dollar tags and identifier dollars; native gaps/mixed/count errors, safe malformed errors, byte/count limits and rewriting amplification. A retained ParameterStatus change from on to off changes the next plan; local planning failure causes no additional write or session retirement. Edge regressions reproduced then fixed operator/comment consumption, unsupported named suffixes and block-comment continuation (W29 excludes block comments from quote continuation). Independent connected-source review found a snapshot count discrepancy; reproduced with an inconsistent IReadOnlyList and fixed with bounded enumeration plus post-snapshot count validation. Relevant RED logs: `dp06-plan-edges-red.log`, `dp06-plan-operators-red.log`, `dp06-plan-continuation-red.log`, `dp06-plan-snapshot-red.log`. Final reviewer found no remaining actionable defects; did not rerun tests. A suggested vertical-tab change was withdrawn after checking the version-matched PG14 scanner, which excludes vertical tab.
- LIVE/resources/follow-up: no server profile, database objects or LIVE result. Static W29 is not Kingbase lexical acceptance. Public parameter/type resolution, command integration, routines/other-mode lexing, batching, plan caching, streaming/Reader, transactions/pools and functional DataSource/DI remain pending. The internal caller must supply current verified lexical facts and an independently snapshotted value list when composing the returned indexes with Extended Query.

### E15 - DP06 public input parameters and catalog-bound snapshots (2026-11-24)

- Scope/state: Q03/Q04/T05 PG input subsets remain In progress. Public KingbaseParameter, KingbaseDbType and KingbaseParameterCollection compose with E14 planning and E13 Bind through internal PgParameterBindings. Provider selectors identify type names; only the actual session catalog supplies wire OIDs. No public command execution is introduced.
- Source/artifact: public parameter files, `Protocol/PgParameterBindings.cs`, `ParameterTests.cs`, `PgParameterBindingsTests.cs`, explicit package consumers, README and design section 7.2. Local `KingbaseConnector.0.1.0.nupkg` SHA-256 `4D5BF3B23F3D9AA431921689076FBAD1D93EBC88DC0784E9ADBD9782E5A02134`.
- Contract: exact CLR inference, independent DbType/provider/name constraints and explicit NULL types; one collection owner, normalized unique nonempty names, atomic range/replacement/rename failure and detachment on removal. Snapshot metadata, validate all selectors/directions/value sizes and aggregate 1 MiB input text before cloning byte arrays; at most 1024 inputs. Input Size never truncates; numeric Precision and explicitly assigned Scale (including zero) reject rounding/overflow without changing original Decimal/provider text. These are local exact constraints, not server typmods or casts. ResetDbType removes all selectors while preserving value metadata. Unsupported directions/types/modes fail locally.
- Commands/results: initial 15 public-model and 42 binding cases failed behaviorally against temporary signatures. Final 17 unit and 45 binding cases pass per modern runtime. `./eng/verify.ps1` exited 0: 528 protocol plus 155 unit cases per net8.0/net9.0/net10.0 (2,049 passes), 3 visible live-gate skips, five product builds with zero warnings/errors and five fresh-cache configuration/numeric/parameter package consumers. Logs: `artifacts/validation/dp06-parameters-unit-red.log`, `dp06-parameters-bind-red.log`, `dp06-parameters-unit-green-final.log`, `dp06-parameters-full-final.log`. Standard public parameter paths execute through explicit package assets on .NET 8; protocol SIM uses modern assets.
- SIM/review: actual shifted catalog OIDs, selector conflicts, typed NULL, exact values, owned snapshots, safe failures, UTF16/binary size, culture-independent numeric constraints, invalid metadata, BCL collection access and public-model-to-Extended-Query composition. Regressions reproduced then fixed SourceVersion default, oversized byte cloning before validation, aggregate text bounds and zero-valued integral constructor ambiguity. Removed the ambiguous provider-type constructor; use a property initializer or collection Add(name,type). RED logs: `dp06-parameters-sourceversion-red.log`, `dp06-parameters-bounds-red.log`, `dp06-parameters-zero-red.log`. Independent connected-source reviewer confirmed the constructor fix and found no remaining actionable defects; tests/builds were verified by the implementer.
- LIVE/resources/follow-up: no real profile, database objects or LIVE result. Public Connection/Command/Reader/DataSource, streaming, output parameters, other-mode types/lexing, server typmods, pooling and functional DI remain pending. No new references to official DLLs or external drivers; SDK remains unpinned and package version remains standard 0.1.0.
