// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using static BuildXL.Interop.Libraries;
using static BuildXL.Interop.Unix.Constants;
using static BuildXL.Interop.Unix.IO;
using static BuildXL.Interop.Unix.Impl_Common;
using static BuildXL.Interop.Unix.Memory;
using static BuildXL.Interop.Unix.Process;
using static BuildXL.Interop.Unix.Processor;
using System.Reflection;

namespace BuildXL.Interop.Unix
{
    /// <summary>
    /// The IO class for Linux-specific operations
    /// </summary>
    internal static class Impl_Linux
    {
        /// <summary>
        /// Version of __fxstatat syscalls to use.
        /// </summary>
        private const int __Ver = 1;

        private const string ProcPath = "/proc";
        private const string ProcStatPath = "/stat";
        private const string ProcMemInfoPath = "/meminfo";
        private const string ProcStatusPath = "/status";
        private const string ProcIoPath = "/io";

        private static long TicksPerSecond;

        /// <summary>
        /// glibc 2.34 consolidated libpthread into the libc shared object.
        /// Use LibC for 2.34 and higher version.
        /// </summary>
        public static bool IsGLibC234OrGreater()
        {
            string libcVersionString = Marshal.PtrToStringAnsi(gnu_get_libc_version());
            var components = libcVersionString.Split('.');
            int majorVersion = Convert.ToInt32(components[0]);
            int minorVersion = Convert.ToInt32(components[1]);

            return majorVersion > 2 || (majorVersion == 2 && minorVersion >= 34);
        }

        /// <summary>
        /// Statx is supported starting from kernel 4.11 and library support was added in glibc 2.28.
        /// </summary>
        /// <remarks>
        /// Check https://man7.org/linux/man-pages/man2/statx.2.html for more details.
        /// 
        /// In the following check, besides checking for the major and minor version numbers of the OS, the statx support
        /// is confirmed by calling statx directly in <see cref="CheckIfStatXSupported"/>.
        /// </remarks>
        public static readonly bool SupportsStatx = Environment.OSVersion.Version is var version 
            && ((version.Major == 4 && version.Minor >= 11) || version.Major >= 5)
            && CheckIfStatXSupported();

        /// <summary>Convert a number of "jiffies", or ticks, to a TimeSpan.</summary>
        /// <param name="ticks">The number of ticks.</param>
        /// <returns>The equivalent TimeSpan.</returns>
        internal static TimeSpan TicksToTimeSpan(double ticks)
        {
            long ticksPerSecond = Volatile.Read(ref TicksPerSecond);
            if (ticksPerSecond == 0)
            {
                // Look up the number of ticks per second in the system's configuration, then use that to convert to a TimeSpan
                ticksPerSecond = sysconf((int)Sysconf_Flags._SC_CLK_TCK);
                Volatile.Write(ref TicksPerSecond, ticksPerSecond);
            }

            return TimeSpan.FromSeconds(ticks / (double)ticksPerSecond);
        }

        private static readonly Lazy<DriveInfo[]> s_sortedDrives
            = new Lazy<DriveInfo[]>(() => DriveInfo.GetDrives().OrderBy(di => di.Name).Reverse().ToArray());

        /// <summary>
        /// Linux specific implementation of <see cref="IO.GetFileSystemType"/>
        /// </summary>
        internal static int GetFileSystemType(SafeFileHandle fd, StringBuilder fsTypeName, long bufferSize)
        {
            var path = ToPath(fd);
            if (path == null)
            {
                return ERROR;
            }

            // DriveInfo is not accurate because it uses statfs, which can't distinguish ext2/ext3/ext4.
            // Instead of calling API, we read /proc/mounts file, sort the path based on length, find the closest match, then get the file system type.
            return Try(() =>
                {
                    var mounts = File.ReadAllLines("/proc/mounts");
                    var dirFsTypes = mounts
                        .Select(l => l.Split(' '))
                        .Select(s => (s[1], s[2])) // select mount point (s[1]) and file system type (s[2])
                        .OrderByDescending(i => i.Item1.Length);
                    var dirAndType = dirFsTypes.FirstOrDefault(d => path.StartsWith(d.Item1));
                    fsTypeName.Append(dirAndType.Item1 == null ? "UNKNOWN" : dirAndType.Item2);
                    return 0;
                },
                ERROR);
        }

