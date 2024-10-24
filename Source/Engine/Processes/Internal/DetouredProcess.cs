// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Native.IO;
using BuildXL.Native.Processes;
using BuildXL.Native.Streams;
using BuildXL.Native.Tracing;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Diagnostics;
using BuildXL.Utilities.Instrumentation.Common;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Threading;
using Microsoft.Win32.SafeHandles;
#if FEATURE_SAFE_PROCESS_HANDLE
using SafeProcessHandle = Microsoft.Win32.SafeHandles.SafeProcessHandle;
#else
using SafeProcessHandle = BuildXL.Interop.Windows.SafeProcessHandle;
#endif

namespace BuildXL.Processes.Internal
{
    /// <summary>
    /// This class implements a managed abstraction of a detoured process creation
    /// </summary>
    /// <remarks>
    /// All public methods of this class are thread safe.
    /// </remarks>
    internal sealed class DetouredProcess : IDisposable
    {
        private readonly SemaphoreSlim m_syncSemaphore = TaskUtilities.CreateMutex();
        private readonly ReadWriteLock m_queryJobDataLock = ReadWriteLock.Create();
        private readonly int m_bufferSize;
        private readonly string m_commandLine;
        private readonly StreamDataReceived m_errorDataReceived;
        private readonly StreamDataReceived m_outputDataReceived;
        private readonly Func<Task> m_processExitingAsync;
        private readonly Func<Task> m_processExited;
        private readonly Encoding m_standardErrorEncoding;
        private readonly Encoding m_standardInputEncoding;
        private readonly Encoding m_standardOutputEncoding;
        private readonly byte[] m_unicodeEnvironmentBlock;
        private readonly string m_workingDirectory;
        private bool m_disposed;
        private IAsyncPipeReader m_errorReader;
        private JobObject m_job;
        private readonly bool m_jobObjectCreatedExternally;
        private bool m_killed;
        private bool m_timedout;
        private bool m_hasDetoursFailures;
        private IAsyncPipeReader m_outputReader;
        private SafeProcessHandle m_processHandle;
        private int m_processId;
        private SafeWaitHandleFromSafeHandle m_processWaitHandle;
        private RegisteredWaitHandle m_registeredWaitHandle;
        private StreamWriter m_standardInputWriter;
        private bool m_waiting;
        private bool m_starting;
        private bool m_started;
        private bool m_exited;
        private readonly TimeSpan? m_timeout;
        private ProcessTreeContext m_processInjector;
        private readonly bool m_disableConHostSharing;
        private long m_startUpTime;
        private readonly string m_timeoutDumpDirectory;
        [SuppressMessage("Microsoft.Reliability", "CA2006:UseSafeHandleToEncapsulateNativeResources")]
        private static readonly IntPtr s_consoleWindow = Native.Processes.Windows.ProcessUtilitiesWin.GetConsoleWindow();
        private readonly bool m_setJobBreakawayOk;
        private readonly bool m_createJobObjectForCurrentProcess;
        private readonly int m_numRetriesPipeReadOnCancel;
        private readonly Action<string> m_debugPipeReporter;
        private int m_killedCallFlag;

        /// Gather information for diagnosing flaky tests
        private readonly bool m_diagnosticsEnabled = false;
        private readonly StringBuilder m_diagnostics;
        internal string Diagnostics => m_diagnostics?.ToString();

        private readonly LoggingContext m_loggingContext;

        /// <summary>
        /// Extended timeout time.
        /// </summary>
        /// <remarks>
        /// Timeout time can be extended when the process gets suspended.
        /// </remarks>
        private long m_extendedTimeoutMs;
        private readonly System.Diagnostics.Stopwatch m_suspendStopwatch = new System.Diagnostics.Stopwatch();

#region public getters

        /// <summary>
        /// Whether this process has started. Once true, it will never become false.
        /// </summary>
        public bool HasStarted => Volatile.Read(ref m_started);

        /// <summary>
        /// Whether this process has exited. Once true, it will never become false. Implies <code>HasStarted</code>.
        /// </summary>
        public bool HasExited => Volatile.Read(ref m_exited);

        /// <summary>
        /// True if this process has started but hasn't exited yet
        /// </summary>
        public bool IsRunning => HasStarted && !HasExited;

        /// <summary>
        /// Retrieves the process id associated with this process.
        /// </summary>
        /// <remarks>
        /// This method can only be invoked after the process has started.
        /// </remarks>
        public int GetProcessId()
        {
            Contract.Requires(HasStarted);
            return Volatile.Read(ref m_processId);
        }

