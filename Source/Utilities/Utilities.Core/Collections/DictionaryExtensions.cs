// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BuildXL.Utilities.Collections
{
    /// <summary>
    /// Set of extension methods for <see cref="IDictionary{TKey,TValue}"/> interface.
    /// </summary>
    public static class DictionaryExtensions
    {
        /// <summary>
        /// Adds a range of values into the dictionary.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="IDictionary{TKey,TValue}.Add(TKey,TValue)"/> method, this one does not throw if a value is already presented in the dictionary.
        /// </remarks>
        public static IDictionary<TKey, TValue> AddRange<TKey, TValue>(this IDictionary<TKey, TValue> @this, IEnumerable<KeyValuePair<TKey, TValue>> other) where TKey : notnull
        {
            foreach (var kvp in other)
            {
                @this[kvp.Key] = kvp.Value;
            }

            return @this;
        }

        /// <summary>
        /// Adds a value to a dictionary.
        /// Returns true if the value was not presented in the dictionary, and false otherwise.
        /// </summary>
        public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> @this, TKey key, TValue value) where TKey : notnull
        {
            if (@this.ContainsKey(key))
            {
                return false;
            }

            @this.Add(key, value);
            return true;
        }
    }
}
