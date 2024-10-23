// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Microsoft.Win32.SafeHandles;

namespace BuildXL.Native.Streams
{
    /// <summary>
    /// Manages outstanding async I/O operations via an I/O completion port. An instance maintains one or more
    /// dedicated I/O completion threads that dispatch each completed request to an <see cref="IIOCompletionTarget"/>.
    /// </summary>
    public interface IIOCompletionManager : IDisposable
    {
        /// <summary>
        /// Installed by <see cref="StartTracingCompletion"/>. This allows e.g. a test to verify that no un-completed I/O
        /// requests make it outside of the test execution. This can help find orphaned-completion bugs which would
        /// otherwise cause finalization problems at app-domain unload.
        /// </summary>
        IOCompletionTraceHook TraceHook { get; }

        /// <summary>
        /// Overrides Instance within a disposable scope. This allows capturing all default-instance
        /// usage for testing, so that a test can assert no default-instance I/O was leaked (not completed before test completion).
        /// </summary>
        IOCompletionTraceHook StartTracingCompletion();

        /// <summary>
        /// Removes the installed <see cref="IOCompletionTraceHook"/> in a thread-safe manner
        /// </summary>
        IOCompletionTraceHook RemoveTraceHook();

        /// <summary>
        /// Binds a file handle such that overlapped IO completions are dispatched to this completion manager.
        /// This must be called before e.g. <see cref="ReadFileOverlapped"/> for the handle.
        /// </summary>
        void BindFileHandle(SafeFileHandle handle);

        /// <summary>
        /// Issues an async read via <c>ReadFile</c>. The eventual completion will be sent to <paramref name="target" />.
        /// Note that <paramref name="pinnedBuffer"/> must be pinned on a callstack that lives until I/O completion or with a pinning <see cref="System.Runtime.InteropServices.GCHandle"/>,
        /// since it directly receives any read bytes from the kernel.
        /// </summary>
        unsafe Overlapped* ReadFileOverlapped(
            IIOCompletionTarget target,
            SafeFileHandle handle,
            byte* pinnedBuffer,
            int bytesToRead,
            long fileOffset);

        /// <summary>
        /// Cancels overlapped.
        /// </summary>
        unsafe void CancelOverlapped(SafeFileHandle handle, Overlapped* overlapped);
    }
}
