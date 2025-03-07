// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#if NETCOREAPP
using System.IO.Enumeration;
#endif
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using BuildXL.Interop.Unix;
using BuildXL.Native.IO.Windows;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using Microsoft.Win32.SafeHandles;
using static BuildXL.Interop.Unix.IO;
using static BuildXL.Utilities.Core.FormattableStringEx;

namespace BuildXL.Native.IO.Unix
{
    /// <summary>
    /// FileSystem related native implementations for Unix based systems
    /// </summary>
    internal sealed class FileSystemUnix : IFileSystem
    {
        /// <summary>
        /// The file name for met information added by macOS Finder on folder inspection.
        /// </summary>
        public const string DsStoreMetaFileName = ".DS_Store";

        internal const int DefaultBufferSize = 4096;

        private const ulong UnusedFileIdPart = 0;

        private static readonly Dictionary<FileFlagsAndAttributes, FileOptions> s_fileOptionsFlags = new Dictionary<FileFlagsAndAttributes, FileOptions>()
        {
            { FileFlagsAndAttributes.FileAttributeEncrypted, FileOptions.Encrypted },
            { FileFlagsAndAttributes.FileFlagDeleteOnClose, FileOptions.DeleteOnClose },
            { FileFlagsAndAttributes.FileFlagSequentialScan, FileOptions.SequentialScan },
            { FileFlagsAndAttributes.FileFlagRandomAccess, FileOptions.RandomAccess },
            { FileFlagsAndAttributes.FileFlagOverlapped, FileOptions.Asynchronous },
            { FileFlagsAndAttributes.FileFlagWriteThrough, FileOptions.WriteThrough }
        };

        private Lazy<bool> m_supportPreciseFileVersion = default;

        private readonly ConcurrentDictionary<string, Regex> m_patternRegexes;

        /// <summary>
        /// Creates an instance of <see cref="FileSystemUnix"/>.
        /// </summary>
        public FileSystemUnix()
        {
            m_supportPreciseFileVersion = new Lazy<bool>(() => SupportPreciseFileVersion());
            var matchEverythingRegex = TranslatePattern("*");
            m_patternRegexes = new ConcurrentDictionary<string, Regex>
            {
                [ "*" ]   = matchEverythingRegex
            };
        }

        /// <inheritdoc />
        public OpenFileResult TryOpenDirectory(
            string directoryPath,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileFlagsAndAttributes flagsAndAttributes,
            out SafeFileHandle handle)
        {
            Contract.Requires(!string.IsNullOrEmpty(directoryPath));

            return TryOpenDirectory(directoryPath, desiredAccess, shareMode, FileMode.Open, flagsAndAttributes, out handle);
        }

        private OpenFileResult TryOpenDirectory(
            string directoryPath,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileMode fileMode,
            FileFlagsAndAttributes flagsAndAttributes,
            out SafeFileHandle handle)
        {
            Contract.Requires(!string.IsNullOrEmpty(directoryPath));

            return TryOpen(
                directoryPath,
                desiredAccess,
                shareMode,
                fileMode,
                flagsAndAttributes.HasFlag(FileFlagsAndAttributes.FileFlagOpenReparsePoint) && IsSymlink(directoryPath),
                out handle);
        }

        /// <inheritdoc />
        public OpenFileResult TryOpenDirectory(string directoryPath, FileShare shareMode, out SafeFileHandle handle)
        {
            Contract.Requires(!string.IsNullOrEmpty(directoryPath));

            return TryOpenDirectory(directoryPath, FileDesiredAccess.None, shareMode, FileFlagsAndAttributes.None, out handle);
        }

        /// <inheritdoc />
        public void CreateDirectory(string directoryPath)
        {
            try
            {
                Directory.CreateDirectory(directoryPath);
            }
            catch (Exception ex)
            {
                throw new BuildXLException("Create directory failed", ex);
            }
        }

        /// <inheritdoc />
        public void RemoveDirectory(string path)
        {
            try
            {
                Directory.Delete(path, false);
            }
            catch (Exception ex)
            {
                throw ThrowForNativeFailure(
                    (ex.InnerException ?? ex).HResult,
                    nameof(Directory.Delete),
                    managedApiName: nameof(RemoveDirectory),
                    message: (ex.InnerException ?? ex).ToString());
            }
        }

        /// <inheritdoc />
        public bool TryRemoveDirectory(string path, out int hr)
        {
            try
            {
                RemoveDirectory(path);
                hr = Marshal.GetLastWin32Error();
            }
            catch (NativeWin32Exception ex)
            {
                hr = ex.NativeErrorCode;
                return false;
            }

            return true;
        }

        /// <inheritdoc />
        public EnumerateDirectoryResult EnumerateDirectoryEntries(
            string directoryPath,
            bool recursive,
            Action<string, string, FileAttributes> handleEntry,
            bool isEnumerationForDirectoryDeletion = false) => EnumerateDirectoryEntries(directoryPath, recursive, "*", handleEntry, isEnumerationForDirectoryDeletion, followSymlinksToDirectories: false);

        /// <inheritdoc />
        public EnumerateDirectoryResult EnumerateDirectoryEntries(
            string directoryPath,
            bool recursive,
            string pattern,
            Action<string, string, FileAttributes> handleEntry,
            bool isEnumerationForDirectoryDeletion,
            bool followSymlinksToDirectories)
        {
            return EnumerateDirectoryEntriesCore(
                directoryPath,
                recursive,
                pattern,
                (filePath, fileName, attributes, size) => handleEntry(filePath, fileName, attributes),
                isEnumerationForDirectoryDeletion: isEnumerationForDirectoryDeletion);
        }

