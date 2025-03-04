// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading.Tasks;

#pragma warning disable SA1139 // Use literal suffix notation instead of casting

namespace BuildXL.Utilities.Core
{
    /// <summary>Utilities for common exception handling patterns, such as mapping them to <see cref="BuildXLException" /></summary>
    public static class ExceptionUtilities
    {
        /// <summary>
        /// Handler for unexpected exceptions passed to 
        /// </summary>
        public static event Action<Exception> UnexpectedException;

        private const int E_FAIL = unchecked((int)0x80004005);

        /// <summary>
        /// Creates a more readable string representation of the given exception.
        /// </summary>
        /// <remarks>
        /// For more details, see: https://github.com/benaadams/Ben.Demystifier
        /// </remarks>
        public static string ToStringDemystified(this Exception exception)
        {
            return exception.DemystifyToString();
        }

        /// <summary>
        /// Creates a more readable string representation of the given exception.
        /// </summary>
        /// <remarks>
        /// For more details, see: https://github.com/benaadams/Ben.Demystifier
        /// </remarks>
        public static string DemystifiedStackTrace(this Exception exception)
        {
            return exception.DemystifyStackTrace();
        }

        /// <summary>
        /// Returns true if an exception is a recoverable IO exception.
        /// </summary>
        public static bool IsRecoverableIoException(this Exception exception)
        {
            return exception is UnauthorizedAccessException or SecurityException or IOException or Win32Exception;
        }

        private static List<string> AllowedUnobservedExceptions = new()
        {
            // HttpClient can arbitrarily cause this to happen
            "An existing connection was forcibly closed by the remote host",
            // The Redis library has a lot of unobserved exceptions happening for unknown reasons
            "StackExchange.Redis.Redis",
            // Protocol errors from gRPC operations that are left unobserved
            "The HTTP/2 server sent invalid data on the connection. HTTP/2 error code 'PROTOCOL_ERROR' (0x1).",
            // Protocol errors from gRPC operations look differently in net8
            "An HTTP/2 connection could not be established because the server did not complete the HTTP/2 handshake.",
            // Unobserved timeouts during cache operations (see work item #2162565)
            "BuildXL.Cache.ContentStore.Service.Grpc.GrpcConnectionTimeoutException: Failed to connect to grpcs://",
            // Unobserved exceptions in BlobRestClient from BlobL3 cache due to ETags being inconsistent between calls. (See work item #2185687)
            "x-ms-error-code: ConditionNotMet"
        };

        private static bool ContainsAllowedException(Exception exception)
        {
            var exceptionText = exception.ToString();
            return AllowedUnobservedExceptions.Any(exceptionText.Contains);
        }

        /// <summary>
        /// Returns true if a given exception is a known exception that should be ignored when handling unobserved task exceptions.
        /// </summary>
        public static bool IsKnownUnobservedException(Exception exception)
        {
            if (exception is AggregateException ae)
            {
                ae = ae.Flatten();
                if (ae.InnerExceptions.Any(ContainsAllowedException))
                {
                    return true;
                }
            }
            else
            {
                return ContainsAllowedException(exception);
            }

            return false;
        }

        /// <summary>
        /// Invokes taskProducer, and handles any raised <see cref="BuildXLException" /> or <see cref="IOException" />
        /// by calling <paramref name="handler" />. The caught exception is re-thrown unless the handler itself throws. The typical
        /// use case is to wrap the <see cref="IOException" /> or <see cref="IOException" /> in a
        /// <see cref="UnauthorizedAccessException" />.
        /// These exceptions are commonly thrown from I/O functions in the BCL, and represent recoverable external errors.
        /// </summary>
        public static async Task<T> HandleRecoverableIOExceptionAsync<T>(Func<Task<T>> taskProducer, Action<Exception> handler)
        {
            Contract.RequiresNotNull(taskProducer);
            Contract.RequiresNotNull(handler);

            // We re-throw in the catch blocks (versus capturing and re-throwing) to avoid stomping on the call stack, etc.).
            try
            {
                return await taskProducer();
            }
            catch (UnauthorizedAccessException ex)
            {
                handler(ex);
                throw;
            }
            catch (SecurityException ex)
            {
                handler(ex);
                throw;
            }
            catch (IOException ex)
            {
                handler(ex);
                throw;
            }
            catch (Win32Exception ex)
            {
                handler(ex);
                throw;
            }
        }

