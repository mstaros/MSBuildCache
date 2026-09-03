# Transaction Worktree Artifact Materialization

## Status

Design discussion. No implementation is described as complete.

## Goal

Allow `CSharpMpc.begin_transaction` to create a project-scoped worktree containing source only for the selected projects while using cached binaries for every configured dependency whose source is absent from the worktree.

The same cached dependency artifacts must be refreshed after every successful `pull_target`.

## Project model

Project selection is the only behavioral distinction.

- **Source projects** are the configured targets selected for the transaction. Their source is present and may be edited and built in the worktree.
- Every configured dependency outside the source-project set is handled as a binary-only target. Its source is absent from the worktree.

A binary-only target may belong to the primary repository or another repository. That origin remains part of its configured-target identity and provenance, but it does not change materialization behavior.

The binary-only set is therefore derived as:

```text
configured dependency closure - source projects
```

The existing split between `BinaryProjects` and `ExternalBinaryProjects` must not create separate placement paths or policies.

## Begin-transaction flow

1. GuardedMcpSdk creates the transaction branch and worktree at the exact target commit.
2. CSharpMpc resolves the selected projects and evaluates their exact configured dependency graph before removing source.
3. CSharpMpc records the selected configuration, platform, target framework, runtime identifier, global-properties identity, and configured-target graph identity.
4. The configured graph is partitioned into source projects and binary-only targets.
5. CSharpMpc queries MSBuildCache without materializing all node outputs. The cache result supplies repository-relative output names and content hashes, not physical paths inside the cache.
6. Selected-project outputs from the initial snapshot are placed as writable copies.
7. Binary-only compiler and runtime artifacts are placed as verified hard links from immutable cached content. Copy fallback is not allowed for these artifacts.
8. Source for binary-only targets is excluded from the worktree.
9. Project references from source projects to binary-only targets are removed from the transaction build and replaced with references to the materialized cached artifacts.
10. CSharpMpc persists a receipt covering the target commit, selected projects, configured-target identities, cache content hashes, destination paths, and actual placement modes.
11. The transaction is returned only after the materialization and receipt validate.

## Build behavior

Only source projects may be restored, generated, built, or tested as source.

Selected-project outputs are transaction-owned and writable. The initial copy is a seed, not authoritative evidence that the selected project is current. Build and test gates must not accept it without validating or rebuilding the selected project against the transaction inputs.

Binary-only artifacts are immutable inputs:

- They must be read-only.
- Their content hashes must match the persisted receipt.
- Their placement must be a hard link rather than a copy.
- Builds must not overwrite them.
- The corresponding excluded project source must never be required.

The worktree and cache must be on a filesystem that supports the required hard links. A required hard-link placement failure is a transaction-preparation failure.

## Pull-target flow

1. CSharpMpc checkpoints pending transaction changes.
2. GuardedMcpSdk merges the current target into the transaction branch.
3. If the merge conflicts, artifact refresh does not run. The user resolves conflicts and calls `pull_target` again.
4. CSharpMpc re-evaluates the exact configured graph from the resolved merged transaction snapshot, using the stored selected-project list.
5. CSharpMpc derives the new binary-only configured-target set.
6. CSharpMpc queries the corresponding cache manifests and content hashes.
7. Replacement hard links are created in staging and verified before publication.
8. The transaction switches to the new binary-only artifacts and persists a replacement receipt.
9. Previous build and test receipts are invalidated.
10. The transaction becomes `IntegrationPrepared` only after artifact refresh succeeds.

A failed refresh must not silently retain an old dependency receipt. A repeated `pull_target` must be able to retry refresh even when the target commit is already an ancestor of the transaction branch.

## Component responsibilities

### GuardedMcpSdk

GuardedMcpSdk remains responsible only for Git lifecycle:

- transaction branches;
- worktree creation and synchronization;
- target merging;
- conflict and recovery state;
- compare-and-swap integration.

It must not acquire MSBuild, project-selection, cache, or artifact-placement knowledge.

### CSharpMpc

CSharpMpc owns:

- selected-project interpretation;
- configured graph evaluation;
- source versus binary-only classification;
- output ownership and provenance;
- copy versus hard-link policy;
- project-reference replacement;
- begin and pull orchestration;
- materialization receipts;
- build and test validation.

Repository origin is retained as provenance, not used as a policy branch.

### MSBuildCache fork

MSBuildCache remains the content-addressed artifact store. It must not acquire transaction or worktree semantics.