        /// <summary>
        /// Linux specific implementation of <see cref="IO.StatFileDescriptor"/>
        /// </summary>
        public static int StatFileDescriptor(SafeFileHandle fd, ref StatBuffer statBuf)
        {
            return StatFile(ToInt(fd), string.Empty, followSymlink: false, ref statBuf);
        }

        /// <summary>
        /// Linux specific implementation of <see cref="IO.StatFile"/>
        /// </summary>
        internal static int StatFile(string path, bool followSymlink, ref StatBuffer statBuf)
        {
            Contract.Requires(!string.IsNullOrEmpty(path));
            return StatFile(AT_FDCWD, path, followSymlink, ref statBuf);
        }

        private static int StatFile(int fd, string path, bool followSymlink, ref StatBuffer statBuf)
        {
            // If statx is supported, we prefer it since it gives us back the file creation time as well
            if (SupportsStatx)
            {
                var buf = new statx_buf();
                int result = StatXFile(fd, path, followSymlink, ref buf);
                if (result != 0)
                {
                    return ERROR;
                }
                else
                {
                    Translate(buf, ref statBuf);
                    return 0;
                }
            }
            else
            {
                var buf = new stat_buf();
                int result = StatFile(fd, path, followSymlink, ref buf);
                if (result != 0)
                {
                    return ERROR;
                }
                else
                {
                    Translate(buf, ref statBuf);
                    return 0;
                }
            }
        }

