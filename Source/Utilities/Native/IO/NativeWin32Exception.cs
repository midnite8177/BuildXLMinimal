// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using static BuildXL.Utilities.Core.FormattableStringEx;

namespace BuildXL.Native.IO
{
    /// <summary>
    /// A possibly-recoverable exception wrapping a failed native call. The <see cref="Win32Exception.NativeErrorCode" /> captures the
    /// associated recent error code (<see cref="System.Runtime.InteropServices.Marshal.GetLastWin32Error" />). The <see cref="Exception.Message" />
    /// accounts for the native code as well as a human readable portion.
    /// </summary>
    /// <remarks>
    /// This is much like <see cref="Win32Exception"/>, but the message field contains the caller-provided part in addition
    /// to the system-provided message (rather than replacing the system provided message).
    /// </remarks>
    [SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors",
        Justification = "We don't need exceptions to cross AppDomain boundaries.")]
    [Serializable]
    public sealed class NativeWin32Exception : Win32Exception
    {
        /// <summary>
        /// Creates an exception representing a native failure (with a corresponding Win32 error code).
        /// The exception's <see cref="Exception.Message" /> includes the error code, a system-provided message describing it,
        /// and the provided application-specific message prefix (e.g. "Unable to open log file").
        /// </summary>
        public NativeWin32Exception(int nativeErrorCode, [Localizable(false)] string messagePrefix)
            : base(nativeErrorCode, GetFormattedMessageForNativeErrorCode(nativeErrorCode, messagePrefix))
        {
            // Win32Exception does not initialize HResult but many others like IOException do.
            // In order to have a uniform error checking, initialize HResult using something similar to HRESULT_FROM_WIN32
            HResult = BuildXL.Utilities.Core.ExceptionUtilities.HResultFromWin32(nativeErrorCode);
        }

        /// <summary>
        /// Creates an exception representing a native failure (with a corresponding Win32 error code).
        /// The exception's <see cref="Exception.Message" /> includes the error code and a system-provided message describing it.
        /// </summary>
        public NativeWin32Exception(int nativeErrorCode)
            : this(nativeErrorCode, null)
        {
        }

        /// <summary>
        /// Returns a human readable error string for a native error code, like <c>Native: Can't access the log file (0x5: Access is denied)</c>.
        /// The message prefix (e.g. "Can't access the log file") is optional.
        /// </summary>
        public static string GetFormattedMessageForNativeErrorCode(int nativeErrorCode, [Localizable(false)] string messagePrefix = null)
        {
            string systemMessage = new Win32Exception(nativeErrorCode).Message;
            return !string.IsNullOrEmpty(messagePrefix)
                ? I($"Native: {messagePrefix} (0x{nativeErrorCode:X}: {systemMessage})")
                : I($"Native: 0x{nativeErrorCode:X}: {systemMessage}");
        }
    }
}