        /// <summary>
        /// Invokes taskProducer, and handles any raised <see cref="IOException" /> or <see cref="UnauthorizedAccessException" />
        /// by calling <paramref name="handler" />. The caught exception is re-thrown unless the handler itself throws. The typical
        /// use case is to wrap the <see cref="IOException" /> or <see cref="UnauthorizedAccessException" /> in a
        /// <see cref="BuildXLException" />.
        /// These exceptions are commonly thrown from I/O functions in the BCL, and represent recoverable external errors.
        /// </summary>
        public static T HandleRecoverableIOException<T>(Func<T> func, Action<Exception> handler)
        {
            Contract.RequiresNotNull(func);
            Contract.RequiresNotNull(handler);

            // We re-throw in the catch blocks (versus capturing and re-throwing) to avoid stomping on the call stack, etc.).
            try
            {
                return func();
            }
            catch (UnauthorizedAccessException ex)
            {
                handler(ex);
                throw;
            }
            catch (SecurityException ex)
            {
                handler(ex);
                throw;
            }
            catch (IOException ex)
            {
                handler(ex);
                throw;
            }
            catch (Win32Exception ex)
            {
                handler(ex);
                throw;
            }
        }

        /// <summary>
        /// Invokes taskProducer, and handles any raised <see cref="IOException" /> or <see cref="UnauthorizedAccessException" />
        /// by calling <paramref name="handler" />. The caught exception is re-thrown unless the handler itself throws. The typical
        /// use case is to wrap the <see cref="IOException" /> or <see cref="UnauthorizedAccessException" /> in a
        /// <see cref="BuildXLException" />.
        /// These exceptions are commonly thrown from I/O functions in the BCL, and represent recoverable external errors.
        /// </summary>
        public static T HandleRecoverableIOException<T, TState>(TState state, Func<TState, T> func, Action<TState, Exception> handler)
        {
            Contract.RequiresNotNull(func);
            Contract.RequiresNotNull(handler);

            // We re-throw in the catch blocks (versus capturing and re-throwing) to avoid stomping on the call stack, etc.).
            try
            {
                return func(state);
            }
            catch (UnauthorizedAccessException ex)
            {
                handler(state, ex);
                throw;
            }
            catch (SecurityException ex)
            {
                handler(state, ex);
                throw;
            }
            catch (IOException ex)
            {
                handler(state, ex);
                throw;
            }
            catch (Win32Exception ex)
            {
                handler(state, ex);
                throw;
            }
        }

        /// <summary>
        /// Invokes taskProducer, and handles any raised <see cref="IOException" /> or <see cref="UnauthorizedAccessException" />
        /// by calling <paramref name="handler" />. The caught exception is re-thrown unless the handler itself throws. The typical
        /// use case is to wrap the <see cref="IOException" /> or <see cref="UnauthorizedAccessException" /> in a
        /// <see cref="BuildXLException" />.
        /// These exceptions are commonly thrown from I/O functions in the BCL, and represent recoverable external errors.
        /// </summary>
        public static void HandleRecoverableIOException(Action action, Action<Exception> handler)
        {
            Contract.RequiresNotNull(action);
            Contract.RequiresNotNull(handler);

            // We re-throw in the catch blocks (versus capturing and re-throwing) to avoid stomping on the call stack, etc.).
            try
            {
                action();
            }
            catch (UnauthorizedAccessException ex)
            {
                handler(ex);
                throw;
            }
            catch (SecurityException ex)
            {
                handler(ex);
                throw;
            }
            catch (IOException ex)
            {
                handler(ex);
                throw;
            }
            catch (Win32Exception ex)
            {
                handler(ex);
                throw;
            }
        }

