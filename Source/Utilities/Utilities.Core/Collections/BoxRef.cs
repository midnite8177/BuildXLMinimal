// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

#nullable disable // Generic collection

namespace BuildXL.Utilities.Collections
{
    /// <summary>
    /// Boxed mutable reference to a value.
    /// </summary>
    public sealed class BoxRef<T>
    {
        /// <summary>
        /// The value
        /// </summary>
        public T Value;

        /// <summary>
        /// Implicit operator for converting any values to <see cref="BoxRef{T}"/>.
        /// </summary>
        [SuppressMessage("Microsoft.Usage", "CA2225:OperatorOverloadsHaveNamedAlternates")]
        public static implicit operator BoxRef<T>(T value)
        {
            return new BoxRef<T>() { Value = value };
        }
    }
}