        /// <summary>
        /// Checks if <code>statx</code> is supported by the underlying OS.
        /// </summary>
        /// <returns>True if supported; otherwise false.</returns>
        /// <remarks>
        /// The check is done by invoking <code>statx</code> on the executing assembly path.
        /// If the invocation throws <see cref="EntryPointNotFoundException"/>, then it means that
        /// glibc does not have <code>statx</code>.
        /// 
        /// An alternative to invoking <code>statx</code> is to spawn a process that calls <code>ldd --version</code>, and
        /// then parse its output. Yet another alternative is to call <code>gnu_get_libc_version()</code>.
        /// </remarks>
        private static bool CheckIfStatXSupported()
        {
            try
            {
                var buf = new statx_buf();
                StatXFile(AT_FDCWD, Assembly.GetExecutingAssembly().Location, false, ref buf);
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        /// <summary>
        /// pathname, dirfd, and flags to identify the target file in one of the following ways:
        ///
        /// An absolute pathname
        ///    If pathname begins with a slash, then it is an absolute pathname that identifies the target file.  In this
        ///    case, dirfd is ignored.
        ///
        /// A relative pathname
        ///    If pathname is a string that begins with a character other than a slash and dirfd is AT_FDCWD, then
        ///    pathname is a relative pathname that is interpreted relative to the process's current working directory.
        ///
        /// A directory-relative pathname
        ///    If  pathname  is  a  string that begins with a character other than a slash and dirfd is a file descriptor
        ///    that refers to a directory, then pathname is a relative pathname that is interpreted relative to the
        ///    directory referred to by dirfd.
        /// </summary>
        private static int StatFile(int dirfd, string pathname, bool followSymlink, ref stat_buf buf)
        {
            Contract.Requires(pathname != null);

            int flags = 0
                | (!followSymlink                 ? AT_SYMLINK_NOFOLLOW : 0)
                | (string.IsNullOrEmpty(pathname) ? AT_EMPTY_PATH : 0);

            int result;
            while (
                (result = fstatat(__Ver, dirfd, pathname, ref buf, flags)) < 0 &&
                Marshal.GetLastWin32Error() == (int)Errno.EINTR);
            return result;
        }

        private static int StatXFile(int dirfd, string pathname, bool followSymlink, ref statx_buf buf)
        {
            Contract.Requires(pathname != null);
            
            int flags = 0
                | (!followSymlink ? AT_SYMLINK_NOFOLLOW : 0)
                | (string.IsNullOrEmpty(pathname) ? AT_EMPTY_PATH : 0);

            int result;
            while (
                // We request all basic stats (returned by the regular stat) plus BTIME (birth time)
                (result = statx(dirfd, pathname, flags, STATX_BASIC_STATS | STATX_BTIME, ref buf)) < 0 &&
                Marshal.GetLastWin32Error() == (int)Errno.EINTR);
            return result;
        }

        /// <summary>
        /// Linux specific implementation of <see cref="IO.SafeReadLink"/>
        /// </summary>
        internal static long SafeReadLink(string link, StringBuilder buffer, long bufferSize)
        {
            var resultLength = readlink(link, buffer, bufferSize);
            if (resultLength < 0) return ERROR;
            buffer.Length = (int)resultLength;
            return resultLength;
        }

        /// <summary>
        /// Linux specific implementation of <see cref="IO.Open"/>
        /// </summary>
        internal static SafeFileHandle Open(string pathname, OpenFlags flags, FilePermissions permissions)
        {
            int result;
            while (
                (result = open(pathname, Translate(flags), permissions)) < 0 &&
                Marshal.GetLastWin32Error() == (int)Errno.EINTR);
            return new SafeFileHandle(new IntPtr(result), ownsHandle: true);
        }

        /// <summary>
        /// Resolves all symlinks within a path including intermediate paths using the realpath syscall.
        /// </summary>
        /// <param name="path">Path to resolve</param>
        /// <param name="stringBuilder">String builder to store result. String builder *must* be initialized with a fixed size (usually max path).</param>
        /// <returns>0 if successful or errno if a failure occurs.</returns>
        internal static int RealPath(string path, StringBuilder stringBuilder)
        {
            Contract.Requires(!string.IsNullOrEmpty(path));
            Contract.Requires(stringBuilder != null);

            var error = 0;
            var resolvedPathPtr = realpath(path, stringBuilder);

            // realpath returns a pointer to the resolved path set in the stringBuilder if successful
            if (resolvedPathPtr == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
            }

            return error;
        }

        /// <summary>
        /// Linux specific implementation of <see cref="IO.GetFilePermissionsForFilePath"/>
        /// </summary>
        internal static int GetFilePermissionsForFilePath(string path, bool followSymlink)
        {
            var stat = new stat_buf();
            int errorCode = StatFile(AT_FDCWD, path, followSymlink, ref stat);
            return errorCode == 0 ? (int)stat.st_mode : ERROR;
        }

        /// <summary>
        /// Linux specific implementation of <see cref="IO.SetFilePermissionsForFilePath"/>
        /// </summary>
        internal static int SetFilePermissionsForFilePath(string path, FilePermissions permissions, bool followSymlink)
        {
            if (!followSymlink && IsSymlink(path))
            {
                // Permissions do not apply to symlinks on Linux systems (only on BSD systems).
                return 0;
            }

            return chmod(path, permissions);
        }

        /// <summary>
        /// Linux specific implementation of <see cref="IO.SetTimeStampsForFilePath"/>
        /// </summary>
        /// <remarks>
        /// Only atime and mtime are settable
        /// </remarks>
        internal static int SetTimeStampsForFilePath(string path, bool followSymlink, StatBuffer buf)
        {
            int flags = followSymlink ? 0 : AT_SYMLINK_NOFOLLOW;
            var atime = new Timespec { Tv_sec = buf.TimeLastAccess,       Tv_nsec = buf.TimeNSecLastAccess };
            var mtime = new Timespec { Tv_sec = buf.TimeLastModification, Tv_nsec = buf.TimeNSecLastModification };
            return utimensat(AT_FDCWD, path, new[] { atime, mtime }, flags);
        }

        /// <summary>
        /// Linux specific implementation of remove directory
        /// </summary>
        /// <remarks>
        /// The managed implementation probes the path before attempting deletion
        /// </remarks>
        internal static int DeleteDirectory(string path)
        {
            return rmdir(path);
        }

        private static ulong ExtractValueFromProcLine(string line)
        {
            return line != null && ulong.TryParse(line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[1], out var val) ? val : 0;
        }

        /// <summary>
        /// Linux specific implementation of <see cref="Memory.GetRamUsageInfo"/> \
        /// </summary>
        internal static int GetRamUsageInfo(ref RamUsageInfo buffer)
        {
            try
            {
                string[] lines = System.IO.File.ReadAllLines($"{ProcPath}{ProcMemInfoPath}");
                string memTotalLine = lines.FirstOrDefault(line => line.StartsWith("MemTotal:"));
                string memAvailableLine = lines.FirstOrDefault(line => line.StartsWith("MemAvailable:"));
                buffer.TotalBytes = ExtractValueFromProcLine(memTotalLine) * 1024;
                buffer.FreeBytes = ExtractValueFromProcLine(memAvailableLine) * 1024;
                return 0;
            }
            #pragma warning disable
            catch (Exception)
            {
                return ERROR;
            }
            #pragma warning restore
        }

        /// <summary>
        /// Retrieve the (immediate) child processes of the given process
        /// </summary>
        internal static IEnumerable<int> GetChildProcesses(int processId)
        {
            IEnumerable<int> childPids = Enumerable.Empty<int>();
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById(processId);
                foreach (ProcessThread thread in proc.Threads)
                {
                    var contents = File.ReadAllText($"{ProcPath}/{proc.Id}/task/{thread.Id}/children");
                    var ids = contents.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(pid => Int32.Parse(pid));
                    childPids = childPids.Concat(ids);
                }
            }
#pragma warning disable
            catch (Exception) { }
#pragma warning restore

            return childPids;
        }

        /// <summary>
        /// Gets resource consumption data for a specific process, throws if the underlying ProcFS structures are not present or malformed.
        /// </summary>
        private static ProcessResourceUsage? CreateProcessResourceUsageForPid(int pid)
        {
            try
            {
                var firstLine = File.ReadAllLines($"{ProcPath}/{pid}/{ProcStatPath}").FirstOrDefault();
                var splits = firstLine.Split(new[] { ' ' }).ToArray();
                var utimeMs = ((ulong)TicksToTimeSpan(double.Parse(splits[13])).TotalMilliseconds);
                var stimeMs = ((ulong)TicksToTimeSpan(double.Parse(splits[14])).TotalMilliseconds);

                string[] lines = System.IO.File.ReadAllLines($"{ProcPath}/{pid}/{ProcIoPath}");
                string readOps = lines.FirstOrDefault(line => line.StartsWith("syscr:"));
                string bytesRead = lines.FirstOrDefault(line => line.StartsWith("read_bytes:"));
                string writeOps = lines.FirstOrDefault(line => line.StartsWith("syscw:"));
                string bytesWritten = lines.FirstOrDefault(line => line.StartsWith("write_bytes:"));

                lines = System.IO.File.ReadAllLines($"{ProcPath}/{pid}/{ProcStatusPath}");
                string workingSetSize = lines.FirstOrDefault(line => line.StartsWith("VmRSS:"));
                string peakWorkingSetSize = lines.FirstOrDefault(line => line.StartsWith("VmHWM:"));
                string name = lines.FirstOrDefault(line => line.StartsWith("Name:"));

                return new ProcessResourceUsage()
                {
                    UserTimeMs = utimeMs,
                    SystemTimeMs = stimeMs,
                    DiskReadOps = ExtractValueFromProcLine(readOps),
                    DiskBytesRead = ExtractValueFromProcLine(bytesRead),
                    DiskWriteOps = ExtractValueFromProcLine(writeOps),
                    DiskBytesWritten = ExtractValueFromProcLine(bytesWritten),
                    WorkingSetSize = ExtractValueFromProcLine(workingSetSize) * 1024,
                    PeakWorkingSetSize = ExtractValueFromProcLine(peakWorkingSetSize) * 1024,
                    ProcessId = pid,
                    Name = name.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries)[1],
                };
            }
#pragma warning disable
            catch (Exception)
            {
                return null;
            }
#pragma warning restore
        }

