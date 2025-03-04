// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using Microsoft.Win32.SafeHandles;
using static BuildXL.Interop.Unix.IO;
using static BuildXL.Utilities.Core.FormattableStringEx;

namespace BuildXL.Native.IO.Unix
{
    /// <inheritdoc />
    public sealed class FileUtilitiesUnix : IFileUtilities
    {
        /// <summary>
        /// The optimal buffer size for copy throughput and keeping system call overhead minimal:
        /// see: https://eklitzke.org/efficient-file-copying-on-linux#:~:text=The%20file%20includes%20a%20cryptic,calls%20need%20to%20be%20made.
        /// </summary>
        private static readonly long s_copyBufferSizeMax = 512 * 1024;

        /// <summary>
        /// A concrete native FileSystem implementation based on Unix APIs
        /// </summary>
        private readonly FileSystemUnix m_fileSystem;

        /// <summary>
        /// Whether the `copy_file_range` system call is supported.
        /// Will be set to false if calling `copy_file_range` fails with <see cref="EntryPointNotFoundException"/>.
        /// </summary>
        private bool m_copyFileRangeSupported = true;

        /// <summary>
        /// Whether the `sendfile` system call is supported.
        /// Will be set to false if calling `sendfile` fails with <see cref="EntryPointNotFoundException"/>.
        /// </summary>
        private bool m_sendFileSupported = true;

        /// <inheritdoc />
        public PosixDeleteMode PosixDeleteMode { get; set; }

        /// <summary>
        /// Creates a concrete FileUtilities instance
        /// </summary>
        public FileUtilitiesUnix()
        {
            m_fileSystem = new Unix.FileSystemUnix();
            PosixDeleteMode = PosixDeleteMode.RunFirst;
        }

        /// <inheritdoc />
        internal IFileSystem FileSystem => m_fileSystem;

        /// <inheritdoc />
        public bool? DoesLogicalDriveHaveSeekPenalty(char driveLetter) => false;

        /// <inheritdoc />
        public void DeleteDirectoryContents(
            string path,
            bool deleteRootDirectory = false,
            Func<string, bool, bool> shouldDelete = null,
            ITempCleaner tempDirectoryCleaner = null,
            bool bestEffort = false,
            CancellationToken? cancellationToken = default)
        {
            DeleteDirectoryContentsInternal(path, deleteRootDirectory, shouldDelete, tempDirectoryCleaner, bestEffort, cancellationToken);
        }

        private int DeleteDirectoryContentsInternal(
            string path,
            bool deleteRootDirectory,
            Func<string, bool, bool> shouldDelete,
            ITempCleaner tempDirectoryCleaner,
            bool bestEffort,
            CancellationToken? cancellationToken)
        {
            int remainingChildCount = 0;

            if (!Directory.Exists(path))
            {
                return remainingChildCount;
            }

            shouldDelete = shouldDelete ?? ((a,b) => true);

            EnumerateDirectoryResult result = m_fileSystem.EnumerateDirectoryEntries(
                path,
                (name, attributes) =>
                {
                    cancellationToken?.ThrowIfCancellationRequested();

                    var isDirectory = FileUtilities.IsDirectoryNoFollow(attributes);
                    string childPath = Path.Combine(path, name);

                    if (isDirectory)
                    {
                        int subDirectoryCount = DeleteDirectoryContentsInternal(
                            childPath,
                            deleteRootDirectory: shouldDelete(childPath, isDirectory),
                            shouldDelete: shouldDelete,
                            tempDirectoryCleaner: tempDirectoryCleaner,
                            bestEffort: bestEffort,
                            cancellationToken: cancellationToken);

                        if (subDirectoryCount > 0)
                        {
                            ++remainingChildCount;
                        }
                    }
                    else
                    {
                        if (shouldDelete(childPath, attributes.HasFlag(FileAttributes.ReparsePoint)))
                        {
                            // This method already has retry logic, so no need to do retry in DeleteFile
                            DeleteFile(childPath, retryOnFailure: !bestEffort, tempDirectoryCleaner: tempDirectoryCleaner);
                        }
                        else
                        {
                            ++remainingChildCount;
                        }
                    }
                }, isEnumerationForDirectoryDeletion: true);

            if (deleteRootDirectory && remainingChildCount == 0)
            {
                bool success = Helpers.RetryOnFailure(
                    finalRound =>
                    {
                        // An exception will be thrown on failure, which will trigger a retry, this deletes the path itself
                        // and any file or dir still in recursively through the 'true' flag
                        Directory.Delete(path, true);

                        // Only reached if there are no exceptions
                        return true;
                    },
                    numberOfAttempts: bestEffort ? 1 : Helpers.DefaultNumberOfAttempts);

                if (!success && Directory.Exists(path))
                {
                    var code = (int)Tracing.LogEventId.RetryOnFailureException;
                    throw new BuildXLException($"Failed to delete directory: {path}.  Search for DX{code:0000} log messages to see why.");
                }
            }

            return remainingChildCount;
        }

