// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using BuildXL.Interop;

#nullable enable

namespace BuildXL.Processes
{
    /// <summary>
    /// Interface for sandboxed processes
    ///
    /// To create an instance of this interface and start executing the created process use
    /// <see cref="SandboxedProcessFactory.StartAsync"/>.
    /// </summary>
    public interface ISandboxedProcess : IDisposable
    {
        /// <summary>
        /// Gets the unique identifier for the associated process.
        /// </summary>
        int ProcessId { get; }

        /// <summary>
        /// Returns the string representation of the accessed file path.
        /// </summary>
        /// <param name="reportedFileAccess">The file access object on which to get the path location.</param>
        string? GetAccessedFileName(ReportedFileAccess reportedFileAccess);

        /// <summary>
        /// Gets the peak working set size for the executing process tree. If the root process exits, the peak working set is considered null.
        /// </summary>
        ProcessMemoryCountersSnapshot? GetMemoryCountersSnapshot();

        /// <summary>
        /// Attempt to empty the working set of this process and optionally suspend the process.
        /// </summary>
        EmptyWorkingSetResult TryEmptyWorkingSet(bool isSuspend);

        /// <summary>
        /// Attempt to resume the suspended process
        /// </summary>
        bool TryResumeProcess();

        /// <summary>
        /// Gets the maximum heap size of the sandboxed process.
        /// </summary>
        long GetDetoursMaxHeapSize();

        /// <summary>
        /// Differences in the number of messages that were sent (or were about to be sent) and messages that were received by the sandbox after the execution of sandboxed process.
        /// </summary>
        int GetLastMessageCount();

        /// <summary>
        /// Differences in the number of messages that were successfully sent and messages that were received by the sandbox after the execution of sandboxed process.
        /// </summary>
        int GetLastConfirmedMessageCount();

        /// <summary>
        /// Asynchronously starts the process.  All the required process start information must be provided prior to calling this method
        /// (e.g., via the constructor or a custom initialization method).  To wait for the process to finish, call <see cref="GetResultAsync"/>.
        /// To instantly kill the process, call <see cref="KillAsync"/>.
        /// </summary>
        void Start();

        /// <summary>
        /// Waits for the process to finish and returns the result of the process execution.
        /// </summary>
        Task<SandboxedProcessResult> GetResultAsync();

        /// <summary>
        /// Kills the process; only produces result after process has terminated.
        /// </summary>
        /// <remarks>
        /// Also kills all nested processes; if the process hasn't already finished by itself, the Result task gets canceled.
        /// This method is typically used for cancelling a pip (e.g. due to resource exhaustion). Killing a pip on timeout is handled
        /// internally by implementors of this interface, and those kills don't go through this method. With that spirit, dump collection
        /// is not expected to happen under this method either.
        /// </remarks>
        Task KillAsync();
    }
}