        /// <summary>
        /// Retrieves the Windows job object associated with this process, if any.
        /// </summary>
        /// <remarks>
        /// This method can only be invoked after the process has started.
        /// </remarks>
        /// <returns>
        /// Result is null after this instance has been disposed or on non-Windows OSes.
        /// </returns>
        public JobObject GetJobObject()
        {
            Contract.Requires(HasStarted);
            return Volatile.Read(ref m_job);
        }

        /// <summary>
        /// Retrieves whether this process has exceeded its specified timeout.
        /// </summary>
        /// <remarks>
        /// This method can only be invoked after the process has started.
        /// </remarks>
        public bool TimedOut
        {
            get
            {
                Contract.Requires(HasStarted);
                return Volatile.Read(ref m_timedout);
            }
        }

        /// <summary>
        /// Retrieves whether an attempt was made to kill this process or a nested child process.
        /// </summary>
        /// <remarks>
        /// This method can only be invoked after the process has started.
        /// </remarks>
        public bool Killed
        {
            get
            {
                Contract.Requires(HasStarted);
                return Volatile.Read(ref m_killed);
            }
        }

        /// <summary>
        /// Retrieves whether there are failures in the detouring code.
        /// </summary>
        /// <remarks>
        /// This method can only be invoked after the process has started.
        /// </remarks>
        public bool HasDetoursInjectionFailures
        {
            get
            {
                Contract.Requires(HasStarted);
                return Volatile.Read(ref m_hasDetoursFailures);
            }
        }

        /// <summary>
        /// Path of the memory dump created if a process times out. This may be null if the process did not time out
        /// or if capturing the dump failed
        /// </summary>
        public string DumpFileDirectory { get; private set; }

        /// <summary>
        /// Exception describing why creating a memory dump may have failed.
        /// </summary>
        public Exception DumpCreationException { get; private set; }

        /// <summary>
        /// Tries to kill the process and all child processes.
        /// </summary>
        /// <remarks>
        /// It's okay to call this method at any time; however, before process start and after process termination or disposing of
        /// this instance, it does nothing.
        /// </remarks>
        public void Kill(int exitCode)
        {
            if (Interlocked.Increment(ref m_killedCallFlag) > 1)
            {
                return;
            }
            // Notify the injected that the process is being killed
            m_processInjector?.OnKilled();
            LogDiagnostics($"Process will be killed with exit code {exitCode}");
            var processHandle = m_processHandle;
            if (processHandle != null && !processHandle.IsInvalid && !processHandle.IsClosed)
            {
                // Ignore result, as there is a race with regular process termination that we cannot do anything about.
                m_killed = true;
                // No job object means that we are on an old OS; let's just terminate this process (we can't reliably terminate all child processes)
                Analysis.IgnoreResult(Native.Processes.ProcessUtilities.TerminateProcess(processHandle, exitCode));
                LogDiagnostics("DetouredProcess is killed using TerminateProcess");
            }

            using (m_queryJobDataLock.AcquireWriteLock())
            {
                JobObject jobObject = m_job;
                if (jobObject != null)
                {
                    // Ignore result, as there is a race with regular shutdown.
                    m_killed = true;
                    Analysis.IgnoreResult(jobObject.Terminate(exitCode));

                    LogDiagnostics("DetouredProcess is killed via JobObject termination");
                }
            }

            LogDiagnostics("Stack trace:");
            LogDiagnostics(Environment.StackTrace);
        }

        /// <summary>
        /// Gets the process exit code.
        /// </summary>
        /// <remarks>
        /// This method can only be invoked after the process has exited.");
        /// The result is undefined if this instance has been disposed.
        /// </remarks>
        public int GetExitCode()
        {
            Contract.Requires(HasExited);
            using (m_syncSemaphore.AcquireSemaphore())
            {
                if (m_disposed)
                {
                    return -1;
                }

                if (!ProcessUtilities.GetExitCodeProcess(m_processHandle, out int exitCode))
                {
                    throw new NativeWin32Exception(Marshal.GetLastWin32Error(), "Unable to get exit code.");
                }

                return exitCode;
            }
        }

        /// <summary>
        /// Gets process time information (start, exit, user-time, etc.) for the primary process. This accounts for the process started
        /// directly but not for any child processes.
        /// </summary>
        /// <remarks>
        /// This method can only be invoked after the process has started but before disposal.
        /// Note that aggregate accounting is available by querying the wrapping job object, if present;
        /// see <see cref="GetJobObject"/>.
        /// </remarks>
        public ProcessTimes GetTimesForPrimaryProcess()
        {
            Contract.Requires(HasStarted);
            using (m_syncSemaphore.AcquireSemaphore())
            {
                Contract.Assume(!m_disposed);
                Contract.Assume(m_processHandle != null, "Process not yet started.");

                if (!ProcessUtilities.GetProcessTimes(m_processHandle.DangerousGetHandle(), out long creation, out long exit, out long kernel, out long user))
                {
                    throw new NativeWin32Exception(Marshal.GetLastWin32Error(), "Unable to get times.");
                }

                return new ProcessTimes(creation: creation, exit: exit, kernel: kernel, user: user);
            }
        }

