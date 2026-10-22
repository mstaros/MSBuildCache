// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Graph;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Hashing;

namespace Microsoft.MSBuildCache;

public sealed partial class MSBuildCacheLocalPlugin
{
    /// <summary>
    /// Reuses the project predictor, fingerprint factory and local cache for a caller-executed target.
    /// The caller supplies the source inventory and executable input closure; no helper process is started.
    /// A null execution result, exception or cancellation is never published. Successful results must
    /// describe an execution whose complete inputs are represented by the supplied files and properties.
    /// </summary>
    /// <remarks>
    /// Cache ownership is limited to lookup and publication. The callback can run nested cached builds.
    /// Results are target metadata, not a claimed inventory of the files written by a previous build.
    /// </remarks>
    public static async Task<(string? Result, bool Reused)> RunTargetAsync(
        ProjectGraph graph,
        string projectPath,
        string targetName,
        IReadOnlyCollection<string> sourceFiles,
        IReadOnlyCollection<string> executionInputs,
        IReadOnlyDictionary<string, string> executionProperties,
        string repositoryRoot,
        IReadOnlyDictionary<string, string> cacheSettings,
        PluginLoggerBase logger,
        Func<CancellationToken, Task<string?>> executeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        if (string.IsNullOrWhiteSpace(targetName))
        {
            throw new ArgumentException("A target name is required.", nameof(targetName));
        }

        // The build targets honor MSBuildCacheEnabled before constructing the plugin. Direct
        // callers need the same opt-out before prediction, fingerprinting or opening the store.
        bool disabled = cacheSettings.TryGetValue("Enabled", out string? enabled)
            && string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
        string fullProjectPath = Path.GetFullPath(projectPath);
        disabled |= graph.ProjectNodes.Any(node =>
            string.Equals(node.ProjectInstance.FullPath, fullProjectPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(node.ProjectInstance.GetPropertyValue("MSBuildCacheEnabled"), "false", StringComparison.OrdinalIgnoreCase));
        if (disabled)
        {
            string? result = await executeAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return (result, false);
        }

        PluginSettings settings = PluginSettings.Create<PluginSettings>(
            cacheSettings, logger, repositoryRoot, supportsProbeAndEnumerationCapture: false);
        string nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        PathNormalizer normalizer = new(settings.RepoRoot, nugetRoot);
        NodeContext node = NodeContext.CreateForTarget(graph, projectPath, targetName, sourceFiles,
            executionInputs, executionProperties, settings, normalizer, logger);
        Directory.CreateDirectory(node.LogDirectory);
        byte[]? inputFingerprint = null;

        (string? cached, _) = await WithClientAsync(async (client, factory) =>
        {
            Fingerprint? fingerprint = await factory.GetWeakFingerprintAsync(node);
            inputFingerprint = fingerprint?.Hash;
            if (inputFingerprint is null)
            {
                return (null, false);
            }

            (_, NodeBuildResult? result) = await client.GetNodeAsync(node, materializeOutputs: false, cancellationToken);
            string? value = result?.TargetResults.SingleOrDefault(r => r.TargetName == targetName)
                ?.TaskItems.SingleOrDefault()?.Metadata.GetValueOrDefault("Result");
            return (value, value is not null);
        });
        if (cached is not null)
        {
            return (cached, true);
        }

        DateTime started = DateTime.UtcNow;
        string? executed = await executeAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (executed is null || inputFingerprint is null)
        {
            return (executed, false);
        }

        // A fresh hasher observes edits made during execution. Never publish a result under stale inputs.
        await WithClientAsync(async (client, factory) =>
        {
            Fingerprint? current = await factory.GetWeakFingerprintAsync(node);
            if (current is not null && current.Hash.AsSpan().SequenceEqual(inputFingerprint))
            {
                await client.AddNodeAsync(node, pathSet: null, Array.Empty<string>(), _ => new NodeBuildResult(
                    new SortedDictionary<string, ContentHash>(StringComparer.OrdinalIgnoreCase),
                    new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    [new NodeTargetResult(targetName, [new NodeTargetResultTaskItem(targetName,
                        new Dictionary<string, string> { ["Result"] = executed })])],
                    started, DateTime.UtcNow, buildId: null), cancellationToken);
            }

            return (executed, false);
        });
        return (executed, false);

        async Task<(string?, bool)> WithClientAsync(
            Func<ICacheClient, IFingerprintFactory, Task<(string?, bool)>> action)
        {
            using IDisposable cacheLock = await AcquireCacheSessionLockAsync(settings, logger, cancellationToken);
            using IContentHasher hasher = HashInfoLookup.Find(BuildXL.Cache.ContentStore.Hashing.HashType.Murmur).CreateContentHasher();
            IInputHasher inputs = new TargetInputHasher(hasher, node.Inputs, cancellationToken);
            IFingerprintFactory factory = new FingerprintFactory(hasher, inputs, settings, normalizer);
            await using ICacheClient client = await CreateLocalCacheClientAsync(
                settings, factory, hasher, nugetRoot, cancellationToken);
            try
            {
                return await action(client, factory);
            }
            finally
            {
                await client.ShutdownAsync(CancellationToken.None);
            }

        }
    }

    private sealed class TargetInputHasher(
        IContentHasher hasher, IReadOnlyList<string> paths, CancellationToken cancellationToken) : IInputHasher
    {
        private readonly HashSet<string> _paths = new(paths, StringComparer.OrdinalIgnoreCase);
        public bool ContainsPath(string absolutePath) => _paths.Contains(absolutePath);

        public async ValueTask<byte[]?> GetHashAsync(string absolutePath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ContainsPath(absolutePath) || !File.Exists(absolutePath))
            {
                return null;
            }

            await using FileStream stream = new(absolutePath, FileMode.Open, System.IO.FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return (await hasher.GetContentHashAsync(stream)).ToHashByteArray();
        }
    }
}

