// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Threading;
using BuildXL.Utilities.Core;

#nullable disable // Disabling nullability for generic type

namespace BuildXL.Utilities.Collections
{
    /// <summary>
    /// Provides a cache which maps a key to a specified value.
    /// </summary>
    /// <remarks>
    ///  This class is thread safe.
    ///
    ///  Values are always evicted if a new entry's hash causes it
    /// to be placed in the slot for the incumbent value. However, values are stored in two slots so the chances of eviction of
    /// commonly used values is somewhat reduced.
    /// </remarks>
    /// <typeparam name="TKey">the key type</typeparam>
    /// <typeparam name="TValue">the value type</typeparam>
    public sealed class ObjectCache<TKey, TValue>
    {
        private struct Entry
        {
            /// <summary>
            /// 0 means not present
            /// </summary>
            public int ModifiedHashCode;
            public TKey Key;
            public TValue Value;
        }

        private readonly Entry[] m_slots;
        private readonly ReaderWriterLockSlim[] m_locks;
        private readonly IEqualityComparer<TKey> m_comparer;

        private long m_hits;
        private long m_misses;

        /// <summary>
        /// Gets the current number of cache hits
        /// </summary>
        public long Hits => Volatile.Read(ref m_hits);

        /// <summary>
        /// Gets the current number of cache misses
        /// </summary>
        public long Misses => Volatile.Read(ref m_misses);

        /// <summary>
        /// Gets the number of slots in the cache
        /// </summary>
        public int Capacity => m_slots.Length;

        /// <summary>
        /// Constructs a new lossy cache
        /// </summary>
        /// <param name="capacity">the capacity determining the number of slots available in the cache. For best results, this should be a prime number.</param>
        /// <param name="comparer">the equality comparer for computing hash codes and equality of keys</param>
        public ObjectCache(int capacity, IEqualityComparer<TKey> comparer = null)
        {
            Contract.Requires(capacity > 0);

            m_slots = new Entry[capacity];
            var locks = new ReaderWriterLockSlim[HashCodeHelper.GetGreaterOrEqualPrime(Math.Min(Environment.ProcessorCount * 4, capacity))];
            for (int i = 0; i < locks.Length; i++)
            {
                locks[i] = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            }

            m_locks = locks;
            m_comparer = comparer ?? EqualityComparer<TKey>.Default;
        }

        /// <summary>
        /// Attempts to retrieve the value for the specified key from the cache
        /// </summary>
        /// <param name="key">the key</param>
        /// <param name="value">the value</param>
        /// <returns>true if the value for the key exists in the cache, otherwise false</returns>
        public bool TryGetValue(TKey key, out TValue value)
        {
            // Look in the primary slot for the value
            GetEntry(key, out _, out int modifiedHashCode, out Entry entry);
            if (entry.ModifiedHashCode == modifiedHashCode)
            {
                var entryKey = entry.Key;
                if (m_comparer.Equals(entryKey, key))
                {
                    Interlocked.Increment(ref m_hits);
                    value = entry.Value;
                    return true;
                }
            }

            // Try the backup slot
            modifiedHashCode = HashCodeHelper.Combine(modifiedHashCode, 17);
            GetEntry(ref modifiedHashCode, out _, out entry);
            if (entry.ModifiedHashCode == modifiedHashCode)
            {
                var entryKey = entry.Key;
                if (m_comparer.Equals(entryKey, key))
                {
                    Interlocked.Increment(ref m_hits);
                    value = entry.Value;
                    return true;
                }
            }

            Interlocked.Increment(ref m_misses);
            value = default(TValue);
            return false;
        }

        /// <summary>
        /// Clears the object cache.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < m_slots.Length; i++)
            {
                m_slots[i] = new Entry();
            }
        }

        private void GetEntry(TKey key, out uint index, out int modifiedHashCode, out Entry entry)
        {
            modifiedHashCode = m_comparer.GetHashCode(key);

            GetEntry(ref modifiedHashCode, out index, out entry);
        }

        private void GetEntry(ref int modifiedHashCode, out uint index, out Entry entry)
        {
            // Zero is reserved hash code for unset entries
            if (modifiedHashCode == 0)
            {
                modifiedHashCode = int.MaxValue;
            }

            unchecked
            {
                index = (uint)modifiedHashCode % (uint)m_slots.Length;
                uint lockIndex = (uint)index % (uint)m_locks.Length;

                // Note: A global lock here gets a ton of contention (1.2% of all execution of BuildXL time is spent waiting here), so we use many locks
                // The note was made when we used BuildXL.Utilities.Threading.ReadWriteLock. The switch to ReaderWriterLockSlim is unlikely to invalidate the note.
                entry = default;
                bool checkLockAcquired = false;

                try
                {
                    m_locks[lockIndex].EnterReadLock();
                    entry = m_slots[index];
                }
                catch (Exception)
#pragma warning disable ERP022 // Unobserved exception in a generic exception handler
                {
                    // For some reason, an exception was thrown while trying to acquire the lock.
                    // This is unexpected, but we don't want to crash the process.

                    // Needs to check if lock is acquired or not before exiting read lock.
                    checkLockAcquired = true;
                }
#pragma warning restore ERP022 // Unobserved exception in a generic exception handler
                finally
                {
                    if (!checkLockAcquired || m_locks[lockIndex].IsReadLockHeld)
                    {
                        m_locks[lockIndex].ExitReadLock();
                    }
                }
            }
        }

        private void SetEntry(uint index, Entry entry)
        {
            uint lockIndex = (uint)index % (uint)m_locks.Length;
            bool lockAcquired = false;

            try
            {
                // Try to get a write lock, but do not wait for the lock to become available.
                lockAcquired = m_locks[lockIndex].TryEnterWriteLock(TimeSpan.Zero);

                // Only write if we successfully acquired the write lock.
                if (lockAcquired)
                {
                    m_slots[index] = entry;
                }
            }
            finally
            {
                if (lockAcquired)
                {
                    m_locks[lockIndex].ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// Adds an item to the cache
        /// </summary>
        /// <param name="key">the key</param>
        /// <param name="value">the value</param>
        /// <returns>true if the item was not found in the cache</returns>
        public bool AddItem(TKey key, TValue value)
        {
            int missCount = 0;

            // Place in the primary slot
            GetEntry(key, out uint index, out int modifiedHashCode, out Entry entry);
            if (entry.ModifiedHashCode != modifiedHashCode || !m_comparer.Equals(entry.Key, key))
            {
                entry = new Entry()
                {
                    ModifiedHashCode = modifiedHashCode,
                    Key = key,
                    Value = value,
                };

                SetEntry(index, entry);
                missCount++;
            }

            // Place in the backup slot as well
            modifiedHashCode = HashCodeHelper.Combine(modifiedHashCode, 17);
            GetEntry(ref modifiedHashCode, out index, out entry);
            if (entry.ModifiedHashCode != modifiedHashCode || !m_comparer.Equals(entry.Key, key))
            {
                entry = new Entry()
                {
                    ModifiedHashCode = modifiedHashCode,
                    Key = key,
                    Value = value,
                };

                SetEntry(index, entry);
                missCount++;
            }

            // value was missed on both slots so report not found by returning true
            return missCount == 2;
        }
    }
}
