# KingbaseConnector Documentation

Baseline date: 2026-11-23. Last updated: 2026-11-24. The repository currently contains design and development documentation and local reference binaries. No product projects or tests have been created. See the progress tracker for subsequent status changes.

| Document | Purpose |
| --- | --- |
| [Unified driver design](kbconnector-design.md) | Scope, architecture, server identification, compatibility, types, built-in pooling, the normal PG path and exceptional explicit MySQL endpoint, metadata, and V9-to-V8 delivery gates. |
| [Feature and version matrix](kbconnector-feature-matrix.md) | Stable feature IDs, official evidence, candidate server profiles, proposed contracts, and acceptance requirements. |
| [Development guide](kbconnector-development.md) | Repository setup, implementation order, dependencies, validation workflow, and the definition of a completed feature. |
| [Development progress](kbconnector-development-progress.md) | The single current status record for milestones, work packages, all matrix features, blockers, and completion evidence. |
| [Official driver inventory](reference/official-driver-inventory.json) | Paths and complete SHA-256 identities for 25 Kdbndp.dll files representing 10 unique binaries, with framework metadata and selected static evidence. |

Use the design to understand the intended behavior, the matrix to identify a feature's requirements, and the development guide and tracker to implement and complete it. Update the tracker with each completed functional unit; do not maintain another editable completion list in the design or matrix.

PG remains the default connection protocol in all compatibility modes. The explicit MySQL backend is designed for exceptional installations exposing only MySQL protocol. WireProtocol selects the backend; CompatibilityMode selects database semantics. Both backends use built-in provider pooling with protocol-isolated sessions. These are intended contracts, not implemented capabilities.

All maintained project documentation is written in English. Keep temporary walkthroughs, review reports, and decompiled source outside the repository. Retain durable technical facts and source references in the design, matrix, or inventory where they belong.

The inventory is research evidence. It does not participate in runtime version detection or establish working server compatibility. Keep official static evidence, implementation progress, simulated validation, and live server acceptance separate. Source references also include the Kingbase MySQL-compatible C010 release declaration of MySQL protocol; endpoint-specific acceptance remains pending. Proposed API names and defaults remain identified as proposals until settled during the corresponding development task.