The fork needs a narrow public capability that:

- queries a cache entry without materializing it;
- returns output names and content hashes;
- places requested content at caller-selected destinations;
- accepts a required per-file realization policy;
- returns the actual per-file placement result;
- fails when a required hard link was not produced.

Physical paths inside the cache must not be exposed as durable artifact paths. Cache layout and eviction remain cache implementation details.

The existing `GetNodeAsync(..., materializeOutputs: false, ...)` and `NodeBuildResult.Outputs` provide part of the query behavior. Existing CAS placement accepts destinations, but the current implementation uses `FileRealizationMode.Any` for ordinary outputs and discards successful per-file placement results. That is insufficient for strict hard-link enforcement.

## Re-audit and concrete recipe

### Existing code to reuse

| Existing code | Reusable behavior | Missing behavior |
| --- | --- | --- |
| `src/Common/Caching/ICacheClient.cs` | `GetNodeAsync(nodeContext, materializeOutputs, ...)` | A caller still needs a fully initialized node, fingerprint factory, and live cache session. |
| `src/Common/NodeBuildResult.cs` | `Outputs` maps repository-relative paths to `ContentHash`; `PackageFilesToCopy` marks package-backed outputs. | It does not contain configured-target ownership, output role, repository provenance, or placement results. |
| `src/Common/Caching/CacheClient.cs` | Passing `materializeOutputs: false` resolves the cache entry and returns its manifest without placing outputs. A local-only hit also pins its content list. | Placement is hidden behind the private cache-entry interface and normal destinations are forced under `RepoRoot`. |
| `src/Common/Caching/CasCacheClient.cs` | A known content hash can be placed at a requested path through BuildXL CAS. | The method is private, normal outputs use `FileRealizationMode.Any`, and successful per-file results are discarded. |
| BuildXL `FileRealizationMode` | Already defines `HardLink` and verified `Copy`. | The fork does not let an external caller require a mode per destination. |
| BuildXL `PlaceFileResult` | Distinguishes `PlacedWithHardLink`, `PlacedWithCopy`, missing content, and errors. | The fork currently reduces the batch to success or exception. |
| `src/Local/LocalCacheFactory.cs` and `MSBuildCacheLocalPlugin.cs` | Create and start the Local cache and CAS session. | Construction is coupled to the MSBuild plugin lifecycle. |

### Audit conclusions

1. `GetNodeAsync(..., materializeOutputs: false, ...)` is the correct manifest lookup and should be reused. It is not a complete public query feature that CSharpMpc can instantiate.
2. `NodeBuildResult.Outputs` should remain the cached path-to-content map. Do not add transaction fields or increment `NodeBuildResult.CurrentVersion`; that would invalidate existing cache entries.
3. `PackageFilesToCopy` entries are deliberately excluded from CAS publication. Their hashes appear in `Outputs`, but replay comes from the NuGet package root. They must not be submitted blindly to placement by CAS hash.
4. `MSBuildCachePluginBase.GetRepoRoot` disables caching for a graph spanning multiple Git repositories. CSharpMpc must query each owning repository separately. Origin is required for routing and provenance, not for placement policy.
5. The plugin ignores observed outputs outside its repository root. CSharpMpc's current configured-generation producer writes into an external temporary generation root, so merely enabling the plugin on that invocation will not cache those files.
6. MSBuildCache's manifest cannot replace CSharpMpc's configured-generation metadata. CSharpMpc still needs canonical configured-target identity, graph edges, output ownership and kind, reference bindings, package claims, and toolchain provenance.
7. Directly referencing the current Local package is insufficient. Graph parsing, node construction, source hashing, fingerprint creation, cache locking, and client creation are private or protected inside `MSBuildCachePluginBase`.
8. The current placement code treats every mode except `CopyNoVerify` as read-only. Explicit verified `Copy` for a selected source-project seed must instead produce a writable destination.

The fork is therefore focused: reuse the existing lookup and CAS implementation, expose one reusable query lifetime, and expose explicit placement results.

### Fork recipe: reusable query lifetime

Add one public façade in the Local package. The exact type name is deferred to implementation review. Its responsibility is narrow:

- accept one repository root, graph entry points, requested targets, and global properties;
- reuse the existing settings, parser, node construction, source hashing, and fingerprint factory;
- open the existing Local cache and hold its directory lock and CAS session;
- expose the configured nodes created by graph initialization;
- query a node by calling `GetNodeAsync(nodeContext, materializeOutputs: false, ...)`;
- return an explicit hit or miss together with node identity and `NodeBuildResult`;
- remain alive from query through placement so pinned content cannot disappear;
- dispose through the existing cache shutdown path.