        /// <inheritdoc />
        public string FindAllOpenHandlesInDirectory(string directoryPath, HashSet<string> pathsPossiblyPendingDelete = null, Func<string, bool, bool> shouldDelete = null) => throw new NotImplementedException();

        /// <inheritdoc />
        public Possible<string, DeletionFailure> TryDeleteFile(
            string path,
            bool retryOnFailure = true,
            ITempCleaner tempDirectoryCleaner = null)
        {
            try
            {
                DeleteFile(path, retryOnFailure, tempDirectoryCleaner);
                return path;
            }
            catch (BuildXLException ex)
            {
                return new DeletionFailure(path, ex);
            }
        }

        /// <inheritdoc />
        public bool Exists(string path)
        {
            var maybeExistence = m_fileSystem.TryProbePathExistence(path, followSymlink: false);
            return maybeExistence.Succeeded && maybeExistence.Result != PathExistence.Nonexistent;
        }

        /// <inheritdoc />
        public void DeleteFile(
            string path,
            bool retryOnFailure = true,
            ITempCleaner tempDirectoryCleaner = null)
        {
            Contract.Requires(!string.IsNullOrEmpty(path));
            bool successfullyDeletedFile = false;

            if (!Exists(path))
            {
                // Skip deletion all together if nothing exists at the specified path
                return;
            }

            Action<string> delete =
                (string pathToDelete) =>
                {
                    var isDirectory = FileUtilities.DirectoryExistsNoFollow(pathToDelete);
                    if (isDirectory)
                    {
                        DeleteDirectoryContents(pathToDelete, deleteRootDirectory: true);
                    }
                    else
                    {
                        File.Delete(pathToDelete);
                    }
                };

            if (retryOnFailure)
            {
                successfullyDeletedFile = Helpers.RetryOnFailure(
                    attempt =>
                    {
                        delete(path);
                        return true;
                    });
            }
            else
            {
                try
                {
                    delete(path);
                    successfullyDeletedFile = true;
                }
#pragma warning disable ERP022 // Unobserved exception in generic exception handler
                catch
                {
                }
#pragma warning restore ERP022 // Unobserved exception in generic exception handler
            }

            if (!successfullyDeletedFile)
            {
                throw new BuildXLException("Deleting file '" + path + "' failed!");
            }
        }

        /// <inheritdoc />
        public bool TryMoveDelete(string path, string deletionTempDirectory)
        {
            try
            {
                DeleteFile(path);
                return true;
            }
            catch (Exception ex)
            {
                throw new BuildXLException("Deleting file '" + path + "' failed, reason: " + ex.Message ?? ex.InnerException.Message);
            }
        }

