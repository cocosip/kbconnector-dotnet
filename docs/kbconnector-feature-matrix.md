# KingbaseConnector Feature and Version Difference Matrix

Baseline date: 2026-11-23. Last updated: 2026-11-24. Status: documentation baseline ready to guide development; specific proposals remain subject to confirmation. Repository: `D:/Code/dotnet/kbconnector-dotnet`.

Related documents: [unified driver design](kbconnector-design.md), [official DLL evidence inventory](reference/official-driver-inventory.json), [developer guide](kbconnector-development.md), and [development progress](kbconnector-development-progress.md). Complete V9 before extending support to V8; this matrix does not claim that V8 is supported.

## 1. Reading Rules and Scope

**At the 2026-11-23 baseline, no product functionality had been implemented or accepted, and no tests had run against a real server.** The tables describe evidence from official materials, proposed contracts for the new driver, and acceptance requirements. They are not a list of implemented features. The [development progress document](kbconnector-development-progress.md) is the single source of current implementation, simulation, and live-validation status; this matrix retains the baseline and requirements.

| Dimension | Values and meaning |
| --- | --- |
| Official evidence | DOC: official documentation; SYM: DLL declarations; BODY: static implementation paths. Evidence applies only to the identified materials. |
| New driver status | Implementation status: Not started, In progress, Implemented, Blocked, Complete, Deferred, or Not applicable. Record SIM and per-profile LIVE results independently; implementation status alone does not establish validation. |
| Scope R | Required for V9: acceptance must cover applicable server combinations. CRUD alone does not complete V9. |
| Scope C | Conditional for V9: establish whether the server provides the capability and whether configuration and permissions permit it. Implement and accept capabilities that exist and are in scope; otherwise record the exact limitation. Missing environments remain pending validation. |
| Scope L | Separate proposal for later work: not automatically included in the first V9 release. Record scope changes explicitly; do not silently move committed features into this category. |
| Scope V8 | Version adaptation and regression work after V9 is complete; retain it in the unified core package. |

When an official method name or handler class is absent, record only that the symbol was not found; do not conclude that the server lacks the capability. Client package filenames, assembly versions, Kingbase product versions, PG kernel versions, database modes, deployment forms, and target frameworks are separate dimensions.

Design section 14 gives the full descriptions and links for sources L1–L3 and W1–W33. A "proposal" is a requirement proposed for this project, not a claim that an identical official API exists. Feature IDs index subsequent tasks, tests, and defects. Accept R items on applicable server combinations. Record confirmed absent types or server capabilities as limitations; do not manufacture support through client-side simulation.

## 2. Official Client Sample Identities

### 2.1 Primary Binaries

The numeric suffix in each alias is the number of `BuiltInKingbaseType` attribute entries extracted for this baseline. It is neither a feature count nor a count of server-supported types.

| Sample | First 16 SHA-256 characters | AssemblyVersion | Actual TargetFramework | Built-in attributes |
| --- | --- | --- | --- | ---: |
| D8-91 | 90F99C32B47F9A49 | 4.0.7.1222 | .NETStandard v2.0 | 91 |
| D9-63 | 1DA0795A0F279E67 | 4.0.7.11 | .NETStandard v2.0 | 63 |
| D9-82a | 2FCD7B1CE18ECAE5 | 4.0.7.1222 | .NETStandard v2.0 | 82 |
| D9-82b | CEF666D063085AB9 | 4.0.7.1222 | .NETStandard v2.0 | 82 |
| D9-100 | B835FE19D283D977 | 4.0.7.1222 | **.NETFramework v4.5** | 100 |
| D9-107 | 8478D5E56C417737 | 4.0.9.520 | .NETStandard v2.0 | 107 |

D9-107 has FileVersion `4.0.9.0520`. Identical AssemblyVersion values do not guarantee identical contents. The JSON inventory retains full SHA-256 hashes, paths for every duplicate file, assembly dependencies, and symbols. Truncated hashes are not the archival identities.

### 2.2 Package Directories and Binaries

| Supplied directory | Primary sample location | Permitted and prohibited inferences |
| --- | --- | --- |
| V8R6C9B14 | `netstandard2.0/Kdbndp.dll` → D8-91 | User-supplied V8 research anchor; no server acceptance evidence. |
| KingbaseES_V009R001C010B0004_X64_Windows_EFCore | Four relative paths listed below | These locations in R1, R2, and R4 contain the same four hashes. Different package names do not prove that these DLLs differ. |
| KingbaseES_V009R002C016B0006_X64_Windows_EFCore | Same paths below | Same qualification as above. |
| KingbaseES_V009R004C019B0004_X64_Windows_EFCore | Same paths below | Same qualification as above. |
| KingbaseES_V009R003C018B0003_X64_Windows_Kdbndp | `net45/netstandard2.0/Kdbndp.dll` → D9-107 | Contains implementation differences; this does not establish that an R3 server has more capabilities than an R4 server. |

The four relative paths in R1/R2/R4 are:

| Relative path | Sample |
| --- | --- |
| `NET45_EFCORE/netstandard2.0_20240514/Kdbndp.dll` | D9-63 |
| `NET45_EFCORE/netstandard2.0_202405/Kdbndp.dll` | D9-82a |
| `NET45_EFCORE/net45_20240524/netstandard2.0/Kdbndp.dll` | D9-82b |
| `NET45_EFCORE/netstandard2.0/Kdbndp.dll` | D9-100; directory name and actual TFM disagree. |

There are 25 Kdbndp.dll files with 10 unique hashes. The other four older Framework assets have hash prefixes `D6AE3F8BEFB3CDC8`, `75287CAE4AECA716`, `9EFF171F2693DE0D`, and `8629122C0CF87CAB`; all remain in the JSON inventory. This matrix does not establish complete behavioral conclusions for those assets.

### 2.3 Explicit Type Attribute Differences

`—` means that no built-in attribute with the corresponding name was found in that sample. It does not establish the absence of a generic implementation or server capability. These are **client-side hard-coded attributes**, not a server OID table queried from a live instance.

| Type name | D8-91 | D9-63 | D9-82a | D9-82b | D9-100 | D9-107 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| binary | 3383 | — | 5022 | 5022 | 3383 | 3383 |
| varbinary | 3384 | — | 5024 | 5024 | 3384 | 3384 |
| year | 7024 | — | — | — | 7024 | 7025 |
| oracle_date | — | — | — | — | — | 8020 |
| dsinterval | 7000 | 7000 | 7000 | 7000 | 7000 | 7000 |
| yminterval | 7002 | 7002 | 7002 | 7002 | 7002 | 7002 |
| sqlserver_datetime | 7881 | — | 7881 | 7881 | 7881 | 7881 |
| datetime2 | — | — | — | — | 7690 | 7690 |
| uint8 | 7084 | — | 7084 | 7084 | 7084 | 7084 |
| uint4 | 7082 | — | 7082 | 7082 | 7082 | 7082 |
| int16 | — | — | — | — | — | 4659 |