        internal static IEnumerable<ProcessResourceUsage?> GetResourceUsageForProcessTree(int processId, bool includeChildren)
        {
            var stack = new Stack<int>();
            stack.Push(processId);

            while (stack.Any())
            {
                var next = stack.Pop();

                var resourceUsage = CreateProcessResourceUsageForPid(next);
                if (includeChildren)
                {
                    var children = GetChildProcesses(next);
                    foreach (var child in children)
                    {
                        stack.Push(child);
                    }
                }

                yield return resourceUsage;
            }

            yield break;
        }

        internal static int GetProcessMemoryUsageSnapshot(int pid, ref ProcessResourceUsage buffer, long bufferSize, bool includeChildProcesses)
        {
            var resourceUsage = GetResourceUsageForProcessTree(pid, includeChildProcesses);
            buffer.WorkingSetSize = resourceUsage.Where(u => u.HasValue).Aggregate(0UL, (acc, usage) => acc + usage.Value.WorkingSetSize);
            buffer.PeakWorkingSetSize = resourceUsage.Where(u => u.HasValue).Aggregate(0UL, (acc, usage) => acc + usage.Value.PeakWorkingSetSize);

            return 0;
        }

        /// <summary>
        /// Linux specific implementation of <see cref="Memory.GetMemoryPressureLevel"/>
        /// </summary>
        internal static int GetMemoryPressureLevel(ref PressureLevel level)
        {
            // there is no memory pressure level on Linux
            level = PressureLevel.Normal;
            return 0;
        }

