// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Execution;

namespace Microsoft.MSBuildCache;

public sealed class NodeContext
{
    /// <summary>
    /// Creates a caller-executed target from the same project predictions and graph used by the plugin.
    /// Source inputs are taken from the evaluated dependency closure. Execution inputs are actual files,
    /// never an interpretation of a previous build's observed-write record as a full output inventory.
    /// </summary>
    public static NodeContext CreateForTarget(
        Microsoft.Build.Graph.ProjectGraph graph, string projectPath, string targetName,
        IReadOnlyCollection<string> sourceFiles, IReadOnlyCollection<string> executionInputs,
        IReadOnlyDictionary<string, string> executionProperties, PluginSettings settings,
        PathNormalizer normalizer, Microsoft.Build.Experimental.ProjectCache.PluginLoggerBase logger)
    {
        string fullProjectPath = Path.GetFullPath(projectPath);
        HashSet<Microsoft.Build.Graph.ProjectGraphNode> closure = new();
        Stack<Microsoft.Build.Graph.ProjectGraphNode> pending = new(graph.ProjectNodes.Where(
            node => string.Equals(node.ProjectInstance.FullPath, fullProjectPath, StringComparison.OrdinalIgnoreCase)));
        if (pending.Count == 0)
        {
            throw new ArgumentException("Project is absent from the evaluated graph.", nameof(projectPath));
        }

        while (pending.TryPop(out Microsoft.Build.Graph.ProjectGraphNode? node))
        {
            if (closure.Add(node))
            {
                foreach (Microsoft.Build.Graph.ProjectGraphNode dependency in node.ProjectReferences)
                {
                    pending.Push(dependency);
                }
            }
        }

        Parsing.Parser parser = new(logger, settings.RepoRoot);
        IReadOnlyDictionary<Microsoft.Build.Graph.ProjectGraphNode, Parsing.ParserInfo> predictions = parser.ParseCached(graph);
        HashSet<string> sources = new(sourceFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        HashSet<string> inputs = new(executionInputs.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        SortedDictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildCacheCallerTargetProtocol"] = "1"
        };
        HashSet<string> ignoredProperties = new(settings.GlobalPropertiesToIgnore, StringComparer.OrdinalIgnoreCase);
        foreach (Microsoft.Build.Graph.ProjectGraphNode node in closure)
        {
            if (predictions.TryGetValue(node, out Parsing.ParserInfo? prediction))
            {
                foreach (Parsing.PredictedInput input in prediction.Inputs)
                {
                    if (sources.Contains(input.AbsolutePath) || !IsUnder(input.AbsolutePath, settings.RepoRoot))
                    {
                        inputs.Add(input.AbsolutePath);
                    }
                }
            }
            else
            {
                // The parser predicts only projects under the repository root. A project outside it, such as one in a
                // sibling repository, is described by its project file and the assembly the caller's build produced.
                inputs.Add(node.ProjectInstance.FullPath);
                string targetPath = node.ProjectInstance.GetPropertyValue("TargetPath");
                if (targetPath.Length != 0)
                {
                    inputs.Add(Path.GetFullPath(Path.Combine(node.ProjectInstance.Directory, targetPath)));
                }
            }

            // Supplement predictions with declared content and copied data, including linked fixtures.
            // Other item types can declare copied fixtures through standard copy metadata.
            foreach (ProjectItemInstance item in node.ProjectInstance.Items)
            {
                string copyToOutput = item.GetMetadataValue("CopyToOutputDirectory");
                string copyToPublish = item.GetMetadataValue("CopyToPublishDirectory");
                bool copiesToOutput = copyToOutput.Length != 0 && !copyToOutput.Equals("Never", StringComparison.OrdinalIgnoreCase);
                bool copiesToPublish = copyToPublish.Length != 0 && !copyToPublish.Equals("Never", StringComparison.OrdinalIgnoreCase);
                if (!item.ItemType.Equals("Content", StringComparison.OrdinalIgnoreCase) && !copiesToOutput && !copiesToPublish)
                {
                    continue;
                }

                string path = item.GetMetadataValue("FullPath");
                if (path.Length != 0 && (sources.Contains(path) || !IsUnder(path, settings.RepoRoot)))
                {
                    inputs.Add(path);
                }
            }
            SortedDictionary<string, string> configured = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> property in node.ProjectInstance.GlobalProperties)
            {
                if (!ignoredProperties.Contains(property.Key))
                {
                    configured[property.Key] = NormalizeValue(property.Value);
                }
            }

            string identity = (prediction?.ProjectFileRelativePath ?? node.ProjectInstance.FullPath) + ":" + System.Text.Json.JsonSerializer.Serialize(configured);
            properties["Configuration:" + identity] = node.ProjectInstance.GetPropertyValue("NETCoreSdkVersion");
        }

        foreach (KeyValuePair<string, string> property in executionProperties)
        {
            properties["Execution:" + property.Key] = NormalizeValue(property.Value);
        }

        Microsoft.Build.Graph.ProjectGraphNode representative = closure.First(node =>
            string.Equals(node.ProjectInstance.FullPath, fullProjectPath, StringComparison.OrdinalIgnoreCase));
        return new NodeContext(settings.LogDirectory, representative.ProjectInstance, Array.Empty<NodeContext>(),
            Path.GetRelativePath(settings.RepoRoot, fullProjectPath), properties,
            inputs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(), referenceAssemblyRelativePath: null,
            new HashSet<string>([targetName], StringComparer.OrdinalIgnoreCase));

        string NormalizeValue(string value)
        {
            string root = Path.GetFullPath(settings.RepoRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(value.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase)
                ? "{RepoRoot}" : normalizer.Normalize(value.Replace('/', '\\'));
        }

        static bool IsUnder(string path, string directory)
        {
            string relative = Path.GetRelativePath(directory, path);
            return !Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
    }

    private static readonly byte[] PropertyHashDelimiter = [0x01];
    private static readonly byte[] PropertyValueHashDelimiter = [0x02];

    private readonly string _logDirectory;
    private bool _logDirectoryCreated;

    public NodeContext(
        string baseLogDirectory,
        ProjectInstance projectInstance,
        IReadOnlyList<NodeContext> dependencies,
        string projectFileRelativePath,
        IReadOnlyDictionary<string, string> filteredGlobalProperties,
        IReadOnlyList<string> inputs,
        string? referenceAssemblyRelativePath,
        HashSet<string> targetNames)
    {
        Id = GenerateId(projectFileRelativePath, filteredGlobalProperties);
        _logDirectory = Path.Combine(baseLogDirectory, Id);
        ProjectInstance = projectInstance;
        Dependencies = dependencies;
        ProjectFileRelativePath = projectFileRelativePath;
        FilteredGlobalProperties = filteredGlobalProperties;
        Inputs = inputs;
        ReferenceAssemblyRelativePath = referenceAssemblyRelativePath;
        TargetNames = targetNames;
    }

    public string Id { get; }

    public string LogDirectory
    {
        get
        {
            // If something is accessing it, assume it wants to use it, so create the directory.
            if (!_logDirectoryCreated)
            {
                Directory.CreateDirectory(_logDirectory);
                _logDirectoryCreated = true;
            }

            return _logDirectory;
        }
    }

    public ProjectInstance ProjectInstance { get; }

    public IReadOnlyList<NodeContext> Dependencies { get; }

    public string ProjectFileRelativePath { get; }

    public IReadOnlyDictionary<string, string> FilteredGlobalProperties { get; }

    public IReadOnlyList<string> Inputs { get; }

    public string? ReferenceAssemblyRelativePath { get; }

    public HashSet<string> TargetNames { get; }

    public DateTime? StartTimeUtc { get; private set; }

    public void SetStartTime() => StartTimeUtc = DateTime.UtcNow;

    public DateTime? EndTimeUtc { get; private set; }

    public void SetEndTime() => EndTimeUtc = DateTime.UtcNow;

    public NodeBuildResult? BuildResult { get; private set; }

    public void SetBuildResult(NodeBuildResult buildResult)
    {
        if (BuildResult != null)
        {
            throw new InvalidOperationException("Build result already set");
        }

        BuildResult = buildResult;
    }

    /// <summary>
    /// Generate a stable Id which we can use for sorting and comparison purposes across builds.
    /// </summary>
    private static string GenerateId(string projectFileRelativePath, IReadOnlyDictionary<string, string> filteredGlobalProperties)
    {
        // In practice, the dictionary we're given is SortedDictionary<string, string>, so try casting.
        if (filteredGlobalProperties is not SortedDictionary<string, string> sortedProperties)
        {
            sortedProperties = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kvp in filteredGlobalProperties)
            {
                sortedProperties.Add(kvp.Key, kvp.Value);
            }
        }

#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms. This is not used for crypto.
        using MD5 hasher = MD5.Create();
#pragma warning restore CA5351 // Do Not Use Broken Cryptographic Algorithms

        foreach (KeyValuePair<string, string> kvp in sortedProperties)
        {
            AddCaseInsensitiveStringToHash(hasher, kvp.Key);
            AddBytesToHash(hasher, PropertyValueHashDelimiter);
            AddCaseInsensitiveStringToHash(hasher, kvp.Value);
            AddBytesToHash(hasher, PropertyHashDelimiter);

            static void AddCaseInsensitiveStringToHash(MD5 hasher, string str) => AddBytesToHash(hasher, Encoding.UTF8.GetBytes(str.ToUpperInvariant()));
            static void AddBytesToHash(MD5 hasher, byte[] bytes) => hasher.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] hash = hasher.Hash!;

        string id = $"{projectFileRelativePath}_{Convert.ToBase64String(hash)}";

        // Avoid casing issues
        id = id.ToUpperInvariant();

        // Ensure the id is path-friendly
        id = id
            .Replace('\\', '_')
            .Replace('/', '_')
            .Replace('+', '.')
            .TrimEnd('=');

        return id;
    }
}