Design implication: T06, T09, T12, T16, and T17 cannot use a fixed cross-version OID table. They require the session's actual catalog and verified rules for selecting wire formats.

### 2.4 Implementation Paths and Symbol Differences

| Item | D8-91 | D9-63 | D9-82a | D9-82b | D9-100 | D9-107 |
| --- | --- | --- | --- | --- | --- | --- |
| Four-mode DbMode enum | Present | Present | Present | Present | Present | Present, plus NoMode |
| MySqlBitStringHandler class | Present | Not found | Not found | Not found | Present | Present |
| Dedicated IntervalYM / IntervalDS classes | Present | Not found | Not found | Present | Present | Present |
| SqlServerNumericHandler class | Not found | Not found | Not found | Not found | Not found | Present |
| SqlServerMoneyHandler class | Not found | Not found | Not found | Not found | Not found | Present |
| SqlServerDateTime2Handler class | Not found | Not found | Not found | Not found | Not found | Present |
| Int128Handler class | Not found | Not found | Not found | Not found | Not found | Present |
| Separate SM3 authentication path | Present | Present | Present | Present | Present | Present |
| SCRAM-SHA-256 SASL path | Present | Present | Present | Present | Present | Present |

Additional interpretation:

- D8-91 `LoadKingbaseInfo` reads `server_version`, queries `show database_mode`, and then loads types. D9-107 prefers `database_mode` already supplied by the handshake and queries only for NoMode. Detection must accommodate both verifiable entry paths.
- D9-63/82a contain YM/DS attributes, but the dedicated classes above were not found. An enum entry and a dedicated codec are not equivalent evidence.
- D9-100 and D9-107 both declare `datetime2=7690`. D9-100 shares a mapping through SqlServerDateTimeHandlerFactory; D9-107 adds a separate DateTime2 factory. An absent dedicated class or literal name `sqlserver_datetime2` does not establish a missing binding. The server's actual name, OID, and wire format still require LIVE validation.
- `Int128Handler` is the official class name. Some default CLR paths still use `long`; the name does not establish support for `System.Int128` or all 128-bit values.
- All six implementations contain SM3 and SCRAM-SHA-256 paths. This does not establish SCRAM-SM3, SM4, or Chinese cryptographic-standard TLS support. The static SASL branch explicitly accepts SCRAM-SHA-256; register each algorithm separately.
- The V8 EF DLL references EF Core 2.2.4 and Kdbndp 4.0.7.1222. This project plans independent EF8/9/10 providers rather than reuse of that older provider.
- D8-91 references older BouncyCastle and other dependencies; D9-107 additionally references NLog. Select dependencies for the independent implementation's needs rather than copying those DLL dependencies.

Additional BODY observations retained in the JSON inventory: D9-107's default Int128Handler read discards the first Int64; its default DateTime2 read includes local-time conversion and a fixed eight-hour subtraction; and `GetSchema("DataTypes")` directly throws `NotSupportedException`. These observations relate to T17, T12, and M01 respectively. Differential testing must not automatically treat those official paths as correct.

## 3. Server Version, Deployment, and Mode Registration

### 3.1 Candidate Acceptance Combinations

These are target or unconfirmed combinations, **not an existing support table**. "Unconfirmed" means neither supported nor unsupported. "Full-mode" describes a server product deployment; register each actual database mode separately. A single physical connection need not switch among all four semantics.

| Candidate profile | Product generation | Deployment | Mode | Baseline evidence | Acceptance evidence |
| --- | --- | --- | --- | --- | --- |
| P9-F-PG | V9 | Full-mode (target deployment) | PG | User target; W2 states driver modes only and does not establish deployment form. | Not implemented / Not accepted |
| P9-F-OR | V9 | Full-mode | Oracle | Same as above. | Not implemented / Not accepted |
| P9-F-MY | V9 | Full-mode | MySQL | Same as above. | Not implemented / Not accepted |
| P9-F-MS | V9 | Full-mode | SQL Server | Same as above. | Not implemented / Not accepted |
| P9-S-OR | V9 | Standalone Oracle-compatible edition | Oracle | User materials and target; register the exact server. | Not implemented / Not accepted |
| P9-S-MY | V9 | Standalone MySQL-compatible edition | MySQL | W20 documents a MySQL-compatible release; PG endpoint availability and the exact target build still require a real profile. | Not implemented / Not accepted |
| P9-S-MS | V9 | Standalone SQL Server-compatible edition | SQL Server | Same as above. | Not implemented / Not accepted |
| P9-S-MY-WIRE | V9 | Exceptional standalone MySQL-compatible deployment; PG endpoint unavailable | MySQL via explicit MySQL wire | User requirement; W20 documents MySQL protocol in C010, but not PG being disabled or a fixed port. Native capabilities remain U07; W14-W16 are protocol references. | Not implemented / Not accepted |
| P9-F-MY-WIRE | V9 | Full-mode MySQL-endpoint availability remains unconfirmed | MySQL via explicit MySQL wire | Separate candidate only if such an endpoint exists; do not infer it from all-mode naming. | Unconfirmed |
| P9-S-PG | V9 | Existence of a standalone PG deployment is unconfirmed. | PG | Do not infer its existence from standalone editions for the other three modes. | Unconfirmed |
| P8-OR | V8R6C9B14 onward | Register the actual installed deployment. | Oracle | W1 installation text identifies it. | V8 stage not started |
| P8-PG | Same as above. | Same as above. | PG | W1 installation text identifies it. | V8 stage not started |
| P8-MY | Same as above. | Same as above. | MySQL | W1 installation text identifies it. | V8 stage not started |
| P8-MS | Same as above. | Unconfirmed | SQL Server | DLL symbols alone do not establish this server combination. | Unconfirmed |

The original candidates above describe the normal PG path. The added MY-WIRE candidates cover exceptional explicit MySQL connections; they do not replace MySQL-mode PG coverage. Profile applicability is protocol-specific. PG COPY/notifications/refcursor/OID LOB APIs are outside initial MySQL-wire coverage; record N/A with a concrete applicability reason, never because tests are missing. Required shared ADO.NET surfaces and all four newly added R items remain mandatory for accepted MySQL-only profiles. No native TDS path is proposed.

The local R1/R2/R3/R4 package names remain clues for selecting server samples. They are not profiles for servers that have already run. Confirm V9 subversion coverage in V9-0. At minimum, register differences for every distinct full product version obtained; do not extrapolate all versions from a single V9 instance.

