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
        using Fixture fixture = new();
        string first = fixture.CreateTree("first");
        string second = fixture.CreateTree("second");
        int executions = 0;
        Task<string?> Execute(CancellationToken _) => Task.FromResult<string?>((++executions).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsFalse((await fixture.Run(first, Execute)).Reused);
        await File.WriteAllTextAsync(Path.Combine(second, "notes.md"), "Unrelated documentation");
        Assert.IsTrue((await fixture.Run(second, Execute)).Reused);
        Assert.AreEqual(1, executions);
        await File.AppendAllTextAsync(Path.Combine(second, "lib", "Library.cs"), "// changed implementation");
        Assert.IsFalse((await fixture.Run(second, Execute)).Reused);
        await File.AppendAllTextAsync(Path.Combine(second, "tests", "runtime.dll"), "changed runtime");
        Assert.IsFalse((await fixture.Run(second, Execute)).Reused);
        Assert.IsFalse((await fixture.Run(second, Execute, "serial")).Reused);
        Assert.AreEqual(4, executions);
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
            var nested = await fixture.Run(root, _ => Task.FromResult<string?>("nested passed"), "nested", token);
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

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MSBuildCacheTargetTests", Guid.NewGuid().ToString("N"));

        public string CreateTree(string name)
        {
            string root = Path.Combine(_directory, name);
            Directory.CreateDirectory(Path.Combine(root, "lib"));
            Directory.CreateDirectory(Path.Combine(root, "tests"));
            File.WriteAllText(Path.Combine(root, "lib", "lib.proj"), "<Project><ItemGroup><Compile Include=\"Library.cs\" /></ItemGroup><Target Name=\"Build\" /></Project>");
            File.WriteAllText(Path.Combine(root, "lib", "Library.cs"), "public class Library { }");
            File.WriteAllText(Path.Combine(root, "tests", "tests.proj"), "<Project><ItemGroup><ProjectReference Include=\"../lib/lib.proj\" /><Compile Include=\"Tests.cs\" /></ItemGroup><Target Name=\"Build\" /></Project>");
            File.WriteAllText(Path.Combine(root, "tests", "Tests.cs"), "public class Tests { }");
            File.WriteAllText(Path.Combine(root, "tests", "runtime.dll"), "identical compiled runtime");
            return root;
        }

        public Task<(string? Result, bool Reused)> Run(string root, Func<CancellationToken, Task<string?>> execute,
            string mode = "parallel", CancellationToken cancellationToken = default)
        {
            string project = Path.Combine(root, "tests", "tests.proj");
            ProjectGraph graph = new(project);
            Dictionary<string, string> settings = new()
            {
                ["LocalCacheRootPath"] = Path.Combine(_directory, "cache"),
                ["LogDirectory"] = Path.Combine(_directory, "logs"),
                ["CacheUniverse"] = "in-process-target-tests",
                ["LocalCacheSizeInMegabytes"] = "100",
                ["AsyncCachePublishing"] = "false",
                ["AsyncCacheMaterialization"] = "false",
            };
            string[] sources = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".proj", StringComparison.Ordinal)).ToArray();
            return MSBuildCacheLocalPlugin.RunTargetAsync(graph, project, "Test", sources,
                [Path.Combine(root, "tests", "runtime.dll")],
                new Dictionary<string, string> { ["Mode"] = mode }, root, settings,
                NullPluginLogger.Instance, execute, cancellationToken);
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
