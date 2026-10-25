# Do not merge BuildXL's cloud dependencies into the plugin

The Release plugin (`Microsoft.MSBuildCache.Local.dll`, 30 MB) ILRepack-merges every assembly in the output folder. BuildXL's cache packages bring in its distributed cache: Azure SDKs, Kusto telemetry, gRPC and protobuf, WCF and MSAL. They add thousands of public types (Kusto ~1,600, Azure ~440, System.ServiceModel 358, Newtonsoft is unaffected) and the native `msalruntime.dll`. The local cache (`LocalCacheFactory`: local content store, BuildXL hashing, RocksDB memoization) does not use them.

- Exclude from the merge: `Azure.*`, `Microsoft.Azure.*`, `Kusto.*`, `Microsoft.Identity.*`, `Microsoft.IdentityModel.*`, `System.IdentityModel.*`, `Grpc.*`, `Google.Protobuf`, `protobuf-net*`, `System.ServiceModel*`, `System.Private.ServiceModel`, `System.ClientModel`, `System.Memory.Data`. They stay in the output folder, so ILRepack still resolves references to them.
- Repack.Tests asserts the merged assembly defines no types in those namespaces.

Unchanged: all BuildXL assemblies, RocksDB, `BuildXLNatives.dll`, and the general libraries (Newtonsoft.Json, Polly, Microsoft.Extensions.*). Runtime verification happens in CSharpMpc, whose suites load the plugin with only the merged DLL and natives beside it.

## Summary

The Release plugin no longer merges BuildXL's distributed-cache dependencies (Azure SDKs, Kusto, gRPC and protobuf, WCF, MSAL): 29 MB to 13 MB. Public BuildXL types built on them are internalized, so MSBuild's plugin scan still succeeds.
Consumer impact: breaking - types from six BuildXL assemblies are internal in the merged plugin; Microsoft.MSBuildCache types are unchanged

## Self-check

Complete every row before commit. Use one status: passed, fixed, or non_applicable.
passed: Issues = None; Fixed is empty. fixed: describe the issues and their fixes;
include Verification: followed by the checks/results in Fixed. Number multiple issues
and corresponding fixes. non_applicable: explain why in Issues; Fixed is empty.
Drift covers unintended behavior/default/contract changes, silent fallbacks,
swallowed errors, weakened tests, and unrelated behavior changes.
Keep the rows in this order. Escape any pipe inside a cell as \|.

| Area | Status | Issues | Fixed |
| --- | --- | --- | --- |
| Bugs | passed | None |  |
| Drift | fixed | Excluding the cloud assemblies left 72 public BuildXL types (gRPC messages and services, code-first gRPC contracts, Azure credentials, an EventHubs wrapper) whose base types or contracts live in them, so MSBuild's exported-type scan of the plugin would fail. | ILRepack internalizes BuildXL.Cache.ContentStore.Grpc, BuildXL.Cache.ContentStore.Distributed, BuildXL.Cache.ContentStore, BuildXL.Cache.MemoizationStore, BuildXL.Cache.ContentStore.Interfaces and BuildXL.Utilities; each defines some of those types. The obsolete Grpc.AspNetCore/protobuf-net.Grpc.AspNetCore internalize flags are gone with those assemblies.<br>Verification: loading every public type of the merged plugin with only host assemblies available: 0 failures (72 before internalizing). |
| Performance | fixed | The merged plugin was 29 MB with 22,812 types (6,836 public), mostly BuildXL's distributed-cache dependencies. | 13 MB, 9,619 types (2,258 public).<br>Verification: measured from the Release build and its package. |
| Allocations | passed | None |  |
| Complexity | passed | None |  |
| Test coverage | fixed | Nothing checked what the merge contains or that MSBuild's type scan works with only what ships beside the plugin. | Repack.Tests: CloudDependenciesNotMerged (no Azure, Kusto, gRPC, protobuf, WCF or identity namespaces) and ExportedTypesLoadWithoutUnmergedAssemblies (exported-type scan in a context that refuses unshipped assemblies).<br>Verification: Repack.Tests 4/4; Common.Tests 279/283, including InProcessTargetCacheTests on the merged plugin. The 4 failures (WarningPolicyTests) start bare \`dotnet msbuild\`, which on the patched test host resolves to a dotnet without an SDK. |
| Logic and edge cases | passed | None |  |
| Others | fixed | Common.Tests and Repack.Tests target net9.0, which the patched-only test host (.NET 11) cannot start; retargeting to net11.0 resolves System.Security.Cryptography.Pkcs/Xml 10.0.10, rejected as vulnerable. | Both keep net9.0 with RollForward=Major and run on the patched runtime.<br>Verification: both suites ran on the patched host. |
