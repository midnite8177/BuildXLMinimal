// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BuildXL.Utilities.Collections
{
#if !NET5_0_OR_GREATER
    /// <summary>
    /// Readonly view of a set.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1710:IdentifiersShouldHaveCorrectSuffix")]
    public interface IReadOnlySet<T> : IEnumerable<T>, IReadOnlyCollection<T>
    {
        /// <summary>
        /// Check whether given data exist in the set.
        /// </summary>
        bool Contains(T item);
    }
#endif

    /// <summary>
    /// Readonly representation of a set.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1710:IdentifiersShouldHaveCorrectSuffix")]
    public sealed class ReadOnlyHashSet<T> : HashSet<T>, IReadOnlySet<T>
    {
        /// <nodoc/>
        public ReadOnlyHashSet()
        {
        }

        /// <nodoc/>
        public ReadOnlyHashSet(IEqualityComparer<T> comparer)
            : base(comparer)
        {
        }

        /// <nodoc/>
        public ReadOnlyHashSet(IEnumerable<T> collection)
            : base(collection)
        {
        }

        /// <nodoc/>
        public ReadOnlyHashSet(IEnumerable<T> collection, IEqualityComparer<T> comparer)
            : base(collection, comparer)
        {
        }
    }
}
