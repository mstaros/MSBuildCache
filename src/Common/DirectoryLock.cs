// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;

namespace Microsoft.MSBuildCache;

/// <summary>
/// Exclusive ownership of an embedded cache. Contending independent builds wait; descendants fail
/// explicitly rather than deadlocking while their parent owns the cache.
/// </summary>
internal sealed class DirectoryLock : IDisposable
{
    private readonly string _lockFilePath;
    private readonly PluginLoggerBase _logger;
    private FileStream? _lockFile;

    public DirectoryLock(string lockFilePath, PluginLoggerBase logger)
    {
        _lockFilePath = lockFilePath;
        _logger = logger;
    }

    public bool Acquire()
    {
        if (_lockFile != null)
        {
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_lockFilePath)!);
        FileStream? stream = null;
        try
        {
            try
            {
                stream = File.Open(_lockFilePath, FileMode.OpenOrCreate, System.IO.FileAccess.Write, FileShare.Read);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                return false;
            }

            using Process process = Process.GetCurrentProcess();
            byte[] owner = Encoding.UTF8.GetBytes(
                process.Id.ToString(CultureInfo.InvariantCulture) + ":" +
                process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            stream.SetLength(0);
            stream.Write(owner, 0, owner.Length);
            stream.Flush();
            _logger.LogMessage($"Acquired cache lock=[{_lockFilePath}]", MessageImportance.Low);
            _lockFile = stream;
            stream = null;
            return true;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        bool waiting = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Acquire())
            {
                // Cancellation can race acquisition. Never leave a newly acquired lock behind.
                if (cancellationToken.IsCancellationRequested)
                {
                    Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (waiting)
                {
                    _logger.LogMessage($"Acquired MSBuildCache after waiting {timer.ElapsedMilliseconds} ms: {_lockFilePath}");
                }

                return;
            }

            if (IsOwnedByAncestor())
            {
                throw new InvalidOperationException(
                    $"A parent build owns MSBuildCache '{_lockFilePath}'. A nested build cannot wait for its parent. " +
                    "Build the dependency in the existing project graph or explicitly disable caching for that nested invocation.");
            }

            if (!waiting)
            {
                _logger.LogMessage($"Waiting for another build to release MSBuildCache: {_lockFilePath}");
                waiting = true;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsOwnedByAncestor()
    {
        string owner;
        try
        {
            using FileStream stream = File.Open(_lockFilePath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream, Encoding.UTF8);
            owner = reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return false;
        }

        string[] fields = owner.Split(':');
        if (fields.Length != 2
            || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int ownerId)
            || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ownerStartTicks))
        {
            // A writer may still be publishing its owner record. Retry along with the file lock.
            return false;
        }

        using Process current = Process.GetCurrentProcess();
        return IsAncestor(ownerId, ownerStartTicks, current.Id);
    }

    internal static bool IsAncestor(int ownerId, long ownerStartTicks, int processId)
    {
        if (ownerId == processId)
        {
            // Same-process plugin reentry is rejected by SinglePluginInstanceLock.
            return false;
        }

        HashSet<int> seen = new();
        while (processId > 0 && seen.Add(processId))
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (processId == ownerId)
                {
                    return process.StartTime.ToUniversalTime().Ticks == ownerStartTicks;
                }

                // An older ancestor cannot descend from this newer owner. This also avoids
                // querying unrelated service/system ancestors when builds have a common launcher.
                if (process.StartTime.ToUniversalTime().Ticks < ownerStartTicks)
                {
                    return false;
                }

                processId = GetParentProcessId(process);
            }
            catch (ArgumentException)
            {
                // The process exited during the ancestry check.
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return false;
    }

    private static int GetParentProcessId(Process process)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // PROCESS_BASIC_INFORMATION is six pointer-sized fields; the last is the parent PID.
            IntPtr[] information = new IntPtr[6];
            int status = NtQueryInformationProcess(process.Handle, 0, information, IntPtr.Size * information.Length, out _);
            if (status != 0)
            {
                throw new Win32Exception($"Cannot inspect cache-lock owner ancestry (NTSTATUS 0x{status:X8}).");
            }

            return checked((int)information[5].ToInt64());
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string stat;
            try
            {
                stat = File.ReadAllText("/proc/" + process.Id.ToString(CultureInfo.InvariantCulture) + "/stat");
            }
            catch (FileNotFoundException)
            {
                return 0;
            }
            catch (DirectoryNotFoundException)
            {
                return 0;
            }

            // comm is parenthesized and can contain spaces or closing parentheses.
            string[] fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
            return int.Parse(fields[1], CultureInfo.InvariantCulture);
        }

        throw new PlatformNotSupportedException("Waiting for MSBuildCache requires process ancestry checks on this platform.");
    }

    private static bool IsSharingViolation(IOException exception)
    {
        int error = exception.HResult & 0xffff;
        return error is 32 or 33 || (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && error == 11);
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 6)] IntPtr[] processInformation,
        int processInformationLength,
        out int returnLength);

    public void Dispose()
    {
        if (_lockFile is null)
        {
            return;
        }

        try
        {
            // Clear live-owner metadata before releasing the lock. A subsequent owner must not
            // briefly expose this process as its owner while publishing its own record.
            _lockFile.SetLength(0);
        }
        finally
        {
            _lockFile.Dispose();
            _lockFile = null;
        }
    }
}