        /// <summary>
        /// Writes to standard input.
        /// </summary>
        /// <remarks>
        /// If the process has exited or this instance has been disposed, then this method does nothing.
        /// </remarks>
        public async Task WriteStandardInputLineAsync(string line)
        {
            Contract.Requires(HasStarted);
            using (await m_syncSemaphore.AcquireAsync())
            {
                if (m_standardInputWriter != null)
                {
                    await m_standardInputWriter.WriteLineAsync(line);

                    // Standard input writer is not set to auto flush; need to flush manually.
                    await m_standardInputWriter.FlushAsync();
                }
            }
        }

        /// <summary>
        /// Closes the standard input.
        /// </summary>
        /// <remarks>
        /// If the process has exited or this instance has been disposed, then this method does nothing.
        /// </remarks>
        public void CloseStandardInput()
        {
            Contract.Requires(HasStarted);
            using (var releaser = m_syncSemaphore.AcquireSemaphore())
            {
                InternalCloseStandardInput(releaser);
            }
        }

#endregion

        public DetouredProcess(
            int bufferSize,
            string commandLine,
            string workingDirectory,
            byte[] unicodeEnvironmentBlock,
            Encoding standardInputEncoding,
            Encoding standardErrorEncoding,
            StreamDataReceived errorDataReceived,
            Encoding standardOutputEncoding,
            StreamDataReceived outputDataReceived,
            Func<Task> processExitingAsync,
            Func<Task> processExited,
            TimeSpan? timeout,
            bool disableConHostSharing,
            LoggingContext loggingContext,
            string timeoutDumpDirectory,
            bool setJobBreakawayOk,
            bool createJobObjectForCurrentProcess,
            bool diagnosticsEnabled,
            int numRetriesPipeReadOnCancel,
            Action<string> debugPipeReporter,
            JobObject externallyProvidedJobObject)
        {
            Contract.Requires(bufferSize >= 128);
            Contract.Requires(!string.IsNullOrEmpty(commandLine));
            Contract.Requires(standardInputEncoding != null);
            Contract.Requires(standardErrorEncoding != null);
            Contract.Requires(standardOutputEncoding != null);
            Contract.Requires(!timeout.HasValue || timeout.Value >= TimeSpan.Zero);

            m_bufferSize = bufferSize;
            m_commandLine = commandLine;
            m_workingDirectory = workingDirectory;
            m_unicodeEnvironmentBlock = unicodeEnvironmentBlock;
            m_standardInputEncoding = standardInputEncoding;
            m_standardErrorEncoding = standardErrorEncoding;
            m_errorDataReceived = errorDataReceived;
            m_standardOutputEncoding = standardOutputEncoding;
            m_outputDataReceived = outputDataReceived;
            m_processExitingAsync = processExitingAsync;
            m_processExited = processExited;
            m_timeout = timeout;
            m_disableConHostSharing = disableConHostSharing;
            m_setJobBreakawayOk = setJobBreakawayOk;
            m_createJobObjectForCurrentProcess = createJobObjectForCurrentProcess;
            if (m_workingDirectory != null && m_workingDirectory.Length == 0)
            {
                m_workingDirectory = Directory.GetCurrentDirectory();
            }

            m_loggingContext = loggingContext;
            m_timeoutDumpDirectory = timeoutDumpDirectory;
            m_numRetriesPipeReadOnCancel = numRetriesPipeReadOnCancel;
            m_debugPipeReporter = debugPipeReporter;
            m_job = externallyProvidedJobObject;
            m_jobObjectCreatedExternally = (externallyProvidedJobObject != null);

            if (diagnosticsEnabled)
            {
                m_diagnosticsEnabled = true;
                m_diagnostics = new StringBuilder();
            }
        }

