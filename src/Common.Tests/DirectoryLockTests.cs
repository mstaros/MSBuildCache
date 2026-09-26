// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.MSBuildCache.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class DirectoryLockTests
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable. Justification: Always set by MSTest
    public TestContext TestContext { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    [TestMethod]
    public void Acquire()
    {
        string lockFilePath = GetLockFilePath();
        using (DirectoryLock directoryLock1 = new(lockFilePath, NullPluginLogger.Instance))
        {
            Assert.IsTrue(directoryLock1.Acquire());
        }
    }

    [TestMethod]
    public void Reentry()
    {
        string lockFilePath = GetLockFilePath();
        using (DirectoryLock directoryLock1 = new(lockFilePath, NullPluginLogger.Instance))
        {
            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue(directoryLock1.Acquire());
            }
        }
    }

    [TestMethod]
    public void Contention()
    {
        string lockFilePath = GetLockFilePath();
        using (DirectoryLock directoryLock1 = new(lockFilePath, NullPluginLogger.Instance))
        using (DirectoryLock directoryLock2 = new(lockFilePath, NullPluginLogger.Instance))
        {
            Assert.IsTrue(directoryLock1.Acquire());

            // Second locker cannot acquire
            Assert.IsFalse(directoryLock2.Acquire());

            directoryLock1.Dispose();

            // Second locker can now acquire
            Assert.IsTrue(directoryLock2.Acquire());
        }
    }

    [TestMethod]
    public async Task WaitsForOwnerThenAcquires()
    {
        string path = GetLockFilePath();
        using DirectoryLock owner = new(path, NullPluginLogger.Instance);
        MockPluginLogger logger = new();
        using DirectoryLock waiter = new(path, logger);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Assert.IsTrue(owner.Acquire());
        Task acquisition = waiter.AcquireAsync(timeout.Token);
        Assert.IsFalse(acquisition.IsCompleted);
        owner.Dispose();
        await acquisition;
        Assert.IsTrue(logger.LogEntries.Any(entry => entry.Message.StartsWith("Waiting for another build", StringComparison.Ordinal)));
        using DirectoryLock competitor = new(path, NullPluginLogger.Instance);
        Assert.IsFalse(competitor.Acquire());
    }

    [TestMethod]
    public async Task CancellingWaitDoesNotAcquireOrLeakOwnership()
    {
        string path = GetLockFilePath();
        using DirectoryLock owner = new(path, NullPluginLogger.Instance);
        using DirectoryLock waiter = new(path, NullPluginLogger.Instance);
        using CancellationTokenSource cancellation = new();
        Assert.IsTrue(owner.Acquire());
        Task acquisition = waiter.AcquireAsync(cancellation.Token);
        await cancellation.CancelAsync();
        try
        {
            await acquisition;
            Assert.Fail("The waiting acquisition should be cancelled.");
        }
        catch (OperationCanceledException)
        {
        }

        owner.Dispose();
        using DirectoryLock next = new(path, NullPluginLogger.Instance);
        Assert.IsTrue(next.Acquire());
    }

    [TestMethod]
    public async Task InvalidCacheDirectoryIsNotTreatedAsContention()
    {
        string file = GetLockFilePath() + ".parent";
        await File.WriteAllTextAsync(file, "a file, not a cache directory");
        try
        {
            using DirectoryLock directoryLock = new(Path.Combine(file, "lock"), NullPluginLogger.Instance);
            await Assert.ThrowsExactlyAsync<IOException>(() => directoryLock.AcquireAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public async Task ReadOnlyLockIsNotTreatedAsContention()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Windows file attribute access check.");
        }

        string path = GetLockFilePath();
        await File.WriteAllTextAsync(path, string.Empty);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            using DirectoryLock directoryLock = new(path, NullPluginLogger.Instance);
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => directoryLock.AcquireAsync(CancellationToken.None));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [TestMethod]
    public async Task IndependentProcessWaitsAndLiveAncestryIsRecognized()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("Windows process/lock integration check.");
        }

        string path = GetLockFilePath();
        string escapedPath = path.Replace("'", "''", StringComparison.Ordinal);
        string script = "$s=[IO.File]::Open('" + escapedPath + "',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::Write,[IO.FileShare]::Read);"
            + "$p=[Diagnostics.Process]::GetCurrentProcess();"
            + "$b=[Text.Encoding]::UTF8.GetBytes($p.Id.ToString()+':'+$p.StartTime.ToUniversalTime().Ticks.ToString());"
            + "$s.SetLength(0);$s.Write($b,0,$b.Length);$s.Flush();"
            + "[Console]::WriteLine('locked');[Console]::ReadLine()|Out-Null;$s.Dispose();";
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using Process child = Process.Start(startInfo)!;
        try
        {
            Task<string?> ready = child.StandardOutput.ReadLineAsync();
            Assert.AreSame(ready, await Task.WhenAny(ready, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.AreEqual("locked", await ready);
            using Process parent = Process.GetCurrentProcess();
            long parentStart = parent.StartTime.ToUniversalTime().Ticks;
            Assert.IsTrue(DirectoryLock.IsAncestor(parent.Id, parentStart, child.Id));
            Assert.IsFalse(DirectoryLock.IsAncestor(parent.Id, parentStart + 1, child.Id));

            using DirectoryLock waiter = new(path, NullPluginLogger.Instance);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            Task acquisition = waiter.AcquireAsync(timeout.Token);
            Assert.IsFalse(acquisition.IsCompleted);
            await child.StandardInput.WriteLineAsync("release");
            await child.StandardInput.FlushAsync();
            await acquisition;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }

            await child.WaitForExitAsync();
        }
    }

    [TestMethod]
    public async Task StressTest()
    {
        string path = GetLockFilePath();
        int activeOwners = 0;
        int acquired = 0;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        Task[] contenders = Enumerable.Range(0, 16).Select(async _ =>
        {
            for (int iteration = 0; iteration < 5; iteration++)
            {
                using DirectoryLock directoryLock = new(path, NullPluginLogger.Instance);
                await directoryLock.AcquireAsync(timeout.Token);
                Assert.AreEqual(1, Interlocked.Increment(ref activeOwners));
                await Task.Yield();
                Interlocked.Increment(ref acquired);
                Assert.AreEqual(0, Interlocked.Decrement(ref activeOwners));
            }
        }).ToArray();
        await Task.WhenAll(contenders);
        Assert.AreEqual(80, acquired);
    }

    private string GetLockFilePath() => Path.Combine(TestContext.TestRunDirectory!, TestContext.TestName! + ".lock");
}