        /// <summary>
        /// Linux specific implementation of <see cref="Processor.GetCpuLoadInfo"/>
        /// </summary>
        internal static int GetCpuLoadInfo(ref CpuLoadInfo buffer, long bufferSize)
        {
            try
            {
                var firstLine = File.ReadAllLines($"{ProcPath}{ProcStatPath}").FirstOrDefault();
                if (string.IsNullOrEmpty(firstLine)) return ERROR;
                var splits = firstLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

                return ulong.TryParse(splits[1], out buffer.UserTime) &&
                       ulong.TryParse(splits[3], out buffer.SystemTime) &&
                       ulong.TryParse(splits[4], out buffer.IdleTime)
                     ? 0
                     : ERROR;
            }
            #pragma warning disable
            catch (Exception)
            {
                return ERROR;
            }
            #pragma warning restore
        }

        // CODESYNC: NormalizeAndHashPath in StringOperations.cpp
        // TODO: there is no reason for this hash computation to be done in native StringOperations.cpp
        private const uint Fnv1Prime32 = 16777619;
        private const uint Fnv1Basis32 = 2166136261;
        private static uint _Fold(uint hash, byte value)
        {
            unchecked { return (hash * Fnv1Prime32) ^ (uint)value; }
        }
        private static uint Fold(uint hash, uint value)
        {
            unchecked { return _Fold(_Fold(hash, (byte)value), (byte)(((uint)value) >> 8)); }
        }

        internal static int NormalizePathAndReturnHash(byte[] pPath, byte[] normalizedPath)
        {
            Contract.Requires(pPath.Length == normalizedPath.Length);
            unchecked
            {
                uint hash = Fnv1Basis32;
                int i = 0;
                for (; i < pPath.Length && pPath[i] != 0; i++)
                {
                    normalizedPath[i] = pPath[i];
                    hash = Fold(hash, normalizedPath[i]);
                }

                Contract.Assert(i < normalizedPath.Length);
                normalizedPath[i] = 0;
                return (int)hash;
            }
        }

        internal static string GetMountNameForPath(string path)
        {
            return s_sortedDrives.Value.FirstOrDefault(di => path.StartsWith(di.Name))?.Name;
        }

        private static bool IsSymlink(string path)
        {
            var buf = new stat_buf();
            return
                fstatat(__Ver, AT_FDCWD, path, ref buf, AT_SYMLINK_NOFOLLOW) == 0 &&
                (buf.st_mode & (uint)FilePermissions.S_IFMT) == (uint)FilePermissions.S_IFLNK;
        }

        private static string ToPath(SafeFileHandle fd)
        {
            var path = new StringBuilder(MaxPathLength);
            return SafeReadLink($"{ProcPath}/self/fd/{ToInt(fd)}", path, path.Capacity) >= 0
                ? path.ToString()
                : null;
        }

        private static O_Flags Translate(OpenFlags flags)
        {
            return Enum
                .GetValues(typeof(OpenFlags))
                .Cast<OpenFlags>()
                .Where(f => flags.HasFlag(f))
                .Aggregate(O_Flags.O_NONE, (acc, f) => acc | TranslateOne(f));
        }

        private static O_Flags TranslateOne(OpenFlags flag)
        {
            return flag switch
            {
                OpenFlags.O_RDONLY   => O_Flags.O_RDONLY,
                OpenFlags.O_WRONLY   => O_Flags.O_WRONLY,
                OpenFlags.O_RDWR     => O_Flags.O_RDWR,
                OpenFlags.O_NONBLOCK => O_Flags.O_NONBLOCK,
                OpenFlags.O_APPEND   => O_Flags.O_APPEND,
                OpenFlags.O_CREAT    => O_Flags.O_CREAT,
                OpenFlags.O_TRUNC    => O_Flags.O_TRUNC,
                OpenFlags.O_EXCL     => O_Flags.O_EXCL,
                OpenFlags.O_NOFOLLOW => O_Flags.O_NOFOLLOW | O_Flags.O_PATH,
                OpenFlags.O_SYMLINK  => O_Flags.O_NOFOLLOW | O_Flags.O_PATH,
                OpenFlags.O_CLOEXEC  => O_Flags.O_CLOEXEC,
                                   _ => O_Flags.O_NONE,
            };
        }

