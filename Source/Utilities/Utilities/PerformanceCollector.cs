// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using Microsoft.Win32.SafeHandles;
using static BuildXL.Interop.Dispatch;
using static BuildXL.Interop.Unix.Memory;
using static BuildXL.Interop.Unix.Processor;
using static BuildXL.Interop.Windows.IO;
using static BuildXL.Interop.Windows.Memory;
using static BuildXL.Interop.Windows.Processor;

namespace BuildXL.Utilities
{
    /// <summary>
    /// Collects system performance data
    /// </summary>
    public sealed class PerformanceCollector : IDisposable
    {
        private readonly int m_processorCount;

        // The thread that collects performance info
        private Timer m_collectionTimer;
        private readonly object m_collectionTimerLock = new object();
        private readonly object m_collectLock = new object();
        private readonly TimeSpan m_collectionFrequency;
        private readonly bool m_collectHeldBytesFromGC;
        private readonly TestHooks m_testHooks;
        private readonly Action<Exception> m_errorHandler;

        // Objects that aggregate performance info during their lifetime
        private readonly HashSet<Aggregator> m_aggregators = new HashSet<Aggregator>();

        #region State needed for collecting various metrics

        // Used for calculating the Process CPU time
        private DateTime m_processTimeLastCollectedAt = DateTime.MinValue;
        private TimeSpan m_processTimeLastValue;

        // Used for calculating the Machine CPU time
        private DateTime m_machineTimeLastCollectedAt = DateTime.MinValue;
        private long m_machineTimeLastVale;
        private CpuLoadInfo m_lastCpuLoadInfo;

        // Used for collecting disk activity
        private readonly (DriveInfo driveInfo, SafeFileHandle safeFileHandle, DISK_PERFORMANCE diskPerformance)[] m_drives;

        // NetworkMonitor to measure network bandwidth of the computer.
        private NetworkMonitor m_networkMonitor;

        // Used for calculating the network sample time
        private DateTime m_networkTimeLastCollectedAt = DateTime.MinValue;

        #endregion

        /// <summary>
        /// Gets the drives registered with the <see cref="PerformanceCollector"/>
        /// </summary>
        public IEnumerable<string> GetDrives()
        {
            return OperatingSystemHelper.IsUnixOS ? // Drive names are processed differently depending on OS
                m_drives.Select(t => t.driveInfo.Name) :
                m_drives.Where(t => !t.safeFileHandle.IsInvalid && !t.safeFileHandle.IsClosed).Select(t => t.driveInfo.Name.TrimStart('\\').TrimEnd('\\').TrimEnd(':'));
        }

        /// <summary>
        /// Test hooks for PerformanceCollector
        /// </summary>
        public class TestHooks
        {
            /// <summary>
            /// Return a specific AvailableDiskSpace
            /// </summary>
            public int? AvailableDiskSpace;

            /// <summary>
            /// Return a specific AvailableDiskSpace after <see cref="DelayAvailableDiskSpaceUtcTime"/>
            /// </summary>
            public int? DelayedAvailableDiskSpace;

            /// <summary>
            /// <see cref="DelayedAvailableDiskSpace"/> will be set after set Utc Time
            /// </summary>
            public DateTime DelayAvailableDiskSpaceUtcTime;
        }

        // Used for calculating the BuildXL CPU time
        private readonly Func<(ulong? KernelTime, ulong? UserTime, ulong? NumProcesses)> m_queryJobObject;
        private DateTime m_jobObjectLastCollectedAt = DateTime.MinValue;
        private ulong m_jobObjectTotalTimeLastValue;
        private readonly bool m_logWmiCounters;

