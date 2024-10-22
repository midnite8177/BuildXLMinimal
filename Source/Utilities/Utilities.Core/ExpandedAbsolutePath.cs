// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using static BuildXL.Utilities.Core.HierarchicalNameTable;

namespace BuildXL.Utilities.Core
{
    /// <summary>
    /// Cached expanded path for absolute path for use in contexts where expanded path should be passed while
    /// ensuring its a valid absolute path or where avoiding repeated expansion of the absolute path is desirable.
    /// </summary>
    public readonly struct ExpandedAbsolutePath : IEquatable<ExpandedAbsolutePath>
    {
        /// <summary>
        /// An invalid expanded absolute path.
        /// </summary>
        public static readonly ExpandedAbsolutePath Invalid = default(ExpandedAbsolutePath);

        /// <summary>
        /// Determines whether an expanded absolute path is valid or not.
        /// </summary>
        public bool IsValid => Path.IsValid;

        /// <summary>
        /// Path of the token.
        /// </summary>
        public readonly AbsolutePath Path;

        /// <summary>
        /// The expanded path
        /// </summary>
        public readonly string ExpandedPath;

        /// <summary>
        /// Constructs an expanded absolute path.
        /// </summary>
        public ExpandedAbsolutePath(AbsolutePath path, PathTable pathTable, NameExpander nameExpander = null)
        {
            Contract.Requires(path.IsValid);
            Path = path;
            ExpandedPath = path.ToString(pathTable, nameExpander: nameExpander);
        }

        /// <summary>
        /// Constructs an expanded absolute path under the assumption that the expanded path is the result
        /// of expanding the given absolute path.
        /// </summary>
        public static ExpandedAbsolutePath CreateUnsafe(AbsolutePath path, string expandedPath)
        {
            Contract.Requires(path.IsValid);
            return new ExpandedAbsolutePath(path, expandedPath);
        }
        /// <summary>
        /// Constructs an expanded absolute path with the given file name
        /// </summary>
        private ExpandedAbsolutePath(AbsolutePath path, string expandedPath)
        {
            Path = path;
            ExpandedPath = expandedPath;
        }

        /// <summary>
        /// Creates an expanded path with file name matching the given file name casing. This is used to ensure expanded path file name
        /// casing which is different than that which is in the path table
        /// </summary>
        public ExpandedAbsolutePath WithFileName(PathTable pathTable, PathAtom fileNameAtom)
        {
            return WithTrailingRelativePath(pathTable, RelativePath.Create(fileNameAtom));
        }

        /// <summary>
        /// Creates an expanded path with trailing relative path matching the given relative path casing. This is used to ensure expanded path 
        /// casing which is different than that which is in the path table
        /// </summary>
        public ExpandedAbsolutePath WithTrailingRelativePath(PathTable pathTable, RelativePath relativePath)
        {
            var relativePathAsString = relativePath.ToString(pathTable.StringTable);
            if (!ExpandedPath.EndsWith(relativePathAsString, StringComparison.OrdinalIgnoreCase))
            {
                Contract.Assert(false, $"File path '{ExpandedPath}' should only differ by casing with respect to '{relativePathAsString}'");
            }

            if (ExpandedPath.EndsWith(relativePathAsString, StringComparison.Ordinal))
            {
                return this;
            }

            return new ExpandedAbsolutePath(Path, ExpandedPath.Remove(ExpandedPath.Length - relativePathAsString.Length) + relativePathAsString);
        }

        /// <summary>
        /// Returns the expanded path.
        /// </summary>
        public override string ToString()
        {
            return ExpandedPath;
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return ExpandedPath.GetHashCode();
        }

        /// <inheritdoc />
        public bool Equals(ExpandedAbsolutePath other)
        {
            return other.ExpandedPath == ExpandedPath;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return StructUtilities.Equals(this, obj);
        }

        /// <nodoc />
        public static bool operator ==(ExpandedAbsolutePath left, ExpandedAbsolutePath right)
        {
            return left.Equals(right);
        }

        /// <nodoc />
        public static bool operator !=(ExpandedAbsolutePath left, ExpandedAbsolutePath right)
        {
            return !left.Equals(right);
        }
    }
}
