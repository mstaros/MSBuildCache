// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

extern alias CachedTargets;

using MSBuildCacheLocalPlugin = CachedTargets::Microsoft.MSBuildCache.MSBuildCacheLocalPlugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.MSBuildCache.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public sealed class InProcessTargetCacheTests
{
    [TestMethod]
    public async Task ReusesAcrossWorktreesAndInvalidatesDependencyArtifactAndOptionsChanges()
    {
        Fixture fixture = new();
        try
        {
            string first = fixture.CreateTree("first");
            string second = fixture.CreateTree("second");
            int executions = 0;
            Task<string?> Execute(CancellationToken _) => Task.FromResult<string?>((++executions).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsFalse((await fixture.Run(first, Execute)).Reused);
            await File.WriteAllTextAsync(Path.Combine(second, "notes.md"), "Unrelated documentation");
            await File.WriteAllTextAsync(Path.Combine(second, "lib", "notes.md"), "Changed project-local documentation");
            await File.WriteAllTextAsync(Path.Combine(second, "lib", "unrelated", "Other.cs"), "public class ChangedSibling { }");
            Assert.IsTrue((await fixture.Run(second, Execute)).Reused);
            Assert.AreEqual(1, executions);
            await File.WriteAllTextAsync(Path.Combine(second, "tests", "notes.md"), "Changed bare None documentation");
            Assert.IsFalse((await fixture.Run(second, Execute)).Reused);
            await File.AppendAllTextAsync(Path.Combine(second, "lib", "Library.cs"), "// changed implementation");
            Assert.IsFalse((await fixture.Run(second, Execute)).Reused);
            await File.AppendAllTextAsync(Path.Combine(second, "lib", "settings.props"), "<!-- changed import -->");
            Assert.IsFalse((await fixture.Run(second, Execute)).Reused);
            await File.AppendAllTextAsync(Path.Combine(second, "tests", "fixture.json"), "changed content");
            Assert.IsFalse((await fixture.Run(second, Execute)).Reused);
            await File.AppendAllTextAsync(Path.Combine(second, "data", "linked.json"), "changed linked fixture");
            Assert.IsFalse((await fixture.Run(second, Execute)).Reused);
            await File.AppendAllTextAsync(Path.Combine(second, "tests", "custom.json"), "changed custom copied fixture");
            Assert.IsFalse((await fixture.Run(second, Execute)).Reused);
            await File.AppendAllTextAsync(Path.Combine(second, "tests", "runtime.dll"), "changed runtime");
            Assert.IsFalse((await fixture.Run(second, Execute)).Reused);
            Assert.IsFalse((await fixture.Run(second, Execute, "serial")).Reused);
            Assert.AreEqual(9, executions);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [TestMethod]
    public async Task FailureIsNotCachedAndNestedCacheUseDoesNotHoldTheStoreLock()
    {
        using Fixture fixture = new();
        string root = fixture.CreateTree("tree");
        int executions = 0;
        async Task<string?> Execute(CancellationToken token)
        {
            executions++;
            var nested = await fixture.Run(root, _ => Task.FromResult<string?>("nested passed"), "nested", cancellationToken: token);
            Assert.IsNotNull(nested.Result);
            return executions == 1 ? null : "passed";
        }

        Assert.IsNull((await fixture.Run(root, Execute)).Result);
        Assert.IsFalse((await fixture.Run(root, Execute)).Reused);
        Assert.IsTrue((await fixture.Run(root, Execute)).Reused);
        Assert.AreEqual(2, executions);
    }

    [TestMethod]
    public async Task InputsChangedDuringExecutionAreNotPublished()
    {
        using Fixture fixture = new();
        string root = fixture.CreateTree("tree");
        string source = Path.Combine(root, "lib", "Library.cs");
        string original = await File.ReadAllTextAsync(source);
        await fixture.Run(root, async _ =>
        {
            await File.AppendAllTextAsync(source, "// changed while executing", _);
            return "passed";
        });
        await File.WriteAllTextAsync(source, original);
        Assert.IsFalse((await fixture.Run(root, _ => Task.FromResult<string?>("passed again"))).Reused);
    }

    [TestMethod]
    public async Task CancellationDoesNotPublishSuccess()
    {
        using Fixture fixture = new();
        string root = fixture.CreateTree("tree");
        using CancellationTokenSource cancellation = new();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await fixture.Run(root, async _ =>
            {
                await cancellation.CancelAsync();
                return "must not publish";
            }, cancellationToken: cancellation.Token);
        });
        Assert.IsFalse((await fixture.Run(root, _ => Task.FromResult<string?>("passed"))).Reused);
    }

    [TestMethod]
    public async Task RenamingAnExecutionInputInvalidatesTheCachedResult()
    {
        using Fixture fixture = new();
        string root = fixture.CreateTree("tree");
        int executions = 0;
        Task<string?> Execute(CancellationToken _) => Task.FromResult<string?>((++executions).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsFalse((await fixture.Run(root, Execute)).Reused);
        File.Move(Path.Combine(root, "tests", "runtime.dll"), Path.Combine(root, "tests", "renamed.dll"));
        Assert.IsFalse((await fixture.Run(root, Execute)).Reused);
        Assert.IsTrue((await fixture.Run(root, Execute)).Reused);
        Assert.AreEqual(2, executions);
    }

    [TestMethod]
    public async Task ConcurrentTargetsSharePredictionForTheirEvaluatedGraph()
    {
        using Fixture fixture = new();
        string root = fixture.CreateTree("tree");
        ProjectGraph graph = new(Path.Combine(root, "tests", "tests.proj"));
        PredictionLogger logger = new();
        int executions = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(index => fixture.Run(root, _ =>
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult<string?>("passed");
        }, "mode-" + index, graph: graph, logger: logger)));
        Assert.AreEqual(1, logger.PredictionCount);
        Assert.AreEqual(4, executions);
    }

    [TestMethod]
    public async Task DisabledProjectExecutesEveryTimeWithoutOpeningTheCache()
    {
        using Fixture fixture = new();
        string root = fixture.CreateTree("tree");
        ProjectGraph graph = new(Path.Combine(root, "tests", "tests.proj"),
            new Dictionary<string, string> { ["MSBuildCacheEnabled"] = "false" });
        int executions = 0;
        Task<string?> Execute(CancellationToken _) => Task.FromResult<string?>((++executions).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsFalse((await fixture.Run(root, Execute, graph: graph)).Reused);
        Assert.IsFalse((await fixture.Run(root, Execute, graph: graph)).Reused);
        Assert.AreEqual(2, executions);
        Assert.IsFalse(Directory.Exists(fixture.CacheRoot));
    }

    private sealed class PredictionLogger : PluginLoggerBase
    {
        public int PredictionCount;
        public override bool HasLoggedErrors { get; protected set; }
        public override void LogError(string error) => HasLoggedErrors = true;
        public override void LogWarning(string warning) { }
        public override void LogMessage(string message, MessageImportance? messageImportance = null)
        {
            if (message.StartsWith("Executed project prediction on", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref PredictionCount);
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MSBuildCacheTargetTests", Guid.NewGuid().ToString("N"));

        public string CacheRoot => Path.Combine(_directory, "cache");

        public string CreateTree(string name)
        {
            string root = Path.Combine(_directory, name);
            Directory.CreateDirectory(Path.Combine(root, "lib", "unrelated"));
            Directory.CreateDirectory(Path.Combine(root, "tests"));
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.WriteAllText(Path.Combine(root, "lib", "lib.proj"), "<Project><Import Project=\"settings.props\" /><ItemGroup><Compile Include=\"Library.cs\" /><None Include=\"Library.cs;settings.props\" /></ItemGroup><Target Name=\"Build\" /></Project>");
            File.WriteAllText(Path.Combine(root, "lib", "Library.cs"), "public class Library { }");
            File.WriteAllText(Path.Combine(root, "lib", "notes.md"), "Project-local documentation");
            File.WriteAllText(Path.Combine(root, "lib", "unrelated", "Other.cs"), "public class Sibling { }");
            File.WriteAllText(Path.Combine(root, "tests", "tests.proj"), "<Project><ItemGroup><ProjectReference Include=\"../lib/lib.proj\" /><Compile Include=\"Tests.cs\" /><None Include=\"notes.md\" /><Content Include=\"fixture.json\" /><None Include=\"../data/linked.json\" CopyToOutputDirectory=\"PreserveNewest\" /><FixtureData Include=\"custom.json\" CopyToPublishDirectory=\"Always\" /></ItemGroup><Target Name=\"Build\" /></Project>");
            File.WriteAllText(Path.Combine(root, "tests", "Tests.cs"), "public class Tests { }");
            File.WriteAllText(Path.Combine(root, "tests", "notes.md"), "A bare None item");
            File.WriteAllText(Path.Combine(root, "tests", "fixture.json"), "{}");
            File.WriteAllText(Path.Combine(root, "tests", "custom.json"), "{}");
            File.WriteAllText(Path.Combine(root, "data", "linked.json"), "{}");
            File.WriteAllText(Path.Combine(root, "lib", "settings.props"), "<Project><PropertyGroup><FixtureValue>1</FixtureValue></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "tests", "runtime.dll"), "identical compiled runtime");
            return root;
        }

        public Task<(string? Result, bool Reused)> Run(string root, Func<CancellationToken, Task<string?>> execute,
            string mode = "parallel", ProjectGraph? graph = null, PluginLoggerBase? logger = null,
            CancellationToken cancellationToken = default)
        {
            string project = Path.Combine(root, "tests", "tests.proj");
            graph ??= new ProjectGraph(project);
            Dictionary<string, string> settings = new()
            {
                ["LocalCacheRootPath"] = Path.Combine(_directory, "cache"),
                ["LogDirectory"] = Path.Combine(_directory, "logs"),
                ["CacheUniverse"] = "in-process-target-tests",
                ["LocalCacheSizeInMegabytes"] = "100",
                ["AsyncCachePublishing"] = "false",
                ["AsyncCacheMaterialization"] = "false",
            };
            string[] sources = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            return MSBuildCacheLocalPlugin.RunTargetAsync(graph, project, "Test", sources,
                Directory.GetFiles(Path.Combine(root, "tests"), "*.dll"),
                new Dictionary<string, string> { ["Mode"] = mode }, root, settings,
                logger ?? NullPluginLogger.Instance, execute, cancellationToken);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}

