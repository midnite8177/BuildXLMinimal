// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using BuildXL.Native.IO;
using BuildXL.Native.Processes.Windows;
using BuildXL.Utilities.Core;

namespace BuildXL.Processes
{
    /// <summary>
    /// Dumps processes
    /// </summary>
    public static partial class ProcessDumper
    {
        /// <summary>
        /// Protects calling <see cref="ProcessUtilitiesWin.MiniDumpWriteDump(IntPtr, uint, SafeHandle, uint, IntPtr, IntPtr, IntPtr)"/>, since all Windows DbgHelp functions are single threaded.
        /// </summary>
        private static readonly object s_dumpProcessLock = new object();

        private static readonly HashSet<string> s_skipProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "conhost", // Conhost dump causes native error 0x8007012b (Only part of a ReadProcessMemory or WriteProcessMemory request was completed) - Build 1809
        };

        /// <summary>
        /// Attempts to create a process memory dump at the requested location. Any file already existing at that location will be overwritten
        /// </summary>
        public static bool TryDumpProcess(Process process, string dumpPath, out Exception dumpCreationException, bool compress = false, Action<string> debugLogger = null) =>
            TryDumpProcess(process.SafeHandle, process.Id, process.ProcessName, dumpPath, out dumpCreationException, compress, debugLogger);

        /// <summary>
        /// Attempts to create a process memory dump at the requested location. Any file already existing at that location will be overwritten
        /// </summary>
        public static bool TryDumpProcess(SafeHandle processHandle, int processId, string processName, string dumpPath, out Exception dumpCreationException, bool compress = false, Action<string> debugLogger = null)
        {
            if (OperatingSystemHelper.IsMacOS)
            {
                dumpCreationException = new PlatformNotSupportedException();
                return false;
            }

            if (OperatingSystemHelper.IsLinuxOS)
            {
                return TryDumpLinuxProcess(processId, dumpPath, out dumpCreationException, debugLogger);
            }

            try
            {
                bool dumpResult = TryDumpProcess(processHandle, processId, dumpPath, out dumpCreationException, compress);
                if (!dumpResult)
                {
                    Contract.Assume(dumpCreationException != null, "Exception was null on failure.");
                }

                return dumpResult;
            }
            catch (Win32Exception ex)
            {
                dumpCreationException = new BuildXLException("Failed to get process handle to create a process dump for: " + processName, ex);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                dumpCreationException = new BuildXLException("Failed to get process handle to create a process dump for: " + processName, ex);
                return false;
            }
            catch (NotSupportedException ex)
            {
                dumpCreationException = new BuildXLException("Failed to get process handle to create a process dump for: " + processName, ex);
                return false;
            }
        }

        /// <summary>
        /// Attempts to create a process memory dump at the requested location. Any file already existing at that location will be overwritten
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:DoNotDisposeObjectsMultipleTimes")]
        public static bool TryDumpProcess(SafeHandle processHandle, int processId, string dumpPath, out Exception dumpCreationException, bool compress = false)
        {
            if (OperatingSystemHelper.IsUnixOS)
            {
                dumpCreationException = new PlatformNotSupportedException();
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dumpPath));

                FileUtilities.DeleteFile(dumpPath);
                var uncompressedDumpPath = dumpPath;

                if (compress)
                {
                    uncompressedDumpPath = dumpPath + ".dmp.tmp";
                    Analysis.IgnoreResult(FileUtilities.TryDeleteFile(uncompressedDumpPath));
                }

                using (FileStream fs = new FileStream(uncompressedDumpPath, FileMode.Create))
                {
                    lock (s_dumpProcessLock)
                    {
                        bool dumpSuccess = ProcessUtilitiesWin.MiniDumpWriteDump(
                            hProcess: processHandle.DangerousGetHandle(),
                            processId: (uint)processId,
                            hFile: fs.SafeFileHandle,
                            dumpType: (uint)ProcessUtilitiesWin.MINIDUMP_TYPE.MiniDumpWithFullMemory,
                            expParam: IntPtr.Zero,
                            userStreamParam: IntPtr.Zero,
                            callbackParam: IntPtr.Zero);

                        if (!dumpSuccess)
                        {
                            var code = Marshal.GetLastWin32Error();
                            var message = new Win32Exception(code).Message;

                            throw new BuildXLException($"Failed to create process dump. Native error: ({code:x}) {message}, dump-path={dumpPath}");
                        }
                    }
                }

