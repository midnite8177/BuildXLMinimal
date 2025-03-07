// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.Reflection;
using System.Text;
using BuildXL.Native.IO;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;

#nullable enable

namespace BuildXL.Processes
{
    /// <summary>
    /// Information about attempted file access
    /// </summary>
    public readonly struct ReportedFileAccess : IEquatable<ReportedFileAccess>
    {
        /// <summary>
        /// Prefix for reads
        /// </summary>
        public const string ReadDescriptionPrefix = " R  ";

        /// <summary>
        /// Prefix for writes
        /// </summary>
        public const string WriteDescriptionPrefix = " W  ";

        /// <summary>
        /// Magic number indicating that no USN was/could be obtained.
        /// </summary>
        public static readonly Usn NoUsn = new Usn(0xFFFFFFFFFFFFFFFF);

        /// <summary>
        /// An action to take on a file or device that exists or does not exist.
        /// </summary>
        public readonly CreationDisposition CreationDisposition;

        /// <summary>
        /// The requested access to the file or device
        /// </summary>
        public readonly DesiredAccess DesiredAccess;

        /// <summary>
        /// Reported error code
        /// </summary>
        /// <remarks>
        /// This value is often the same as <see cref="RawError"/>, but it may be different in some cases.
        /// The reason for this is that the error code is sometimes adjusted to match the expected error code because, e.g.,
        /// some Windows APIs do not return ERROR_SUCCESS even when the operation is successful. See Bug 2234559 for details.
        /// </remarks>
        public readonly uint Error;

        /// <summary>
        /// Last-error code
        /// </summary>
        public readonly uint RawError;

        /// <summary>
        /// USN number. Note that 0 is the default USN number (e.g. when no journal is present),
        /// and the <code>NoUsn</code> value indicates the absence of a USN number.
        /// </summary>
        /// <remarks>
        /// This value is only set (to a value different than <code>NoUsn</code>) when
        /// the requested policy had the <code>ReportUsnAfterOpen</code> flag
        /// or had an expected USN set.
        /// </remarks>
        public readonly Usn Usn;

        /// <summary>
        /// The file or device attributes and flags
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1726:UsePreferredTerms", MessageId = "Flags")]
        public readonly FlagsAndAttributes FlagsAndAttributes;

        /// <summary>
        /// Computed attributes for this file or directory access.
        /// </summary>
        /// <remarks>
        /// This is separate from <see cref="FlagsAndAttributes"/>. This represents the attributes artifact that is opened/created by the operation.
        /// <see cref="FlagsAndAttributes"/> represents the attributes applied to this operation.
        /// </remarks>
        public readonly FlagsAndAttributes OpenedFileOrDirectoryAttributes;

        /// <summary>
        /// Full path that was accessed. If this path is equivalent to <see cref="ManifestPath"/>, it is null.
        /// If it is not equivalent to <see cref="ManifestPath"/>, then this path is outside of the path table altogether
        /// (manifest absolute path is invalid) or refers to a descendant (i.e., the manifest path id was used in a scope rule).
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1721:PropertyNamesShouldNotMatchGetMethods")]
        public readonly string? Path;

        /// <summary>
        /// Path as given in the file access manifest. This path may have been echoed as part of an exact match (<see cref="Path"/> is then null)
        /// or as part of a scope (<see cref="Path"/> is a descendant).
        /// </summary>
        public readonly AbsolutePath ManifestPath;

        /// <summary>
        /// The process which caused the reported file access
        /// </summary>
        public readonly ReportedProcess Process;

        /// <summary>
        /// The requested sharing mode of the file or device.
        /// </summary>
        public readonly ShareMode ShareMode;

        /// <summary>
        /// Whether the file access was allowed or denied
        /// </summary>
        public readonly FileAccessStatus Status;

        /// <summary>
        /// What method was used for determining the <see cref="FileAccessStatus"/>
        /// </summary>
        public readonly FileAccessStatusMethod Method;

        // The following fields are a byte-wide and should be kept
        // together at the end of the structure to minimize padding.

        /// <summary>
        /// Level of access requested by this file operation (e.g. CreateFile can request read, write or both).
        /// </summary>
        public readonly RequestedAccess RequestedAccess;

        /// <summary>
        /// The operation that caused the reported file access
        /// </summary>
        public readonly ReportedFileOperation Operation;

        /// <summary>
        /// If true, the file access was marked for explicit reporting (with <see cref="FileAccessPolicy.ReportAccess"/>),
        /// possibly in a containing scope.
        /// </summary>
        public readonly bool ExplicitlyReported;

        /// <summary>
        /// Enumerate pattern
        /// </summary>
        public readonly string? EnumeratePattern;

        /// <summary>
        /// Creates an instance from an absolute path
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1726:UsePreferredTerms", MessageId = "flags")]
        public ReportedFileAccess(
            ReportedFileOperation operation,
            ReportedProcess process,
            RequestedAccess requestedAccess,
            FileAccessStatus status,
            bool explicitlyReported,
            uint error,
            uint rawError,
            Usn usn,
            DesiredAccess desiredAccess,
            ShareMode shareMode,
            CreationDisposition creationDisposition,
            FlagsAndAttributes flagsAndAttributes,
            AbsolutePath manifestPath,
            string? path,
            string? enumeratePattern,
            FlagsAndAttributes openedFileOrDirectoryAttribute = (FlagsAndAttributes)FlagsAndAttributesConstants.InvalidFileAttributes,
            FileAccessStatusMethod fileAccessStatusMethod = FileAccessStatusMethod.PolicyBased)
        {
            Operation = operation;
            Process = process;
            RequestedAccess = requestedAccess;
            Status = status;
            ExplicitlyReported = explicitlyReported;
            Error = error;
            RawError = rawError;
            Usn = usn;
            DesiredAccess = desiredAccess;
            ShareMode = shareMode;
            CreationDisposition = creationDisposition;
            FlagsAndAttributes = flagsAndAttributes;
            OpenedFileOrDirectoryAttributes = openedFileOrDirectoryAttribute;
            ManifestPath = manifestPath;
            Path = path;
            EnumeratePattern = enumeratePattern;
            Method = fileAccessStatusMethod;
        }

        /// <nodoc/>
        public ReportedFileAccess CreateWithStatus(FileAccessStatus status)
        {
            return new ReportedFileAccess(
                Operation,
                Process,
                RequestedAccess,
                status,
                ExplicitlyReported,
                Error,
                RawError,
                Usn,
                DesiredAccess,
                ShareMode,
                CreationDisposition,
                FlagsAndAttributes,
                ManifestPath,
                Path,
                EnumeratePattern,
                OpenedFileOrDirectoryAttributes,
                Method);
        }

        /// <nodoc/>
        public ReportedFileAccess CreateWithPathAndAttributes(string path, AbsolutePath manifestPath, FlagsAndAttributes flagsAndAttributes)
        {
            return new ReportedFileAccess(
                Operation,
                Process,
                RequestedAccess,
                Status,
                ExplicitlyReported,
                Error,
                RawError,
                Usn,
                DesiredAccess,
                ShareMode,
                CreationDisposition,
                flagsAndAttributes,
                manifestPath,
                path,
                EnumeratePattern,
                OpenedFileOrDirectoryAttributes,
                Method);
        }

        /// <summary>
        /// Error code returned when probing for an absent file.
        /// </summary>
        public const int ERROR_FILE_NOT_FOUND = 0x2;

        /// <summary>
        /// Error code returned when probing for an absent path.
        /// </summary>
        public const int ERROR_PATH_NOT_FOUND = 0x3;

        /// <summary>
        /// Indicates if this file access was to a path that did not exist.
        /// </summary>
        /// <remarks>
        /// Below we simply compare the error code with the values of ERROR_PATH_NOT_FOUND and ERROR_PATH_NOT_FOUND.
        /// The particular error depends on if the final or non-final component is missing.
        /// http://msdn.microsoft.com/en-us/library/windows/desktop/ms681382(v=vs.85).aspx
        /// </remarks>
        public bool IsNonexistent => Error == ERROR_PATH_NOT_FOUND || Error == ERROR_FILE_NOT_FOUND;

        /// <inherit />
        public bool Equals(ReportedFileAccess other)
        {
            return ManifestPath == other.ManifestPath &&
                   string.Equals(Path, other.Path, OperatingSystemHelper.PathComparison) &&
                   RequestedAccess == other.RequestedAccess &&
                   Status == other.Status &&
                   Process == other.Process &&
                   Error == other.Error &&
                   // Do not compare RawError, because its value can be non-deterministic.
                   Usn == other.Usn &&
                   ExplicitlyReported == other.ExplicitlyReported &&
                   DesiredAccess == other.DesiredAccess &&
                   ShareMode == other.ShareMode &&
                   CreationDisposition == other.CreationDisposition &&
                   FlagsAndAttributes == other.FlagsAndAttributes &&
                   OpenedFileOrDirectoryAttributes == other.OpenedFileOrDirectoryAttributes &&
                   string.Equals(EnumeratePattern, other.EnumeratePattern, OperatingSystemHelper.PathComparison) &&
                   Method == other.Method;
        }

        /// <summary>
        /// Creates a short description of the operation and path. The following is the summary for writing bar.txt and
        /// reading bar2.txt:
        ///
        /// W c:\foo\bar.txt
        /// R c:\foo\bar2.txt
        /// </summary>
        public string ShortDescribe(PathTable pathTable)
        {
            return (IsWriteViolation ? WriteDescriptionPrefix : ReadDescriptionPrefix) + GetPath(pathTable);
        }

        /// <summary>
        /// Determines whether the current violation is a write violation
        /// </summary>
        public bool IsWriteViolation => (RequestedAccess & RequestedAccess.Write) != 0;

        /// <summary>
        /// Describes the operation that cause this reported file access, including all parameter value, except the path
        /// </summary>
        public string Describe()
        {
            using PooledObjectWrapper<StringBuilder> wrapper = Pools.GetStringBuilder();
            StringBuilder sb = wrapper.Instance;
            sb.Append('[');
            sb.Append(Process.Path);
            sb.Append(':');
            sb.Append(Process.ProcessId);
            sb.Append(']');

            if (RequestedAccess != RequestedAccess.None)
            {
                sb.AppendFormat("({0:G})", RequestedAccess);
            }

            sb.Append(' ');

            switch (Operation)
            {
                case ReportedFileOperation.ZwCreateFile:
                case ReportedFileOperation.ZwOpenFile:
                case ReportedFileOperation.NtCreateFile:
                case ReportedFileOperation.CreateFile:
                case ReportedFileOperation.Unknown:
                {
                    sb.Append(Operation.ToString());
                    sb.Append("(..., ");
                    UInt32FlagsFormatter<DesiredAccess>.Append(sb, (uint)DesiredAccess);
                    sb.Append(", ");
                    UInt32FlagsFormatter<ShareMode>.Append(sb, (uint)ShareMode);
                    sb.Append(", , ");
                    UInt32EnumFormatter<CreationDisposition>.Append(sb, (uint)CreationDisposition);
                    sb.Append(", ");
                    UInt32FlagsFormatter<FlagsAndAttributes>.Append(sb, (uint)FlagsAndAttributes);
                    sb.Append(')');
                    break;
                }

                case ReportedFileOperation.CopyFileSource:
                {
                    sb.Append("CopyFile([Source], ...)");
                    break;
                }

                case ReportedFileOperation.CopyFileDestination:
                {
                    sb.Append("CopyFile(..., [Destination])");
                    break;
                }

                case ReportedFileOperation.CreateHardLinkSource:
                {
                    sb.Append("CreateHardLink(..., [ExistingFile)");
                    break;
                }

                case ReportedFileOperation.CreateHardLinkDestination:
                {
                    sb.Append("CreateHardLink([NewLink], ...)");
                    break;
                }

                case ReportedFileOperation.MoveFileSource:
                {
                    sb.Append("MoveFile([Source], ...)");
                    break;
                }

                case ReportedFileOperation.MoveFileDestination:
                {
                    sb.Append("MoveFile(..., [Destination])");
                    break;
                }

                case ReportedFileOperation.SetFileInformationByHandleSource:
                {
                    sb.Append("SetFileInformationByHandle([Source], ...)");
                    break;
                }

                case ReportedFileOperation.SetFileInformationByHandleDest:
                {
                    sb.Append("SetFileInformationByHandle(..., [Destination])");
                    break;
                }

                case ReportedFileOperation.ZwSetRenameInformationFileSource:
                {
                    sb.Append("ZwSetRenameInformationFile([Source], ...)");
                    break;
                }

                case ReportedFileOperation.ZwSetRenameInformationFileDest:
                {
                    sb.Append("ZwSetRenameInformationFile(..., [Destination])");
                    break;
                }

                case ReportedFileOperation.ZwSetFileNameInformationFileSource:
                {
                    sb.Append("ZwSetFileNameInformationFile([Source], ...)");
                    break;
                }

                case ReportedFileOperation.ZwSetFileNameInformationFileDest:
                {
                    sb.Append("ZwSetFileNameInformationFile(..., [Destination])");
                    break;
                }

                case ReportedFileOperation.MoveFileWithProgressSource:
                {
                    sb.Append("MoveFileWithProgress([Source]...)");
                    break;
                }

                case ReportedFileOperation.MoveFileWithProgressDest:
                {
                    sb.Append("MoveFileWithProgress([Dest]...)");
                    break;
                }

                case ReportedFileOperation.FindFirstFileEx:
                {
                    sb.Append("FindFirstFileEx(...)");
                    if (RequestedAccess == RequestedAccess.Enumerate)
                    {
                        sb.Append(", ");
                        sb.Append("Enumerate Pattern:" + EnumeratePattern);
                    }

                    break;
                }

                case ReportedFileOperation.NtQueryDirectoryFile:
                case ReportedFileOperation.ZwQueryDirectoryFile:
                {
                    sb.Append(Operation.ToString());
                    sb.Append("(...)");
                    if (RequestedAccess == RequestedAccess.Enumerate)
                    {
                        sb.Append(", ");
                        sb.Append("Enumerate Pattern:" + EnumeratePattern);
                    }

                    break;
                }

                default:
                {
                    sb.Append(Enum.GetName(typeof(ReportedFileOperation), Operation)).Append("(...)");
                    break;
                }
            }

            if (Error != 0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, " => (0x{0:X8}) ", Error);
                sb.Append(NativeWin32Exception.GetFormattedMessageForNativeErrorCode(unchecked((int)Error)));
            }

            if (Usn != NoUsn)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, " (USN 0x{0:X8}) ", Usn);
            }

            // If the status was Denied, don't include it in the description,
            // because an access that was denied by manifest may have been
            // allowed in practice (no failure injection), and the message
            // would be confusing.
            if (Status != FileAccessStatus.Denied)
            {
                // Other modes are interesting and should be logged
                sb.Append(" => ");
                sb.Append(Status);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Gets the fully expanded path
        /// </summary>
        public string GetPath(PathTable pathTable)
        {
            return Path ?? ManifestPath.ToString(pathTable);
        }

        /// <summary>
        /// Whether this access represents a directory creation
        /// </summary>
        public bool IsDirectoryCreation() => 
            Operation == ReportedFileOperation.CreateDirectory;

        /// <summary>
        /// Whether this access represents a directory creation, and the directory was effectively created
        /// </summary>
        public bool IsDirectoryEffectivelyCreated() => IsDirectoryCreation() && Error == 0;

        /// <summary>
        /// Whether this access represents a directory removal, and the directory was effectively removed
        /// </summary>
        public bool IsDirectoryEffectivelyRemoved() => IsDirectoryRemoval() && Error == 0;

        /// <summary>
        /// Whether this access represents a directory removal
        /// </summary>
        public bool IsDirectoryRemoval() => 
            Operation == ReportedFileOperation.RemoveDirectory;

        /// <summary>
        /// Whether this access represents a directory creation or removal
        /// </summary>
        public bool IsDirectoryCreationOrRemoval() => IsDirectoryCreation() || IsDirectoryRemoval();

        /// <summary>
        /// Indicate whether the file handle that was opened for this operation was a directory using it's file attributes.
        /// </summary>
        /// <remarks>
        /// CODESYNC: Public\Src\Sandbox\Windows\DetoursServices\DetouredFunctions.cpp, IsDirectoryFromAttributes
        ///
        /// Computing whether or not a directory reparse point should be treated as a file or a directory can be expensive.
        /// Thus, the caller can provide a delegate to do this computation, and it will only be called if the file is a directory
        /// and has a reparse point attribute.
        /// </remarks>
        public bool IsOpenedHandleDirectory(Func<bool> treatDirectoryReparsePointAsFile) =>
            OpenedFileOrDirectoryAttributes != (FlagsAndAttributes)FlagsAndAttributesConstants.InvalidFileAttributes
            && OpenedFileOrDirectoryAttributes.HasFlag(FlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY)
            && (!OpenedFileOrDirectoryAttributes.HasFlag(FlagsAndAttributes.FILE_ATTRIBUTE_REPARSE_POINT) || !treatDirectoryReparsePointAsFile());

        /// <summary>
        /// Creates an instance from an absolute path.
        /// </summary>
        public static ReportedFileAccess Create(
            ReportedFileOperation operation,
            ReportedProcess process,
            RequestedAccess requestedAccess,
            FileAccessStatus status,
            bool explicitlyReported,
            uint error,
            uint rawError,
            Usn usn,
            DesiredAccess desiredAccess,
            ShareMode shareMode,
            CreationDisposition creationDisposition,
            FlagsAndAttributes flagsAndAttributes,
            AbsolutePath path,
            string? enumeratePattern = null)
        {
            return new ReportedFileAccess(
                operation,
                process,
                requestedAccess,
                status,
                explicitlyReported,
                error,
                rawError,
                usn,
                desiredAccess,
                shareMode,
                creationDisposition,
                flagsAndAttributes,
                path,
                null,
                enumeratePattern);
        }

        /// <summary>
        /// Creates an instance from a full path, trying to look up a matching absolute path from the path table
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1726:UsePreferredTerms", MessageId = "flags")]
        public static ReportedFileAccess Create(
            ReportedFileOperation operation,
            ReportedProcess process,
            RequestedAccess requestedAccess,
            FileAccessStatus status,
            bool explicitlyReported,
            uint error,
            uint rawError,
            Usn usn,
            DesiredAccess desiredAccess,
            ShareMode shareMode,
            CreationDisposition creationDisposition,
            FlagsAndAttributes flagsAndAttributes,
            PathTable pathTable,
            string path,
            string? enumeratePattern = null)
        {
            if (AbsolutePath.TryGet(pathTable, (StringSegment)path, out AbsolutePath absolutePath))
            {
                return new ReportedFileAccess(
                    operation,
                    process,
                    requestedAccess,
                    status,
                    explicitlyReported,
                    error,
                    rawError,
                    usn,
                    desiredAccess,
                    shareMode,
                    creationDisposition,
                    flagsAndAttributes,
                    absolutePath,
                    null,
                    enumeratePattern);
            }

            return new ReportedFileAccess(
                operation,
                process,
                requestedAccess,
                status,
                explicitlyReported,
                error,
                rawError,
                usn,
                desiredAccess,
                shareMode,
                creationDisposition,
                flagsAndAttributes,
                AbsolutePath.Invalid,
                path,
                enumeratePattern);
        }

        /// <summary>
        /// Creates an instance from a full path, trying to look up a matching absolute path from the path table
        /// </summary>
        [SuppressMessage("Microsoft.Naming", "CA1726:UsePreferredTerms", MessageId = "flags")]
        public static ReportedFileAccess Create(
            ReportedFileOperation operation,
            ReportedProcess process,
            RequestedAccess requestedAccess,
            FileAccessStatus status,
            bool explicitlyReported,
            uint error,
            uint rawError,
            Usn usn,
            DesiredAccess desiredAccess,
            ShareMode shareMode,
            CreationDisposition creationDisposition,
            FlagsAndAttributes flagsAndAttributes,
            FlagsAndAttributes openedFileOrDirectoryAttribute,
            PathTable pathTable,
            string path,
            string? enumeratePattern = null)
        {
            if (AbsolutePath.TryGet(pathTable, (StringSegment)path, out AbsolutePath absolutePath))
            {
                return new ReportedFileAccess(
                    operation,
                    process,
                    requestedAccess,
                    status,
                    explicitlyReported,
                    error,
                    rawError,
                    usn,
                    desiredAccess,
                    shareMode,
                    creationDisposition,
                    flagsAndAttributes,
                    absolutePath,
                    null,
                    enumeratePattern,
                    openedFileOrDirectoryAttribute);
            }

            return new ReportedFileAccess(
                operation,
                process,
                requestedAccess,
                status,
                explicitlyReported,
                error,
                rawError,
                usn,
                desiredAccess,
                shareMode,
                creationDisposition,
                flagsAndAttributes,
                AbsolutePath.Invalid,
                path,
                enumeratePattern,
                openedFileOrDirectoryAttribute);
        }

        /// <nodoc />
        public void Serialize(
            BuildXLWriter writer,
            Dictionary<ReportedProcess, int>? processMap,
            Action<BuildXLWriter, AbsolutePath>? writePath)
        {
            writer.Write((byte)Operation);

            if (processMap is not null && processMap.TryGetValue(Process, out int index))
            {
                writer.WriteCompact(index);
            }
            else
            {
                Process.Serialize(writer);
            }

            writer.WriteCompact((int)RequestedAccess);
            writer.WriteCompact((int)Status);
            writer.Write(ExplicitlyReported);
            writer.Write(Error);
            writer.Write(RawError);
            writer.Write(Usn.Value);
            writer.Write((uint)DesiredAccess);
            writer.Write((uint)ShareMode);
            writer.Write((uint)CreationDisposition);
            writer.Write((uint)FlagsAndAttributes);
            writer.Write((uint)OpenedFileOrDirectoryAttributes);

            if (writePath is not null)
            {
                writePath(writer, ManifestPath);
            }
            else
            {
                writer.Write(ManifestPath);
            }

            writer.WriteNullableString(Path);
            writer.WriteNullableString(EnumeratePattern);
            writer.Write((byte)Method);
        }

        /// <nodoc />
        public static ReportedFileAccess Deserialize(
            BuildXLReader reader, 
            IReadOnlyList<ReportedProcess>? processes, 
            Func<BuildXLReader, AbsolutePath>? readPath)
        {
            return new ReportedFileAccess(
                operation: (ReportedFileOperation)reader.ReadByte(),
                process: processes is not null ? processes[reader.ReadInt32Compact()] : ReportedProcess.Deserialize(reader),
                requestedAccess: (RequestedAccess)reader.ReadInt32Compact(),
                status: (FileAccessStatus)reader.ReadInt32Compact(),
                explicitlyReported: reader.ReadBoolean(),
                error: reader.ReadUInt32(),
                rawError: reader.ReadUInt32(),
                // In general if process is executed externally, e.g., in VM, the obtained USN cannot be translated to the host.
                // However, for our low-privilege build, we are going to map the host volumes to the VM, and thus the USN
                // can still be used.
                usn: new Usn(reader.ReadUInt64()),
                desiredAccess: (DesiredAccess)reader.ReadUInt32(),
                shareMode: (ShareMode)reader.ReadUInt32(),
                creationDisposition: (CreationDisposition)reader.ReadUInt32(),
                flagsAndAttributes: (FlagsAndAttributes)reader.ReadUInt32(),
                openedFileOrDirectoryAttribute: (FlagsAndAttributes)reader.ReadUInt32(),
                manifestPath: readPath is not null ? readPath(reader) : reader.ReadAbsolutePath(),
                path: reader.ReadNullableString(),
                enumeratePattern: reader.ReadNullableString(),
                fileAccessStatusMethod: (FileAccessStatusMethod)reader.ReadByte());
        }

        /// <inherit />
        public override bool Equals(object? obj)
        {
            return StructUtilities.Equals(this, obj);
        }

        /// <inherit />
        public override int GetHashCode()
        {
            unchecked
            {
                return HashCodeHelper.Combine(new int[] {
                    string.IsNullOrEmpty(Path) ? ManifestPath.GetHashCode() : OperatingSystemHelper.PathComparer.GetHashCode(Path),
                    string.IsNullOrEmpty(EnumeratePattern) ? 0 : OperatingSystemHelper.PathComparer.GetHashCode(EnumeratePattern),
                    Process is not null ? (int)Process.ProcessId : 0,
                    (int)RequestedAccess,
                    (int)Status,
                    (int)Error,
                    // Do not include RawError, because its value can be non-deterministic.
                    Usn.GetHashCode(),
                    (int)DesiredAccess,
                    (int)ShareMode,
                    (int)CreationDisposition,
                    (int)FlagsAndAttributes,
                    (int)OpenedFileOrDirectoryAttributes
                });
            }
        }

        /// <summary>
        /// Checks whether two file access violations are the same.
        /// </summary>
        public static bool operator ==(ReportedFileAccess left, ReportedFileAccess right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Checks whether two file access violations are different.
        /// </summary>
        public static bool operator !=(ReportedFileAccess left, ReportedFileAccess right)
        {
            return !left.Equals(right);
        }

        /// <summary>
        /// Gets the name of enums by value.
        /// </summary>
        private static class UInt32EnumFormatter<TEnum>
        {
            public static void Append(StringBuilder sb, uint value)
            {
                Contract.Assume(typeof(TEnum).GetTypeInfo().IsEnum);
                Contract.Assume(typeof(TEnum).GetTypeInfo().GetEnumUnderlyingType() == typeof(uint));
                sb.Append((TEnum)(object)value);
            }
        }

        /// <summary>
        /// Fast flags enum formatter that separates bits with '|'
        /// </summary>
        private static class UInt32FlagsFormatter<TEnum>
        {
            private static readonly string[] s_names = GetNames();

            [SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline")]
            static UInt32FlagsFormatter()
            {
                Contract.Assume(typeof(TEnum).GetTypeInfo().IsEnum);
                Contract.Assume(typeof(TEnum).GetTypeInfo().GetEnumUnderlyingType() == typeof(uint));
            }

            private static string[] GetNames()
            {
                var names = new string[32];
                int k = 1;
                for (int j = 0; j < 32; j++, k <<= 1)
                {
                    names[j] = unchecked(((TEnum)(object)(uint)k).ToString()!);
                }

                return names;
            }

            public static void Append(StringBuilder sb, uint value)
            {
                var i = unchecked((int)value);
                bool first = true;
                int k = 1;
                for (int j = 0; j < 32; j++, k <<= 1)
                {
                    if ((i & k) != 0)
                    {
                        if (first)
                        {
                            first = false;
                        }
                        else
                        {
                            sb.Append('|');
                        }

                        sb.Append(s_names[j]);
                    }
                }

                if (first)
                {
                    sb.Append(0);
                }
            }
        }
    }
}