        /// <summary>
        /// Starts the process. An <paramref name="inheritableReportHandle"/> may be provided, in which case
        /// that handle will be inherited to the new process.
        /// </summary>
        /// <remarks>
        /// Start may be only called once on an instance, and not after this instance was disposed.
        /// A provided <paramref name="inheritableReportHandle"/> will be closed after process creation
        /// (since it should then be owned by the child process).
        /// </remarks>
        /// <exception cref="BuildXLException">Thrown if creating or detouring the process fails.</exception>
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope")]
        public void Start(
            Guid payloadGuid,
            ArraySegment<byte> payloadData,
            SafeFileHandle inheritableReportHandle,
            string dllNameX64,
            string dllNameX86)
        {
            using (m_syncSemaphore.AcquireSemaphore())
            {
                if (m_starting || m_disposed)
                {
                    throw new InvalidOperationException("Cannot invoke start process more than once or after this instance has been Disposed.");
                }

                m_starting = true;

                bool redirectStreams = m_errorDataReceived != null || m_outputDataReceived != null;

                // The process creation flags:
                // - We use CREATE_DEFAULT_ERROR_MODE to ensure that the hard error mode of the child process (i.e., GetErrorMode)
                //   is deterministic. Inheriting error mode is the default, but there may be some concurrent operation that temporarily
                //   changes it (process global). The CLR has been observed to do so.
                // - We use CREATE_NO_WINDOW when redirecting stdout/err/in and there is a parent console (to prevent creating extra conhost.exe
                //   processes on Windows when running headless) or when console sharing is disabled (typical for BuildXL which takes over
                //   management of the console). Otherwise we use the parent console for output.
                int creationFlags = ProcessUtilities.CREATE_DEFAULT_ERROR_MODE;
                if (redirectStreams && (s_consoleWindow != IntPtr.Zero || m_disableConHostSharing))
                {
                    creationFlags |= ProcessUtilities.CREATE_NO_WINDOW;
                }

                bool useManagedPipeReader = !PipeReaderFactory.ShouldUseLegacyPipeReader();

                SafeFileHandle standardInputWritePipeHandle = null;
                SafeFileHandle standardOutputReadPipeHandle = null;
                SafeFileHandle standardErrorReadPipeHandle = null;
                NamedPipeServerStream standardOutputPipeStream = null;
                NamedPipeServerStream standardErrorPipeStream = null;

                try
                {
                    // set up the environment block parameter
                    var environmentHandle = default(GCHandle);
                    var payloadHandle = default(GCHandle);

                    SafeFileHandle hStdInput = null;
                    SafeFileHandle hStdOutput = null;
                    SafeFileHandle hStdError = null;
                    SafeThreadHandle threadHandle = null;

                    try
                    {
                        IntPtr environmentPtr = IntPtr.Zero;
                        if (m_unicodeEnvironmentBlock != null)
                        {
                            creationFlags |= ProcessUtilities.CREATE_UNICODE_ENVIRONMENT;
                            environmentHandle = GCHandle.Alloc(m_unicodeEnvironmentBlock, GCHandleType.Pinned);
                            environmentPtr = environmentHandle.AddrOfPinnedObject();
                        }

                        if (redirectStreams)
                        {
                            Pipes.CreateInheritablePipe(
                                Pipes.PipeInheritance.InheritRead,
                                Pipes.PipeFlags.WriteSideAsync,
                                readHandle: out hStdInput,
                                writeHandle: out standardInputWritePipeHandle);
                        }
                        else
                        {
                            // Avoid stdin hooking when not needed.
                            hStdInput = new SafeFileHandle(new IntPtr(-1), true);
                        }

                        if (m_outputDataReceived != null)
                        {
                            if (useManagedPipeReader)
                            {
                                standardOutputPipeStream = Pipes.CreateNamedPipeServerStream(
                                    PipeDirection.In,
                                    PipeOptions.Asynchronous,
                                    PipeOptions.None,
                                    out hStdOutput);
                            }
                            else
                            {
                                Pipes.CreateInheritablePipe(
                                    Pipes.PipeInheritance.InheritWrite,
                                    Pipes.PipeFlags.ReadSideAsync,
                                    readHandle: out standardOutputReadPipeHandle,
                                    writeHandle: out hStdOutput);
                            }
                        }
                        else
                        {
                            // Pass through to the parent console.
                            hStdOutput = new SafeFileHandle(new IntPtr(-1), true);
                        }

                        if (m_errorDataReceived != null)
                        {
                            if (useManagedPipeReader)
                            {
                                standardErrorPipeStream = Pipes.CreateNamedPipeServerStream(
                                    PipeDirection.In,
                                    PipeOptions.Asynchronous,
                                    PipeOptions.None,
                                    out hStdError);
                            }
                            else
                            {
                                Pipes.CreateInheritablePipe(
                                    Pipes.PipeInheritance.InheritWrite,
                                    Pipes.PipeFlags.ReadSideAsync,
                                    readHandle: out standardErrorReadPipeHandle,
                                    writeHandle: out hStdError);
                            }
                        }
                        else
                        {
                            // Pass through to the parent console.
                            hStdError = new SafeFileHandle(new IntPtr(-1), true);
                        }

                        // Include the pip process in the job object for the build to ensure it is automatically closed.
                        if (m_createJobObjectForCurrentProcess)
                        {
                            JobObject.SetTerminateOnCloseOnCurrentProcessJob();
                        }

                        // Initialize the injector
                        m_processInjector = new ProcessTreeContext(
                            payloadGuid,
                            inheritableReportHandle,
                            payloadData,
                            dllNameX64,
                            dllNameX86,
                            m_numRetriesPipeReadOnCancel,
                            m_debugPipeReporter,
                            m_loggingContext);

                        if (!m_jobObjectCreatedExternally)
                        {
                            m_job = new JobObject(null);

                            // We want the effects of SEM_NOGPFAULTERRORBOX on all children (but can't set that with CreateProcess).
                            // That's not set otherwise (even if set in this process) due to CREATE_DEFAULT_ERROR_MODE above.
                            m_job.SetLimitInformation(terminateOnClose: true, failCriticalErrors: false, allowProcessesToBreakAway: m_setJobBreakawayOk);
                        }

                        m_processInjector.Listen();

                        // The call to the CreateDetouredProcess below will add a newly created process to the job.
                        System.Diagnostics.Stopwatch startUpTimeWatch = System.Diagnostics.Stopwatch.StartNew();
                        var detouredProcessCreationStatus =
                            ProcessUtilities.CreateDetouredProcess(
                                m_commandLine,
                                creationFlags,
                                environmentPtr,
                                m_workingDirectory,
                                hStdInput,
                                hStdOutput,
                                hStdError,
                                m_job,
                                m_processInjector.Injector,
                                out m_processHandle,
                                out threadHandle,
                                out m_processId,
                                out int errorCode);
                        startUpTimeWatch.Stop();
                        m_startUpTime = startUpTimeWatch.ElapsedMilliseconds;

                        if (detouredProcessCreationStatus != CreateDetouredProcessStatus.Succeeded)
                        {
                            // TODO: Indicating user vs. internal errors (and particular phase failures e.g. adding to job object or injecting detours)
                            //       is good progress on the transparency into these failures. But consider making this indication visible beyond this
                            //       function without throwing exceptions; consider returning a structured value or logging events.
                            string message;
                            if (detouredProcessCreationStatus.IsDetoursSpecific())
                            {
                                message = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Internal error during process creation: {0:G}",
                                    detouredProcessCreationStatus);
                            }
                            else if (detouredProcessCreationStatus == CreateDetouredProcessStatus.ProcessCreationFailed)
                            {
                                message = "Process creation failed";
                            }
                            else
                            {
                                message = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Process creation failed: {0:G}",
                                    detouredProcessCreationStatus);
                            }

                            throw new BuildXLException(
                                message,
                                new NativeWin32Exception(errorCode));
                        }

                        // TODO: We should establish good post-conditions for CreateDetouredProcess. As a temporary measure, it would be nice
                        //       to determine if we are sometimes getting invalid process handles with retVal == true. So for now we differentiate
                        //       that possible case with a unique error string.
                        if (m_processHandle.IsInvalid)
                        {
                            throw new BuildXLException("Unable to start or detour a process (process handle invalid)", new NativeWin32Exception(errorCode));
                        }
                    }
                    finally
                    {
                        if (environmentHandle.IsAllocated)
                        {
                            environmentHandle.Free();
                        }

                        if (payloadHandle.IsAllocated)
                        {
                            payloadHandle.Free();
                        }

                        if (hStdInput != null && !hStdInput.IsInvalid)
                        {
                            hStdInput.Dispose();
                        }

                        if (hStdOutput != null && !hStdOutput.IsInvalid)
                        {
                            hStdOutput.Dispose();
                        }

                        if (hStdError != null && !hStdError.IsInvalid)
                        {
                            hStdError.Dispose();
                        }

                        if (inheritableReportHandle != null && !inheritableReportHandle.IsInvalid)
                        {
                            inheritableReportHandle.Dispose();
                        }

                        if (threadHandle != null && !threadHandle.IsInvalid)
                        {
                            threadHandle.Dispose();
                        }
                    }

                    if (standardInputWritePipeHandle != null)
                    {
                        var standardInputStream = new FileStream(standardInputWritePipeHandle, FileAccess.Write, m_bufferSize, isAsync: true);

                        // Do not set auto flush to true because, if it is set to true, StreamWriter would immediately do a write operation to the pipe (although the data is empty).
                        // When the pipe is closed (or is being closed), the write operation would fail and throw an exception. This can happen if the detoured process does not need
                        // any standard input, and the process terminates quickly before this pipe setup is completed.
                        // It was not an issue before .NET 8 because when writing to a FileStream that represented a closed or disconnected pipe, the underlying operating system
                        // error was ignored and the write was reported as successful. However, nothing was written to the pipe. Starting in .NET 8, when writing to a FileStream whose
                        // underlying pipe is closed or disconnected, the write fails and an IOException is thrown.
                        // See breaking changes in .NET 8: https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/8.0/filestream-disposed-pipe
                        m_standardInputWriter = new StreamWriter(standardInputStream, m_standardInputEncoding, m_bufferSize) { AutoFlush = false };
                    }

                    if (standardOutputReadPipeHandle != null)
                    {
                        var standardOutputFile = AsyncFileFactory.CreateAsyncFile(
                            standardOutputReadPipeHandle,
                            FileDesiredAccess.GenericRead,
                            ownsHandle: true,
                            kind: FileKind.Pipe);
                        m_outputReader = new AsyncPipeReader(standardOutputFile, m_outputDataReceived, m_standardOutputEncoding, m_bufferSize);
                        m_outputReader.BeginReadLine();
                    }

                    if (standardOutputPipeStream != null)
                    {
                        m_outputReader = PipeReaderFactory.CreateManagedPipeReader(
                            standardOutputPipeStream,
                            message => m_outputDataReceived(message),
                            m_standardOutputEncoding,
                            m_bufferSize,
                            // Force to use StreamReader based pipe reader because Pipeline one does not handle
                            // different kinds of line endings.
                            overrideKind: PipeReaderFactory.Kind.Stream);
                        m_outputReader.BeginReadLine();
                    }

                    if (standardErrorReadPipeHandle != null)
                    {
                        var standardErrorFile = AsyncFileFactory.CreateAsyncFile(
                            standardErrorReadPipeHandle,
                            FileDesiredAccess.GenericRead,
                            ownsHandle: true,
                            kind: FileKind.Pipe);
                        m_errorReader = new AsyncPipeReader(standardErrorFile, m_errorDataReceived, m_standardErrorEncoding, m_bufferSize);
                        m_errorReader.BeginReadLine();
                    }

                    if (standardErrorPipeStream != null)
                    {
                        m_errorReader = PipeReaderFactory.CreateManagedPipeReader(
                            standardErrorPipeStream,
                            message => m_errorDataReceived(message),
                            m_standardErrorEncoding,
                            m_bufferSize,
                            // Force to use StreamReader based pipe reader because Pipeline one does not handle
                            // different kinds of line endings.
                            overrideKind: PipeReaderFactory.Kind.Stream);
                        m_errorReader.BeginReadLine();
                    }

                    Contract.Assert(!m_processHandle.IsInvalid);
                    m_processWaitHandle = new SafeWaitHandleFromSafeHandle(m_processHandle);

                    m_waiting = true;

                    TimeSpan timeout = m_timeout ?? Timeout.InfiniteTimeSpan;
                    m_registeredWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                        m_processWaitHandle,
                        CompletionCallbackAsync,
                        null,
                        timeout,
                        true);

