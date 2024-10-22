// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading.Tasks;

namespace BuildXL.Utilities.Core.Tasks
{
    /// <summary>
    /// Useful extension methods for tasks
    /// </summary>
    public static class TaskExtensionMethods
    {
        /// <summary>
        /// Repeatedly await a list of tasks, until their values don't change
        /// </summary>
        /// <remarks>
        /// The default value of <code>T</code> is assumed to be the initial value.
        /// </remarks>
        public static async Task<T[]> WhenStable<T>(this Func<Task<T>>[] taskProducers, IEqualityComparer<T> comparer)
        {
            Contract.RequiresNotNull(comparer);
            Contract.RequiresNotNull(taskProducers);
            Contract.RequiresForAll(taskProducers, producer => producer != null);

            T[] lastValues;
            var newValues = new T[taskProducers.Length];
            do
            {
                lastValues = newValues;
                newValues = new T[taskProducers.Length];
                for (int i = 0; i < taskProducers.Length; i++)
                {
                    newValues[i] = await taskProducers[i]();
                }
            }
            while (!lastValues.SequenceEqual(newValues, comparer));
            return newValues;
        }

        /// <summary>
        /// Repeatedly await a list of tasks, until their values don't change
        /// </summary>
        /// <remarks>
        /// The default value of <code>T</code> is assumed to be the initial value.
        /// </remarks>
        public static Task<T[]> WhenStable<T>(this Func<Task<T>>[] taskProducers)
        {
            Contract.RequiresNotNull(taskProducers);

            return WhenStable(taskProducers, EqualityComparer<T>.Default);
        }
    }
}