### 3.2 Required Fields for Every Real Profile

| Field | Recording requirement |
| --- | --- |
| profileId / parentCandidate | Unique environment identity and corresponding candidate above; do not mark a candidate as passed by overwriting its row. |
| productVersionRaw / parsedBuild | Exact full product version response, parsed result, and query source; retain unknown values as unknown. |
| serverVersionRaw | Raw handshake value, stored separately; do not present it as the full product build number. |
| deployment / mode | Confirmed deployment and actual database mode; deployment may be Unknown if unconfirmed, with its impact recorded. |
| wireProtocol / endpoint | Actual selected protocol, configured port, endpoint reachability, and backend routing; MY-WIRE acceptance must prove no PG endpoint is used. Ports are not protocol evidence by themselves. |
| mode policy / provenance | Configured CompatibilityMode, effective mode, server-reported value if available, and discovery/query source; explicit selection is not automatic confirmation. |
| native capabilities | Verified authentication plugins, TLS/collation, reset path/effects, cancellation method/permissions, catalog queries, and feature applicability for the selected wire protocol. |
| database settings | Encoding, case/collation settings, time zone, and relevant session parameters. |
| auth / TLS | Algorithm, TLS verification policy, and summary of certificate test configuration; no credentials or private keys. |
| topology / platform | Topology such as standalone or primary/standby, server OS/CPU, and client OS/CPU/runtime. |
| driver / artifacts | New driver commit, package version/hash, and full hash of the official comparison DLL. |
| testResults | Feature ID, test ID, input boundaries, observed behavior, failure classification, log location, and cleanup result. |

Track `implementation`, `simulation`, and `liveByProfile` separately; a single check mark must not represent all three. Classify differences as client defects, confirmed server limitations, permission/configuration limitations, missing environments, or unknown causes, so missing environments are not attributed to the client.

## 4. Feature Inventory and Acceptance Requirements

Every feature below was unimplemented and unaccepted at the 2026-11-23 baseline. Record subsequent status and supporting evidence only in [development progress](kbconnector-development-progress.md). R/C/L classifications are proposals in this baseline. Stage identifiers correspond to design section 12.

### 4.1 Connections, Detection, Authentication, and Transport

| ID | Feature | Official evidence / version differences | New driver contract and key acceptance requirements | Scope / stage |
| --- | --- | --- | --- | --- |
| C01 | ConnectionStringBuilder / factory | L1 SYM, W4 | Strongly typed configuration, aliases and conflict validation, password redaction; DbProviderFactory registration. | R / V9-1 |
| C02 | TCP, DNS, synchronous/asynchronous Open | L1 BODY | Overall timeout covers pool waiting and all initialization; cancellation releases resources. PG remains the normal path in every mode. Exceptional MySQL-only installations use explicit WireProtocol=MySql with the actual port (C20-C21). Reject mismatched replies with bounded diagnostics; no protocol auto-selection/switching or port scanning/fallback. Port numbers alone do not establish protocol. | R / V9-1 |
| C03 | Full product version detection | L1 KingbaseESVersion property derives from parsed server_version; U02 | Property names do not establish full build numbers. Bounded read-only detection retains raw responses; freeze SQL in V9-0. | R / V9-0/1 |
| C04 | Automatic detection / explicit CompatibilityMode | D8 queries; D9-107 prefers handshake. Explicit selection is this project's contract. | CompatibilityMode defaults to Auto: obtain mode after authentication using the selected backend's verified discovery (PG parameters/SHOW database_mode; native MySQL bootstrap under U07). Explicit PostgreSql/Oracle/MySql/SqlServer selects driver compatibility rules; reject a conflicting server-reported mode. Never switch protocols or change the server mode. Preserve configuration/effective/server-reported identities, isolate pool policies, and validate required catalog/codecs in both paths. Auto must fail on an unknown mode rather than defaulting to PG. | R / V9-0/1 |
| C05 | Full-mode / standalone edition compatibility | User target; W2 only states driver modes, W3 does not establish deployment. | Shared core; use Unknown for unconfirmed deployment and do not infer protocol from deployment. | R / V9-0/3 |
| C06 | Session encoding / parameter status | L1 BODY | Strict encoding, session/lexical parameter updates and plan invalidation, temporal wire settings such as integer_datetimes where applicable; no stale codecs or binding plans after changes. Retire sessions with untrackable state. | R / V9-1 |
| C07 | TLS, CA, hostname validation | L1/W5; documented defaults differ from DLL defaults. | Platform TLS; proposal to encrypt and verify certificate chains/hostnames by default. Require explicit configuration for plaintext, fallback, or skipped verification; include negative certificate tests. | R / V9-1/3 |
| C08 | Password / MD5 authentication | L1 BODY | Use only the algorithm requested by the server; no proactive downgrade or secret logging. | R / V9-1 |
| C09 | SCRAM-SHA-256 | SASL paths in all six samples; W32-W33. | Validate nonce, iteration-count bounds, server signature, and malformed challenges. | R / V9-1 |
| C10 | Separate SM3 authentication | BODY in all six samples. | Separate algorithm contract; validate challenges/encoding and live interoperability independently. | R / V9-1/3 |
| C11 | SCRAM-SM3 / SM4 | W1 lists options; complete corresponding driver implementations were not established by this analysis. | Gather evidence and accept each separately; C10 results do not establish either capability. | C / V9-3 |
| C12 | Chinese cryptographic-standard TLS | Not established by an SM3 class; U05. | Define support boundaries after establishing target server, platform, and cryptographic library paths. | C / V9-3 |
| C13 | GSS/SSPI, client certificates | L1 has related symbols; platform and flow need confirmation. | Separate authentication from mutual TLS; register Windows/Linux differences individually. | C / V9-3 |
| C14 | Unix domain socket | W4, W10 | Platform availability, paths/permissions/cancellation; TCP tests do not substitute for these tests. | C / V9-3 |
| C15 | Keepalive / broken-connection detection | L1, W12 | SQL keepalive runs only when idle; long queries use TCP/read timeouts. Broken connections trigger state changes and pool invalidation handling. | R / V9-3 |
| C16 | Credential sources and rotation | W5 describes password files/callbacks; establish specific DLL APIs separately. | Explicit password / DataSource callback; invoke for new authentication (physical creation or verified reauthenticating reset), not ordinary rents; cancellation, rotation, identity isolation, and secret-free logs. | R / V9-1/3 |
| C17 | Passfile / environment variables | KESPASSFILE in W4/W5. | Explicit opt-in, source precedence, file permissions/escaping/host matching; do not copy PG environment-variable behavior by default. | C / V9-3 |
| C18 | Startup / session configuration | W4 Application Name/Search Path/Client Encoding/Timezone. | Allowlist, encoding/escaping, application points at startup/reconnect, and pool reset; no arbitrary SQL injection entry point. | R / V9-1/2 |
| C19 | Connection state / ChangeDatabase | L1 BODY, W13 | Close/reopen, StateChange, Dispose; reject changes with active resources and cross-database changes on DataSource-created connections. Direct construction may reopen with new catalogs/pool identity; old session returns only to its original owner. Define failure states. | R / V9-1/2 |
| C20 | Wire protocol selection / endpoint contract | User normal-PG/exceptional-MySQL-only requirement; design section 3. | WireProtocol defaults to PostgreSql; MySql is explicit and requires Port. Separate protocol from CompatibilityMode, isolate pools, reject unsupported combinations and mismatched handshakes without auto-switching or port scanning. | R / V9-1 |
| C21 | MySQL handshake / TLS / authentication | W14; Kingbase capabilities remain U07. | Exceptional endpoint only: bounded HandshakeV10/capabilities/packets, plugin switch and continuation, platform TLS at the correct stage; reject unsupported plugins/downgrades. Record actual Kingbase identity/mode/version queries and negative authentication tests. | R / V9-2 |

