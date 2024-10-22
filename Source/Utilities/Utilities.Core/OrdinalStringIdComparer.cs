// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.ContractsLight;

namespace BuildXL.Utilities.Core
{
    /// <summary>
    /// A comparer that can compare two string id's in a ordinal manor.
    /// </summary>
    internal sealed class OrdinalStringIdComparer : IComparer<StringId>
    {
        private readonly StringTable m_stringTable;

        /// <summary>
        /// Constructor
        /// </summary>
        public OrdinalStringIdComparer(StringTable stringTable)
        {
            Contract.RequiresNotNull(stringTable);

            m_stringTable = stringTable;
        }

        /// <inheritdoc />
        public int Compare(StringId x, StringId y)
        {
            return m_stringTable.CompareOrdinal(x, y);
        }
    }

    /// <summary>
    /// An equality-comparer that can compare two string id a case-sensitive manner.
    /// </summary>
    internal sealed class OrdinalStringIdEqualityComparer : IEqualityComparer<StringId>
    {
        bool IEqualityComparer<StringId>.Equals(StringId x, StringId y)
        {
            return x == y;
        }

        int IEqualityComparer<StringId>.GetHashCode(StringId obj)
        {
            return StringTable.GetHashCode(obj);
        }
    }
}