        private EnumerateDirectoryResult EnumerateDirectoryEntriesCore(
            string directoryPath,
            bool recursive,
            string pattern,
            Action<string /*filePath*/, string /*fileName*/, FileAttributes /*attributes*/, long /*fileSize*/> handleEntry,
            bool isEnumerationForDirectoryDeletion)
        {
#if NETCOREAPP
            try
            {
                GetFileFullPathsWithExtension(directoryPath).Any();

                IEnumerable<string> GetFileFullPathsWithExtension(string directory)
                {
                    return new FileSystemEnumerable<string>(directory, (ref FileSystemEntry entry) => entry.ToFullPath(), new EnumerationOptions() {
                        RecurseSubdirectories = recursive,
                        MatchType = MatchType.Win32,
                        AttributesToSkip = 0,
                        IgnoreInaccessible = false,
                        MatchCasing = MatchCasing.CaseInsensitive })
                    {
                        ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                        {
                            return FileUtilities.IsDirectoryNoFollow(entry.Attributes);
                        },
                        ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                        {
                            var fileName = entry.FileName.ToString();
                            if (!isEnumerationForDirectoryDeletion && fileName.Equals(DsStoreMetaFileName))
                            {
                                return false;
                            }

                            if (GetRegex(pattern).Match(fileName).Success)
                            {
                                handleEntry(entry.Directory.ToString(), fileName, entry.Attributes, entry.Length);
                            }

                            return false;
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                return EnumerateDirectoryResult.CreateFromException(directoryPath, ex);
            }

            return new EnumerateDirectoryResult(directoryPath, EnumerateDirectoryStatus.Success, (int)NativeIOConstants.ErrorNoMoreFiles);
#else
            throw new NotImplementedException();
#endif
        }

        /// <inheritdoc />
        public EnumerateDirectoryResult EnumerateFiles(
            string directoryPath,
            bool recursive,
            string pattern,
            Action<string /*filePath*/, string /*fileName*/, FileAttributes /*attributes*/, long /*fileSize*/> handleFileEntry)
        {
            return EnumerateDirectoryEntriesCore(
                directoryPath,
                recursive,
                pattern,
                (filePath, fileName, attributes, size) =>
                {
                    if ((attributes & FileAttributes.Directory) == 0)
                    {
                        handleFileEntry(filePath, fileName, attributes, size);
                    }
                },
                isEnumerationForDirectoryDeletion: false);
        }

        /// <inheritdoc />
        public EnumerateDirectoryResult EnumerateDirectoryEntries(string directoryPath, Action<string, FileAttributes> handleEntry, bool isEnumerationForDirectoryDeletion = false) =>
            EnumerateDirectoryEntries(directoryPath, false, (currentDirectory, fileName, fileAttributes) => handleEntry(fileName, fileAttributes), isEnumerationForDirectoryDeletion);

        private static Regex TranslatePattern(string pattern)
        {
            string regexStr = Regex
                .Escape(pattern)
                .Replace("\\?", ".")
                .Replace("\\*", ".*");
            return new Regex($"^{regexStr}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        private Regex GetRegex(string pattern)
        {
            return m_patternRegexes.GetOrAdd(pattern, TranslatePattern);
        }

        /// <inheritdoc />
        public EnumerateDirectoryResult EnumerateDirectoryEntries(
            string directoryPath,
            bool enumerateDirectory,
            string pattern,
            uint directoriesToSkipRecursively,
            bool recursive,
            IDirectoryEntriesAccumulator accumulators,
            bool isEnumerationForDirectoryDeletion)
        {
            try
            {
                var directoryEntries = Directory.GetFileSystemEntries(directoryPath);
                Array.Sort(directoryEntries, StringComparer.InvariantCulture);

                var enumerator = directoryEntries.GetEnumerator();
                var accumulator = accumulators.Current;

                var patternRegex = GetRegex(pattern);

                while (enumerator.MoveNext())
                {
                    var entry = enumerator.Current as string;
                    var filename = entry.Split(Path.DirectorySeparatorChar).Last();

                    if (!isEnumerationForDirectoryDeletion && filename.Equals(DsStoreMetaFileName))
                    {
                        continue;
                    }

                    FileAttributes attributes = File.GetAttributes(entry);
                    var isDirectory = (attributes & FileAttributes.Directory) == FileAttributes.Directory;

                    if (patternRegex.Match(filename).Success)
                    {
                        if (!(enumerateDirectory ^ isDirectory) && directoriesToSkipRecursively == 0)
                        {
                            accumulator.AddFile(filename);
                        }
                    }

                    accumulator.AddTrackFile(filename, attributes);

                    if ((recursive || directoriesToSkipRecursively > 0) && isDirectory)
                    {
                        accumulators.AddNew(accumulator, filename);

                        var recurs = EnumerateDirectoryEntries(
                            entry,
                            enumerateDirectory,
                            pattern,
                            directoriesToSkipRecursively == 0 ? 0 : directoriesToSkipRecursively - 1,
                            recursive,
                            accumulators,
                            isEnumerationForDirectoryDeletion);

                        if (!recurs.Succeeded)
                        {
                            return recurs;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var result = EnumerateDirectoryResult.CreateFromException(directoryPath, ex);
                accumulators.Current.Succeeded = false;
                return result;
            }

            return new EnumerateDirectoryResult(
                directoryPath,
                EnumerateDirectoryStatus.Success,
                (int)NativeIOConstants.ErrorNoMoreFiles);
        }

        /// <inheritdoc />
        public string GetFinalPathNameByHandle(SafeFileHandle handle, bool volumeGuidPath = false) => throw new NotImplementedException();

        /// <inheritdoc />
        public bool TryGetFinalPathNameByPath(string path, out string finalPath, out int nativeErrorCode, bool volumeGuidPath = false) 
        {
            var sb = new StringBuilder(MaxDirectoryPathLength());
            nativeErrorCode = RealPath(path, sb);

            if (nativeErrorCode != 0)
            {
                finalPath = null;
                return false;
            }

            finalPath = sb.ToString();
            return true;
        }

        /// <inheritdoc />
        public OpenFileResult TryOpenFileById(
            SafeFileHandle existingHandleOnVolume,
            FileId fileId,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileFlagsAndAttributes flagsAndAttributes,
            out SafeFileHandle handle) => throw new NotImplementedException();

        /// <inheritdoc />
        public bool CanGetFileIdAndVolumeIdByHandle()
            => false; // TODO: Task #1272136 (FileContentTable)

        /// <inheritdoc />
        public unsafe MiniUsnRecord? ReadFileUsnByHandle(SafeFileHandle fileHandle, bool forceJournalVersion2 = false)
            => null; // TODO: Task #1272136 (FileContentTable)

        /// <inheritdoc />
        public unsafe FileSystemType GetVolumeFileSystemByHandle(SafeFileHandle fileHandle)
        {
            var buffer = new StringBuilder(32);
            int result = GetFileSystemType(fileHandle, buffer, buffer.Capacity);
            if (result != 0)
            {
                throw ThrowForNativeFailure(Marshal.GetLastWin32Error(), nameof(GetFileSystemType), managedApiName: nameof(GetVolumeFileSystemByHandle));
            }

            return buffer.ToString().ToUpperInvariant() switch
            {
                "APFS" => FileSystemType.APFS,
                "HFS"  => FileSystemType.HFS,
                "EXT3" => FileSystemType.EXT3,
                "EXT4" => FileSystemType.EXT4,
                     _ => FileSystemType.Unknown
            };
        }

        /// <inheritdoc />
        public unsafe Usn? TryWriteUsnCloseRecordByHandle(SafeFileHandle fileHandle)
            => null; // TODO: Task #1272136 (FileContentTable)

        /// <inheritdoc />
        public unsafe uint GetShortVolumeSerialNumberByHandle(SafeFileHandle fileHandle) => throw new NotImplementedException();

        /// <inheritdoc />
        public unsafe FileIdAndVolumeId? TryGetFileIdAndVolumeIdByHandle(SafeFileHandle fileHandle)
            => null; // TODO: Task #1272136 (FileContentTable)

        /// <inheritdoc />
        public List<Tuple<VolumeGuidPath, ulong>> ListVolumeGuidPathsAndSerials()
            => new List<Tuple<VolumeGuidPath, ulong>>(0); // TODO: Task #1272136 (FileContentTable)

        /// <inheritdoc />
        public ulong GetVolumeSerialNumberByHandle(SafeFileHandle fileHandle)
            => 0; // TODO: Task #1272136 (FileContentTable)

        /// <nodoc />
        private static FileOptions FileFlagsAndAttributesToFileOptions(FileFlagsAndAttributes flagsAndAttributes) =>
            s_fileOptionsFlags.Aggregate(FileOptions.None, (acc, kvp) => flagsAndAttributes.HasFlag(kvp.Key) ? acc | kvp.Value : acc);

        /// <nodoc />
        internal static FileAccess FileDesiredAccessToFileAccess(FileDesiredAccess desiredAccess)
        {
            FileAccess access = FileAccess.Read;

            if (desiredAccess.HasFlag(FileDesiredAccess.GenericWrite))
            {
                access = FileAccess.Write;
            }

            if (desiredAccess.HasFlag(FileDesiredAccess.GenericRead) && desiredAccess.HasFlag(FileDesiredAccess.GenericWrite))
            {
                access = FileAccess.ReadWrite;
            }

            return access;
        }

        /// <nodoc />
        internal static uint NativeErrorCodeForException(Exception ex)
        {
            switch (ex)
            {
                case ArgumentOutOfRangeException _:
                case ArgumentNullException _:
                case ArgumentException _:
                    return NativeIOConstants.ErrorInvalidParameter;
                case UnauthorizedAccessException _:
                    return NativeIOConstants.ErrorAccessDenied;
                case DirectoryNotFoundException _:
                case FileNotFoundException _:
                    return NativeIOConstants.ErrorFileNotFound;
                case PathTooLongException _:
                    return NativeIOConstants.ErrorPathNotFound;
                case IOException _:
                case SecurityException _:
                case NotSupportedException _:
                default:
                    return NativeIOConstants.ErrorNotSupported;
            }
        }

        private static OpenFlags CreateOpenFlags(FileDesiredAccess desiredAccess, FileShare fileShare, FileMode fileMode, bool openSymlink)
        {
            OpenFlags flags = (ShouldCreateAndOpen(fileMode) ? OpenFlags.O_CREAT : OpenFlags.O_RDONLY) | OpenFlags.O_CLOEXEC;

            switch (fileMode)
            {
                case FileMode.Append:
                    flags |= OpenFlags.O_APPEND;
                    break;
                case FileMode.Truncate:
                    flags |= OpenFlags.O_TRUNC;
                    break;
                case FileMode.CreateNew:
                    if (!openSymlink)
                    {
                        flags |= OpenFlags.O_EXCL;
                    }

                    break;
                default:
                    break;
            }

            if (fileShare == FileShare.None)
            {
                flags |= OpenFlags.O_EXLOCK;
            }
            else
            {
                flags |= OpenFlags.O_SHLOCK;
            }

            if (desiredAccess.HasFlag(FileDesiredAccess.GenericRead) && !desiredAccess.HasFlag(FileDesiredAccess.GenericWrite))
            {
                flags |= OpenFlags.O_RDONLY;
            }

            if (desiredAccess.HasFlag(FileDesiredAccess.GenericWrite) && !desiredAccess.HasFlag(FileDesiredAccess.GenericRead))
            {
                flags |= OpenFlags.O_WRONLY;
            }

            if (desiredAccess.HasFlag(FileDesiredAccess.GenericRead | FileDesiredAccess.GenericWrite))
            {
                flags |= OpenFlags.O_RDWR;
            }

            if (openSymlink)
            {
                flags |= OpenFlags.O_SYMLINK;
            }

            return flags;
        }

        private static FilePermissions CreateFilePermissions(FileDesiredAccess desiredAccess)
        {
            FilePermissions permissions = 0;

            if (desiredAccess.HasFlag(FileDesiredAccess.GenericRead))
            {
                permissions |= (FilePermissions.S_IRUSR | FilePermissions.S_IRGRP | FilePermissions.S_IROTH);
            }

            if (desiredAccess.HasFlag(FileDesiredAccess.GenericWrite))
            {
                permissions |= (FilePermissions.S_IWUSR | FilePermissions.S_IWGRP | FilePermissions.S_IWOTH);
            }

            return permissions;
        }

        private static bool ShouldCreateAndOpen(FileMode fileMode)
        {
            return fileMode == FileMode.CreateNew || fileMode == FileMode.Create || fileMode == FileMode.OpenOrCreate;
        }

        private OpenFileResult TryOpen(
            string path,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileMode fileMode,
            bool openSymlink,
            out SafeFileHandle handle)
        {
            try
            {
                var mayBeExistence = TryProbePathExistence(path, openSymlink);
                handle = Open(path, CreateOpenFlags(desiredAccess, shareMode, fileMode, openSymlink), CreateFilePermissions(desiredAccess));

                if (handle.IsInvalid)
                {
                    handle = null;
                    return createErrorResult(Marshal.GetLastWin32Error());
                }

                int successCode =
                    mayBeExistence.Succeeded && mayBeExistence.Result == PathExistence.ExistsAsFile
                    ? NativeIOConstants.ErrorFileExists
                    : (mayBeExistence.Succeeded && mayBeExistence.Result == PathExistence.ExistsAsDirectory
                        ? NativeIOConstants.ErrorAlreadyExists
                        : NativeIOConstants.ErrorSuccess);

                return OpenFileResult.Create(path, successCode, fileMode, handleIsValid: true);
            }
            catch (Exception e)
            {
                handle = null;
                return createErrorResult((int)NativeErrorCodeForException(e));
            }

            OpenFileResult createErrorResult(int errorCode)
            {
                return OpenFileResult.Create(path, (int)errorCode, fileMode, handleIsValid: false);
            }
        }

        private static bool IsSymlink(string path)
        {
            var mode = TryGetFilePermission(path, followSymlink: false, throwOnFailure: false);
            if (mode < 0)
            {
                return false;
            }

            FilePermissions permissions = checked((FilePermissions)mode);
            return permissions.HasFlag(FilePermissions.S_IFLNK);
        }

        /// <inheritdoc />
        public OpenFileResult TryCreateOrOpenFile(
            string path,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileMode creationDisposition,
            FileFlagsAndAttributes flagsAndAttributes,
            out SafeFileHandle handle)
        {
            return TryOpen(
                path,
                desiredAccess,
                shareMode,
                creationDisposition,
                flagsAndAttributes.HasFlag(FileFlagsAndAttributes.FileFlagOpenReparsePoint) && IsSymlink(path),
                out handle);
        }

        /// <inheritdoc />
        public unsafe bool TrySetDeletionDisposition(SafeFileHandle handle) => throw new NotImplementedException();

        /// <inheritdoc />
        public ReOpenFileStatus TryReOpenFile(
            SafeFileHandle existing,
            FileDesiredAccess desiredAccess,
            FileShare shareMode,
            FileFlagsAndAttributes flagsAndAttributes,
            out SafeFileHandle reopenedHandle) => throw new NotImplementedException();

        /// <inheritdoc />
        public FileStream CreateFileStream(
           string path,
           FileMode fileMode,
           FileAccess fileAccess,
           FileShare fileShare,
           FileOptions options,
           bool force)
        {
            // The bufferSize of 4096 bytes is the default as used by the other FileStream constructors
            // http://index/mscorlib/system/io/filestream.cs.html
            return ExceptionUtilities.HandleRecoverableIOException(
                () =>
                {
                    try
                    {
                        return new FileStream(path, fileMode, fileAccess, fileShare, bufferSize: DefaultBufferSize, options: options);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // This is a workaround to allow write access to a file that is marked as readonly. It is
                        // exercised when hashing the output files of pips that create readonly files. The hashing currently
                        // opens files as write
                        if (force)
                        {
                            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                            return new FileStream(path, fileMode, fileAccess, fileShare, bufferSize: DefaultBufferSize, options: options);
                        }

                        throw;
                    }
                },
                ex =>
                {
                    throw new BuildXLException(I($"Failed to open path '{path}' with mode='{fileMode}', access='{fileAccess}', share='{fileShare}'"), ex);
                });
        }

        /// <inheritdoc />
        public unsafe bool TryPosixDelete(string pathToDelete, out OpenFileResult openFileResult) => throw new NotImplementedException();

        /// <inheritdoc />
        public int MaxDirectoryPathLength() => NativeIOConstants.MaxPath;

        /// <inheritdoc />
        public CreateHardLinkStatus TryCreateHardLink(string link, string linkTarget)
        {
            // POSIX systems use the opposite ordering of inputs as Windows for linking files
            // Function stub from GNU docs: int link (const char *oldname, const char *newname)
            int result = BuildXL.Interop.Unix.IO.link(linkTarget, link);
            if (result != 0)
            {
                var errno = Marshal.GetLastWin32Error();
                // For correspondence, see: https://www.man7.org/linux/man-pages/man2/link.2.html#ERRORS
                switch (errno)
                {
                    case (int)Errno.EACCES:
                        // write access to the directory containing newpath is denied
                    case (int)Errno.EPERM:
                        // oldpath is a directory
                        // the filesystem containing either src or dest does not support hard links
                        // caller does not have permission to create a hardlink to file
                        // oldpath is marked immutable or append-only
                    case (int)Errno.EROFS:
                        // read-only file system
                        return CreateHardLinkStatus.FailedAccessDenied;
                    case (int)Errno.ELOOP:
                        // too many symbolic links encountered
                    case (int)Errno.EMLINK:
                        // too many hard links to oldpath
                        return CreateHardLinkStatus.FailedDueToPerFileLinkLimit;
                    case (int)Errno.EEXIST:
                        // newpath already exists
                        return CreateHardLinkStatus.FailedDestinationExists;
                    case (int)Errno.EXDEV:
                        // oldpath and newpath are on different filesystems
                        return CreateHardLinkStatus.FailedSinceDestinationIsOnDifferentVolume;
                    case (int)Errno.ENOENT:
                        // oldpath does not exist
                        return CreateHardLinkStatus.FailedSourceDoesNotExist;
                    default:
                        return CreateHardLinkStatus.Failed;
                }
            }

            return CreateHardLinkStatus.Success;
        }

        /// <inheritdoc />
        public CreateHardLinkStatus TryCreateHardLinkViaSetInformationFile(string link, string linkTarget, bool replaceExisting = true) => throw new NotImplementedException();

        /// <inheritdoc />
        public Possible<Unit> TryCreateSymbolicLink(string symLinkFileName, string target, bool isTargetFile)
        {
            try
            {
                var parentDirPath = Path.GetDirectoryName(symLinkFileName);
                if (!string.IsNullOrWhiteSpace(parentDirPath) && !Directory.Exists(parentDirPath))
                {
                    CreateDirectory(parentDirPath);
                }

                int err = symlink(target, symLinkFileName);
                if (err == 0)
                {
                    return Unit.Void;
                }

                return new NativeFailure(Marshal.GetLastWin32Error());
            }
            catch (UnauthorizedAccessException e)
            {
                return new Failure<string>(e.ToString());
            }
            catch (IOException e)
            {
                return new Failure<string>(e.ToString());
            }
        }

        /// <inheritdoc />
        public void SetFileAttributes(string path, FileAttributes attributes)
        {
            int result = GetFilePermission(path);
            if (result < 0)
            {
                throw new BuildXLException($"Failed to get permissions for file '{path}' - error: {Marshal.GetLastWin32Error()}");
            }

            FilePermissions filePermissions = checked((FilePermissions)result);
            FilePermissions writePermissions = (FilePermissions.S_IWUSR | FilePermissions.S_IWGRP | FilePermissions.S_IWOTH);

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                filePermissions &= ~writePermissions;
            }
            else
            {
                filePermissions |= writePermissions;
            }

            result = SetFilePermissionsForFilePath(path, filePermissions);

            if (result < 0)
            {
                throw new BuildXLException($"Failed to set permissions for file '{path}' - error: {Marshal.GetLastWin32Error()}");
            }
        }

        /// <inheritdoc />
        public bool TryReadSeekPenaltyProperty(SafeFileHandle driveHandle, out bool hasSeekPenalty, out int error) => throw new NotImplementedException();

        /// <inheritdoc />
        public FileAttributes GetFileAttributes(string path)
        {
            return TryGetFileAttributes(path);
        }

        private static FileAttributes TryGetFileAttributes(string path)
        {
            try
            {
                return File.GetAttributes(path);
            }
            catch (Exception ex)
            {
                throw new BuildXLException("Getting file attributes failed", ex);
            }
        }

        /// <inheritdoc />
        public unsafe FileAttributes GetFileAttributesByHandle(SafeFileHandle fileHandle) => throw new NotImplementedException();

        /// <inheritdoc />
        public unsafe bool IsPendingDelete(SafeFileHandle fileHandle) => throw new NotImplementedException();

        /// <inheritdoc />
        public FileFlagsAndAttributes GetFileFlagsAndAttributesForPossibleReparsePoint(string expandedPath)
        {
            Possible<ReparsePointType> reparsePointType = TryGetReparsePointType(expandedPath);
            var isActionableReparsePoint = false;

            if (reparsePointType.Succeeded)
            {
                isActionableReparsePoint = IsReparsePointActionable(reparsePointType.Result);
            }

            var openFlags = FileFlagsAndAttributes.FileFlagOverlapped;

            if (isActionableReparsePoint)
            {
                openFlags = openFlags | FileFlagsAndAttributes.FileFlagOpenReparsePoint;
            }

            return openFlags;
        }

        /// <inheritdoc />
        public unsafe bool TryRename(SafeFileHandle handle, string destination, bool replaceExisting) => throw new NotImplementedException();

        /// <inheritdoc />
        public bool IsReparsePointActionable(ReparsePointType reparsePointType)
        {
            return reparsePointType == ReparsePointType.UnixSymlink;
        }

        /// <inheritdoc />
        public Possible<ReparsePointType> TryGetReparsePointType(string path)
        {
            return GetReparsePointType(path);
        }

        private static Possible<ReparsePointType> GetReparsePointType(string path)
        {
            if (IsSymlink(path))
            {
                return ReparsePointType.UnixSymlink;
            }
            
            return ReparsePointType.None;
        }

        /// <inheritdoc />
        public string GetChainOfReparsePoints(SafeFileHandle handle, string sourcePath, IList<string> chainOfReparsePoints, bool includeOnlyReparsePoints = true)
        { 
            return GetChainOfReparsePoints(sourcePath, chainOfReparsePoints, includeOnlyReparsePoints);
        }

        /// <inheritdoc />
        public string GetChainOfReparsePoints(string sourcePath, IList<string> chainOfReparsePoints, bool includeOnlyReparsePoints = false)
        {
            Contract.Requires(!string.IsNullOrWhiteSpace(sourcePath));
            Contract.Requires(chainOfReparsePoints != null);

            if (!includeOnlyReparsePoints)
            {
                chainOfReparsePoints.Add(sourcePath);
            }

            do
            {
                var possibleReparsePointType = TryGetReparsePointType(sourcePath);
                if (!possibleReparsePointType.Succeeded || !possibleReparsePointType.Result.IsActionable())
                {
                    return possibleReparsePointType.Succeeded ? sourcePath : null;
                }

                var possibleTarget = TryGetReparsePointTarget(sourcePath);
                if (!possibleTarget.Succeeded)
                {
                    return null;
                }

                var maybeResolvedTarget = FileUtilities.ResolveSymlinkTarget(sourcePath, possibleTarget.Result);

                if (!maybeResolvedTarget.Succeeded)
                {
                    return null;
                }

                if (includeOnlyReparsePoints)
                {
                    chainOfReparsePoints.Add(sourcePath);
                }

                sourcePath = maybeResolvedTarget.Result;

                if (!includeOnlyReparsePoints)
                {
                    chainOfReparsePoints.Add(sourcePath);
                }
            } while (true);
        }

        /// <inheritdoc />
        public Possible<string> TryGetReparsePointTarget(SafeFileHandle handle, string sourcePath)
        {
            return TryGetReparsePointTarget(sourcePath);
        }

        internal Possible<string> TryGetReparsePointTarget(string sourcePath)
        {
            try
            {
                var maxPathLength = MaxDirectoryPathLength();
                var sb = new StringBuilder(maxPathLength);
                long numCharactersWritten = SafeReadLink(sourcePath, sb, maxPathLength);
                if (numCharactersWritten >= 0 && numCharactersWritten <= maxPathLength)
                {
                    return sb.ToString().TrimEnd('\0');
                }
                else
                {
                    return createFailure("Encountered error: " + Marshal.GetLastWin32Error());
                }
            }
            catch (Exception e)
            {
                return createFailure("Exception caught", e);
            }

            RecoverableExceptionFailure createFailure(string message, Exception e = null)
            {
                return new RecoverableExceptionFailure(
                    new BuildXLException(nameof(TryGetReparsePointTarget) + " failed: " + message, e));
            }
        }

        /// <inheritdoc />
        public unsafe ReadUsnJournalResult TryReadUsnJournal(
            SafeFileHandle volumeHandle,
            byte[] buffer,
            ulong journalId,
            Usn startUsn = default(Usn),
            bool forceJournalVersion2 = false,
            bool isJournalUnprivileged = false) => throw new NotImplementedException();

        /// <inheritdoc />
        public QueryUsnJournalResult TryQueryUsnJournal(SafeFileHandle volumeHandle) => throw new NotImplementedException();

        /// <inheritdoc />
        public unsafe NtStatus FlushPageCacheToFilesystem(SafeFileHandle handle) => throw new NotImplementedException();

        /// <inheritdoc />
        public void CreateJunction(string junctionPoint, string targetDir, bool createDirectoryForJunction, bool allowNonExistentTarget) => throw new NotImplementedException();

        /// <inheritdoc />
        public Possible<PathExistence, NativeFailure> TryProbePathExistence(string path, bool followSymlink)
        {
            return TryProbePathExistence(path, followSymlink, out _);
        }

        /// <inheritdoc />
        public Possible<PathExistence, NativeFailure> TryProbePathExistence(string path, bool followSymlink, out bool isReparsePoint)
        {
            var mode = GetFilePermission(path, followSymlink: false, throwOnFailure: false);

            if (mode < 0)
            {
                isReparsePoint = false;
                return PathExistence.Nonexistent;
            }

            FilePermissions permissions = checked((FilePermissions)mode);
            isReparsePoint = permissions.HasFlag(FilePermissions.S_IFLNK);

            if (followSymlink)
            {
                // CodeQL [SM00414] Path is controlled in FrontEnd (marked by code ql due to a code path from FrontEndHostController.Nuget.cs).
                return Directory.Exists(path)
                    ? PathExistence.ExistsAsDirectory
                    : PathExistence.ExistsAsFile;
            }
            else
            {
                return
                    permissions.HasFlag(FilePermissions.S_IFDIR) ? PathExistence.ExistsAsDirectory :
                    permissions.HasFlag(FilePermissions.S_IFLNK) ? PathExistence.ExistsAsFile :
                    PathExistence.ExistsAsFile;
            }
        }

        /// <inheritdoc />
        public uint GetHardLinkCountByHandle(SafeFileHandle handle) => throw new NotImplementedException();

        /// <inheritdoc />
        public bool PathMatchPattern(string path, string pattern)
        {
            return GetRegex(pattern).Match(path).Success;
        }

        /// <inheritdoc/>
        public bool IsWciReparseArtifact(string path)
        {
            return false;
        }

        /// <inheritdoc/>
        public bool IsWciReparsePoint(string path)
        {
            return false;
        }

        /// <inheritdoc/>
        public bool IsWciTombstoneFile(string path)
        {
            return false;
        }

        /// <inheritdoc/>
        public bool IsVolumeMapped(string volume) => false;

        /// <inheritdoc/>
        public FileIdAndVolumeId? TryGetFileIdentityByHandle(SafeFileHandle fileHandle)
        {
            var statBuffer = new StatBuffer();

            unsafe
            {
                return StatFileDescriptor(fileHandle, ref statBuffer) != 0
                    ? default
                    : new FileIdAndVolumeId(
                        unchecked((ulong)statBuffer.DeviceID),
                        new FileId(UnusedFileIdPart, unchecked((ulong)statBuffer.InodeNumber)));
            }
        }

        /// <inheritdoc/>
        public (FileIdAndVolumeId, Usn)? TryGetVersionedFileIdentityByHandle(SafeFileHandle fileHandle)
        {
            var statBuffer = new StatBuffer();

            if (StatFileDescriptor(fileHandle, ref statBuffer) != 0)
            {
                return default;
            }

            var fileIdAndVolumeId = new FileIdAndVolumeId(
                unchecked((ulong)statBuffer.DeviceID),
                new FileId(UnusedFileIdPart, unchecked((ulong)statBuffer.InodeNumber)));

            long sec = statBuffer.TimeLastStatusChange;
            long nsec = statBuffer.TimeNSecLastStatusChange;
            if (!IsPreciseFileVersionSupportedByEnlistmentVolume || !CheckIfVolumeSupportsPreciseFileVersionByHandle(fileHandle))
            {
                // Nanosecond precision may not be supported, and
                // either enlistment volume does not support precise file version, or
                // the volume where the file resides does not support precise file version.
                // Use the modified timestamp. See TryEstablishVersionedFileIdentityByHandle below. The lastModification timestamp is modified when establishing the file verison
                // Yes, this can result in unreliability.
                sec = statBuffer.TimeLastModification;
                nsec = statBuffer.TimeNSecLastModification;
            }

            ulong version = unchecked((ulong)Timespec.SecToNSec(sec));
            version += unchecked((ulong)nsec);

            return (fileIdAndVolumeId, new Usn(version));
        }

        /// <inheritdoc />
        public (FileIdAndVolumeId, Usn)? TryEstablishVersionedFileIdentityByHandle(SafeFileHandle fileHandle, bool flushPageCache)
        {
            var statBuffer = new StatBuffer();

            if (StatFileDescriptor(fileHandle, ref statBuffer) != 0)
            {
                return default;
            }

            var fileIdAndVolumeId = new FileIdAndVolumeId(
                unchecked((ulong)statBuffer.DeviceID),
                new FileId(UnusedFileIdPart, unchecked((ulong)statBuffer.InodeNumber)));

            long sec = statBuffer.TimeLastStatusChange;
            long nsec = statBuffer.TimeNSecLastStatusChange;

            if (!IsPreciseFileVersionSupportedByEnlistmentVolume || !CheckIfVolumeSupportsPreciseFileVersionByHandle(fileHandle))
            {
                // Nanosecond precision may not be supported (nsec == 0), and
                // either enlistment volume does not support precise file version, or
                // the volume where the file resides does not support precise file version.

                // Get the current time.
                var elapsedSeconds = (long)(DateTime.UtcNow - UnixEpoch).TotalSeconds;
                if (elapsedSeconds - statBuffer.TimeLastModification <= 1)
                {
                    // Set the modified time 1s to the past, only if it matches the current time.
                    // It means that modification and establishing identity happened in sub-second and high-precision timestamp is not supported.
                    // This most likely happens for outputs or intermediate outputs.

                    var setTimeStatBuffer = new StatBuffer();
                    setTimeStatBuffer.TimeCreation = statBuffer.TimeCreation;
                    setTimeStatBuffer.TimeNSecCreation = statBuffer.TimeNSecCreation;

                    setTimeStatBuffer.TimeLastAccess = statBuffer.TimeLastAccess;
                    setTimeStatBuffer.TimeNSecLastAccess = statBuffer.TimeNSecLastAccess;

                    setTimeStatBuffer.TimeLastModification = statBuffer.TimeLastModification - 1;
                    setTimeStatBuffer.TimeNSecLastModification = statBuffer.TimeNSecLastModification;

                    setTimeStatBuffer.TimeLastStatusChange = statBuffer.TimeLastStatusChange;
                    setTimeStatBuffer.TimeNSecLastStatusChange = statBuffer.TimeNSecLastStatusChange;

                    // Accessing hidden files in MacOs: http://www.westwind.com/reference/os-x/invisibles.html
                    // 
                    // An alternative would be using fcntl with F_GETPATH that takes a handle and returns
                    // the concrete OS path owned by the handle; or calling fsetattrlist().
                    //
                    // TODO: in any case, this should be moved to Interop.IO so that there it is decided
                    //       what to do based on the current os
                    var path = OperatingSystemHelper.IsMacOS
                        ? $"/.vol/{fileIdAndVolumeId.VolumeSerialNumber}/{fileIdAndVolumeId.FileId.Low}"
                        : $"/proc/self/fd/{fileHandle.DangerousGetHandle().ToInt32()}";

                    int result = SetTimeStampsForFilePath(path, followSymlink: true, setTimeStatBuffer);

                    if (result != 0)
                    {
                        return default;
                    }

                    sec = statBuffer.TimeLastModification - 1;
                    nsec = statBuffer.TimeNSecLastModification;
                }
                else
                {
                    sec = statBuffer.TimeLastModification;
                    nsec = statBuffer.TimeNSecLastModification;
                }
            }

            ulong version = unchecked((ulong)Timespec.SecToNSec(sec));
            version += unchecked((ulong)nsec);

            return (fileIdAndVolumeId, new Usn(version));

            // TODO: if high-precision file timestamp is supported, this function should simply call TryGetVersionedFileIdentityByHandle.
            // return TryGetVersionedFileIdentityByHandle(fileHandle);
        }

        /// <inheritdoc />
        public bool IsPreciseFileVersionSupportedByEnlistmentVolume
        {
            get => m_supportPreciseFileVersion.Value;
            set
            {
                m_supportPreciseFileVersion = new Lazy<bool>(() => value);
            }
        }

        /// <inheritdoc />
        public bool CheckIfVolumeSupportsPreciseFileVersionByHandle(SafeFileHandle fileHandle)
        {
            try
            {
                return GetVolumeFileSystemByHandle(fileHandle) == FileSystemType.APFS;
            }
            catch (NativeWin32Exception)
            {
                return false;
            }
        }

        private bool SupportPreciseFileVersion()
        {
            // Use temp file name as an approximation whether file system supports precise file version.
            string path = FileUtilities.GetTempFileName();
            bool result = false;

            using (var fileStream = CreateFileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.None, false))
            {
                result = CheckIfVolumeSupportsPreciseFileVersionByHandle(fileStream.SafeFileHandle);
            }

            File.Delete(path);
            return result;
        }

        /// <inheritdoc />
        public bool IsInKernelCopyingSupportedByHostSystem => !Interop.Dispatch.IsMacOS;

        private static int TryGetFilePermission(string path, bool followSymlink = false, bool throwOnFailure = true)
        {
            var statBuffer = new StatBuffer();

            if (StatFile(path, followSymlink, ref statBuffer) != 0)
            {
                if (throwOnFailure)
                {
                    throw new BuildXLException(I($"Failed to stat file '{path}' to get its permission - error: {Marshal.GetLastWin32Error()}"));
                }
                else
                {
                    return -1;
                }
            }

            return unchecked((int)statBuffer.Mode);
        }

        /// <summary>
        /// Gets file permission.
        /// </summary>
        public int GetFilePermission(string path, bool followSymlink = false, bool throwOnFailure = true)
        {
            return TryGetFilePermission(path, followSymlink, throwOnFailure);
        }

        /// <summary>
        /// Throws an exception for the unexpected failure of a native API.
        /// </summary>
        /// <remarks>
        /// We don't want native failure checks erased at any contract-rewriting setting.
        /// The return type is <see cref="Exception"/> to facilitate a pattern of <c>throw ThrowForNativeFailure(...)</c> which informs csc's flow control analysis.
        /// </remarks>
        internal static Exception ThrowForNativeFailure(int error, string nativeApiName, [CallerMemberName] string managedApiName = "<unknown>", string message = null)
        {
            Contract.Requires(!string.IsNullOrEmpty(nativeApiName) && !string.IsNullOrEmpty(managedApiName));

            string failureMessage = string.IsNullOrEmpty(message) ? string.Empty : ": " + message;

            // Internally throw NativeWin32Exception.
            throw new NativeWin32Exception(error, I($"{nativeApiName} for {managedApiName} failed{failureMessage}"));
        }

        /// <inheritdoc />
        public bool IsPathRooted(string path) => GetRootLength(path) == 1;

        /// <inheritdoc />
        public int GetRootLength(string path) => path.Length > 0 && IsDirectorySeparator(path[0]) ? 1 : 0;

        /// <inheritdoc />
        public bool IsDirectorySeparator(char c) => c == Path.DirectorySeparatorChar;

        /// <inheritdoc />
        public Possible<string> TryResolveReparsePointRelativeTarget(string path, string relativeTarget)
        {
            var resolvedSourcePath = path;

            // readlink on Linux returns a relative path that is relative to a source path that is fully resolved
            // This means that if there is a directory symlink in the middle of the path we cannot simply 
            // append the relativeTarget to the given path.
            // Example:
            // Root
            // |
            // +--Enlist
            //    |
            //    +---Intermediate
            //    |   \---Current
            //    |           file.lnk ==> ..\..\Target\file.txt
            //    |
            //    +---Source ==> Intermediate\Current
            //    |
            //    \---Target
            //            file.txt
            // When Root/Enlist/Source/file.lnk is the sourcePath, readlink will return ../../Target/file.txt
            // the returned resolved symlink target for a relative symlink will be
            // Root/Target/file.txt rather than Root/Enlist/Target/file.txt when calling FileUtilities.TryResolveRelativeTarget
            // TODO: verify this behaviour on macos
            if (OperatingSystemHelper.IsLinuxOS)
            {
                // Full resolve up to the parent of the source path to avoid the problem described above
                var resolvedPathSb = new StringBuilder(MaxDirectoryPathLength());
                var error = RealPath(Path.GetDirectoryName(path), resolvedPathSb);

                if (error != 0)
                {
                    // An error occured with resolving the path
                    return new NativeFailure(error);
                }

                resolvedSourcePath = Path.Combine(resolvedPathSb.ToString(), Path.GetFileName(path));
            }

            return FileUtilities.TryResolveRelativeTarget(resolvedSourcePath, relativeTarget);
        }

        /// <inheritdoc />
        public bool TryWriteFileSync(SafeFileHandle handle, byte[] content, out int nativeErrorCode) => throw new NotImplementedException();

        /// <inheritdoc />
        public bool IsDirectorySymlinkOrJunction(string path)
        {
            var reparsePointType = TryGetReparsePointType(path);
            return reparsePointType.Succeeded && reparsePointType.Result == ReparsePointType.UnixSymlink;
        }

        /// <inheritdoc/>
        public string GetFullPath(string path)
        {
            Contract.Requires(!string.IsNullOrEmpty(path));

            try
            {
                return Path.GetFullPath(path);
            }
            catch(Exception e) when (e is ArgumentException || e is SecurityException || e is NotSupportedException || e is PathTooLongException)
            {
                throw new BuildXLException(I($"Failed to get a full path from '{path}'"), e);
            }
        }

        /// <inheritdoc/>
        public bool SupportsCreationDate() => Interop.Unix.IO.SupportsCreationDate();
    }
}