### 4.2 Commands, Parameters, Results, and Routines

| ID | Feature | Official evidence / version differences | New driver contract and key acceptance requirements | Scope / stage |
| --- | --- | --- | --- | --- |
| Q01 | Simple / Extended Query | L1 BODY; PG v3 style with extensions. | PG Parse/Bind/Describe/Execute/Sync state machine; mode selection does not select a protocol. Q20 owns native MySQL execution; TDS remains outside scope. | R / V9-1 |
| Q02 | Nonquery / scalar / reader | L1 SYM/BODY | Consistent row counts, NULL, empty results, trailing errors, and RecordsAffected across modes. | R / V9-1 |
| Q03 | Named and positional parameters | L1 BODY | Proposed @name/:name; PG-native $n and MySQL positional ? apply only to their backend. Lex comments/quotes/blocks using verified session escape/quote settings, not mode alone. Include lexical state in plan-cache identity; distinguish user variables, duplicate repeated MySQL binds, and reject mixed/unbound placeholders and unhandled setting changes inside multi-statement commands. | R / V9-1 |
| Q04 | Parameter types, precision/scale, typed NULL | L1 SYM, W6 | Explicit type precedence; internal snapshots do not mutate user parameters; no silent rounding. | R / V9-1 |
| Q05 | Synchronous/asynchronous execution, cancellation, and timeouts | L1 BODY | All I/O paths support cancellation; PG cancel waits for cancellation-channel closure plus original-channel synchronization, not only a successful send. MySQL cancellation/abort follows its verified path; no late cancellation of a later command/lease and no SQL replay. | R / V9-1/3 |
| Q06 | Multiple results and statements | L1 BODY | NextResult, per-statement parameters/row counts/error locations; confirm PG ReadyForQuery or the verified MySQL terminal status after closure. | R / V9-2 |
| Q07 | Explicit Prepare / Unprepare | L1, W8 | Type signatures, connection ownership, transaction errors, and schema invalidation; no fallback by interpolating SQL. | R / V9-2 |
| Q08 | Automatic preparation | L1, W8 | Bounded cache, eviction and pool reset, name collisions/concurrency; no unbounded server resource use. | R / V9-2 |
| Q09 | Sequential batch / DbBatch | Proposed new public API. | Standard base class on modern assets; sequential execution without automatic atomicity; precise partial-success reporting. | R / V9-2 |
| Q10 | Function/procedure resolution and overloads | L1 CommandType branches differ by mode. | Generate SELECT/CALL/blocks from routine kind/catalogs; reject ambiguity before sending. | R / V9-0/2 |
| Q11 | Input/Output/InputOutput/ReturnValue | L1 SYM/BODY, U03 | Accept all four directions separately; publish outputs atomically after the successful response tail, preserving original values on failure. | R / V9-2 |
| Q12 | Oracle package routines | L1 related call paths require live confirmation. | Separate tests for package names, overloads, NULL, and scalar/composite outputs. | C / V9-2 |
| Q13 | refcursor / multiple cursors | L1 type and call paths. | Transaction/session resource ownership, parent/child reader closure order; invalidation must not return a readable object. | C / V9-2 |
| Q14 | SQL Server routine outputs / return codes | U03 pending evidence; proprietary type classes do not establish proprietary result messages. | First establish whether result sets/standard messages can express the values, then decide on extensions; do not substitute the first cell for ReturnValue. | R / V9-0/2 |
| Q15 | SequentialAccess / Stream / TextReader | L1 Reader SYM | Stream large values; partial reads, NULL, cross-column positioning, and early closure. | R / V9-2 |
| Q16 | CommandBehavior / reader contract | L1 SYM | Actual semantics of SchemaOnly, KeyInfo, SingleRow/SingleResult, and CloseConnection. | R / V9-2 |
| Q17 | SQLSTATE / Notice / deterministic exceptions | L1 BODY plus proposed new classification. | Error fields, execution phase, whether a request was sent / outcome is uncertain; redact business content by default. | R / V9-1/3 |
| Q18 | Synchronization after Cancel / damaged connection handling | L1 state-machine analysis plus new contract. | Drain limits, failed transaction state; connections broken mid-packet must not return to the pool. | R / V9-1/3 |
| Q19 | DeriveParameters | L1 CommandBuilder symbols; proposed new contract. | Resolve types/directions/order/overloads from catalogs; define parameter collection replacement; lack of permission must not appear as an empty signature. | C / V9-2 |
| Q20 | MySQL commands / prepared execution / results | W15; independent native-endpoint contract. | COM_QUERY and prepared binding/execute/close; parameterized execution uses prepared binding even when automatic handle caching is disabled. Streaming parameter data where exposed; text/binary rows, negotiated OK/ERR/EOF, multiple results, counts/identity/status, native error fields, cancellation or physical retirement. No PG messages or unsafe interpolation. | R / V9-2 |

### 4.3 Transactions

