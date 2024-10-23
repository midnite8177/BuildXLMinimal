// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BuildXL.Native.IO
{
    /// <summary>
    /// Normalized status indication (derived from a native error code and the creation disposition).
    /// </summary>
    /// <remarks>
    /// This is useful for two reasons: it is an enum for which we can know all cases are handled, and successful opens
    /// are always <see cref="OpenFileStatus.Success"/> (the distinction between opening / creating files is moved to
    /// <see cref="OpenFileResult.OpenedOrTruncatedExistingFile"/>)
    /// </remarks>
    public enum OpenFileStatus : byte
    {
        /// <summary>
        /// The file was opened (a valid handle was obtained).
        /// </summary>
        /// <remarks>
        /// The <see cref="OpenFileResult.NativeErrorCode"/> may be something other than <c>ERROR_SUCCESS</c>,
        /// since some open modes indicate if a file existed already or was created new via a special error code.
        /// </remarks>
        Success,

        /// <summary>
        /// The file was not found, and no handle was obtained.
        /// </summary>
        FileNotFound,

        /// <summary>
        /// Some directory component in the path was not found, and no handle was obtained.
        /// </summary>
        PathNotFound,

        /// <summary>
        /// The file was opened already with an incompatible share mode, and no handle was obtained.
        /// </summary>
        SharingViolation,

        /// <summary>
        /// The file cannot be opened with the requested access level, and no handle was obtained.
        /// </summary>
        AccessDenied,

        /// <summary>
        /// The file already exists (and the open mode specifies failure for existent files); no handle was obtained.
        /// </summary>
        FileAlreadyExists,

        /// <summary>
        /// The device the file is on is not ready. Should be treated as a nonexistent file.
        /// </summary>
        ErrorNotReady,

        /// <summary>
        /// The volume the file is on is locked. Should be treated as a nonexistent file.
        /// </summary>
        FveLockedVolume,

        /// <summary>
        /// The operaiton timed out. This generally occurs because of remote file materialization taking too long in the
        /// filter driver stack. Waiting and retrying may help.
        /// </summary>
        Timeout,

        /// <summary>
        /// The file cannot be accessed by the system. Should be treated as a nonexistent file.
        /// </summary>
        CannotAccessFile,

        /// <summary>
        /// The name of the file cannot be resolved by the system.
        /// </summary>
        CannotResolveFilename,

        /// <summary>
        /// The parameter is incorrect.
        /// </summary>
        InvalidParameter,

        /// <summary>
        /// The specified path is invalid. (from 'winerror.h')
        /// </summary>
        BadPathname,

        /// <summary>
        /// The filename, directory name, or volume label syntax is incorrect.
        /// </summary>
        InvalidName,

        /// <summary>
        /// The directory name is invalid.
        /// </summary>
        ErrorDirectory,

        /// <summary>
        /// Cannot access the file because another process has locked a portion of the file.
        /// </summary>
        LockViolation,

        /// <summary>
        /// See <see cref="OpenFileResult.NativeErrorCode"/>
        /// </summary>
        UnknownError,
    }

    /// <summary>
    /// Extensions to OpenFileStatus
    /// </summary>
#pragma warning disable SA1649 // File name should match first type name
    public static class OpenFileStatusExtensions
#pragma warning restore SA1649
    {
        /// <summary>
        /// Whether the status is one that should be treated as a nonexistent file
        /// </summary>
        /// <remarks>
        /// CODESYNC: <see cref="Windows.FileSystemWin.IsHresultNonexistent(int)"/>
        /// </remarks>
        public static bool IsNonexistent(this OpenFileStatus status)
        {
            return status == OpenFileStatus.PathNotFound
                || status == OpenFileStatus.FileNotFound
                || status == OpenFileStatus.ErrorDirectory
                || status == OpenFileStatus.ErrorNotReady
                || status == OpenFileStatus.FveLockedVolume
                || status == OpenFileStatus.BadPathname
                || status == OpenFileStatus.InvalidName
                || status == OpenFileStatus.CannotAccessFile
                || status == OpenFileStatus.CannotResolveFilename
                || status == OpenFileStatus.InvalidParameter;
        }

        /// <summary>
        /// Whether the status is one that implies other process blocking the handle.
        /// </summary>
        public static bool ImpliesOtherProcessBlockingHandle(this OpenFileStatus status)
        {
            return status == OpenFileStatus.SharingViolation 
                || status == OpenFileStatus.AccessDenied
                || status == OpenFileStatus.LockViolation;
        }
    }
}