        /// <summary>
        /// Attempts to assign a root cause to an arbitrary exception.
        /// </summary>
        /// <remarks>
        /// The analysis strategy is so:
        /// - Walking from the outermost to innermost exception, find the first root cause
        ///   (i.e., first that is not <see cref="ExceptionRootCause.Unknown"/>)
        /// - Respect <see cref="BuildXLException.RootCause"/> (we allow the enlightened creator
        ///   of the exception to provide a root cause).
        /// - For everything else, try to match patterns of exception type, HRESULT, etc.
        /// </remarks>
        public static ExceptionRootCause AnalyzeExceptionRootCause(Exception ex)
        {
            Contract.RequiresNotNull(ex);

            const int OutOfDiskSpaceHResult = unchecked((int)0x80070070);
            const int DataErrorCRCResult = unchecked((int)0x80070017);
            const int DataErrorSeek = unchecked((int)0x80070019);
            const int DataErrorSector = unchecked((int)0x8007001B);
            const int DataErrorWriteFault = unchecked((int)0x8007001D);
            const int DataErrorReadFault = unchecked((int)0x8007001E);
            const int DataErrorGeneralFault = unchecked((int)0x8007001F);
            const int NoSystemResourcesFault = unchecked((int)0x800705AA);
            const int PipeNotConnectedFault = unchecked((int)0x800700E9);
            const int NotEnoughStorage = unchecked((int)0x8);
            const int IncorrectFunction = unchecked((int)0x00000001);
            const int ConnectionReset = unchecked((int)0x80072746);

            var buildXLException = ex as BuildXLException;
            if (buildXLException != null)
            {
                return buildXLException.RootCause;
            }

            if (ex is OutOfMemoryException)
            {
                return ExceptionRootCause.OutOfMemory;
            }

            if (ex is EventSourceException evSrcException && evSrcException.InnerException is BuildXLException innerBuldXLException)
            {
                return innerBuldXLException.RootCause;
            }

            int exHResult = GetHResult(ex);

            if (ex is IOException && exHResult == IncorrectFunction)
            {
                return ExceptionRootCause.DeviceAccessError;
            }

            if (ex is IOException && ex.Message.Contains("No space left on device"))
            {
                return ExceptionRootCause.OutOfDiskSpace;
            }

            Win32Exception win32Ex = ex as Win32Exception;
            if (exHResult == OutOfDiskSpaceHResult ||
                (win32Ex != null && win32Ex.ErrorCode == NotEnoughStorage))
            {
                return ExceptionRootCause.OutOfDiskSpace;
            }

            if (exHResult == DataErrorCRCResult ||
                exHResult == DataErrorSeek ||
                exHResult == DataErrorSector ||
                exHResult == DataErrorWriteFault ||
                exHResult == DataErrorReadFault ||
                exHResult == DataErrorGeneralFault)
            {
                return ExceptionRootCause.DataErrorDriveFailure;
            }

            if (exHResult == NoSystemResourcesFault)
            {
                return ExceptionRootCause.NoSystemResources;
            }

            if (ex is FileLoadException ||
                (ex is FileNotFoundException && ex.Message.Contains("Could not load file or assembly")) ||
                ex is DllNotFoundException ||
                ex is TypeLoadException)
            {
                return ExceptionRootCause.MissingRuntimeDependency;
            }

            if (exHResult == PipeNotConnectedFault)
            {
                // Verify that the failure is caused by the console.
                try
                {
                    Console.Out.Flush();
                    Console.Error.Flush();
                }
                catch (IOException)
                {
                    return ExceptionRootCause.ConsoleNotConnected;
                }
            }

            if (exHResult == ConnectionReset)
            {
                return ExceptionRootCause.NetworkException;
            }

            var aggregateException = ex as AggregateException;
            if (aggregateException != null)
            {
                // Aggregate exceptions may have multiple inner exceptions. We take the first root cause we find
                // (rather than trying to faithfully represent multiple root causes).
                aggregateException = aggregateException.Flatten();
                foreach (Exception inner in aggregateException.InnerExceptions)
                {
                    ExceptionRootCause innerRootCause = AnalyzeExceptionRootCause(inner);
                    if (innerRootCause != ExceptionRootCause.Unknown)
                    {
                        return innerRootCause;
                    }
                }
            }
            else if (ex.InnerException != null)
            {
                return AnalyzeExceptionRootCause(ex.InnerException);
            }

            return ExceptionRootCause.Unknown;
        }

