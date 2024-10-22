// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.ContractsLight;

namespace BuildXL.Utilities.Core
{
    /// <summary>
    /// An equality comparer that can compare two string IDs a case-insensitive manner.
    /// </summary>
    internal sealed class CaseInsensitiveStringIdEqualityComparer : IEqualityComparer<StringId>
    {
        private readonly StringTable m_stringTable;

        public CaseInsensitiveStringIdEqualityComparer(StringTable stringTable)
        {
            Contract.RequiresNotNull(stringTable);

            m_stringTable = stringTable;
        }

        bool IEqualityComparer<StringId>.Equals(StringId x, StringId y)
        {
            return (!x.IsValid && !y.IsValid) || (x.IsValid && y.IsValid && m_stringTable.CaseInsensitiveEquals(x, y));
        }

        int IEqualityComparer<StringId>.GetHashCode(StringId obj)
        {
            return !obj.IsValid
                ? -4
                : m_stringTable.CaseInsensitiveGetHashCode(obj);
        }
    }

    /// <summary>
    /// A comparer that can compare two string IDs in a case-insensitive manner.
    /// </summary>
    public sealed class CaseInsensitiveStringIdComparer : IComparer<StringId>
    {
        private readonly StringTable m_stringTable;

        /// <summary>
        /// Constructor
        /// </summary>
        public CaseInsensitiveStringIdComparer(StringTable stringTable)
        {
            Contract.RequiresNotNull(stringTable);

            m_stringTable = stringTable;
        }

        /// <summary>
        /// Compare two string ids
        /// </summary>
        public int Compare(StringId x, StringId y)
        {
            return
                x == y ? 0 : //                                              ==> equal
                !x.IsValid && y.IsValid ? 2 : // first invalid second valid  ==> pick an order
                x.IsValid && !y.IsValid ? -2 : // first valid second invalid ==> pick the opposite order
                m_stringTable.CompareCaseInsensitive(x, y); // both valid    ==> delegate
        }
    }
}