| ID | Feature | Official evidence / version differences | New driver contract and key acceptance requirements | Scope / stage |
| --- | --- | --- | --- | --- |
| X01 | Local Begin/Commit/Rollback | L1, W7; do not copy the manual's "one transaction per process" wording. | One transaction per connection, without restricting other connections; track state. Loss of commit acknowledgement means an uncertain outcome, without retry. | R / V9-1 |
| X02 | Isolation levels and read-only semantics | L1, W7, W30 | Test actual semantics by mode/version; do not silently substitute unsupported levels. | R / V9-1/2 |
| X03 | Savepoint / RollbackTo / Release | L1 SYM/BODY | Identifier escaping, recovery from failed transactions, and live-established effects of routines/DDL. | R / V9-2 |
| X04 | Single-resource TransactionScope | L1, W7/W10 | Explicit/automatic enlistment, opt-out, async flow, and transaction-affine pending-session reuse for sequential logical opens in the same pool/transaction, including Maximum Pool Size=1. Exclusive leases, transaction completion races, concurrent-open promotion, timeout rollback, and per-protocol applicability. | C / V9-3 |
| X05 | Distributed / two-phase transactions | L1 volatile resource enlistment; W7 explicitly lacks crash recovery; U05. | Platform transaction manager and backend-specific preparation/recovery: verified PG PREPARE TRANSACTION family, or separately established native MySQL XA behavior. No PG SQL on MySQL wire; no reliable-recovery claim without durable recovery. U05/U07 determine applicability. | C / V9-3 |

### 4.4 Types and Codecs

PG OID requirements concern PG profiles. T33 supplies the separate MySQL type/catalog identity; shared type-family contracts apply where that native endpoint actually exposes the type. Do not mark PG-only types implemented on MySQL wire by inventing conversions.

Every item in this section must cover input binding, result reads, NULL, and error boundaries; add output parameters, arrays, and COPY where applicable. Passing one path does not establish the others.

| ID | Type family | Official evidence / version differences | New driver contract and key acceptance requirements | Scope / stage |
| --- | --- | --- | --- | --- |
| T01 | Type catalog / OID resolution | L1 BODY such as LoadBackendTypes. | Session catalog, qualified names, base/element/member relationships; do not misbind custom types with the same name. | R / V9-1 |
| T02 | bool / BIT / bit strings | MySqlBitStringHandler availability differs. | Distinguish booleans from multibit values; BIT(1/n), leading zeros, sign, and length. | R / V9-1/2 |
| T03 | Signed integers | L1 SYM/BODY | Minimum/maximum values at every width, overflow, and tinyint/mediumint semantics. | R / V9-1 |
| T04 | Unsigned integers | uint4/uint8 attribute availability differs. | Full UInt32/UInt64 ranges without implicit truncation through long/decimal. | R / V9-2 |
| T05 | NUMERIC/NUMBER/DECIMAL | L1, W6 | Explicit failure outside the default decimal range; lossless round trips using provider-specific high-precision values. | R / V9-1/2 |
| T06 | SQL Server NUMERIC/MONEY | Dedicated classes found only in D9-107. | Do not infer CLR SqlTypes mappings from a mode name; compare precision/scale/negative values/overflow. | R / V9-2 |
| T07 | float/double / special values | L1 SYM/BODY | NaN, ±Infinity, negative zero, rounding, and differences between exact bit patterns and text. | R / V9-1 |
| T08 | char/varchar/text/nchar/nvarchar | L1, W6 | Unicode, fixed-length padding, byte/character lengths, case metadata, and strict encoding. | R / V9-1/2 |
| T09 | bytea/binary/varbinary/RAW | Two sets of binary OIDs. | Empty and large values, zero bytes; select binary/text formats using the actual catalog. | R / V9-1/2 |
| T10 | PG DATE/TIME/TIMESTAMP/TZ | L1 temporal handling paths. | Kind, time zones, DST, infinity/BC/values outside CLR ranges, and precision; provider-specific fallback values. | R / V9-1/2 |
| T11 | Oracle DATE | oracle_date attribute found only in D9-107. | Preserve hours/minutes/seconds; binding rules for mode aliases and proprietary OIDs must not truncate to a date. | R / V9-2 |
| T12 | SQL Server DATETIME/DATETIME2 | datetime attribute differences; D9-107 datetime2=7690 / dedicated factory. | Date boundaries, ticks, rounding, and wire format; test UTC, zones other than UTC+8, and DST; do not copy fixed-offset conversions. | R / V9-2 |
| T13 | MySQL DATE/DATETIME/TIMESTAMP | L1 mode branches. | Explicit provider-specific values for zero dates and values outside CLR ranges; no silent conversion to MinValue. | R / V9-2 |
| T14 | MySQL TIME | L1 type clues; live confirmation required. | Negative values, durations beyond 24 hours, microseconds, and range; TimeOnly cannot represent every value. | R / V9-2 |
| T15 | INTERVAL / YM / DS | Attribute presence differs from dedicated class availability. | Preserve months, days, and time separately; no fixed month length; negative and high-precision values. | R / V9-2 |
| T16 | YEAR | 7024 versus 7025. | Catalog resolution, boundaries, and zero semantics; do not derive OIDs directly from version strings. | R / V9-2 |
| T17 | int16 / wide integers | D9-107 int16=4659; default read consumes two Int64 values and returns only the second. | Establish sign/byte order; nonzero high bits, positive/negative boundaries, and narrowing overflow; lossless provider-specific values, with Int128 mapping decided separately. | C / V9-2 |
| T18 | UUID | L1, W6 | Guid byte order, text/binary, NULL/arrays. | R / V9-2 |
| T19 | JSON / JSONB | L1, W6 | Text and version prefixes, Unicode; default to text and explicitly register structured CLR mappings. | R / V9-2 |
| T20 | XML | L1, W6 | String/stream representation without automatic external entity resolution; encoding and large values. | R / V9-2 |
| T21 | Arrays | L1 array handler. | Multiple dimensions, lower bounds, NULL elements, empty arrays, custom elements, and size budgets. | C / V9-2 |
| T22 | range | L1 range handler. | Empty/infinite bounds, inclusive/exclusive endpoints, and subtypes; acceptance depends on server availability. | C / V9-2 |
| T23 | enum / domain / MySQL SET | L1 type catalog/mappings; W20 lists ENUM and SET. | Explicit registration, qualified names, domain base types, enum/SET labels and combinations, empty versus NULL, and constraint errors. Confirm metadata and text/binary behavior per backend; do not treat SET as integer flags without a verified contract. | C / V9-2 |
| T24 | composite / record | L1 type catalog/handlers. | Field order, NULL, nesting, and result contract for anonymous record values. | C / V9-2 |
| T25 | Network addresses | L1 type attributes/handlers. | Establish inet/cidr/mac and other types individually; text/provider-specific values and invalid input. | C / V9-2 |
| T26 | Geometry / full-text search / hstore and others | L1 attributes/symbols. | Separate subtests for each type; enum presence does not establish completion; register extension installation separately. | C / V9-2 |
| T27 | Unknown types and catalog reload | New failure/reload contract; L1 catalog clues. | Explicit text path; reject unknown binary formats; explicit reload after drop/recreate or OID reuse. | R / V9-2 |
| T28 | GetFieldValue / typed getters | L1 Reader SYM. | Consistency among GetValue/GetFieldType/provider-specific getters; default CLR types must not vary by row. | R / V9-1/2 |
| T29 | PG MONEY | W6, L1 MoneyHandler. | Separate from SQL Server money in T06; accept scale, negative values, overflow, locale, and text/binary paths independently. | R / V9-2 |
| T30 | Catalog / identifier types | W6, L1: oid/xid/cid/oidvector/name/internal char. | Exact mappings for uint/vectors/single bytes and related forms; business integer support must not leave metadata-dependent types uncovered. | R / V9-1/2 |
| T31 | SQL Server SMALLDATETIME/DATETIMEOFFSET/TIME | L1 name/mapping clues; live existence unconfirmed. | Establish range, precision, and offset preservation separately from T12; do not extrapolate from the DateTime2 class. | C / V9-2 |
| T32 | Custom mappings / name translation | L1 TypeMapper/NameTranslator symbols. | Explicit DataSource-level registration; qualified names, conflicts, enums, and composite field order; no cross-pool contamination through mutable global mappings. | C / V9-2 |
| T33 | MySQL wire types / Kingbase catalog identity | W14-W15; actual catalog facts remain U07. | Type codes/flags/collation/length/precision and native catalog metadata; exact shared/provider values, independent text/binary input/result/NULL boundaries. No fabricated PG OIDs or reuse of PG binary codecs. | R / V9-2 |

