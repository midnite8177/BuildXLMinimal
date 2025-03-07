// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Diagnostics.ContractsLight;
using System.Threading;
using System.Collections.Concurrent;

namespace BuildXL.Utilities.Core
{
    /// <summary>
    /// Thread-safe pool of reusable objects.
    /// </summary>
    /// <remarks>
    ///     <para>
    /// Object pools are used to improve performance for objects which would otherwise need to be created and collected
    /// at high rates.
    ///     </para>
    ///     <para>
    /// Pools are particularly valuable for collections which suffer from bad allocation patterns
    /// involving a lot of copying as a collection expands. For example, if code frequently needs to
    /// use temporary lists, and the lists end up having to frequently expand their payload repeatedly during
    /// their lifetime, that causes a lot of garbage and copying. With a pooled lists instead, the list will
    /// expand a few times as it is used, and then will stabilize. The same list storage will be used over and
    /// over again without ever needing to expand it again. Not only does this avoid garbage, it also helps
    /// the cache.
    ///     </para>
    /// </remarks>
    public sealed class ObjectPool<T> where T : class
    {
        // Number of times a creator was invoked.
        private long m_factoryCall;

        // Number of times an instance was obtained from the pool (the counter is incremented when regardless whether the was created or not).
        private long m_useCount;

        private readonly int m_size;

        // Storage for the pool objects. The first item is stored in a dedicated field because we
        // expect to be able to satisfy most requests from it.
        private T m_firstItem;
        private readonly ConcurrentStack<T> m_items;

        // Track the approximate length of the backing set separately since getting the length of the
        // ConcurrentStack will result in enumerating the stack. This is used to keep a rough target size
        // of the backing set. Accuracy is not important.
        private int m_stackLength;

        // creator is stored for the lifetime of the pool. We will call this only when pool needs to
        // expand. compared to "new T()", Func gives more flexibility to implementers and faster
        // than "new T()".
        private readonly Func<T> m_creator;

        /// <summary>
        /// Optional cleanup method that is called before putting cached object back to the pool.
        /// </summary>
        /// <remarks>
        /// Using <see cref="Func&lt;T,T&gt;"/> instead of <see cref="Action&lt;T&gt;"/> allows a clients of the
        /// <see cref="ObjectPool&lt;T&gt;"/> to disable pooling by returning new object in the cleanup method.
        /// <example>
        /// ObjectPool&lt;StringBuilder&gt; disabledPool = new ObjectPool&lt;StringBuilder&gt;(
        ///     creator: () => new StringBuilder(),
        ///     cleanup: sb => new StringBuilder());
        ///
        /// ObjectPool&lt;StringBuilder&gt; regularPool = new ObjectPool&lt;StringBuilder&gt;(
        ///     creator: () => new StringBuilder(),
        ///     cleanup: sb => sb.Clear());
        /// </example>
        /// </remarks>
        private readonly Func<T, T> m_cleanup;

        /// <summary>
        /// Creates a new object pool for a specific type of object.
        /// </summary>
        /// <param name="creator">A method to invoke in order to create object instances to insert into the pool.</param>
        /// <param name="cleanup">An optional method to invoke whenever an object is returned into the pool.</param>
        /// <remarks>
        /// The cleanup method is expected to return the object to a 'clean' state such that it can
        /// recycled into the pool and be handed out as a fresh instance. This method typically clears
        /// an object's state to make it look new for subsequent uses.
        /// </remarks>
        public ObjectPool(Func<T> creator, Action<T> cleanup)
            : this(creator, FromActionToFunc(cleanup), Environment.ProcessorCount * 4)
        { }

        /// <summary>
        /// Creates a new object pool for a specific type of object.
        /// </summary>
        /// <param name="creator">A method to invoke in order to create object instances to insert into the pool.</param>
        /// <param name="cleanup">An optional method to invoke whenever an object is returned into the pool.</param>
        /// <remarks>
        /// The cleanup method is expected to return the object to a 'clean' state such that it can
        /// recycled into the pool and be handed out as a fresh instance. This method typically clears
        /// an object's state to make it look new for subsequent uses.
        /// </remarks>
        public ObjectPool(Func<T> creator, Func<T, T> cleanup)
            : this(creator, cleanup, Environment.ProcessorCount * 4)
        { }

