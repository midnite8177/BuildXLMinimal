// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if !FEATURE_SAFE_PROCESS_HANDLE

using System;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace BuildXL.Interop.Windows
{
    /// <nodoc />
    [SuppressUnmanagedCodeSecurity]
    public sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        [DllImport(BuildXL.Interop.Libraries.WindowsKernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        // constructor get's called by pinvoke
        private SafeProcessHandle()
            : base(true)
        {
        }

        /// <nodoc />
        protected override bool ReleaseHandle()
        {
            return CloseHandle(handle);
        }
    }
}
#endif