        /// <inheritdoc />
        public void SetFileTimestamps(string path, FileTimestamps timestamps, bool followSymlink)
        {
            Contract.Requires(timestamps.CreationTime >= UnixEpoch);
            Contract.Requires(timestamps.AccessTime >= UnixEpoch);
            Contract.Requires(timestamps.LastWriteTime >= UnixEpoch);
            Contract.Requires(timestamps.LastChangeTime >= UnixEpoch);

            var statBuffer = new StatBuffer();

            Timespec creationTime = Timespec.CreateFromUtcDateTime(timestamps.CreationTime);
            Timespec lastAccessTime = Timespec.CreateFromUtcDateTime(timestamps.AccessTime);
            Timespec lastModificationTime = Timespec.CreateFromUtcDateTime(timestamps.LastWriteTime);
            Timespec lastStatusChangeTime = Timespec.CreateFromUtcDateTime(timestamps.LastChangeTime);

            statBuffer.TimeCreation = creationTime.Tv_sec;
            statBuffer.TimeNSecCreation = creationTime.Tv_nsec;

            statBuffer.TimeLastAccess = lastAccessTime.Tv_sec;
            statBuffer.TimeNSecLastAccess = lastAccessTime.Tv_nsec;

            statBuffer.TimeLastModification = lastModificationTime.Tv_sec;
            statBuffer.TimeNSecLastModification = lastModificationTime.Tv_nsec;

            statBuffer.TimeLastStatusChange = lastStatusChangeTime.Tv_sec;
            statBuffer.TimeNSecLastStatusChange = lastStatusChangeTime.Tv_nsec;

            int result = SetTimeStampsForFilePath(path, followSymlink, statBuffer);

            if (result != 0)
            {
                throw new BuildXLException("Failed to open a file to set its timestamps - error: " + Marshal.GetLastWin32Error());
            }
        }

        /// <inheritdoc />
        public FileTimestamps GetFileTimestamps(string path, bool followSymlink)
        {
            var statBuffer = new StatBuffer();

            if (StatFile(path, followSymlink, ref statBuffer) != 0)
            {
                throw new BuildXLException(I($"Failed to stat file '{path}' to get its timestamps - error: {Marshal.GetLastWin32Error()}"));
            }

            var creationTime = new Timespec() { Tv_sec = statBuffer.TimeCreation, Tv_nsec = statBuffer.TimeNSecCreation };
            var lastChangeTimeTime = new Timespec() { Tv_sec = statBuffer.TimeLastStatusChange, Tv_nsec = statBuffer.TimeNSecLastStatusChange };
            var lastWriteTime = new Timespec() { Tv_sec = statBuffer.TimeLastModification, Tv_nsec = statBuffer.TimeNSecLastModification };
            var accessTime = new Timespec() { Tv_sec = statBuffer.TimeLastAccess, Tv_nsec = statBuffer.TimeNSecLastAccess };

            return new FileTimestamps(
                creationTime: creationTime.ToUtcTime(),
                lastChangeTime: lastChangeTimeTime.ToUtcTime(),
                lastWriteTime: lastWriteTime.ToUtcTime(),
                accessTime: accessTime.ToUtcTime());
        }

        /// <summary>
        /// Gets device and inode number.
        /// </summary>
        public int GetDeviceAndInodeNumbers(string path, bool followSymlink, out ulong device, out ulong inode)
        {
            var statBuffer = new StatBuffer();

            int result = StatFile(path, followSymlink, ref statBuffer);

            device = unchecked((ulong)statBuffer.DeviceID);
            inode = unchecked((ulong)statBuffer.InodeNumber);

            return result;
        }

        /// <inheritdoc />
        public Task WriteAllTextAsync(
            string filePath,
            string text,
            Encoding encoding)
        {
            Contract.Requires(!string.IsNullOrEmpty(filePath));
            Contract.Requires(text != null);
            Contract.Requires(encoding != null);

            byte[] bytes = encoding.GetBytes(text);
            return WriteAllBytesAsync(filePath, bytes);
        }