### 4.5 COPY, Large Objects, and Notifications

B01-B04 and B06 concern verified PG profiles; initial MySQL-wire coverage does not expose these PG protocol APIs. B05 ordinary LOB binding requires separate native-endpoint acceptance.

| ID | Feature | Official evidence / version differences | New driver contract and key acceptance requirements | Scope / stage |
| --- | --- | --- | --- | --- |
| B01 | Text COPY import/export | L1, W9 | Encoding, NULL, delimiters/escaping, error recovery, cancellation, and exclusive resource ownership. | C / V9-2 |
| B02 | Binary COPY import/export | L1, W9 | Codecs, row/column counts/NULL/header; explicit Complete confirms success, Dispose before completion cancels; inspect outer transaction state independently. | C / V9-2 |
| B03 | Raw COPY stream | L1, W9 | Explicit raw-stream and closure semantics; truncation must not permit dirty-connection reuse. This is not database backup or cross-version restore. | C / V9-2 |
| B04 | LargeObjectManager / OID LOB | L1 SYM/BODY | Create/read/write/seek/delete and transaction ownership; accept separately from bytea/BLOB. | C / V9-2 |
| B05 | BLOB/CLOB/NCLOB binding | L1 type/reader clues. | Establish whether values use ordinary binding, streams, or locators on real servers; length and partial reads/writes. | R / V9-2 |
| B06 | LISTEN/NOTIFY / Wait | L1, W11 | Command/Wait-driven dispatch without implicit background push; timeout/cancellation, callback reentrancy rejection, LISTEN cleanup, and no guaranteed replay after disconnection. | C / V9-2/3 |
| B07 | Logical replication / CDC API | Not established by the supplied materials. | W9's "copy" refers to COPY; research CDC protocol and product requirements separately. | L / Later |
| B08 | DataTable/DataSet COPY convenience API | D9-107 CopyFromDataset SYM. | May be proposed separately on top of B02; field order/types/NULL/identifiers and transaction boundaries; distinct from Update in M08. | L / Later |

### 4.6 Pooling, High Availability, and Resources

| ID | Feature | Official evidence / version differences | New driver contract and key acceptance requirements | Scope / stage |
| --- | --- | --- | --- | --- |
| P01 | Pool identity / capacity / waiting | L1 pool paths. | Built-in provider pool, Pooling=true/false, private DataSource versus normalized direct-construction pools; include protocol/mode/credential/mapping identity. Atomic limits count opening/resetting/retiring/transaction-reserved/old-generation sessions; same-transaction eligible reuse precedes ordinary waits. Fair waits, Open budget, cancellation handoff, and no leaks. | R / V9-1/3 |
| P02 | Session reset and dirty-connection disposal | L1, W31 reset paths plus stricter contract. | Reset before reuse under a bounded cleanup budget independent of caller cancellation. Verify transaction/autocommit/isolation, database/roles, settings, temporary state, LISTEN where applicable, and prepared-cache invalidation. PG reset plus idle ReadyForQuery; verified MySQL reset/change-user or close. No unsafe reset bypass. | R / V9-2/3 |
| P03 | Lifetime / pool clearing / generations | L1 pool APIs plus lease proposal. | Lazy minimum, idle/total expiry, ClearPool/ClearAllPools/DataSource clearing, generations, and owner disposal. Wake waiters on disposal; active leases finish then retire and still count toward capacity. Credential rotation/new authentication, safe reclamation of unused direct-pool registry entries, and old-child rejection; no forced interruption or indefinite disposal wait. | R / V9-3 |
| P04 | Multiple hosts / failover | L1 host failover BODY. | Select hosts only when creating connections; redetect nodes with different patch versions; no transaction migration or SQL replay. | R / V9-3 |
| P05 | Primary/read-only targets and selection policy | L1 connection configuration/multiple-host clues. | Select using confirmed role queries; classify post-switch invalidation and permission errors. | C / V9-3 |
| P06 | One active operation per connection | L1 operation-state clues plus new contract. | Exclusive reader/COPY/command and cleanup/cancel ownership; concurrent use fails explicitly. Old callbacks/readers cannot touch a new lease. Thread-safe pool/DataSource does not imply concurrent Connection/Command/Reader use. | R / V9-1 |
| P07 | Modern query multiplexing | Not established by the official Multiplexing name. | Separate later state-machine assessment; the first public contract does not promise concurrent sharing of a session. | L / Later |

### 4.7 Schema and Foundations for Future EF Providers