        [DllImport("kernel32.dll")]
        private static extern void RaiseFailFastException(IntPtr exceptionRecord, IntPtr contextRecord, uint flags);

        /// <summary>
        /// More robust version of <see cref="Environment.FailFast(string, Exception)"/>.
        /// </summary>
        public static Exception FailFast([Localizable(false)] string message, Exception exception)
        {
            if (!OperatingSystemHelper.IsUnixOS)
            {
                // Generate crash dump / notify debugger / attach post-mortem debugger.
                RaiseFailFastException(IntPtr.Zero, IntPtr.Zero, 0);
            }

            // Try harder to exit if a debugger tried to ignore the fail-fast. Is that possible?
            Environment.FailFast(message, exception);
            throw new InvalidOperationException("Unreachable");
        }

        /// <summary>
        /// Invokes application defined unexpected exception handler. See <see cref="UnexpectedException"/>
        /// </summary>
        public static bool HandleUnexpectedException(Exception ex)
        {
            UnexpectedException?.Invoke(ex);

            return false;
        }

        /// <summary>
        /// Gets the error code (Win32 error code e.g. <c>ERROR_ACCESS_DENIED</c> if possible) of the innermost exception
        /// </summary>
        /// <remarks>
        /// <see cref="Win32Exception.NativeErrorCode"/> is most valuable if that is the innermost exception; it should correspond
        /// to an error captured with <see cref="Marshal.GetLastWin32Error"/>, or an NTSTATUS. Note that using <see cref="Exception.HResult"/>
        /// in that case (set to a generic <c>E_FAIL</c>) erases very valuable information.
        /// </remarks>
        public static int GetLogEventErrorCode(this Exception exception)
        {
            Exception innermostException = exception.GetBaseException();
            innermostException = innermostException ?? exception;

            if (innermostException is Win32Exception win32Exception)
            {
                return win32Exception.NativeErrorCode;
            }

            return innermostException.HResult;
        }

        /// <summary>
        /// Get the HResult value from the exception. Handle the Win32Exception cases because they dont't properly set HResult.
        /// </summary>
        public static int GetHResult(Exception exception)
        {
            int exHResult = exception.HResult;
            if (exHResult == E_FAIL)
            {
                if (exception is Win32Exception win32Exception)
                {
                    exHResult = HResultFromWin32(win32Exception.NativeErrorCode);
                }
            }

            return exHResult;
        }

        /// <summary>
        /// Converts a Win32 error code to HResult
        /// </summary>
        public static int HResultFromWin32(int nativeErrorCode)
        {
            if (nativeErrorCode < 0 || nativeErrorCode > 0xFFFF)
            {
                return nativeErrorCode;
            }

            return unchecked((int)0x80070000) | nativeErrorCode;
        }

        /// <summary>
        /// Converts HResult back to a Win32 error code.
        /// </summary>
        public static int Win32ErrorCodeFromHResult(int hResult)
        {
            return (int)unchecked((uint)hResult & (~0x80070000));
        }

        /// <summary>
        /// Returns native error code from an exception.
        /// </summary>
        public static int NativeErrorCode(this Exception e)
        {
            return Win32ErrorCodeFromHResult(e.HResult);
        }

        /// <summary>
        /// Gets full log event message including inner exceptions from the given exception
        /// </summary>
        public static string GetLogEventMessage(this Exception exception)
        {
            using (PooledObjectWrapper<StringBuilder> wrapper = Pools.StringBuilderPool.GetInstance())
            {
                var instance = wrapper.Instance;
                bool first = true;
                for (Exception currentException = exception; currentException != null; currentException = currentException.InnerException)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        instance.AppendLine(": ");
                    }

                    instance.Append(currentException.Message);
                }

                return instance.ToString();
            }
        }
    }
}