        private static uint Concat(params uint[] elems) => elems.Aggregate(0U, (a, e) => a*10+e);

        private static void Translate(stat_buf from, ref StatBuffer to)
        {
            to.DeviceID                 = (int)from.st_dev;
            to.InodeNumber              = from.st_ino;
            to.Mode                     = (ushort)from.st_mode;
            to.HardLinks                = (ushort)from.st_nlink;
            to.UserID                   = from.st_uid;
            to.GroupID                  = from.st_gid;
            to.Size                     = from.st_size;
            to.TimeLastAccess           = from.st_atime;
            to.TimeLastModification     = from.st_mtime;
            to.TimeLastStatusChange     = from.st_ctime;
            to.TimeCreation             = 0; // not available
            // even though EXT4 supports nanosecond precision, the kernel time is not
            // necessarily getting updated every 1ns so nsec values can still be quantized
            to.TimeNSecLastAccess       = from.st_atime_nsec;
            to.TimeNSecLastModification = from.st_mtime_nsec;
            to.TimeNSecLastStatusChange = from.st_ctime_nsec;
            to.TimeNSecCreation         = 0; // not available
        }

        private static void Translate(statx_buf from, ref StatBuffer to)
        {
            // statx return the device id already decomposed into major/minor, so let's put it back
            // together so the result is compatible with the regular stat buffer
            to.DeviceID                 = (int)makedev(from.stx_dev_major, from.stx_dev_minor);
            to.InodeNumber              = from.stx_ino;
            to.Mode                     = (ushort)from.stx_mode;
            to.HardLinks                = (ushort)from.stx_nlink;
            to.UserID                   = from.stx_uid;
            to.GroupID                  = from.stx_gid;
            to.Size                     = (long)from.stx_size;
            to.TimeLastAccess           = from.stx_atime.tv_sec;
            to.TimeLastModification     = from.stx_mtime.tv_sec;
            to.TimeLastStatusChange     = from.stx_ctime.tv_sec;
            to.TimeCreation             = from.stx_btime.tv_sec; 
            // even though EXT4 supports nanosecond precision, the kernel time is not
            // necessarily getting updated every 1ns so nsec values can still be quantized
            to.TimeNSecLastAccess       = from.stx_atime.tv_nsec;
            to.TimeNSecLastModification = from.stx_mtime.tv_nsec;
            to.TimeNSecLastStatusChange = from.stx_ctime.tv_nsec;
            to.TimeNSecCreation         = from.stx_btime.tv_nsec;
        }

        private static T Try<T>(Func<T> action, T errorValue)
        {
            try
            {
                return action();
            }
            #pragma warning disable ERP022 // Unobserved exception in generic exception handler
            catch
            {
                return errorValue;
            }
            #pragma warning restore ERP022 // Unobserved exception in generic exception handler
        }

        #region P-invoke consts, structs, and other type definitions
        #pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

        private const int AT_FDCWD              = -100;
        private const int AT_SYMLINK_NOFOLLOW   = 0x100;
        private const int AT_EMPTY_PATH         = 0x1000;

        private const uint STATX_BASIC_STATS    = 0x000007ff;
        private const uint STATX_BTIME          = 0x00000800U;

        /// <summary>
        /// struct stat from stat.h
        /// </summary>
        /// <remarks>
        /// IMPORTANT: the explicitly specified size of 256 must match the value of 'sizeof(struct stat)' in C
        /// </remarks>
        [StructLayout(LayoutKind.Sequential, Size = 144)]
        private struct stat_buf
        {
            public  UInt64   st_dev;     // device
            public  UInt64   st_ino;     // inode
            public  UInt64   st_nlink;   // number of hard links
            public  UInt32   st_mode;    // protection
            public  UInt32   st_uid;     // user ID of owner
            public  UInt32   st_gid;     // group ID of owner
            private UInt32   _padding;   // padding for structure alignment
            public  UInt64   st_rdev;    // device type (if inode device)
            public  Int64    st_size;    // total size, in bytes
            public  Int64    st_blksize; // blocksize for filesystem I/O
            public  Int64    st_blocks;  // number of blocks allocated
            public  Int64    st_atime;   // time of last access
            public  Int64    st_atime_nsec; // Timespec.tv_nsec partner to st_atime
            public  Int64    st_mtime;   // time of last modification
            public  Int64    st_mtime_nsec; // Timespec.tv_nsec partner to st_mtime
            public  Int64    st_ctime;   // time of last status change
            public  Int64    st_ctime_nsec; // Timespec.tv_nsec partner to st_ctime
            /* More spare space here for future expansion (controlled by explicitly specifying struct size) */
        }