        /// <summary>
        /// Creates a new PerformanceCollector with the specified collection frequency.
        /// </summary>
        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope",
            Justification = "Handle is owned by PerformanceCollector and is disposed on its disposal")]
        public PerformanceCollector(TimeSpan collectionFrequency, bool logWmiCounters = true, bool collectBytesHeld = false, Action<Exception> initializationErrorHandler = null, Action<Exception> collectionErrorHandler = null, TestHooks testHooks = null, Func<(ulong?, ulong?, ulong?)> queryJobObject = null)
        {
            m_collectionFrequency = collectionFrequency;
            m_processorCount = Environment.ProcessorCount;
            m_collectHeldBytesFromGC = collectBytesHeld;
            m_testHooks = testHooks;
            m_queryJobObject = queryJobObject;
            m_logWmiCounters = logWmiCounters;
            m_errorHandler = collectionErrorHandler;

            // Figure out which drives we want to get counters for
            var drives = new Dictionary<string, (DriveInfo, SafeFileHandle, DISK_PERFORMANCE)>(OperatingSystemHelper.PathComparer);

            foreach (var drive in DriveInfo.GetDrives())
            {
                // Occasionally, diskStat.Drive seems to have a duplicate diskName. Handle the conflict to avoid an exception so the build can continue.
                if (drives.ContainsKey(drive.Name))
                {
                    continue;
                }

                if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                {
                    if (OperatingSystemHelper.IsUnixOS)
                    {
                        drives.Add(drive.Name, (drive, null, default));
                    }
                    else if (drive.Name.Length == 3 && drive.Name.EndsWith(@":\", StringComparison.OrdinalIgnoreCase))
                    {
                        string path = @"\\.\" + drive.Name[0] + ":";
                        SafeFileHandle handle = CreateFileW(path, FileDesiredAccess.None, FileShare.Read, IntPtr.Zero, FileMode.Open, FileFlagsAndAttributes.FileAttributeNormal, IntPtr.Zero);
                        if (!handle.IsClosed && !handle.IsInvalid)
                        {
                            drives.Add(drive.Name, (drive, handle, default));
                        }
                        else
                        {
                            handle.Dispose();
                        }
                    }
                }
            }

            m_drives = drives.Values.ToArray();

            // Initialize network telemetry objects
            InitializeNetworkMonitor();

            if (!OperatingSystemHelper.IsUnixOS)
            {
                InitializeWMIAsync().Forget(initializationErrorHandler);
            }

            // Perform all initialization before starting the timer
            m_collectionTimer = new Timer(Collect, null, 0, 0);
        }

        private volatile ManagementScope m_wmiScope;
        private ManagementObjectSearcher m_modifiedPageSizeWMIQuery, m_cpuWMIQuery, m_systemWMIQuery;
        private ManagementOperationObserver m_modifiedPageSizeWMIWatcher, m_cpuWMIWatcher, m_systemWMIWatcher;
        private double? m_modifiedPagelistBytes = null;
        private double? m_machineCpuWMI = null;
        private int? m_contextSwitchesPerSec = null;
        private int? m_processes = null;
        private int? m_cpuQueueLength = null;
        private int? m_threads = null;

        private async Task InitializeWMIAsync()
        {
            if (!m_logWmiCounters)
            {
                return;
            }

            await Task.Yield();

            m_wmiScope = new ManagementScope(string.Format("\\\\{0}\\root\\CIMV2", "."), null);
            m_wmiScope.Connect();

            ObjectQuery memoryQuery = new ObjectQuery("SELECT ModifiedPageListBytes FROM Win32_PerfFormattedData_PerfOS_Memory");
            m_modifiedPageSizeWMIQuery = new ManagementObjectSearcher(m_wmiScope, memoryQuery);
            m_modifiedPageSizeWMIWatcher = new ManagementOperationObserver();
            m_modifiedPageSizeWMIWatcher.ObjectReady += new ObjectReadyEventHandler(UpdateModifiedPageSize);

            ObjectQuery cpuQuery = new ObjectQuery("SELECT * FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name=\"_Total\"");
            m_cpuWMIQuery = new ManagementObjectSearcher(m_wmiScope, cpuQuery);
            m_cpuWMIWatcher = new ManagementOperationObserver();
            m_cpuWMIWatcher.ObjectReady += new ObjectReadyEventHandler(UpdateWMICpu);

            ObjectQuery systemQuery = new ObjectQuery("SELECT * FROM Win32_PerfFormattedData_PerfOS_System");
            m_systemWMIQuery = new ManagementObjectSearcher(m_wmiScope, systemQuery);
            m_systemWMIWatcher = new ManagementOperationObserver();
            m_systemWMIWatcher.ObjectReady += new ObjectReadyEventHandler(UpdateWMISystem);
        }

        /// <summary>
        /// Collects a sample and sends the data to any aggregators
        /// </summary>
        private void Collect(object state)
        {
            lock (m_aggregators)
            {
                if (m_aggregators.Count == 0)
                {
                    // Nobody's listening. No-op
                    ReschedulerTimer();
                    return;
                }
            }

            try
            {
                CollectOnce();
            }
            catch (Exception e)
            {
                Exception exceptionToReport = e;

                // We've observed failures to collect the information on Linux (see bug #2070047)
                // According to the dotnet team, this may happen if something happens reading/processing the process'
                // stat file (see https://github.com/dotnet/runtime/issues/87356).
                // Let's try to get the file contents to hopefully help understand this issue better.
                // TODO: We can remove this after a number of instances (tracking with bug #2073411).
                if (OperatingSystemHelper.IsLinuxOS)
                {
                    string procSelfStatDiagnostics = "/proc/self/stat contents:\n";
                    try
                    {
                        procSelfStatDiagnostics += File.ReadAllText("/proc/self/stat");
                    }
#pragma warning disable EPC12 // Suspicious exception handling: only Message property is observed in exception block.
                    catch (Exception ex)
                    {
                        procSelfStatDiagnostics += $"(Exception occurred while reading /proc/self/stat: {ex.Message})";
                    }
#pragma warning restore EPC12

                    var fullMessage = $"Original exception message: {e.Message}.\n Debugging information: {procSelfStatDiagnostics}";
                    exceptionToReport = new BuildXLException(fullMessage, e);
                }

                m_errorHandler?.Invoke(exceptionToReport);
            }
            finally
            {
                ReschedulerTimer();
            }
        }

        private void CollectOnce()
        {
            // This must be reacquired for every collection and may not be cached because some of the fields like memory usage are only set in the Process() constructor
            lock (m_collectLock)
            {
                Process currentProcess = Process.GetCurrentProcess();

                // Compute the performance data
                double? machineCpu = 0.0;
                double? machineAvailablePhysicalBytes = null;
                double? machineTotalPhysicalBytes = null;
                double? machineCommitUsedBytes = null;
                double? machineCommitLimitBytes = null;

                double? processCpu = GetProcessCpu(currentProcess);
                double processThreads = currentProcess.Threads.Count;
                double processPrivateBytes = currentProcess.PrivateMemorySize64;
                double processWorkingSetBytes = currentProcess.WorkingSet64;
                double processHeldBytes = m_collectHeldBytesFromGC ? GC.GetTotalMemory(forceFullCollection: true) : 0;

                double? jobObjectCpu = null;
                double? jobObjectProcesses = null;

                DiskStats[] diskStats = null;

                TryGetMemoryCounters(out machineTotalPhysicalBytes, out machineAvailablePhysicalBytes);

                if (!OperatingSystemHelper.IsUnixOS)
                {
                    machineCpu = GetMachineCpu();

                    var buildXlJobObjectInfo = m_queryJobObject?.Invoke();
                    jobObjectProcesses = buildXlJobObjectInfo?.NumProcesses;
                    jobObjectCpu = GetJobObjectCpu(buildXlJobObjectInfo?.UserTime, buildXlJobObjectInfo?.KernelTime);

                    PERFORMANCE_INFORMATION performanceInfo = PERFORMANCE_INFORMATION.CreatePerfInfo();
                    if (GetPerformanceInfo(out performanceInfo, performanceInfo.cb))
                    {
                        machineCommitUsedBytes = performanceInfo.CommitUsed.ToInt64() * performanceInfo.PageSize.ToInt64();
                        machineCommitLimitBytes = performanceInfo.CommitLimit.ToInt64() * performanceInfo.PageSize.ToInt64();
                    }

                    diskStats = GetDiskCountersWindows();

                    TryGetModifiedPagelistSizeAsync();
                    TryGetWMICpuAsync();
                    TryGetWMISystemAsync();
                }
                else
                {
                    diskStats = GetDiskCountersUnix();
                    machineCpu = GetMachineCpuUnix();
                }

                // stop network monitor measurement and gather data
                m_networkMonitor?.StopMeasurement();

                DateTime temp = DateTime.UtcNow;
                TimeSpan duration = temp - m_networkTimeLastCollectedAt;
                m_networkTimeLastCollectedAt = temp;

                double? machineKbitsPerSecSent = null;
                double? machineKbitsPerSecReceived = null;

                if (m_networkMonitor != null)
                {
                    machineKbitsPerSecSent = Math.Round(1000 * BytesToKbits(m_networkMonitor.TotalSentBytes) / Math.Max(duration.TotalMilliseconds, 1.0), 3);
                    machineKbitsPerSecReceived = Math.Round(1000 * BytesToKbits(m_networkMonitor.TotalReceivedBytes) / Math.Max(duration.TotalMilliseconds, 1.0), 3);
                }

                // Update the aggregators
                lock (m_aggregators)
                {
                    foreach (var aggregator in m_aggregators)
                    {
                        aggregator.RegisterSample(
                            processCpu: processCpu,
                            processPrivateBytes: processPrivateBytes,
                            processWorkingSetBytes: processWorkingSetBytes,
                            processThreads: processThreads,
                            processGcHeldBytes: processHeldBytes,
                            machineCpu: machineCpu,
                            machineTotalPhysicalBytes: machineTotalPhysicalBytes,
                            machineAvailablePhysicalBytes: machineAvailablePhysicalBytes,
                            machineCommitUsedBytes: machineCommitUsedBytes,
                            machineCommitLimitBytes: machineCommitLimitBytes,
                            machineBandwidth: m_networkMonitor?.Bandwidth,
                            machineKbitsPerSecSent: machineKbitsPerSecSent,
                            machineKbitsPerSecReceived: machineKbitsPerSecReceived,
                            machineDiskStats: diskStats,
                            machineCpuWMI: m_machineCpuWMI,
                            modifiedPagelistBytes: m_modifiedPagelistBytes,
                            jobObjectCpu: jobObjectCpu,
                            jobObjectProcesses: jobObjectProcesses,
                            contextSwitchesPerSec: m_contextSwitchesPerSec,
                            processes: m_processes,
                            cpuQueueLength: m_cpuQueueLength,
                            threads: m_threads,
                            machineActiveTcpConnections: GetMachineActiveTcpConnections(),
                            machineOpenFileDescriptors: GetMachineOpenFileDescriptors());
                    }
                }

                // restart network monitor to start new measurement
                m_networkMonitor?.StartMeasurement();
            }
        }

        private static bool TryGetMemoryCounters(out double? machineTotalPhysicalBytes, out double? machineAvailablePhysicalBytes)
        {
            machineTotalPhysicalBytes = null;
            machineAvailablePhysicalBytes = null;

            if (!OperatingSystemHelper.IsUnixOS)
            {
                MEMORYSTATUSEX memoryStatusEx = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memoryStatusEx))
                {
                    machineAvailablePhysicalBytes = memoryStatusEx.ullAvailPhys;
                    machineTotalPhysicalBytes = memoryStatusEx.ullTotalPhys;
                    return true;
                }
            }
            else
            {
                RamUsageInfo ramUsageInfo = new RamUsageInfo();
                if (GetRamUsageInfo(ref ramUsageInfo) == MACOS_INTEROP_SUCCESS)
                {
                    machineAvailablePhysicalBytes = ramUsageInfo.FreeBytes;
                    machineTotalPhysicalBytes = ramUsageInfo.TotalBytes;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get machine memory counters
        /// </summary>
        public static bool TryGetMemoryCountersMb(out int? machineTotalPhysicalMb, out int? machineAvailablePhysicalMb)
        {
            machineTotalPhysicalMb = null;
            machineAvailablePhysicalMb = null;

            if (TryGetMemoryCounters(out double? machineTotalPhysicalBytes, out double? machineAvailablePhysicalBytes))
            {
                machineTotalPhysicalMb = (int)Aggregator.BytesToMB(machineTotalPhysicalBytes);
                machineAvailablePhysicalMb = (int)Aggregator.BytesToMB(machineAvailablePhysicalBytes);
                return true;
            }

            return false;
        }

        private void TryGetModifiedPagelistSizeAsync()
        {
            try
            {
                if (m_wmiScope?.IsConnected == true)
                {
                    m_modifiedPageSizeWMIQuery.Get(m_modifiedPageSizeWMIWatcher);
                }
            }
#pragma warning disable ERP022 // It is OK for WMI to fail sometimes
            catch (Exception)
            {
            }
#pragma warning restore ERP022
        }

        private void UpdateModifiedPageSize(object sender, ObjectReadyEventArgs obj)
        {
            try
            {
                m_modifiedPagelistBytes = (UInt64)obj.NewObject["ModifiedPageListBytes"];
            }
#pragma warning disable ERP022 // It is OK for WMI to fail sometimes
            catch (Exception)
            {
            }
#pragma warning restore ERP022
        }

        private void TryGetWMICpuAsync()
        {
            try
            {
                if (m_wmiScope?.IsConnected == true)
                {
                    m_cpuWMIQuery.Get(m_cpuWMIWatcher);
                }
            }
#pragma warning disable ERP022 // It is OK for WMI to fail sometimes
            catch (Exception)
            {
            }
#pragma warning restore ERP022
        }

        private void TryGetWMISystemAsync()
        {
            try
            {
                if (m_wmiScope?.IsConnected == true)
                {
                    m_systemWMIQuery.Get(m_systemWMIWatcher);
                }
            }
#pragma warning disable ERP022 // It is OK for WMI to fail sometimes
            catch (Exception)
            {
            }
#pragma warning restore ERP022
        }

        private void UpdateWMICpu(object sender, ObjectReadyEventArgs obj)
        {
            try
            {
                m_machineCpuWMI = 100 - Convert.ToUInt32(obj.NewObject["PercentIdleTime"]);
            }
#pragma warning disable ERP022 // It is OK for WMI to fail sometimes
            catch (Exception)
            {
            }
#pragma warning restore ERP022
        }

        private void UpdateWMISystem(object sender, ObjectReadyEventArgs obj)
        {
            try
            {
                m_contextSwitchesPerSec = Convert.ToInt32(obj.NewObject["ContextSwitchesPersec"]);
                m_processes = Convert.ToInt32(obj.NewObject["Processes"]);
                m_threads = Convert.ToInt32(obj.NewObject["Threads"]);
                m_cpuQueueLength = Convert.ToInt32(obj.NewObject["ProcessorQueueLength"]);
            }
#pragma warning disable ERP022 // It is OK for WMI to fail sometimes
            catch (Exception)
            {
            }
#pragma warning restore ERP022
        }

        /// <summary>
        /// Obtains the number of active TCP connections which are established on the machine.
        /// </summary>
        internal static int GetMachineActiveTcpConnections()
        {
            try
            {
                IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
                // GetActiveTcpConnections() - Obtains a list of all active TCP connections on the local machine.
                // These connections are established between remote and local endpoints.
                return properties.GetActiveTcpConnections().Length;
            }
#pragma warning disable ERP022 // Do not log an error in case of failure.
            catch (Exception)
            {
                return -1;
            }
#pragma warning restore ERP022
        }

        /// <summary>
        /// Obtains the number of file descriptors that are currently open in the machine.
        /// </summary>
        internal static int GetMachineOpenFileDescriptors()
        {
            if (!OperatingSystemHelper.IsLinuxOS)
            {
                return 0;
            }

            try
            {
                // '/proc/self/fd' is a pseudo directory that provides symlinks to all the file descriptors that the current process has open.
                return Directory.EnumerateFiles("/proc/self/fd").Count();
            }
#pragma warning disable ERP022 // Do not log an error in case of failure.
            catch (Exception)
            {
                return -1;
            }
#pragma warning restore ERP022        
        }

        private static double BytesToKbits(long bytes)
        {
            // Convert to Kbits
            return (bytes / 1024.0) * 8;
        }

        /// <summary>
        /// Converts Bytes to GigaBytes
        /// </summary>
        public static double BytesToGigaBytes(long bytes)
        {
            // Convert to Gigabytes
            return ((double)bytes / (1024 * 1024 * 1024));
        }

        private void ReschedulerTimer()
        {
            lock (m_collectionTimerLock)
            {
                m_collectionTimer?.Change((int)m_collectionFrequency.TotalMilliseconds, 0);
            }
        }

        /// <summary>
        /// Creates an Aggregator to recieve performance data over the lifetime of the Aggregator
        /// </summary>
        [SuppressMessage("Microsoft.Reliability", "CA2000:DisposeObjectsBeforeLosingScope",
            Justification = "Disposing is the responsibility of the caller")]
        public Aggregator CreateAggregator()
        {
            Aggregator result = new Aggregator(this);
            lock (m_aggregators)
            {
                m_aggregators.Add(result);
            }

            return result;
        }

        /// <summary>
        /// Removes the aggregator and prevents it from receiving future updates
        /// </summary>
        private void RemoveAggregator(Aggregator aggregator)
        {
            lock (m_aggregators)
            {
                m_aggregators.Remove(aggregator);
            }
        }

        /// <nodoc/>
        public void Dispose()
        {
            lock (m_aggregators)
            {
                foreach (var aggregator in m_aggregators)
                {
                    aggregator.Dispose();
                }
            }

            lock (m_collectionTimerLock)
            {
                if (m_collectionTimer != null)
                {
                    m_collectionTimer.Dispose();
                    m_collectionTimer = null;
                }
            }

            if (m_drives != null)
            {
                foreach (var item in m_drives)
                {
                    item.safeFileHandle?.Dispose();
                }
            }

            if (!OperatingSystemHelper.IsUnixOS)
            {
                m_modifiedPageSizeWMIQuery?.Dispose();
            }
        }

        #region Perf data collection implementations

        /// <summary>
        /// Initializes network performance counters.
        /// </summary>
        private void InitializeNetworkMonitor()
        {
            try
            {
                // initialize NetworkMonitor and start measurement
                m_networkMonitor = new NetworkMonitor();
                m_networkTimeLastCollectedAt = DateTime.UtcNow;
                m_networkMonitor.StartMeasurement();
            }
#pragma warning disable ERP022
            catch
            {
                // NetworkMonitor is not working so set to null
                m_networkMonitor = null;
            }
#pragma warning restore ERP022
        }

        private DiskStats[] GetDiskCountersWindows()
        {
            DiskStats[] diskStats = new DiskStats[m_drives.Length];
            for (int i = 0; i < m_drives.Length; i++)
            {
                if (m_testHooks != null)
                {
                    // Various tests may need to inject artificial results for validation of scenarios
                    if (m_testHooks.DelayAvailableDiskSpaceUtcTime != null &&
                        m_testHooks.DelayedAvailableDiskSpace != null &&
                        m_testHooks.DelayAvailableDiskSpaceUtcTime <= DateTime.UtcNow)
                    {
                        diskStats[i] = new DiskStats(availableDiskSpace: m_testHooks.DelayedAvailableDiskSpace.Value);
                        continue;
                    }
                    else if (m_testHooks.AvailableDiskSpace != null)
                    {
                        diskStats[i] = new DiskStats(availableDiskSpace: m_testHooks.AvailableDiskSpace.Value);
                        continue;
                    }
                }

                var drive = m_drives[i];
                if (!drive.safeFileHandle.IsClosed && !drive.safeFileHandle.IsInvalid)
                {
                    uint bytesReturned;

                    try
                    {
                        DISK_PERFORMANCE perf = default(DISK_PERFORMANCE);
                        bool result = DeviceIoControl(drive.safeFileHandle, IOCTL_DISK_PERFORMANCE,
                            inputBuffer: IntPtr.Zero,
                            inputBufferSize: 0,
                            outputBuffer: out perf,
                            outputBufferSize: Marshal.SizeOf(typeof(DISK_PERFORMANCE)),
                            bytesReturned: out bytesReturned,
                            overlapped: IntPtr.Zero);
                        if (result && drive.driveInfo.TotalSize != 0)
                        {
                            diskStats[i] = new DiskStats(
                                availableDiskSpace: BytesToGigaBytes(drive.driveInfo.AvailableFreeSpace),
                                diskPerformance: perf);
                        }
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException)
                    {
                        // Occasionally the handle is disposed even though it's checked against being closed and valid
                        // above. In those cases, just catch the failure and continue on to avoid crashes.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // This might occur when trying to read drive.driveInfo.TotalSize or drive.driveInfo.AvailableFreeSpace
                        // for a drive that we don't have permission to access
                    }
                }
            }

            return diskStats;
        }

        private DiskStats[] GetDiskCountersUnix()
        {
            DiskStats[] stats = new DiskStats[m_drives.Length];
            for (int i = 0; i < m_drives.Length; i++)
            {
                try
                {
                    stats[i] = new DiskStats(availableDiskSpace: BytesToGigaBytes(m_drives[i].driveInfo.AvailableFreeSpace));
                }
                catch (IOException)
                {
                    // No stats for DriveNotFoundException. Leave the struct as uninitialized and it will be marked as invalid.
                }
                catch (UnauthorizedAccessException)
                {
                    // This might occur when trying to read drive.driveInfo.TotalSize or drive.driveInfo.AvailableFreeSpace
                    // for a drive that we don't have permission to access
                }
            }
            return stats;
        }

        private double? GetProcessCpu(Process currentProcess)
        {
            // Processor time consumed by this process
            TimeSpan processTimeCurrentValue;

            try
            {
                processTimeCurrentValue = currentProcess.TotalProcessorTime;
            }
            catch (Win32Exception)
            {
                // Occasionally, we get an 'Unable to retrieve the specified information about the process or thread.'
                return null;
            }

            DateTime processTimeCurrentCollectedAt = DateTime.UtcNow;

            if (m_processTimeLastCollectedAt == DateTime.MinValue)
            {
                m_processTimeLastCollectedAt = processTimeCurrentCollectedAt;
                m_processTimeLastValue = processTimeCurrentValue;
                return null;
            }
            else
            {
                var procUsage = (100.0 * (processTimeCurrentValue - m_processTimeLastValue).Ticks) /
                    ((processTimeCurrentCollectedAt - m_processTimeLastCollectedAt).Ticks * m_processorCount);

                m_processTimeLastCollectedAt = processTimeCurrentCollectedAt;
                m_processTimeLastValue = processTimeCurrentValue;
                return procUsage;
            }
        }

        private double? GetJobObjectCpu(ulong? userTime, ulong? kernelTime)
        {
            if (userTime == null || kernelTime == null)
            {
                return null;
            }

            var totalTime = kernelTime.Value + userTime.Value;
            DateTime currentCollectedAt = DateTime.UtcNow;

            if (m_jobObjectLastCollectedAt == DateTime.MinValue)
            {
                m_jobObjectLastCollectedAt = currentCollectedAt;
                m_jobObjectTotalTimeLastValue = totalTime;
                return null;
            }

            double? jobObjectCpu = null;

            long availProcessorTime = ((currentCollectedAt.ToFileTime() - m_jobObjectLastCollectedAt.ToFileTime()) * (long)m_processorCount);
            if (availProcessorTime > 0)
            {
                if (totalTime > m_jobObjectTotalTimeLastValue)
                {
                    jobObjectCpu = (100.0 * (totalTime - m_jobObjectTotalTimeLastValue)) / availProcessorTime;
                }

                m_jobObjectLastCollectedAt = DateTime.UtcNow;
                m_jobObjectTotalTimeLastValue = totalTime;
            }

            return jobObjectCpu;
        }

        private double? GetMachineCpu()
        {
            double? machineCpu = null;

            if (m_machineTimeLastCollectedAt == DateTime.MinValue)
            {
                m_machineTimeLastCollectedAt = DateTime.UtcNow;
                long idleTime, kernelTime, userTime;
                if (GetSystemTimes(out idleTime, out kernelTime, out userTime))
                {
                    m_machineTimeLastVale = kernelTime + userTime - idleTime;
                }
            }
            else
            {
                long idleTime, kernelTime, userTime;
                if (GetSystemTimes(out idleTime, out kernelTime, out userTime))
                {
                    DateTime machineTimeCurrentCollectedAt = DateTime.UtcNow;
                    long machineTimeCurrentValue = kernelTime + userTime - idleTime;

                    long availProcessorTime = ((machineTimeCurrentCollectedAt.ToFileTime() - m_machineTimeLastCollectedAt.ToFileTime()) * m_processorCount);
                    if (availProcessorTime > 0)
                    {
                        if (machineTimeCurrentValue > m_machineTimeLastVale)
                        {
                            machineCpu = (100.0 * (machineTimeCurrentValue - m_machineTimeLastVale)) / availProcessorTime;
                            // Windows will create a new Processor Group for every 64 logical processors. Technically we should
                            // call GetSystemTimes() multiple times, targeting each processor group for correct data. Instead,
                            // this code assumes each processor group is eavenly loaded and just uses the number of predicted
                            // groups as a factor to scale the system times observed on the default processor group for this thread.
                            // https://msdn.microsoft.com/en-us/library/windows/desktop/dd405503(v=vs.85).aspx
                            machineCpu = machineCpu * Math.Ceiling((double)m_processorCount / 64);

                            // Sometimes the calculated value pops up above 100.
                            machineCpu = Math.Min(100, machineCpu.Value);
                        }

                        m_machineTimeLastCollectedAt = machineTimeCurrentCollectedAt;
                        m_machineTimeLastVale = machineTimeCurrentValue;
                    }
                }
            }

            return machineCpu;
        }

        private double? GetMachineCpuUnix()
        {
            double? machineCpu = null;

            var buffer = new CpuLoadInfo();

            // Initialize the CPU load info
            if (m_lastCpuLoadInfo.SystemTime == 0 && m_lastCpuLoadInfo.UserTime == 0 && m_lastCpuLoadInfo.IdleTime == 0)
            {
                GetCpuLoadInfo(ref buffer);
                m_lastCpuLoadInfo = buffer;
            }

            buffer = new CpuLoadInfo();
            if (GetCpuLoadInfo(ref buffer) == MACOS_INTEROP_SUCCESS)
            {
                double systemTicks = buffer.SystemTime - m_lastCpuLoadInfo.SystemTime;
                double userTicks = buffer.UserTime - m_lastCpuLoadInfo.UserTime;
                double idleTicks = buffer.IdleTime - m_lastCpuLoadInfo.IdleTime;
                double totalTicks = systemTicks + userTicks + idleTicks;

                machineCpu = 100.0 * ((systemTicks + userTicks) / totalTicks);
            }

            m_lastCpuLoadInfo = buffer;

            return machineCpu;
        }

        #endregion

        /// <summary>
        /// Summary of the aggregator (more human-readable)
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public struct MachinePerfInfo
        {
            /// <summary>
            /// CPU usage percentage of BuildXL job object
            /// </summary>
            public long JobObjectCpu;

            /// <summary>
            /// Number of processes in BuildXL job object
            /// </summary>
            public int JobObjectProcesses;

            /// <summary>
            /// CPU usage percentage
            /// </summary>
            public int CpuUsagePercentage;

            /// <summary>
            /// CPU usage percentage
            /// </summary>
            public int CpuWMIUsagePercentage;

            /// <summary>
            /// Disk usage percentages for each disk
            /// </summary>
            public int[] DiskUsagePercentages;

            /// <summary>
            /// Queue depths for each disk
            /// </summary>
            public int[] DiskQueueDepths;

            /// <summary>
            /// Available Disk Space in GigaBytes for each disk
            /// </summary>
            public int[] DiskAvailableSpaceGb;

            /// <summary>
            /// Resource summary to show on the console
            /// </summary>
            public string ConsoleResourceSummary;

            /// <summary>
            /// Resource summary to show in the text log
            /// </summary>
            public string LogResourceSummary;

            /// <summary>
            /// RAM usage percentage
            /// </summary>
            public int? RamUsagePercentage;

            /// <summary>
            /// Total ram in MB
            /// </summary>
            public int? TotalRamMb;

            /// <summary>
            /// Available ram in MB
            /// </summary>
            public int? AvailableRamMb;

            /// <summary>
            /// Commit usage percentage
            /// </summary>
            public int? CommitUsagePercentage;

            /// <summary>
            /// Total committed memory in MB. This is not an indication of physical memory usage.
            /// </summary>
            public int? CommitUsedMb;

            /// <summary>
            /// Maximum memory that can be committed in MB. If the page file can be extended, this is a soft limit.
            /// </summary>
            public int? CommitLimitMb;

            /// <summary>
            /// Network bandwidth available on the machine
            /// </summary>
            public long MachineBandwidth;

            /// <summary>
            /// Kbits/sec sent on all network interfaces
            /// </summary>
            public double MachineKbitsPerSecSent;

            /// <summary>
            /// Kbits/sec received on all network interfaces
            /// </summary>
            public double MachineKbitsPerSecReceived;

            /// <summary>
            /// CPU utilization percent of just this process
            /// </summary>
            public int ProcessCpuPercentage;

            /// <summary>
            /// Working Set for just this process
            /// </summary>
            public int ProcessWorkingSetMB;

            /// <summary>
            /// Modified pagelist that is a part of used RAM
            /// </summary>
            /// <remarks>
            /// Pages in the modified pagelist are dirty and waiting to be written to the pagefile.
            /// </remarks>
            internal int? ModifiedPagelistMb;

            /// <summary>
            /// Modified pagelist / TotalRAM
            /// </summary>
            internal int? ModifiedPagelistPercentage => TotalRamMb > 0 ?
                (int)(100 * ((double)(ModifiedPagelistMb ?? 0) / TotalRamMb)):
                0;

            /// <summary>
            /// Effective Available RAM = Modified pagelist + Available RAM
            /// </summary>
            /// <remarks>
            /// When modified pagelist is not available, EffectiveAvailableRAM equals to AvailableRAM.
            /// </remarks>
            public int? EffectiveAvailableRamMb;

            /// <summary>
            /// Effective RAM usage percentage = (TotalRam - Effective Available RAM) / TotalRAM
            /// </summary>
            public int? EffectiveRamUsagePercentage;

            /// <nodoc/>
            public int Threads;

            /// <nodoc/>
            public int ContextSwitchesPerSec;

            /// <nodoc/>
            public int Processes;

            /// <nodoc/>
            public int CpuQueueLength;

            /// <summary>
            /// Count of all the TCP active connections which are listening and established.
            /// </summary>
            public int MachineActiveTcpConnections;
            
            /// <summary>
            /// Count of all the open file descriptors in the Linux machine.
            /// </summary>
            public int MachineOpenFileDescriptors;
        }

        /// <summary>
        /// Aggregates performance data
        /// </summary>
        public sealed class Aggregator : IDisposable
        {
            // Parent PerformanceCollector. Used during Dispose()
            private readonly PerformanceCollector m_parent;

            private int m_sampleCount = 0;

            /// <summary>
            /// The percent of CPU time consumed by the currently running process
            /// </summary>
            public readonly Aggregation ProcessCpu;

            /// <summary>
            /// The private megabytes consumed by the currently running process
            /// </summary>
            public readonly Aggregation ProcessPrivateMB;

            /// <summary>
            /// The working set in megabytes consumed by the currently running process. This is the number shown in TaskManager.
            /// </summary>
            public readonly Aggregation ProcessWorkingSetMB;

            /// <summary>
            /// Count of threads associated with the current process. Based on System.Diagnostics.Processes.Threads
            /// </summary>
            public readonly Aggregation ProcessThreadCount;

            /// <summary>
            /// The percent of CPU time used by the machine
            /// </summary>
            public readonly Aggregation MachineCpu;

            /// <summary>
            /// The percent of CPU time used by the machine (reported by WMI)
            /// </summary>
            public readonly Aggregation MachineCpuWMI;

            /// <summary>
            /// The available megabytes of physical memory available on the machine
            /// </summary>
            public readonly Aggregation MachineAvailablePhysicalMB;

            /// <summary>
            /// The total megabytes of physical memory on the machine
            /// </summary>
            public readonly Aggregation MachineTotalPhysicalMB;

            /// <summary>
            /// The total megabytes of memory current committed by the system
            /// </summary>
            public readonly Aggregation MachineCommitUsedMB;

            /// <summary>
            /// The total megabytes of memory current that can be committed by the system without extending the page
            /// file. If the page file can be extended, this is a soft limit.
            /// </summary>
            public readonly Aggregation MachineCommitLimitMB;

            /// <summary>
            /// The total megabytes of memory held as reported by the GC
            /// </summary>
            public readonly Aggregation ProcessHeldMB;

            /// <summary>
            /// Total Network bandwidth available on the machine
            /// </summary>
            public readonly Aggregation MachineBandwidth;

            /// <summary>
            /// Kbits/sec sent on all network interfaces on the machine
            /// </summary>
            public readonly Aggregation MachineKbitsPerSecSent;

            /// <summary>
            /// Kbits/sec received on all network interfaces on the machine
            /// </summary>
            public readonly Aggregation MachineKbitsPerSecReceived;

            /// <nodoc />
            public readonly Aggregation ModifiedPagelistMB;

            /// <nodoc />
            public readonly Aggregation JobObjectCpu;

            /// <nodoc />
            public readonly Aggregation JobObjectProcesses;

            /// <nodoc />
            public readonly Aggregation ContextSwitchesPerSec;

            /// <nodoc />
            public readonly Aggregation Processes;

            /// <nodoc />
            public readonly Aggregation CpuQueueLength;

            /// <nodoc />
            public readonly Aggregation Threads;

            /// <nodoc />
            public readonly Aggregation MachineActiveTcpConnections;

            /// <nodoc />
            public readonly Aggregation MachineOpenFileDescriptors;

            /// <summary>
            /// Stats about disk usage. This is guarenteed to be in the same order as <see cref="GetDrives"/>
            /// </summary>
            public IReadOnlyCollection<DiskStatistics> DiskStats => m_diskStats;

            private readonly DiskStatistics[] m_diskStats;

            /// <nodoc/>
            public sealed class DiskStatistics
            {
                /// <summary>
                /// The drive name. If this is a standard drive it will be a single character like 'C'
                /// </summary>
                public string Drive { get; set; }

                /// <nodoc/>
                public Aggregation QueueDepth = new Aggregation();

                /// <nodoc/>
                public Aggregation IdleTime = new Aggregation();

                /// <nodoc/>
                public Aggregation ReadTime = new Aggregation();

                /// <nodoc/>
                public Aggregation ReadCount = new Aggregation();

                /// <nodoc/>
                public Aggregation WriteTime = new Aggregation();

                /// <nodoc/>
                public Aggregation WriteCount = new Aggregation();

                /// <nodoc/>
                public Aggregation BytesRead = new Aggregation();

                /// <nodoc/>
                public Aggregation BytesWritten = new Aggregation();

                /// <nodoc/>
                public Aggregation AvailableSpaceGb = new Aggregation();

                /// <summary>
                /// Calculates the disk active time
                /// </summary>
                /// <param name="lastOnly">Whether the calculation should only apply to the sample taken at the last time window</param>
                public int CalculateActiveTime(bool lastOnly)
                {
                    double percentage = 0;
                    if (lastOnly)
                    {
                        var denom = ReadTime.Difference + WriteTime.Difference + IdleTime.Difference;
                        if (denom > 0)
                        {
                            percentage = (ReadTime.Difference + WriteTime.Difference) / denom;
                        }
                    }
                    else
                    {
                        var denom = ReadTime.Range + WriteTime.Range + IdleTime.Range;
                        if (denom > 0)
                        {
                            percentage = (ReadTime.Range + WriteTime.Range) / denom;
                        }
                    }

                    return (int)(percentage * 100.0);
                }
            }

            /// <nodoc/>
            public Aggregator(PerformanceCollector collector)
            {
                m_parent = collector;

                ProcessCpu = new Aggregation();
                ProcessPrivateMB = new Aggregation();
                ProcessWorkingSetMB = new Aggregation();
                ProcessThreadCount = new Aggregation();
                ProcessHeldMB = new Aggregation();

                MachineCpu = new Aggregation();
                MachineCpuWMI = new Aggregation();
                MachineAvailablePhysicalMB = new Aggregation();
                MachineTotalPhysicalMB = new Aggregation();
                MachineCommitLimitMB = new Aggregation();
                MachineCommitUsedMB = new Aggregation();
                MachineBandwidth = new Aggregation();
                MachineKbitsPerSecSent = new Aggregation();
                MachineKbitsPerSecReceived = new Aggregation();

                ModifiedPagelistMB = new Aggregation();

                JobObjectCpu = new Aggregation();
                JobObjectProcesses = new Aggregation();

                Processes = new Aggregation();
                Threads = new Aggregation();
                CpuQueueLength = new Aggregation();
                ContextSwitchesPerSec = new Aggregation();

                List<Tuple<string, Aggregation>> aggs = new List<Tuple<string, Aggregation>>();
                List<DiskStatistics> diskStats = new List<DiskStatistics>();

                foreach (var drive in collector.GetDrives())
                {
                    aggs.Add(new Tuple<string, Aggregation>(drive, new Aggregation()));
                    diskStats.Add(new DiskStatistics() { Drive = drive });
                }

                m_diskStats = diskStats.ToArray();
                MachineActiveTcpConnections = new Aggregation();
                MachineOpenFileDescriptors = new Aggregation();
            }

            /// <summary>
            /// Compute machine perf info to get more human-readable resource usage info
            /// </summary>
            /// <param name="ensureSample">when true and no performance measurement samples are registered, immediately forces a collection of a performance
            /// measurement sample</param>
            public MachinePerfInfo ComputeMachinePerfInfo(bool ensureSample = false)
            {
                if (ensureSample && Volatile.Read(ref m_sampleCount) == 0)
                {
                    m_parent.CollectOnce();
                }

                MachinePerfInfo perfInfo = default(MachinePerfInfo);
                unchecked
                {
                    using (var sbPool = Pools.GetStringBuilder())
                    using (var sbPool2 = Pools.GetStringBuilder())
                    {
                        StringBuilder consoleSummary = sbPool.Instance;
                        StringBuilder logFileSummary = sbPool2.Instance;

                        perfInfo.CpuUsagePercentage = SafeConvert.ToInt32(MachineCpu.Latest);
                        perfInfo.CpuWMIUsagePercentage = SafeConvert.ToInt32(MachineCpuWMI.Latest);

                        consoleSummary.AppendFormat("CPU:{0}%", perfInfo.CpuUsagePercentage);
                        logFileSummary.AppendFormat("CPU:{0}%", perfInfo.CpuUsagePercentage);
                        if (MachineTotalPhysicalMB.Latest > 0)
                        {
                            var availableRam = SafeConvert.ToInt32(MachineAvailablePhysicalMB.Latest);
                            var totalRam = SafeConvert.ToInt32(MachineTotalPhysicalMB.Latest);

                            var ramUsagePercentage = SafeConvert.ToInt32(((100.0 * (totalRam - availableRam)) / totalRam));
                            Contract.Assert(ramUsagePercentage >= 0 && ramUsagePercentage <= 100);

                            perfInfo.RamUsagePercentage = ramUsagePercentage;
                            perfInfo.TotalRamMb = totalRam;
                            perfInfo.AvailableRamMb = availableRam;
                            consoleSummary.AppendFormat(" RAM:{0}%", ramUsagePercentage);
                            logFileSummary.AppendFormat(" RAM:{0}%", ramUsagePercentage);
                        }

                        if (ModifiedPagelistMB.Latest > 0)
                        {
                            perfInfo.ModifiedPagelistMb = SafeConvert.ToInt32(ModifiedPagelistMB.Latest);
                        }

                        if (JobObjectCpu.Latest > 0)
                        {
                            perfInfo.JobObjectCpu = SafeConvert.ToInt32(JobObjectCpu.Latest);
                        }

                        if (JobObjectProcesses.Latest > 0)
                        {
                            perfInfo.JobObjectProcesses = SafeConvert.ToInt32(JobObjectProcesses.Latest);
                        }

                        if (perfInfo.TotalRamMb.HasValue)
                        {
                            perfInfo.EffectiveAvailableRamMb = SafeConvert.ToInt32(perfInfo.AvailableRamMb.Value + (perfInfo.ModifiedPagelistMb ?? 0));
                            perfInfo.EffectiveRamUsagePercentage = SafeConvert.ToInt32(100.0 * (perfInfo.TotalRamMb.Value - perfInfo.EffectiveAvailableRamMb.Value) / perfInfo.TotalRamMb.Value);
                        }

                        if (MachineCommitLimitMB.Latest > 0)
                        {
                            var commitUsed = SafeConvert.ToInt32(MachineCommitUsedMB.Latest);
                            var commitLimit = SafeConvert.ToInt32(MachineCommitLimitMB.Latest);
                            var commitUsagePercentage = SafeConvert.ToInt32(((100.0 * commitUsed) / commitLimit));

                            perfInfo.CommitUsagePercentage = commitUsagePercentage;
                            perfInfo.CommitUsedMb = commitUsed;
                            perfInfo.CommitLimitMb = commitLimit;
                        }

                        if (MachineBandwidth.Latest > 0)
                        {
                            perfInfo.MachineBandwidth = SafeConvert.ToLong(MachineBandwidth.Latest);
                            perfInfo.MachineKbitsPerSecSent = MachineKbitsPerSecSent.Latest;
                            perfInfo.MachineKbitsPerSecReceived = MachineKbitsPerSecReceived.Latest;
                        }

                        perfInfo.Threads = SafeConvert.ToInt32(Threads.Latest);
                        perfInfo.ContextSwitchesPerSec = SafeConvert.ToInt32(ContextSwitchesPerSec.Latest);
                        perfInfo.Processes = SafeConvert.ToInt32(Processes.Latest);
                        perfInfo.CpuQueueLength = SafeConvert.ToInt32(CpuQueueLength.Latest);
                        perfInfo.MachineActiveTcpConnections = SafeConvert.ToInt32(MachineActiveTcpConnections.Latest);
                        perfInfo.MachineOpenFileDescriptors = SafeConvert.ToInt32(MachineOpenFileDescriptors.Latest);

                        int diskIndex = 0;
                        perfInfo.DiskAvailableSpaceGb = new int[DiskStats.Count];
                        foreach (var disk in DiskStats)
                        {
                            var availableSpaceGb = SafeConvert.ToInt32(disk.AvailableSpaceGb.Latest);
                            perfInfo.DiskAvailableSpaceGb[diskIndex] = availableSpaceGb;
                            diskIndex++;
                        }

                        if (!OperatingSystemHelper.IsUnixOS)
                        {
                            perfInfo.DiskUsagePercentages = new int[DiskStats.Count];
                            perfInfo.DiskQueueDepths = new int[DiskStats.Count];

                            string worstDrive = "N/A";
                            int highestActiveTime = -1;
                            int highestQueueDepth = 0;

                            // Loop through and find the worst looking disk
                            diskIndex = 0;
                            foreach (var disk in DiskStats)
                            {
                                if (disk.ReadTime.Maximum == 0)
                                {
                                    perfInfo.DiskUsagePercentages[diskIndex] = 0;
                                    perfInfo.DiskQueueDepths[diskIndex] = 0;
                                    diskIndex++;
                                    // Don't consider samples unless some activity has been registered
                                    continue;
                                }

                                var activeTime = disk.CalculateActiveTime(lastOnly: true);
                                var queueDepth = SafeConvert.ToInt32(disk.QueueDepth.Latest);
                                perfInfo.DiskUsagePercentages[diskIndex] = activeTime;
                                perfInfo.DiskQueueDepths[diskIndex] = queueDepth;
                                diskIndex++;

                                logFileSummary.Append(FormatDiskUtilization(disk.Drive, activeTime));

                                if (activeTime > highestActiveTime)
                                {
                                    worstDrive = disk.Drive;
                                    highestActiveTime = activeTime;
                                    highestQueueDepth = queueDepth;
                                }
                            }

                            if (highestActiveTime != -1)
                            {
                                consoleSummary.Append(FormatDiskUtilization(worstDrive, highestActiveTime));
                            }
                        }

                        perfInfo.ProcessCpuPercentage = SafeConvert.ToInt32(ProcessCpu.Latest);
                        logFileSummary.AppendFormat(" DominoCPU:{0}%", perfInfo.ProcessCpuPercentage);

                        perfInfo.ProcessWorkingSetMB = SafeConvert.ToInt32(ProcessWorkingSetMB.Latest);
                        logFileSummary.AppendFormat(" DominoRAM:{0}MB", perfInfo.ProcessWorkingSetMB);

                        perfInfo.ConsoleResourceSummary = consoleSummary.ToString();
                        perfInfo.LogResourceSummary = logFileSummary.ToString();
                    }

                    return perfInfo;
                }
            }

            private static string FormatDiskUtilization(string drive, int activeTime)
            {
                return string.Format(CultureInfo.InvariantCulture, " {0}:{1}%", drive, activeTime);
            }

            /// <summary>
            /// Registers a sample of the performance data
            /// </summary>
            internal void RegisterSample(
                double? processCpu,
                double? processPrivateBytes,
                double? processWorkingSetBytes,
                double? processThreads,
                double? processGcHeldBytes,
                double? machineCpu,
                double? machineAvailablePhysicalBytes,
                double? machineTotalPhysicalBytes,
                double? machineCommitUsedBytes,
                double? machineCommitLimitBytes,
                long? machineBandwidth,
                double? machineKbitsPerSecSent,
                double? machineKbitsPerSecReceived,
                DiskStats[] machineDiskStats,
                double? modifiedPagelistBytes,
                double? machineCpuWMI,
                double? jobObjectCpu,
                double? jobObjectProcesses,
                int? contextSwitchesPerSec,
                int? processes,
                int? cpuQueueLength,
                int? threads,
                int? machineActiveTcpConnections,
                int? machineOpenFileDescriptors)
            {
                Interlocked.Increment(ref m_sampleCount);

                ProcessCpu.RegisterSample(processCpu);
                ProcessPrivateMB.RegisterSample(BytesToMB(processPrivateBytes));
                ProcessWorkingSetMB.RegisterSample(BytesToMB(processWorkingSetBytes));
                ProcessThreadCount.RegisterSample(processThreads);
                ProcessHeldMB.RegisterSample(BytesToMB(processGcHeldBytes));

                MachineCpu.RegisterSample(machineCpu);
                MachineCpuWMI.RegisterSample(machineCpuWMI);
                MachineAvailablePhysicalMB.RegisterSample(BytesToMB(machineAvailablePhysicalBytes));
                MachineTotalPhysicalMB.RegisterSample(BytesToMB(machineTotalPhysicalBytes));
                MachineCommitUsedMB.RegisterSample(BytesToMB(machineCommitUsedBytes));
                MachineCommitLimitMB.RegisterSample(BytesToMB(machineCommitLimitBytes));
                MachineBandwidth.RegisterSample(machineBandwidth);
                MachineKbitsPerSecSent.RegisterSample(machineKbitsPerSecSent);
                MachineKbitsPerSecReceived.RegisterSample(machineKbitsPerSecReceived);

                ModifiedPagelistMB.RegisterSample(BytesToMB(modifiedPagelistBytes));

                JobObjectCpu.RegisterSample(jobObjectCpu);
                JobObjectProcesses.RegisterSample(jobObjectProcesses);

                ContextSwitchesPerSec.RegisterSample(contextSwitchesPerSec);
                Threads.RegisterSample(threads);
                Processes.RegisterSample(processes);
                CpuQueueLength.RegisterSample(cpuQueueLength);
                MachineActiveTcpConnections.RegisterSample(machineActiveTcpConnections);
                MachineOpenFileDescriptors.RegisterSample(machineOpenFileDescriptors);

                Contract.Assert(m_diskStats.Length == machineDiskStats.Length);
                for (int i = 0; i < machineDiskStats.Length; i++)
                {
                    if (m_diskStats[i] != null && machineDiskStats[i].IsValid)
                    {
                        m_diskStats[i].AvailableSpaceGb.RegisterSample(machineDiskStats[i].AvailableSpaceGb);
                        m_diskStats[i].ReadTime.RegisterSample(machineDiskStats[i].DiskPerformance.ReadTime);
                        m_diskStats[i].ReadCount.RegisterSample(machineDiskStats[i].DiskPerformance.ReadCount);
                        m_diskStats[i].WriteTime.RegisterSample(machineDiskStats[i].DiskPerformance.WriteTime);
                        m_diskStats[i].WriteCount.RegisterSample(machineDiskStats[i].DiskPerformance.WriteCount);
                        m_diskStats[i].BytesRead.RegisterSample(machineDiskStats[i].DiskPerformance.BytesRead);
                        m_diskStats[i].BytesWritten.RegisterSample(machineDiskStats[i].DiskPerformance.BytesWritten);
                        m_diskStats[i].IdleTime.RegisterSample(machineDiskStats[i].DiskPerformance.IdleTime);
                        m_diskStats[i].QueueDepth.RegisterSample(machineDiskStats[i].DiskPerformance.QueueDepth);
                    }
                }
            }

            internal static double? BytesToMB(double? bytes)
            {
                if (bytes.HasValue)
                {
                    // Technically calculations based on 1024 are MiB, but our codebase calls that MB so let's be consistent
                    return bytes.Value / (1024 * 1024);
                }

                return bytes;
            }

            /// <nodoc/>
            public void Dispose()
            {
                m_parent.RemoveAggregator(this);
            }
        }
    }
}