        /// <summary>
        /// Creates a new object pool for a specific type of object.
        /// </summary>
        /// <param name="creator">A method to invoke in order to create object instances to insert into the pool.</param>
        /// <param name="cleanup">An optional method to invoke whenever an object is returned into the pool.</param>
        /// <param name="size">A size of the pool.</param>
        /// <remarks>
        /// The cleanup method is expected to return the object to a 'clean' state such that it can
        /// recycled into the pool and be handed out as a fresh instance. This method typically clears
        /// an object's state to make it look new for subsequent uses.
        /// </remarks>
        public ObjectPool(Func<T> creator, Func<T, T> cleanup, int size)
        {
            Contract.Requires(creator != null);
            Contract.Requires(size >= 1);

            m_creator = creator;
            m_cleanup = cleanup;
            m_items = new ConcurrentStack<T>();
            m_size = size;
        }

        private T CreateInstance()
        {
            Interlocked.Increment(ref m_factoryCall);
            var inst = m_creator();
            return inst;
        }

        /// <summary>
        /// Gets an object instance from the pool.
        /// </summary>
        /// <returns>An object instance.</returns>
        /// <remarks>
        /// If the pool is empty, a new object instance is allocated. Otherwise, a previously used instance
        /// is returned.
        /// </remarks>
        public PooledObjectWrapper<T> GetInstance()
        {
            // Examine the first element. If that fails, AllocateSlow will look at the remaining elements.
            T inst = m_firstItem;
            if (inst == null || inst != Interlocked.CompareExchange(ref m_firstItem, null, inst))
            {
                inst = AllocateSlow();
            }
            else
            {
                // Got an element from the first element.
            }

            Interlocked.Increment(ref m_useCount);

            return new PooledObjectWrapper<T>(this, inst);
        }

        /// <summary>
        /// Clears the pool.
        /// </summary>
        public void Clear()
        {
            m_firstItem = default(T);
            m_items.Clear();
        }

        /// <summary>
        /// Gets the number of times an object has been obtained from this pool.
        /// </summary>
        public long UseCount => m_useCount;

        /// <summary>
        /// Gets the number of objects that are currently available in the pool.
        /// </summary>
        public int ObjectsInPool => (m_firstItem != null ? 1 : 0) + m_stackLength;

        /// <summary>
        /// Gets the number of times a factory method was called.
        /// </summary>
        public long FactoryCalls => m_factoryCall;

        private T AllocateSlow()
        {
            if (m_items.TryPop(out T inst))
            {
                Interlocked.Decrement(ref m_stackLength);
                return inst;
            }

            return CreateInstance();
        }

        /// <summary>
        /// Returns objects to the pool.
        /// </summary>
        /// <remarks>
        /// Search strategy is a simple linear probing which is chosen for it cache-friendliness.
        /// Note that PutInstance will try to store recycled objects close to the start thus statistically
        /// reducing how far we will typically search in GetInstance.
        /// </remarks>
        public void PutInstance(PooledObjectWrapper<T> wrapper) => PutInstance(wrapper.Instance);

        /// <summary>
        /// Returns objects to the pool.
        /// </summary>
        public void PutInstance(T obj)
        {
            obj = m_cleanup?.Invoke(obj) ?? obj;

            var item = m_firstItem;

            if (item != null || Interlocked.CompareExchange(ref m_firstItem, obj, null) != null)
            {
                FreeSlow(obj);
            }
        }

        /// <summary>
        /// Removes <paramref name="obj"/> from the pool without cleaning up the instance.
        /// </summary>
        public void DetachInstance(T obj)
        {
            var item = m_firstItem;

            if (item != null || Interlocked.CompareExchange(ref m_firstItem, obj, null) != null)
            {
                FreeSlow(obj);
            }
        }

        private void FreeSlow(T obj)
        {
            if (m_stackLength < m_size)
            {
                // Using "m_size" to limit the number of items in the pool.
                // Using "m_size" is just a best effort.
                m_items.Push(obj);
                Interlocked.Increment(ref m_stackLength);
            }
        }

        /// <summary>
        /// Helper function that converts clean up method from <see cref="Action&lt;T&gt;"/> to <see cref="Func&lt;T,T&gt;"/>.
        /// </summary>
        private static Func<T, T> FromActionToFunc(Action<T> cleanup)
        {
            if (cleanup == null)
            {
                return null;
            }

            return t =>
            {
                cleanup(t);
                return t;
            };
        }
    }
}