        /// <summary>
        /// struct statx from stat.h
        /// </summary>
        /// <remarks>
        /// IMPORTANT: the explicitly specified size of 256 must match the value of 'sizeof(struct statx)' in C
        /// </remarks>
        [StructLayout(LayoutKind.Sequential, Size = 256)]
        private struct statx_buf
        {
            public UInt32           stx_mask;        /* Mask of bits indicating filled fields */
            public UInt32           stx_blksize;     /* Block size for filesystem I/O */
            public UInt64           stx_attributes;  /* Extra file attribute indicators */
            public UInt32           stx_nlink;       /* Number of hard links */
            public UInt32           stx_uid;         /* User ID of owner */
            public UInt32           stx_gid;         /* Group ID of owner */
            public UInt16           stx_mode;        /* File type and mode */
            private UInt16          padding;
            public UInt64           stx_ino;         /* Inode number */
            public UInt64           stx_size;        /* Total size in bytes */
            public UInt64           stx_blocks;      /* Number of 512B blocks allocated */
            public UInt64           stx_attributes_mask;        /* Mask to show what's supported in stx_attributes */
            public statx_timestamp  stx_atime;  /* Last access */
            public statx_timestamp  stx_btime;  /* Creation */
            public statx_timestamp  stx_ctime;  /* Last status change */
            public statx_timestamp  stx_mtime;  /* Last modification */

            /* If this file represents a device, then the next two fields contain the ID of the device */
            public UInt32           stx_rdev_major;  /* Major ID */
            public UInt32           stx_rdev_minor;  /* Minor ID */

            /* The next two fields contain the ID of the device containing the filesystem where the file resides */
            public UInt32           stx_dev_major;   /* Major ID */
            public UInt32           stx_dev_minor;   /* Minor ID */
            public UInt64           stx_mnt_id;      /* Mount ID */
            private UInt32          stx_dio_mem_align; /* Memory buffer alignment for direct I/O */
            private UInt32          stx_dio_offset_align; /* File offset alignment for direct I/O */
        };

        private struct statx_timestamp 
        {
            public Int64    tv_sec;    /* Seconds since the Epoch (UNIX time) */
            public UInt32   tv_nsec;   /* Nanoseconds since tv_sec */
            private Int32   _reserved;
        }

        /// <summary>
        /// Flags for <see cref="open"/>
        /// </summary>
        [Flags]
        public enum O_Flags : int
        {
            O_RDONLY    = 0,     // open for reading only
            O_NONE      = 0,
            O_WRONLY    = 1,     // open for writing only
            O_RDWR      = 2,     // open for reading and writing
            O_CREAT     = 64,    // create file if it does not exist
            O_EXCL      = 128,   // error if O_CREAT and the file exists
            O_NOCCTY    = 256,
            O_TRUNC     = 512,   // truncate size to 0
            O_APPEND    = 1024,  // append on each write
            O_NONBLOCK  = 2048,  // do not block on open or for data to become available
            O_ASYNC     = 8192,
            O_DIRECT    = 16384,
            O_DIRECTORY = 65536,
            O_NOFOLLOW  = 131072,  // do not follow symlinks
            O_CLOEXEC   = 524288, // mark as close-on-exec
            O_SYNC      = 1052672,
            O_PATH      = 2097152, // allow open of symlinks
        }