        /// <inheritdoc />
        public Task<bool> WriteAllBytesAsync(
            string filePath,
            byte[] bytes,
            Func<SafeFileHandle, bool> predicate = null,
            Action<SafeFileHandle> onCompletion = null)
        {
            Contract.Requires(!string.IsNullOrEmpty(filePath));
            Contract.Requires(bytes != null);

            return ExceptionUtilities.HandleRecoverableIOExceptionAsync(
                async () =>
                {
                    if (predicate != null)
                    {
                        SafeFileHandle destinationHandle;
                        OpenFileResult predicateQueryOpenResult = m_fileSystem.TryCreateOrOpenFile(
                            filePath,
                            FileDesiredAccess.GenericRead,
                            FileShare.Read | FileShare.Delete,
                            FileMode.OpenOrCreate,
                            FileFlagsAndAttributes.None,
                            out destinationHandle);
                        using (destinationHandle)
                        {
                            if (!predicateQueryOpenResult.Succeeded)
                            {
                                throw new BuildXLException(
                                    I($"Failed to open file '{filePath}' to check its version"),
                                    predicateQueryOpenResult.CreateExceptionForError());
                            }

                            if (!predicate(predicateQueryOpenResult.OpenedOrTruncatedExistingFile ? destinationHandle : null))
                            {
                                return false;
                            }
                        }
                    }

                    using (FileStream stream = CreateReplacementFile(filePath, FileShare.Delete, openAsync: true))
                    {
                        await stream.WriteAsync(bytes, 0, bytes.Length);
                    }

                    if (onCompletion != null)
                    {
                        using (var file = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Delete))
                        {
                            onCompletion(file.SafeFileHandle);
                        }
                    }

                    return true;
                },
                ex => { throw new BuildXLException("File write failed", ex); });
        }

