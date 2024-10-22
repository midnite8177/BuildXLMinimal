// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace BuildXL.Utilities.Core.Tasks
{
    /// <summary>
    /// This type is effectively 'void', but usable as a type parameter.
    /// </summary>
    /// <remarks>
    /// This is useful for generic methods dealing in tasks, since one can avoid having an overload for both
    /// <see cref="Task" /> and <see cref="Task{TResult}" />. One instead provides only a <see cref="Task{TResult}" />
    /// overload, and callers with a void result return <see cref="Void" />.
    /// </remarks>
    [SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes",
        Justification = "There is no point in comparing or hashing Unit.")]
    public readonly struct Unit
    {
        /// <summary>
        /// Void unit type
        /// </summary>
        public static readonly Unit Void = default(Unit);

        /// <summary>
        /// Successfully completed task
        /// </summary>
        public static readonly Task<Unit> VoidTask = Task.FromResult(Void);
    }
}