Factor initialization out of `MSBuildCachePluginBase.BeginBuildInnerAsync` so the plugin and façade call the same implementation. Do not make `Parser`, `ParserInfo`, or `NodeDescriptorFactory` public individually, and do not reproduce the fingerprint algorithm in CSharpMpc.

This façade is query-only. A miss is returned to CSharpMpc; it must not silently run a build.

### Fork recipe: placement by content hash

Update `src/Common/Caching/ICacheClient.cs` with one explicit placement operation. Each requested file supplies:

- the content hash returned in `NodeBuildResult`;
- an absolute caller-selected staging destination;
- the required realization mode, initially limited to verified `Copy` or strict `HardLink`.

Each result preserves:

- destination;
- content hash;
- requested mode;
- actual `PlaceFileResult.ResultCode`;
- placement error, when unsuccessful.

The API must not return a physical CAS path.

In `src/Common/Caching/CacheClient.cs`:

1. Keep the existing manifest-only branch.
2. Put batching, parent-directory creation, concurrency gating, and error aggregation behind the new placement operation.
3. Make normal cache-hit materialization call the same implementation so there are not two placement paths.
4. Keep package replay separate. Only outputs absent from `PackageFilesToCopy` are submitted to CAS.
5. Do not touch timestamps or write the local state file for external staging; those are MSBuild replay behaviors.

In `src/Common/Caching/CasCacheClient.cs`:

1. Pass the caller's mode to `IContentSession.PlaceFileAsync`.
2. Use `FileAccessMode.ReadOnly` for `HardLink`.
3. Use `FileAccessMode.Write` for verified `Copy`.
4. Return every `PlaceFileResult`.
5. Accept `HardLink` only when the result is `PlacedWithHardLink`.
6. Accept `Copy` only when the result is `PlacedWithCopy`.
7. Treat `NotPlacedAlreadyExists`, missing content, a different successful realization, and every error as failure. CSharpMpc supplies fresh staging paths.
8. Never retry a failed hard-link request as a copy.

The plugin's existing `FileRealizationMode.Any` policy remains unchanged for ordinary builds.

### CSharpMpc integration recipe

CSharpMpc remains responsible for interpreting the cache outputs and retains its existing configured-target receipts.

1. Derive one binary-only set: configured dependency closure minus selected source projects.
2. Retain each target's owning repository root only to open the correct repository query session.
3. Query against a full-source checkout at the exact commit being prepared. Do not fingerprint the reduced transaction projection after binary-project inputs are absent.
4. Query one repository at a time because the current cache graph rejects multi-repository graphs.
5. Join an MSBuildCache node to the existing CSharpMpc configured-target identity using evaluated project path, filtered global properties, and target list. Never infer ownership from DLL names or output directories.
6. For selected source-project outputs during initial begin, request verified `Copy`.
7. For binary-only project outputs, request strict `HardLink`.
8. Use CSharpMpc's existing output ownership and reference/runtime classification to choose files. Do not materialize every observed intermediate output.
9. Keep `PackageFilesToCopy` on CSharpMpc's existing package/restore path for the first implementation.
10. Stage all destinations, validate content hashes and actual realization results, then publish using the existing receipt and relocation machinery.
11. Remove binary source and replace its project references only after successful publication.

`BinaryProjects` and `ExternalBinaryProjects` must not select different placement behavior. They may remain temporarily in persisted schema version 3 for compatibility, but normalize immediately into the same binary configured-target set. Removing the stored fields is a later schema migration, not part of the cache fork.

### Pull-target recipe

After every successful `pull_target`, including a no-op merge:

1. Re-evaluate the selected projects and configured dependency closure at the resulting transaction tip.
2. Open repository query sessions for the newly resolved owning commits.
3. Query all binary-only manifests without materialization.
4. Fail closed on any miss or configured-target identity mismatch.
5. Place the replacement artifact set in a new staging namespace with required `HardLink`.
6. Verify content hashes, `PlacedWithHardLink`, read-only state, and receipt identity.
7. Publish the new namespace and receipt atomically.
8. Remove artifacts no longer present in the closure.
9. Invalidate previous build and test evidence.
10. Mark integration prepared only after the replacement receipt is durable.

