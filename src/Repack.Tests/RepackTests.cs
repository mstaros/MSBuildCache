// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.MSBuildCache.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Repack.Tests;

// Check to make sure that the assemblies that defined the MSBuild/Plugin interface are not merged
[TestClass]
public class RepackTests
{
    [TestMethod]
    [DataRow(typeof(MSBuildCacheLocalPlugin))]
    [DataRow(typeof(SharedCompilation.ResolveFileAccesses))]
    public void PluginInterfaceAssembliesNotMerged(Type typeToCheck)
    {
#if DEBUG
        // Using Assert.Inconclusive instead of removing the entire test for visibility that the test exists.
        Assert.Inconclusive("This test only applies to Release builds.");
#endif

        HashSet<string> references = typeToCheck.Assembly
            .GetReferencedAssemblies()
            .Where(a => a.Name is not null)
            .Select(reference => reference.Name!)
            .ToHashSet(StringComparer.Ordinal);

        // Check to make sure that each of the interface assemblies are still actually referenced
        foreach (string expectedRefFileName in PluginInterfaceTypeCheckTests.PluginInterfaceNuGetAssemblies)
        {
            string expectedRef = Path.GetFileNameWithoutExtension(expectedRefFileName);
            Assert.Contains(expectedRef, references);
        }
    }

    // BuildXL's distributed cache dependencies stay out of the plugin; the local cache does not use them.
    [TestMethod]
    public void CloudDependenciesNotMerged()
    {
#if DEBUG
        Assert.Inconclusive("This test only applies to Release builds.");
#endif

        string[] unmerged = ["Azure", "Microsoft.Azure", "Kusto", "Microsoft.Identity", "Microsoft.IdentityModel", "System.IdentityModel", "Grpc", "Google.Protobuf", "ProtoBuf", "System.ServiceModel", "System.ClientModel"];
        using FileStream stream = File.OpenRead(typeof(MSBuildCacheLocalPlugin).Assembly.Location);
        using PEReader reader = new(stream);
        MetadataReader metadata = reader.GetMetadataReader();
        string[] merged = metadata.TypeDefinitions
            .Select(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Namespace))
            .Where(ns => unmerged.Any(prefix => ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.AreEqual(0, merged.Length, string.Join(", ", merged));
    }

    // MSBuild finds the plugin by scanning its exported types. The scan must succeed with only what ships beside the plugin.
    [TestMethod]
    public void ExportedTypesLoadWithoutUnmergedAssemblies()
    {
#if DEBUG
        Assert.Inconclusive("This test only applies to Release builds.");
#endif

        string pluginPath = typeof(MSBuildCacheLocalPlugin).Assembly.Location;
        PluginOnlyLoadContext context = new();
        try
        {
            Type[] exported = context.LoadFromAssemblyPath(pluginPath).GetExportedTypes();
            Assert.IsTrue(exported.Any(type => type.FullName == typeof(MSBuildCacheLocalPlugin).FullName));
        }
        finally
        {
            context.Unload();
        }
    }

    // Resolves MSBuild's interface assemblies and the shared framework from the host; anything else is missing, as beside the packaged plugin.
    private sealed class PluginOnlyLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string name = assemblyName.Name ?? string.Empty;
            bool hostProvided = PluginInterfaceTypeCheckTests.PluginInterfaceNuGetAssemblies.Any(file => Path.GetFileNameWithoutExtension(file) == name)
                || File.Exists(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), name + ".dll"));
            return hostProvided ? null : throw new FileNotFoundException($"{name} does not ship with the plugin.");
        }
    }
}