                    m_started = true;
                }
                catch (Exception e)
                {
                    if (e is AccessViolationException)
                    {
                        Logger.Log.DetouredProcessAccessViolationException(m_loggingContext, creationFlags + " - " + m_commandLine);
                    }

                    // Dispose pipe handles in case they are not assigned to streams.
                    if (m_standardInputWriter == null)
                    {
                        standardInputWritePipeHandle?.Dispose();
                    }

                    if (m_outputReader == null)
                    {
                        standardOutputReadPipeHandle?.Dispose();
                    }

                    if (m_errorReader == null)
                    {
                        standardErrorReadPipeHandle?.Dispose();
                    }

                    throw;
                }
            }
        }

        /// <nodoc />
        internal void StartMeasuringSuspensionTime()
        {
            // Start counting suspended time
            m_suspendStopwatch.Start();
        }

        /// <nodoc />
        internal void StopMeasuringSuspensionTime()
        {
            Contract.Requires(m_suspendStopwatch.IsRunning);
            lock (m_suspendStopwatch)
            {
                m_extendedTimeoutMs += m_suspendStopwatch.ElapsedMilliseconds;
                m_suspendStopwatch.Reset();
            }
        }

        private long GetExtendedTimeoutOnCompletion()
        {
            long extensionMs = 0;
            lock (m_suspendStopwatch)
            {
                extensionMs = m_extendedTimeoutMs;
                if (m_suspendStopwatch.IsRunning)   // We're suspended right now
                {
                    extensionMs += m_suspendStopwatch.ElapsedMilliseconds;
                    m_suspendStopwatch.Restart();
                }

                m_extendedTimeoutMs = 0;
            }

            return extensionMs;
        }

        /// <summary>
        /// Iterates through the job object processes executing an action for each one.
        /// </summary>
        public VisitJobObjectResult TryVisitJobObjectProcesses(Action<SafeProcessHandle, uint> actionForProcess)
        {
            Contract.Requires(HasStarted);
            // Accessing jobObject needs to be protected by m_queryJobDataLock.
            // After we collected the child process ids, we might dispose the job object and the child process might be invalid.
            // Acquiring the reader lock will prevent the job object from disposing. 
            using (m_queryJobDataLock.AcquireReadLock())
            {
                var jobObject = GetJobObject();
                if (m_killed)
                {
                    // Terminated before starting the operation
                    return VisitJobObjectResult.TerminatedBeforeVisitation;
                }

                if (!jobObject.TryGetProcessIds(m_loggingContext, out uint[] childProcessIds))
                {
                    return VisitJobObjectResult.Failed;
                }

                foreach (uint processId in childProcessIds)
                {
                    using (SafeProcessHandle processHandle = ProcessUtilities.OpenProcess(
                        ProcessSecurityAndAccessRights.PROCESS_QUERY_INFORMATION | ProcessSecurityAndAccessRights.PROCESS_SET_QUOTA,
                        false,
                        processId))
                    {
                        if (!jobObject.ContainsProcess(processHandle))
                        {
                            // We are too late: process handle is invalid because it closed already,
                            // or process id got reused by another process.
                            continue;
                        }

                        if (!ProcessUtilities.GetExitCodeProcess(processHandle, out int exitCode))
                        {
                            // we are too late: process id got reused by another process
                            continue;
                        }

                        actionForProcess(processHandle, processId);
                    }
                }
                return VisitJobObjectResult.Success;
            }
        }

        private void StopWaiting(TaskUtilities.SemaphoreReleaser semaphoreReleaser)
        {
            Contract.Requires(semaphoreReleaser.IsValid && semaphoreReleaser.CurrentCount == 0);
            Analysis.IgnoreArgument(semaphoreReleaser);

            if (m_waiting)
            {
                m_waiting = false;
                m_registeredWaitHandle.Unregister(null);
                m_processWaitHandle.Dispose();
                m_processWaitHandle = null;
                m_registeredWaitHandle = null;
            }
        }

        private async Task WaitUntilErrorAndOutputEof(bool cancel, TaskUtilities.SemaphoreReleaser semaphoreReleaser)
        {
            Contract.Requires(semaphoreReleaser.IsValid && semaphoreReleaser.CurrentCount == 0);
            if (m_outputReader != null)
            {
                if (!m_killed && !cancel)
                {
                    await m_outputReader.CompletionAsync(true);
                }

                m_outputReader.Dispose();
                m_outputReader = null;
            }

            if (m_errorReader != null)
            {
                if (!m_killed && !cancel)
                {
                    await m_errorReader.CompletionAsync(true);
                }

                m_errorReader.Dispose();
                m_errorReader = null;
            }

            InternalCloseStandardInput(semaphoreReleaser);
        }

        private void InternalCloseStandardInput(TaskUtilities.SemaphoreReleaser semaphoreReleaser)
        {
            Contract.Requires(semaphoreReleaser.IsValid && semaphoreReleaser.CurrentCount == 0);
            Analysis.IgnoreArgument(semaphoreReleaser);

            if (m_standardInputWriter != null)
            {
                m_standardInputWriter.Dispose();
                m_standardInputWriter = null;
            }
        }

        [SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "FailFast")]
        [SuppressMessage("Microsoft.Naming", "CA2204:Literals should be spelled correctly", MessageId = "DetouredProcess")]
        [SuppressMessage("AsyncUsage", "AsyncFixer03:FireForgetAsyncVoid")]
        [SuppressMessage("Microsoft.Performance", "CA1801")]
        private async void CompletionCallbackAsync(object context, bool timedOut)
        {
            if (timedOut)
            {
                var extendedTimeoutMs = GetExtendedTimeoutOnCompletion();
                if (extendedTimeoutMs > 0)
                {
                    // We consumed part of the timeout while suspended, so we give it another chance
                    m_waiting = true;
                    m_registeredWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                        m_processWaitHandle,
                        CompletionCallbackAsync,
                        null,
                        Convert.ToUInt32(extendedTimeoutMs),
                        true);
                    return;
                }

                Volatile.Write(ref m_timedout, true);

                // Attempt to dump the timed out process before killing it
                if (!m_processHandle.IsInvalid && !m_processHandle.IsClosed && m_workingDirectory != null)
                {
                    DumpFileDirectory = m_timeoutDumpDirectory;

                    Exception dumpCreationException;

                    if (!string.IsNullOrEmpty(DumpFileDirectory) && DumpCreationException == null)
                    {
                        var survivingProcesses = JobObjectProcessDumper.GetAndOptionallyDumpProcesses(
                            jobObject: m_job,
                            loggingContext: m_loggingContext,
                            survivingPipProcessDumpDirectory: DumpFileDirectory,
                            dumpProcess: true,
                            excludedDumpProcessNames: [],
                            dumpException: out dumpCreationException);
                        if (dumpCreationException != null)
                        {
                            DumpCreationException = dumpCreationException;
                        }
                    }
                }

                Kill(ExitCodes.Timeout);

                using (await m_syncSemaphore.AcquireAsync())
                {
                    if (m_processWaitHandle != null)
                    {
                        await m_processWaitHandle;
                        m_exited = true;
                    }
                }
            }
            else
            {
                m_exited = true;
            }

            using (var semaphoreReleaser = await m_syncSemaphore.AcquireAsync())
            {
                Contract.Assume(m_waiting, "CompletionCallback should only be triggered once.");
                StopWaiting(semaphoreReleaser);
            }

            try
            {
                await Task.Run(
                    async () =>
                    {
                        // Before waiting on anything, we call the processExiting callback.
                        // This callback happens to be responsible for triggering or forcing
                        // cleanup of all processes in this job. We can't finish waiting on pipe EOF
                        // (error, output, report, and process-injector pipes) until all handles
                        // to the write-sides are closed.
                        Func<Task> processExiting = m_processExitingAsync;
                        if (processExiting != null)
                        {
                            await processExiting();
                        }

                        using (var semaphoreReleaser = await m_syncSemaphore.AcquireAsync())
                        {
                            // Error and output pipes: Finish reading and then expect EOF (see above).
                            await WaitUntilErrorAndOutputEof(false, semaphoreReleaser);

                            // Stop process injection service. This finishes reading the injector control pipe (for injection requests).
                            // Since we don't get to the 'end' of the pipe until all child-processes holding on to it exit, we must
                            // perform this wait after processExiting() above.
                            if (m_processInjector != null)
                            {
                                // Stop() discards all unhandled requests. That is only safe to do since we are assuming that all processes
                                // in the job have exited (so those requests aren't relevant anymore)
                                await m_processInjector.StopAsync();
                                m_hasDetoursFailures = m_processInjector.HasDetoursInjectionFailures;
                                m_processInjector.Dispose();
                                m_processInjector = null;
                            }
                        }

                        // Now, callback for additional cleanup (can safely wait on extra pipes, such as the SandboxedProcess report pipe,
                        // if processExiting causes process tree teardown; see above).
                        var processExited = m_processExited;
                        if (processExited != null)
                        {
                            await processExited();
                        }
                    });
            }
            catch (Exception exception)
            {
                // Something above may fail and that has to be observed. Unfortunately, throwing a normal exception in a continuation
                // just means someone has to observe the continuation. So we tug on some bootstraps by killing the process here.
                // TODO: It'd be nice if we had a FailFast equivalent that went through AppDomain.UnhandledExceptionEvent for logging.
                ExceptionHandling.OnFatalException(exception, "FailFast in DetouredProcess completion callback");
            }
        }

        /// <summary>
        /// Releases all resources associated with this process.
        /// </summary>
        /// <remarks>
        /// This function can be called at any time, and as often as desired.
        /// </remarks>
        [SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "m_errorReader", Justification = "Disposed in WaitUntilErrorAndOutputEof")]
        [SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "m_outputReader", Justification = "Disposed in WaitUntilErrorAndOutputEof")]
        public void Dispose()
        {
            if (!m_disposed)
            {
                using (var semaphoreReleaser = m_syncSemaphore.AcquireSemaphore())
                {
                    StopWaiting(semaphoreReleaser);
                    WaitUntilErrorAndOutputEof(true, semaphoreReleaser).GetAwaiter().GetResult();

                    if (m_processInjector != null)
                    {
                        // We may have already called Stop() in CompletionCallback, but that's okay.
                        m_processInjector.StopAsync().GetAwaiter().GetResult();
                        m_processInjector.Dispose();
                        m_processInjector = null;
                    }

                    if (m_processHandle != null)
                    {
                        m_processHandle.Dispose();
                        m_processHandle = null;
                    }

                    if (!m_jobObjectCreatedExternally && m_job != null)
                    {
                        m_job.Dispose();  // Terminates any remaining processes in the job object
                        m_job = null;
                    }
                }

                m_syncSemaphore.Dispose();

                m_disposed = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LogDiagnostics(string line)
        {
            if (m_diagnosticsEnabled)
            {
                m_diagnostics.AppendLine(line);
            }
        }

        public long StartTime => m_startUpTime;
    }
}