Selected source-project seed outputs are not validated by binary relinking. Until their pull policy is chosen, they are stale build outputs and cannot satisfy a build or test gate.

### Initial cache-miss policy

Implement the first integration as fail-on-miss. This keeps transaction preparation read-only with respect to source builds and exposes correctness failures.

A later producer may build the missing configured target in a dedicated full-source checkout, publish it, and repeat the query. That producer must emit outputs under a repository root recognized by MSBuildCache, or the fork must separately support logical output roots; the current external configured-generation artifacts directory is ignored.

### Tests required in the fork

Update `src/Common.Tests/CasCacheClientTests.cs` and add coverage proving:

- manifest-only lookup creates no outputs;
- arbitrary staging destinations work;
- hard-link placement returns `PlacedWithHardLink`, is read-only, and shares file identity;
- cross-volume or unsupported hard-link placement fails without copying;
- copy placement returns `PlacedWithCopy`, is writable, and does not share file identity;
- missing content and pre-existing staging destinations fail;
- mixed batches preserve one result per request;
- package-backed outputs are not submitted as CAS content;
- disposal releases the cache lock and session.

Add an end-to-end case in `tests/scenarios.ps1` that populates Local cache, performs manifest-only lookup, places one strict hard link and one verified copy, and checks hashes plus Windows file identity.

### Expected fork changes

| File | Change |
| --- | --- |
| `src/Common/Caching/ICacheClient.cs` | Add explicit placement and per-file result return. |
| `src/Common/Caching/CacheClient.cs` | Share batching between plugin replay and external placement while retaining package semantics. |
| `src/Common/Caching/CasCacheClient.cs` | Honor explicit modes and return exact realization results. |
| `src/Common/MSBuildCachePluginBase.cs` | Factor graph, node, fingerprint, lock, and client initialization for reuse. |
| `src/Local/LocalCacheFactory.cs` and `src/Local/MSBuildCacheLocalPlugin.cs` | Open the reusable Local query lifetime through the plugin's existing construction path. |
| `src/Common.Tests/CasCacheClientTests.cs` | Add strict placement tests. |
| `src/Common.Tests/MSBuildCachePluginBaseTests.cs` | Prove the initialization refactor retains plugin behavior. |
| `tests/scenarios.ps1` | Add manifest-only and strict realization end-to-end coverage. |
| `README.md` | Document the public surface after implementation. |

No first-pass change is expected in `NodeBuildResult.cs` or `SourceGenerationContext.cs`.

### Implementation order

1. Refactor initialization without changing behavior; run the existing tests.
2. Add placement by hash and strict realization tests.
3. Add the reusable Local query lifetime and manifest-only tests.
4. Package the fork and consume it from CSharpMpc.
5. Replace CSharpMpc's current central-file link source with cache placement while retaining configured-target receipts.
6. Integrate begin-transaction materialization.
7. Integrate pull-target refresh and retry.
8. After the flow is stable, migrate the redundant persisted binary-project fields.
## Correctness boundaries

- Cache content used as a hard-link source must be immutable.
- Content hash, configured-target identity, and destination must be receipt-bound.
- A same-name DLL from another target framework, runtime identifier, configuration, platform, or global-properties evaluation must never satisfy the request.
- Link publication must be staged and validated before replacing the active receipt.
- Linked artifacts must remain outside version-controlled transaction source.
- A cache miss must never be treated as an empty dependency.
- Pull completion must not precede dependency refresh.
- Commit gates must verify that the active materialization receipt belongs to the exact gated transaction tip.

## Current implementation gaps

- Selected-project output files are currently filtered out of the source-scope seed rather than copied initially.
- Binary-only immutable files currently use a hard-link-or-copy policy instead of strict hard-link enforcement.
- Local and external binary projects are represented separately even though the build path later combines them.
- `pull_target` invalidates build artifacts but does not recompute and replace binary-only materialization.
- Pinned seed validation currently directs the caller to roll back and recreate the transaction instead of refreshing it.
- MSBuildCache does not expose stable physical CAS paths, nor should it.
- MSBuildCache does not currently return strict per-file realization outcomes through its public cache-client surface.

## Open decisions

1. On a cache miss, should transaction preparation build the missing binary-only configured target centrally, or fail?
2. After `pull_target`, should initial copied outputs of source projects be retained or deleted as stale?
3. Is the required artifact set limited to compiler DLL references, or must it include the full build-and-test runtime closure such as runtime DLLs, dependency metadata, and native assets?
