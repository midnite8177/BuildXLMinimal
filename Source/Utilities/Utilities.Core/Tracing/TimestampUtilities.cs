// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;

namespace BuildXL.Utilities.Core.Tracing
{
    /// <summary>
    /// Helpers for objects which track time. Replaces the need to use a new <see cref="Stopwatch"/> or <see cref="DateTime"/> for
    /// elapsed time measurements
    /// </summary>
    public static class TimestampUtilities
    {
        /// <summary>
        /// Shared stopwatch instance for operations which need to record elapsed time
        /// </summary>
        private static readonly Stopwatch s_stopwatch = Stopwatch.StartNew();

        /// <summary>
        /// The current timestamp as a timespan
        /// </summary>
        public static TimeSpan Timestamp => s_stopwatch.Elapsed;

        /// <summary>
        /// Gets the elapsed time and resets the stopwatch
        /// </summary>
        public static TimeSpan ElapsedAndReset(ref this StopwatchSlim stopwatch)
        {
            var elapsed = stopwatch.Elapsed;
            stopwatch = StopwatchSlim.Start();
            return elapsed;
        }
    }
}
