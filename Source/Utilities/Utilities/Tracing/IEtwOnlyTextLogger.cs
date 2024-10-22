// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace BuildXL.Utilities.Tracing
{
    /// <summary>
    /// Logger for sending log messages to ETW
    /// </summary>
    public interface IEtwOnlyTextLogger
    {
        /// <summary>
        /// Logs the given log event message and information to ETW stream
        /// </summary>
        /// <param name="eventNumber">the event id</param>
        /// <param name="eventLabel">the event label or severity</param>
        /// <param name="message">the full output log message</param>
        void TextLogEtwOnly(int eventNumber, string eventLabel, string message);
    }
}
