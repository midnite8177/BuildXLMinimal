// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace BuildXL.Processes
{
    /// <summary>
    /// Which operation resulted in a reported file access
    /// </summary>
    public enum ReportedFileOperation : byte
    {
        /// <summary>
        /// Unknown operation
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// CreateFile
        /// </summary>
        CreateFile,

        /// <summary>
        /// CreateProcess
        /// </summary>
        CreateProcess,

        /// <summary>
        /// GetFileAttributes
        /// </summary>
        GetFileAttributes,

        /// <summary>
        /// GetFileAttributesEx
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix")]
        GetFileAttributesEx,

        /// <summary>
        /// Process forked
        /// </summary>
        Process,

        /// <summary>
        /// FindFirstFileEx
        /// </summary>
        /// <remarks>
        /// FindFirstFile also indicates this op, since we implement it in terms of FindFirstFleEx.
        /// </remarks>
        [SuppressMessage("Microsoft.Naming", "CA1711:IdentifiersShouldNotHaveIncorrectSuffix")]
        FindFirstFileEx,

        /// <summary>
        /// FindNextFile
        /// </summary>
        FindNextFile,

        /// <summary>
        /// CreateDirectory
        /// </summary>
        CreateDirectory,

        /// <summary>
        /// DeleteFile
        /// </summary>
        DeleteFile,

        /// <summary>
        /// MoveFile (source; read and deleted)
        /// </summary>
        MoveFileSource,

        /// <summary>
        /// MoveFile (destination; written)
        /// </summary>
        MoveFileDestination,

        /// <summary>
        /// SetFileInformationByHandleSource (source; read and deleted)
        /// </summary>
        SetFileInformationByHandleSource,

        /// <summary>
        /// SetFileInformationByHandleDest (destination; written)
        /// </summary>
        SetFileInformationByHandleDest,

        /// <summary>
        /// ZwSetRenameInformationFileSource (source; read and deleted)
        /// </summary>
        ZwSetRenameInformationFileSource,

        /// <summary>
        /// ZwSetRenameInformationFileDest (destination; written)
        /// </summary>
        ZwSetRenameInformationFileDest,

        /// <summary>
        /// ZwSetLinkInformationFileDest
        /// </summary>
        ZwSetLinkInformationFile,

        /// <summary>
        /// ZwSetDispositionInformationFile (delete-on-clode; deleted)
        /// </summary>
        ZwSetDispositionInformationFile,

        /// <summary>
        /// ZwSetModeInformationFile (delete-on-close; deleted)
        /// </summary>
        ZwSetModeInformationFile,

        /// <summary>
        /// ZwSetFileNameInformationFile (source; read and written)
        /// </summary>
        ZwSetFileNameInformationFileSource,

        /// <summary>
        /// ZwSetFileNameInformationFile (destination; written)
        /// </summary>
        ZwSetFileNameInformationFileDest,

        /// <summary>
        /// CopyFile (source; read)
        /// </summary>
        CopyFileSource,

        /// <summary>
        /// CopyFile (destination; written)
        /// </summary>
        CopyFileDestination,

        /// <summary>
        /// CreateHardLink (source; read)
        /// </summary>
        CreateHardLinkSource,

        /// <summary>
        /// CreateHardLink (destination; written)
        /// </summary>
        CreateHardLinkDestination,

        /// <summary>
        /// RemoveDirectory
        /// </summary>
        RemoveDirectory,

        /// <summary>
        /// RemoveDirectory (source; written)
        /// </summary>
        RemoveDirectorySource,

        /// <summary>
        /// NtQueryDirectoryFile
        /// </summary>
        NtQueryDirectoryFile,

        /// <summary>
        /// ZwQueryDirectoryFile
        /// </summary>
        ZwQueryDirectoryFile,

        /// <summary>
        /// NtCreateFile
        /// </summary>
        NtCreateFile,

        /// <summary>
        /// ZwCreateFile
        /// </summary>
        ZwCreateFile,

        /// <summary>
        /// ZwOpenFile
        /// </summary>
        ZwOpenFile,

        /// <summary>
        /// This is a quasi operation. We issue this
        /// report when Detours is changing file open
        /// request with Read/Write access to Read access only.
        /// </summary>
        ChangedReadWriteToReadAccess,

        /// <summary>
        /// This is a quasi operation. The sandbox issues this only when <see cref="FileAccessPolicy.OverrideAllowWriteForExistingFiles"/> is set, representing
        /// that an allow for write check was performed for a given path for the first time (in the scope of a process, another process in the same process 
        /// tree may also report this for the same path)
        /// </summary>
        FirstAllowWriteCheckInProcess,

        /// <summary>
        /// This operation used to indicate to the engine by the Linux sandbox that a process being executed statically links libc
        /// and may have missing file observations.
        /// </summary>
        ProcessRequiresPTrace,

        /// <summary>
        /// Access of reparse point target.
        /// </summary>
        ReparsePointTarget,

        /// <summary>
        /// Access of reparse point target, cached by Detours
        /// </summary>
        ReparsePointTargetCached,

        /// <summary>
        /// Access checks for source of CreateSymbolicLink API.
        /// </summary>
        CreateSymbolicLinkSource,

        /// <summary>
        /// Access check for MoveFileWithgProgress source target.
        /// </summary>
        MoveFileWithProgressSource,

        /// <summary>
        /// Access check for MoveFileWithProgress dest target.
        /// </summary>
        MoveFileWithProgressDest,

        /// <summary>
        /// Multiple operations lumped into one.
        /// </summary>
        MultipleOperations,

        /// <summary>
        /// Process exited
        /// </summary>
        ProcessExit,

        /**
         * The system calls below are specific to the Linux Sandbox.
        */

        /// <summary>
        /// Represents an exec* call from the Linux Sandbox.
        /// </summary>
        ProcessExec,

        /// <summary>
        /// Pseudo operation that is issued by the managed sandbox to indicate that the traced process has completed execution.
        /// </summary>
        ProcessTreeCompletedAck,

        /// <summary>
        /// Generic readlink system call.
        /// </summary>
        Readlink,

        /// <summary>
        /// Generic read file system call.
        /// </summary>
        ReadFile,

        /// <summary>
        /// Generic write file system call.
        /// </summary>
        WriteFile,

        /// <summary>
        /// Source of a hard link operation.
        /// </summary>
        CreateHardlinkSource,

        /// <summary>
        /// Destination of a hard link operation.
        /// </summary>
        CreateHardlinkDest,

        /// <summary>
        /// Open directory system call.
        /// </summary>
        OpenDirectory,

        /// <summary>
        /// Close system call.
        /// </summary>
        Close,

        /// <summary>
        /// Generic probe file system call.
        /// </summary>
        Probe,

        /// <summary>
        /// This operation is used to indicate to the engine by the Linux sandbox that a process is about to breakaway
        /// </summary>
        ProcessBreakaway,
    }
}