                if (compress)
                {
                    using (FileStream compressedDumpStream = new FileStream(dumpPath, FileMode.Create))
                    using (var archive = new ZipArchive(compressedDumpStream, ZipArchiveMode.Create))
                    {
                        var entry = archive.CreateEntry(Path.GetFileNameWithoutExtension(dumpPath) + ".dmp", CompressionLevel.Fastest);

                        using (FileStream uncompressedDumpStream = File.Open(uncompressedDumpPath, FileMode.Open))
                        using (var entryStream = entry.Open())
                        {
                            uncompressedDumpStream.CopyTo(entryStream);
                        }
                    }

                    FileUtilities.DeleteFile(uncompressedDumpPath);
                }

                dumpCreationException = null;
                return true;
            }
            catch (Exception ex)
            {
                dumpCreationException = ex;
                return false;
            }
        }

        /// <summary>
        /// Attempts to dump all processes in a process tree. Any files existing in the dump directory will be deleted.
        /// This method utilizes WMI query to obtain child processes. One of the issues with this method is it cannot retrieve childprocesses if the parent does not exist. This method is supposed to be utilized when the OS is non-windows or when there is no scope of using JobObject ex: ExternalSandboxedProcess and UnSandboxedProcess.
        /// But another implementation of this method is available in JobObjectProcessDumper.cs which makes use of Jobobject to retrieve child processes and this resolves the above issue. Refer DetouredProcess or SandboxedProcess for reference on its usage.
        /// This new implementation is suggested to be used in all future scenarios where there is a scope of utilizing JobObject in Windows OS.
        /// </summary>
        public static bool TryDumpProcessAndChildren(int parentProcessId, string dumpDirectory, out Exception primaryDumpCreationException, int maxTreeDepth = 20, Action<string> debugLogger = null)
        {
            if (OperatingSystemHelper.IsMacOS)
            {
                primaryDumpCreationException = new PlatformNotSupportedException();
                return false;
            }

            DateTime treeDumpInitiateTime = DateTime.Now;

            // Ensure directory exists.
            try
            {
                Directory.CreateDirectory(dumpDirectory);
            }
            catch (Exception ex)
            {
                primaryDumpCreationException = ex;
                return false;
            }

            List<KeyValuePair<string, int>> processesToDump;

            try
            {
                processesToDump = GetProcessTreeIds(parentProcessId, maxTreeDepth);
            }
            catch (BuildXLException ex)
            {
                // We couldn't enumerate the child process tree. Fail the entire operation
                primaryDumpCreationException = ex;
                return false;
            }

            // When dumping individual processes, allow any one dump to fail and keep on processing to collect
            // as many dumps as possible. We keep the first exception only, which is what we report
            bool success = true;
            primaryDumpCreationException = null;
            foreach (var process in processesToDump)
            {
                var maybeProcess = TryGetProcesById(process.Value);
                if (!maybeProcess.Succeeded)
                {
                    primaryDumpCreationException = primaryDumpCreationException ?? maybeProcess.Failure.CreateException();
                    success = false;
                    continue;
                }

                Process p = maybeProcess.Result;

                // Process details cannot be accessed after the process exits. Noop if the process has exited
                string processName = null;
                DateTime processStartTime;
                try
                {
                    processName = p.ProcessName;
                    processStartTime = p.StartTime;
                }
                catch (InvalidOperationException ex)
                {
                    if (!p.HasExited)
                    {
                        primaryDumpCreationException = primaryDumpCreationException ?? ex;
                        return false;
                    }

                    continue;
                }

                if (s_skipProcesses.Contains(processName) || processStartTime > treeDumpInitiateTime)
                {
                    // Ignore processes explicitly configured to be skipped or 
                    // that were created after the tree dump was initiated in case of the likely rare
                    // possibility that a pid got immediately reused.
                    continue;
                }

                var dumpPath = Path.Combine(dumpDirectory, process.Key + ".dmp");
                if (!TryDumpProcess(p, dumpPath, out var e, debugLogger: debugLogger))
                {
                    if (e != null)
                    {
                        Contract.Assume(e != null, $"Exception should not be null on failure. Dump-path: {dumpPath}");
                    }
                    primaryDumpCreationException = primaryDumpCreationException ?? e;
                    success = false;
                }
            }

            return success;
        }

        /// <summary>
        /// Gets the identifiers and process ids of all active processes in a process tree
        /// </summary>
        /// <remarks>
        /// The identifier is made up of a chain of numbers to encode the process tree hierarchy and the process name.
        /// The hierarchy is a number representing the nth chid process of the parent at each level. For example:
        ///
        /// 1_dog
        /// 1_1_cat
        /// 1_2_elephant
        /// 1_3_fish
        /// 1_2_1_donkey
        /// 1_2_2_mule
        ///
        /// represents a hierarchy like this:
        ///                dog
        ///                 |
        ///      ---------------------
        ///      |          |        |
        ///     cat      elephant   fish
        ///                 |
        ///           -------------
        ///          |            |
        ///        donkey        mule
        /// </remarks>
        /// <param name="parentProcessId">ID of parent process</param>
        /// <param name="maxTreeDepth">Maximum depth of process tree to continue dumping child processes for</param>
        /// <returns>Collection of {identifier, pid}</returns>
        /// <exception cref="BuildXLException">May throw a BuildXLException on failure</exception>
        internal static List<KeyValuePair<string, int>> GetProcessTreeIds(int parentProcessId, int maxTreeDepth)
        {
            if (OperatingSystemHelper.IsLinuxOS)
            {
                return GetChildLinuxProcessTreeIds(parentProcessId, maxTreeDepth, isRootQuery: true);
            }

            return GetChildProcessTreeIds(parentProcessId, maxTreeDepth, isRootQuery: true);
        }

        private static List<KeyValuePair<string, int>> GetChildProcessTreeIds(int idToQuery, int maxTreeDepth, bool isRootQuery)
        {
            List<KeyValuePair<string, int>> result = new List<KeyValuePair<string, int>>();

            // If we don't have access to get the process tree, we'll still at least get the root process. 
            try
            {
                if (isRootQuery || maxTreeDepth > 0)
                {
                    string queryBase = isRootQuery ? "select * from win32_process where ProcessId=" :
                        "select * from win32_process where ParentProcessId=";
                    using (var searcher = new ManagementObjectSearcher(queryBase + idToQuery))
                    {
                        var processes = searcher.Get();

                        int counter = 0;
                        foreach (ManagementObject item in processes)
                        {
                            counter++;
                            int processId = Convert.ToInt32(item["ProcessId"].ToString(), CultureInfo.InvariantCulture);
                            string processName = item["Name"].ToString();

                            // Skip any processes that aren't being run by the current username
                            ManagementBaseObject getOwner = item.InvokeMethod("GetOwner", null, null);
                            object user = getOwner["User"];
                            if (user == null || user.ToString() != Environment.UserName)
                            {
                                continue;
                            }

                            result.Add(GetCoreDumpName(counter, processId, processName));

                            foreach (var child in GetChildProcessTreeIds(processId, maxTreeDepth - 1, false))
                            {
                                result.Add(GetCoreDumpName(counter, child.Value, child.Key));
                            }
                        }
                    }
                }

                if (isRootQuery && result.Count == 0)
                {
                    throw new ArgumentException($"Process with an Id of {idToQuery} is inaccessible or not running.");
                }

                return result;
            }
            catch (Exception ex)
            {
                // Catching all exception is OK because there are tons of exceptions that can happen, from
                // creating ManagementOBjectSearcher to enumerating the processes in ManagementOBjectSearcher instance.
                // Moreover, we don't care about those exceptions.
                throw new BuildXLException("Failed to enumerate child processes", ex);
            }
        }

        private static KeyValuePair<string, int> GetCoreDumpName(int counter, int processId, string name) => new(counter.ToString(CultureInfo.InvariantCulture) + "_" + name, processId);

        private static Possible<Process> TryGetProcesById(int processId)
        {
            try
            {
                return Process.GetProcessById(processId);
            }
            catch (InvalidOperationException ex)
            {
                return new Failure<string>("Could not get process by id: " + ex.GetLogEventMessage());
            }
            catch (ArgumentException ex)
            {
                return new Failure<string>("Could not get process by id: " + ex.GetLogEventMessage());
            }
        }
    }
}