| ID | Feature | Official evidence / version differences | New driver contract and key acceptance requirements | Scope / stage |
| --- | --- | --- | --- | --- |
| M01 | Standard GetSchema collections | L1 BODY; D9-107 DataTypes branch throws NotSupportedException. | The new driver must provide the five standard collections with fixed structures to close the contract gap; do not claim they are all implemented by the official driver. | R / V9-1/2 |
| M02 | Tables/Views/Schemas/Columns | L1 metadata queries. | Isolate catalog differences; explicit qualified-name, case, length, nullability, and permission semantics. | R / V9-2 |
| M03 | PK/FK/Indexes/IndexColumns | L1 catalog clues. | Composite column order; extend unique/expression/filtered/included-column metadata where available; do not fabricate missing fields. | R / V9-2 |
| M04 | Sequences/identity/generated | L1 catalog clues plus proposed EF contract. | Distinguish default expressions from generation attributes; precise column metadata. | R / V9-2 |
| M05 | Procedures/Functions/Parameters/UDT | L1 catalog paths. | Mode, packages/overloads, direction, order, precision/types; support Q10/T23/T24. | R / V9-2 |
| M06 | GetColumnSchema/GetSchemaTable | L1 Reader SYM. | Length/precision/scale/base columns/keys/nullability; ordinary reads do not query catalogs per row. | R / V9-1/2 |
| M07 | Restrictions / quoting / permissions | L1 schema paths plus new contract. | Bind filter values; escape schema/table identifiers; separate empty/NULL/case/no-permission tests. | R / V9-2 |
| M08 | DataAdapter/CommandBuilder | L1 SYM. | DataTable/DataSet, optimistic concurrency, key columns, quoting, and valid generated commands in every mode. | R / V9-2 |
| M09 | Result and error contracts required by EF | New foundational contract. | Stable generated-value returns, affected-row counts, concurrency/constraint SQLSTATEs, savepoints, and type information. | R / V9-2/3 |
| M10 | Databases/Users/Constraints and collection aliases | L1 GetSchema BODY; official names such as Schemata/PrimaryKey. | Additional database/user/unique-constraint/constraint-column metadata; canonical Schemas/PrimaryKeys names, discoverable legacy aliases, and restriction order. | R / V9-2 |
| E01 | Separate EF Core 8/9/10 providers | User's later target; L3 is older reference material only. | Develop by EF major version/TFM; core has no EF runtime dependency. | L / Later EF |
| E02 | Offline dialect profiles / model caching | Proposed new architecture. | Select SQL dialect without a connection; include profile in model cache keys and validate the target on connection. | L / Later EF |
| E03 | LINQ/updates/migrations/reverse engineering | Later scope, not ADO functionality. | Separate EF feature matrix; passing M01–M09 does not establish EF support. | L / Later EF |

### 4.8 Engineering, Multiple Targets, Diagnostics, and Release

| ID | Feature | Official evidence / version differences | New driver contract and key acceptance requirements | Scope / stage |
| --- | --- | --- | --- | --- |
| G01 | slnx / CPM / unified versions | User requirement, L2 reference. | Shared props, no individual versions on PackageReference entries; SDK supports net10/slnx. | R / V9-1 |
| G02 | Single core package / optional DI package | Independent implementation proposal; L2 organization reference; user explicitly requires the separate DI deliverable (2026-11-24). | Do not split the core by mode/server version; deliver KingbaseConnector.DependencyInjection, optional for consumers, and validate DataSource lifetime and service registration. | R / V9-1/2 |
| G03 | Package consumption across TFMs | User requirement, L2. | Actually select and run netstandard2.0, netstandard2.1, net8.0, net9.0, and net10.0 assets separately; inspect dependency graphs and APIs. | R / V9-3 |
| G04 | Windows/Linux x64 | Platform proposal in this baseline. | Validate TFM/network/TLS/authentication/pool combinations; compilation alone is insufficient. | R / V9-3 |
| G05 | ARM64 / Framework hosts | No project validation yet at baseline. | Separate platform profiles; a Standard reference does not establish host certification. | C / V9-3 |
| G06 | Logging / Activity / Meter | Proposed new diagnostics. | Core logging abstraction; pool/connection/execution metrics; omit business SQL/parameters/secrets by default. | R / V9-1/3 |
| G07 | Security / failure / resource stress | New implementation requirements. | Malicious lengths, negative authentication tests, broken connections/partial packets, wait cancellation, lost commit acknowledgements, and leaks. | R / V9-1/3 |
| G08 | Differential / live test framework | New validation requirements; L1 comparison driver. | Isolate official DLLs in separate processes; official results may be classified as defects; tests require explicit opt-in. | R / V9-0/3 |
| G09 | NuGet / provenance / licensing / compatibility notes | Release requirements, U06. | No bundled official DLLs/decompiled source; reproducible packages and support tables; confirm licensing before release. | R / V9-3 |
| G10 | Trimming / NativeAOT | No evidence in this baseline; separate enhancement proposal. | Assess reflection registration and dynamic-code paths separately; do not claim unverified compatibility. | L / Later |
| G11 | Configuration and default-value contract | W4/W5 defaults differ from L1 properties; retain evidence in the official DLL inventory. | List names/aliases/units/defaults/application points/migration differences for every setting; test unknown keywords, sensitive data, and password rotation. | R / V9-1/3 |

### 4.9 Later V8 Extension

| ID | Feature | Baseline materials and differences | Implementation and acceptance requirements | Scope / stage |
| --- | --- | --- | --- | --- |
| V01 | V8R6C9B14 identity / mode / catalog | D8-91, W1; SQL Server combination unconfirmed. | Register full profiles; reuse the core and add only rules for real differences. | V8 / V8-1 |
| V02 | All V8 type / authentication / routine paths | D8 is not a strict feature subset of D9. | Rerun C/Q/X/T/B/P/M individually, especially OIDs, intervals, dates, and outputs. | V8 / V8-1 |
| V03 | Earlier V8 subversions | No additional server materials at baseline. | Add evidence and live tests after defining target release lines; do not extrapolate to all V8 versions. | V8 / V8-2 |
| V04 | V9 regression after V8 extension | Shared core package requirement. | Preserve accepted V9 profiles; do not reuse rules/caches incorrectly across endpoints. | V8 / V8-2 |

## 5. Proposed TFM API Differences

This is a proposed API surface. Freeze exact signatures before implementation; compilation differences must not conceal behavioral differences.

| Capability | netstandard2.0 | netstandard2.1 | net8.0 / net9.0 / net10.0 |
| --- | --- | --- | --- |
| Basic ADO.NET, synchronous/Task-based asynchronous APIs | Standard APIs. | Standard APIs. | Standard APIs. |
| DataSource | Provider-specific entry point with the same name; no DbDataSource base class. | Same as netstandard2.0. | Derives from DbDataSource. |
| Sequential batching | Provider-specific batch entry point; no DbBatch base class. | Same as netstandard2.0. | Derives from DbBatch. |
| Asynchronous disposal | Explicit provider-specific asynchronous disposal method; the BCL interface is not the default baseline. | Use IAsyncDisposable available in this asset. | IAsyncDisposable / BCL overrides. |
| DateOnly/TimeOnly | No corresponding BCL types; use provider-specific temporal values / older CLR types. | Same as netstandard2.0. | Explicit mappings may be offered; keep the default policy explicit. |
| Int128 | Provider-specific wide-integer value. | Same as netstandard2.0. | Add explicit mapping only after establishing the server wire format and range. |
| Activity/Meter/logging | Establish compatibility packages and minimum versions through consumer tests. | Same as netstandard2.0. | Corresponding APIs; dependencies managed through CPM. |