        /// <inheritdoc />
        public Task<bool> CopyFileAsync(
            string source,
            string destination,
            Func<SafeFileHandle, SafeFileHandle, bool> predicate = null,
            Action<SafeFileHandle, SafeFileHandle> onCompletion = null)
        {
            Contract.Requires(!string.IsNullOrEmpty(source));
            Contract.Requires(!string.IsNullOrEmpty(destination));

            return ExceptionUtilities.HandleRecoverableIOExceptionAsync(
                async () =>
                {
                    using (FileStream sourceStream = CreateAsyncFileStream(
                        source,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete))
                    {
                        if (predicate != null)
                        {
                            SafeFileHandle destinationHandle;
                            OpenFileResult predicateQueryOpenResult = m_fileSystem.TryCreateOrOpenFile(
                                destination,
                                FileDesiredAccess.GenericRead,
                                FileShare.Read | FileShare.Delete,
                                FileMode.OpenOrCreate,
                                FileFlagsAndAttributes.None,
                                out destinationHandle);
                            using (destinationHandle)
                            {
                                if (!predicateQueryOpenResult.Succeeded)
                                {
                                    throw new BuildXLException(
                                        I($"Failed to open a copy destination '{destination}' to check its version"),
                                        predicateQueryOpenResult.CreateExceptionForError());
                                }

                                if (!predicate(sourceStream.SafeFileHandle, predicateQueryOpenResult.OpenedOrTruncatedExistingFile ? destinationHandle : null))
                                {
                                    return false;
                                }
                            }
                        }

                        using (var destinationStream = CreateReplacementFile(destination, FileShare.Delete, openAsync: true))
                        {
                            await sourceStream.CopyToAsync(destinationStream);
                            var mode = GetFilePermissionsForFilePath(source, followSymlink: false);
                            var result = SetFilePermissionsForFilePath(destination, checked((FilePermissions)mode), followSymlink: false);
                            if (result < 0)
                            {
                                throw new BuildXLException($"Failed to set permissions for file copy at '{destination}' - error: {Marshal.GetLastWin32Error()}");
                            }
                        }

                        if (onCompletion != null)
                        {
                            // CodeQL [SM00414] Path is controlled in FrontEnd (marked by code ql due to a code path from FrontEndHostController.Nuget.cs).
                            using (var dest = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Delete))
                            {
                                onCompletion(sourceStream.SafeFileHandle, dest.SafeFileHandle);
                            }
                        }
                    }

                    return true;
                },
                ex => { throw new BuildXLException(I($"File copy from '{source}' to '{destination}' failed"), ex); });
        }

        /// <inheritdoc />
        public Task MoveFileAsync(
            string source,
            string destination,
            bool replaceExisting = false)
        {
            Contract.Requires(!string.IsNullOrEmpty(source));
            Contract.Requires(!string.IsNullOrEmpty(destination));

            return Task.Run(
                () => ExceptionUtilities.HandleRecoverableIOException(
                    () =>
                    {
                        if (replaceExisting)
                        {
                            DeleteFile(destination);
                        }

                        m_fileSystem.CreateDirectory(Path.GetDirectoryName(destination));
                        File.Move(source, destination);
                    },
                    ex => { throw new BuildXLException(I($"File move from '{source}' to '{destination}' failed"), ex); }));
        }

        /// <inheritdoc />
        public Possible<Unit> CloneFile(string source, string destination, bool followSymlink)
        {
            var flags = followSymlink ? CloneFileFlags.CLONE_NONE : CloneFileFlags.CLONE_NOFOLLOW;
            int result = Interop.Unix.IO.CloneFile(source, destination, flags);
            if (result != 0)
            {
                return new NativeFailure(Marshal.GetLastWin32Error(), I($"Failed to clone '{source}' to '{destination}'"));
            }

            return Unit.Void;
        }

        /// <inheritdoc />
        public Possible<Unit> InKernelFileCopy(string source, string destination, bool followSymlink)
        {
            SafeFileHandle sourceHandle;
            OpenFileResult openResult = m_fileSystem.TryCreateOrOpenFile(
                source,
                FileDesiredAccess.GenericRead,
                FileShare.Read | FileShare.Delete,
                FileMode.Open,
                followSymlink ? FileFlagsAndAttributes.FileFlagOpenReparsePoint : FileFlagsAndAttributes.None,
                out sourceHandle);

            if (!openResult.Succeeded)
            {
                return new NativeFailure(Marshal.GetLastWin32Error(), I($"Failed to open source file '{source}' in {nameof(InKernelFileCopy)}"));
            }

            using (sourceHandle)
            {
                // Ignore return of PosixFadvise(), the file handle was opened successfully above
                // and the advice hint is a less important optimization.
                Interop.Unix.IO.PosixFadvise(sourceHandle, 0, 0, AdviceHint.POSIX_FADV_SEQUENTIAL);

                SafeFileHandle destinationHandle;
                openResult = m_fileSystem.TryCreateOrOpenFile(
                    destination,
                    FileDesiredAccess.GenericRead | FileDesiredAccess.GenericWrite,
                    FileShare.Read | FileShare.Write | FileShare.Delete,
                    FileMode.Create,
                    FileFlagsAndAttributes.None,
                    out destinationHandle);

                if (!openResult.Succeeded)
                {
                    return new NativeFailure(Marshal.GetLastWin32Error(), I($"Failed to open destination file '{destination}' in {nameof(InKernelFileCopy)}"));
                }

                using (destinationHandle)
                {
                    var statBuffer = new StatBuffer();
                    if (StatFileDescriptor(sourceHandle, ref statBuffer) != 0)
                    {
                        destinationHandle.Close();
                        DeleteFile(destination, false);
                        return new NativeFailure(Marshal.GetLastWin32Error(), I($"Failed to stat source file '{source}' for size query in {nameof(InKernelFileCopy)}"));
                    }

                    var length = statBuffer.Size;
                    long bytesCopied = 0;
                    int lastError = 0;

                    do
                    {
                        bytesCopied = CopyBytes(sourceHandle, destinationHandle);
                        if (bytesCopied == -1)
                        {
                            destinationHandle.Close();
                            DeleteFile(destination, false);
                            return new NativeFailure(Marshal.GetLastWin32Error(), I($"{nameof(InKernelFileCopy)} failed  copying '{source}' to '{destination}' with error code: {lastError}"));
                        }

                        length -= bytesCopied;
                    } while (length > 0 && bytesCopied > 0);

                    return Unit.Void;
                }
            }
        }

        private long CopyBytes(SafeFileHandle sourceHandle, SafeFileHandle destinationHandle)
        {
            if (m_copyFileRangeSupported)
            {
                try
                {
                    return Interop.Unix.IO.CopyFileRange(sourceHandle, IntPtr.Zero, destinationHandle, IntPtr.Zero, s_copyBufferSizeMax);
                }
                catch (System.EntryPointNotFoundException)
                {
                    m_copyFileRangeSupported = false;
                }
            }

            if (m_sendFileSupported)
            {
                try
                {
                    return Interop.Unix.IO.SendFile(sourceHandle, destinationHandle, IntPtr.Zero, s_copyBufferSizeMax);
                }
                catch (System.EntryPointNotFoundException)
                {
                    m_sendFileSupported = false;
                }
            }

            return -1;
        }

        /// <inheritdoc />
        public TResult UsingFileHandleAndFileLength<TResult>(
            string path,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileMode creationDisposition,
            FileFlagsAndAttributes flagsAndAttributes,
            Func<SafeFileHandle, long, TResult> handleStream)
        {
            SafeFileHandle handle;
            var openResult = m_fileSystem.TryCreateOrOpenFile(
                       path,
                       desiredAccess,
                       shareMode,
                       creationDisposition,
                       flagsAndAttributes,
                       out handle);

            if (!openResult.Succeeded)
            {
                openResult
                    .CreateFailureForError()
                    .Annotate($"{nameof(m_fileSystem.TryCreateOrOpenFile)} failed in {nameof(UsingFileHandleAndFileLength)}")
                    .Throw();
            }

            using (handle)
            {
                Contract.Assert(handle != null && !handle.IsInvalid);
                var maybeTarget = m_fileSystem.TryGetReparsePointTarget(path);
                var length = maybeTarget.Succeeded ? maybeTarget.Result.Length : new FileInfo(path).Length;
                return handleStream(handle, length);
            }
        }

        /// <inheritdoc />
        public FileStream CreateReplacementFile(
            string path,
            FileShare fileShare,
            bool openAsync = true,
            bool allowExcludeFileShareDelete = false)
        {
            Contract.Requires(allowExcludeFileShareDelete || ((fileShare & FileShare.Delete) != 0));
            Contract.Requires(path != null);

            // doing this create/delete/move dance to ensure that the replacement file at location 'path' gets
            // a new inode and thus have a different identity from the old one (on EXT4 filesystems, a simple
            // "rm file && touch file" is very likely to result in 'file' getting the same inode as it had before)
            var tempFile = FileUtilities.GetTempFileName();
            DeleteFile(path, retryOnFailure: true);
            MoveFileAsync(tempFile, path).GetAwaiter().GetResult();

            return openAsync
                ? CreateAsyncFileStream(path, FileMode.Create, FileAccess.ReadWrite, fileShare, allowExcludeFileShareDelete: allowExcludeFileShareDelete)
                : CreateFileStream(path, FileMode.Create, FileAccess.ReadWrite, fileShare, allowExcludeFileShareDelete: allowExcludeFileShareDelete);
        }

        /// <inheritdoc />
        public Possible<string> GetFileName(string path) => Path.GetFileName(path);

        /// <summary>Doesn't know any known folders, i.e., always returns <c>null</c>.</summary>
        public string GetKnownFolderPath(Guid knownFolder) => null;

        /// <inheritdoc />
        public string GetUserSettingsFolder(string appName)
        {
            Contract.Requires(!string.IsNullOrEmpty(appName));

            var homeFolder = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(homeFolder))
            {
                throw new BuildXLException("Missing environment variable 'HOME'.");
            }

            var settingsFolder = Path.Combine(homeFolder, "." + ToCamelCase(appName));
            m_fileSystem.CreateDirectory(settingsFolder);
            return settingsFolder;
        }

        private static string ToCamelCase(string name)
        {
            return char.IsLower(name[0])
                ? name
                : char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <inheritdoc />
        public bool TryFindOpenHandlesToFile(string filePath, out string diagnosticInfo, bool printCurrentFilePath) => throw new NotImplementedException();

        /// <inheritdoc />
        public uint GetHardLinkCount(string path)
        {
            var statBuffer = new StatBuffer();
            if (StatFile(path, true, ref statBuffer) != 0)
            {
                throw new BuildXLException(I($"Failed to stat file '{path}' to get its hardlink count - error: {Marshal.GetLastWin32Error()}"));
            }

            // TODO: Change hardlink count return type to ulong.
            return unchecked((uint)statBuffer.HardLinks);
        }

        /// <inheritdoc />
        public FileStream CreateAsyncFileStream(
            string path,
            FileMode fileMode,
            FileAccess fileAccess,
            FileShare fileShare,
            FileOptions options = FileOptions.None,
            bool force = false,
            bool allowExcludeFileShareDelete = false)
        {
            Contract.Requires(!string.IsNullOrEmpty(path));
            Contract.Requires(allowExcludeFileShareDelete || ((fileShare & FileShare.Delete) != 0));

            return CreateFileStream(
                path,
                fileMode,
                fileAccess,
                fileShare,
                options | FileOptions.Asynchronous,
                force: force,
                allowExcludeFileShareDelete: allowExcludeFileShareDelete);
        }

        /// <inheritdoc />
        public FileStream CreateFileStream(
            string path,
            FileMode fileMode,
            FileAccess fileAccess,
            FileShare fileShare,
            FileOptions options = FileOptions.None,
            bool force = false,
            bool allowExcludeFileShareDelete = false)
        {
            Contract.Requires(allowExcludeFileShareDelete || ((fileShare & FileShare.Delete) != 0));

            return m_fileSystem.CreateFileStream(path, fileMode, fileAccess, fileShare, options, force);
        }

        /// <inheritdoc />
        public bool HasWritableAccessControl(string path)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(path));

            int result = m_fileSystem.GetFilePermission(path, false, true);

            FilePermissions permissions = checked((FilePermissions)result);
            return permissions.HasFlag(FilePermissions.S_IWUSR)
                || permissions.HasFlag(FilePermissions.S_IWGRP)
                || permissions.HasFlag(FilePermissions.S_IWOTH);
        }

        /// <inheritdoc />
        public bool HasWritableAttributeAccessControl(string path)
        {
            // There is no write attribute specific permissions for unix
            return HasWritableAccessControl(path);
        }

        /// <inheritdoc />
        public void SetFileAccessControl(string path, FileSystemRights fileSystemRights, bool allow, bool disableInheritance)
        {
            FilePermissions permissions = 0;

            if (fileSystemRights.HasFlag(FileSystemRights.AppendData) ||
                fileSystemRights.HasFlag(FileSystemRights.WriteData))
            {
                permissions |= FilePermissions.S_IWGRP | FilePermissions.S_IWOTH;
            }

            if (fileSystemRights.HasFlag(FileSystemRights.Read) ||
                fileSystemRights.HasFlag(FileSystemRights.ReadData))
            {
                permissions |= FilePermissions.S_IRGRP | FilePermissions.S_IROTH;
            }

            if (fileSystemRights.HasFlag(FileSystemRights.ExecuteFile))
            {
                permissions |= FilePermissions.S_IXGRP | FilePermissions.S_IXOTH;
            }

            int result = m_fileSystem.GetFilePermission(path, false, true);

            FilePermissions currentPermissions = checked((FilePermissions)result);

            if (allow)
            {
                currentPermissions |= permissions;
            }
            else
            {
                currentPermissions &= ~permissions;
            }

            // don't follow symlinks because if we do that we may end up modifying source files via symlink outputs
            result = SetFilePermissionsForFilePath(path, currentPermissions, followSymlink: false);

            if (result < 0)
            {
                throw new BuildXLException(I($"Failed to set permissions for file '{path}' - error: {Marshal.GetLastWin32Error()}"));
            }
        }

        /// <inheritdoc />
        public bool TryTakeOwnershipAndSetWriteable(string path)
        {
            // Ownership not applicable for Unix. Setting writable is not implemented for Unix
            return false;
        }


        /// <inheritdoc />
        public void DisableAuditRuleInheritance(string path)
        {
            // Do nothing in Unix.
        }

        /// <inheritdoc />
        public bool IsAclInheritanceDisabled(string path) => true;
    }
}