        /// <summary>
        /// struct stat from stat.h
        /// </summary>
        /// <remarks>
        /// IMPORTANT: the explicitly specified size of 112 must match the value of 'sizeof(struct sysinfo)' in C
        /// </remarks>
        [StructLayout(LayoutKind.Sequential, Size = 112)]
        internal struct sysinfo_buf
        {
            public Int64 uptime;     /* Seconds since boot */
            public UInt64 load1, load2, load3; /* 1, 5, and 15 minute load averages */
            public UInt64 totalram;  /* Total usable main memory size */
            public UInt64 freeram;   /* Available memory size */
            public UInt64 sharedram; /* Amount of shared memory */
            public UInt64 bufferram; /* Memory used by buffers */
            public UInt64 totalswap; /* Total swap space size */
            public UInt64 freeswap;  /* Swap space still available */
            public UInt16 procs;    /* Number of current processes */
            public UInt64 totalhigh; /* Total high memory size */
            public UInt64 freehigh;  /* Available high memory size */
            public UInt32 mem_unit;   /* Memory unit size in bytes */
            /* Padding */
        };

        [Flags]
        internal enum Sysconf_Flags : int
        {
            _SC_CLK_TCK = 2,
        };

        #endregion

        #region P-invoke function definitions
        [DllImport(LibC, SetLastError = true)]
        private static extern int open(string pathname, O_Flags flags, FilePermissions permission);

        [DllImport(LibC, EntryPoint = "__fxstatat", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int fstatat(int __ver, int fd, string pathname, ref stat_buf buff, int flags);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int statx(int fd, string pathname, int flags, uint mask, ref statx_buf buff);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int rmdir(string pathname);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int utimensat(int dirfd, string pathname, Timespec[] times, int flags);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int sysinfo(ref sysinfo_buf buf);

        [DllImport(LibC, EntryPoint = "copy_file_range", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern long copyfilerange(int fd_in, IntPtr off_in, int fd_out, IntPtr off_out, long len, uint flags);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern long sendfile(int fd_out, int fd_in, IntPtr offset, long count);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int posix_fadvise(int fd, long offset, long len, int advice);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern long sysconf(int name);

        [DllImport(LibC, EntryPoint = "gnu_dev_makedev", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern ulong makedev(uint major, uint minor);

        [DllImport(LibC, SetLastError = true)]
        unsafe internal static extern int lsetxattr(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            void* value,
            ulong size,
            int flags);

        [DllImport(LibC, SetLastError = true)]
        internal static extern long lgetxattr(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            ref long value,
            ulong size,
            int flags);

        [DllImport(LibC, SetLastError = true)]
        internal static extern IntPtr realpath(string path, StringBuilder resolved_path);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_open")]
        public static extern IntPtr sem_open_libc([MarshalAs(UnmanagedType.LPStr)] string name, int oflag, int mode, uint value);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_wait")]
        public static extern int sem_wait_libc(IntPtr sem);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_timedwait")]
        public static extern int sem_timedwait_libc(IntPtr sem, Timespec abs_timeout);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_trywait")]
        public static extern int sem_trywait_libc(IntPtr sem);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_post")]
        public static extern int sem_post_libc(IntPtr sem);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_getvalue")]
        public static extern int sem_getvalue_libc(IntPtr sem, out int sval);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_close")]
        public static extern int sem_close_libc(IntPtr sem);

        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_unlink")]
        public static extern int sem_unlink_libc([MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(LibPthread, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_open")]
        public static extern IntPtr sem_open_libpthread([MarshalAs(UnmanagedType.LPStr)] string name, int oflag, int mode, uint value);

        [DllImport(LibPthread, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_wait")]
        public static extern int sem_wait_libpthread(IntPtr sem);

        [DllImport(LibPthread, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_timedwait")]
        public static extern int sem_timedwait_libpthread(IntPtr sem, Timespec abs_timeout);

        [DllImport(LibPthread, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_trywait")]
        public static extern int sem_trywait_libpthread(IntPtr sem);

        [DllImport(LibPthread, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_post")]
        public static extern int sem_post_libpthread(IntPtr sem);

        [DllImport(LibPthread, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_getvalue")]
        public static extern int sem_getvalue_libpthread(IntPtr sem, out int sval);

        [DllImport(LibPthread, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_close")]
        public static extern int sem_close_libpthread(IntPtr sem);

        [DllImport(LibPthread, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "sem_unlink")]
        public static extern int sem_unlink_libpthread([MarshalAs(UnmanagedType.LPStr)] string name);
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

        /// <summary>
        /// Get glibc version
        /// </summary>
        [DllImport(LibC, SetLastError = true, CharSet = CharSet.Ansi, EntryPoint = "gnu_get_libc_version")]
        public static extern IntPtr gnu_get_libc_version();

        #endregion
    }
}