Consumer tests for Standard assets must not automatically select net8/net10 assets. CPU architecture and TFM are separate dimensions; each platform support claim requires its own test record.

## 6. Key Acceptance Test Groups

| Test group | Related features | Minimum coverage |
| --- | --- | --- |
| A01 Identity initialization | C03–C06, T01, V01 | Omitted/Auto/explicit CompatibilityMode, handshake with/without mode, explicit selection with no reported mode, conflicting server-reported mode, missing/new-format full version, isolated pool policies, no catalog permissions, initialization timeout. |
| A02 Type differences | T09, T11, T12, T16, T17 | Actual catalog OIDs differing from both sets of official constants; same-named types in different schemas, unknown OIDs, no replay of commands already sent. |
| A03 Lossless numbers / temporal values | T02–T17, T29–T31 | Boundary integers/nonzero high bits in wide integers, high precision, negative intervals, UTC/zones other than UTC+8/DST, zero dates, values beyond CLR ranges, money and catalog types; inputs/results/outputs in all four modes. |
| A04 SQL lexing / parameters | Q03, Q04, Q06 | Apparent parameters in quotes/comments, dollar quoting, Oracle blocks, ::, repeated names, typed NULL, type conflicts, mixed placeholders; PG standard_conforming_strings and verified MySQL escape/quote flags before/after changes, cache invalidation, and unsafe multi-statement setting transitions. |
| A05 Routines / cursors | Q10–Q14, Q19 | Functions/procedures/packages/overloads, derived parameter collection replacement, all four directions, multiple results/cursors, original outputs retained on failure tails, access after transaction end. |
| A06 Cancellation / uncertain outcomes | Q05, Q17, Q18, X01 | Cancellation before sending, disconnection after sending, delayed cancellation-channel closure after original results, no new command/lease before the barrier, late Cancel, timed-out COMMIT, failed transactions, partial packets, synchronization failures. |
| A07 Pools / reset / primary-standby | P01–P06 | Cancellation at pool capacity, pool clearing racing with old leases, unread readers/COPY, temporary objects/LISTEN, role changes, failover between nodes with different patch versions. |
| A08 Metadata / EF foundations | M01–M10 | DataTypes, canonical collection names/aliases, restriction order, composite PK/FK, quoted objects, multiple schemas, insufficient permissions, unknown information for expression columns. |
| A09 Authentication / TLS | C07–C13, C16–C17 | Invalid passwords/signatures/challenges; expired/wrong-host/untrusted-CA certificates; plaintext fallback, password files, callback cancellation/rotation/pool isolation; record algorithms individually. |
| A10 Packages / resources | G01–G09, G11 | Consumption of each TFM, Windows/Linux, sustained pool stress, large-value streams, fragmentation/allocation limits, configuration defaults/aliases/units. |
| A11 Incremental V8 support | V01–V04 | Same feature groups in every available V8 mode; regress accepted V9 profiles after each new difference. |
| A12 Connection lifecycle / sessions | C18–C19, P01–P03 | Close/reopen, successful/failed direct-connection database changes and original-pool return, rejection of cross-database DataSource changes before lease release, rejection with active transactions/readers/COPY, StateChange, startup parameter reset. |
| A13 COPY / notifications | B01–B06, P06 | Disposal without Complete, outer transaction after cancellation, truncation/partial packets, interleaved notifications/results, Wait timeout/cancellation, callback reentrancy, resubscription after disconnection. |
| A14 Custom mappings | T21–T24, T32 | Same-named types in different DataSources, catalog reloads, enum-name/composite-field translation conflicts, no cross-use of global caches. |
| A15 System transactions | X04–X05 | Enlistment switch/explicit enlistment/async flow, sequential opens at maximum pool size 1 reusing the same active transaction, no cross-transaction reuse, transaction completion/clear races, concurrent-open promotion, crashes and in-doubt states; verify PG preparation and native MySQL XA applicability/recovery separately. |
| A16 Exceptional MySQL-only endpoint | C20-C21, Q20, T23, T33, C03-C07, Q03-Q07, Q17-Q18, M01-M10 | Default PG in all modes; explicit MySQL with PG unavailable, explicit port, Auto/explicit mode provenance/conflicts, verified Kingbase identity, plugin/TLS/capabilities, malformed/fragmented packets, text/prepared/streamed parameters, results/types/metadata including applicable ENUM/SET, protocol mismatch without fallback, inapplicable PG APIs, cancellation routing/abort. |
| A17 Pool lifecycle and shutdown | P01-P03, P06, C16, C19, Q05, Q18, X04, G06-G07 | Both backends: Pooling=false, private/global isolation, hard capacity across opening/reset/clear/old leases, FIFO/cancel handoff, two-borrower state reset, prepared invalidation, expiry/minimum, owner disposal/waiters, credential rotation/unused-registry reclamation, eligible same-transaction reuse at size 1 and completion/clear races on X04 profiles, bounded cleanup and socket/resource counts. MySQL-only reuse must need no PG connection. |

Each group still requires executable subcases linked to feature IDs. The range notation in this table is not an executed test report.

## 7. Completion and Release Recording Rules

1. The 2026-11-23 baseline completed material analysis and draft documentation only. No product feature had been implemented or accepted, and no profile had LIVE status. Consult [development progress](kbconnector-development-progress.md) for all subsequent status.
2. A basic end-to-end path in V9-1 is a milestone, not "complete V9 support." V9 releases follow design section 11.2: accept all R items, decide every C item explicitly, and retain pending-validation items.
3. Record each official difference with its DLL hash/class/member first, then establish through server catalogs, protocol, or behavior whether adaptation is required. Do not assert unproven conclusions such as "R1 must use OID 5022" or "V8 only has PG."
4. If the new driver fails or disagrees with official results, retain inputs, return values, and independent evidence. "The official driver does it too" does not resolve precision, resource, or transaction defects.
5. Maintain subsequent SIM and LIVE records only in [development progress](kbconnector-development-progress.md), without rewriting the static evidence sources in this matrix. Each record must retain feature ID/subcase, new driver commit/package, profileId, SIM result, LIVE result, difference/limitation classification, and evidence/defect links. Preserve the separate implementation/simulation/liveByProfile dimensions and all profile fields required by section 3.2.

This matrix and the design form the proposed baseline. Implementation planning must retain these feature and evidence indexes; add evidence for new version materials and live findings as they become available. The [developer guide](kbconnector-development.md) describes the working process, and [development progress](kbconnector-development-progress.md) holds the current execution and validation records.
