// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BuildXL.Utilities
{
    /// <summary>
    /// Utilities for using Code Contracts.
    /// </summary>
    public static class ContractUtilities
    {
        /// <summary>
        /// Allows erasing a precondition / postcondition expression as part of rewriting contracts (even when runtime checking is enabled).
        /// </summary>
        /// <example>
        /// Contract.Requires(ContractUtilities.Static(ExpensiveCheckIsTrue()));
        /// </example>
        public static bool Static(bool expression)
        {
            return expression;
        }
    }
}
