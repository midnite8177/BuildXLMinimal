// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BuildXL.Native.Streams
{
    /// <summary>
    /// Completion and success status of an async I/O operation.
    /// </summary>
    public enum FileAsyncIOStatus
    {
        /// <summary>
        /// The I/O operation is still in progress.
        /// </summary>
        Pending,

        /// <summary>
        /// The I/O operation has completed, and was successful.
        /// </summary>
        Succeeded,

        /// <summary>
        /// The I/O operation has completed, and failed with an error.
        /// </summary>
        Failed,
    }
}
