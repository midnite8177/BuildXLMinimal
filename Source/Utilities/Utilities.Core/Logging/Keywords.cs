﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;

namespace BuildXL.Utilities.Instrumentation.Common
{
    /// <summary>
    /// Major groupings of events used for filtering.
    /// </summary>
    [SuppressMessage("Microsoft.Design", "CA1034:NestedTypesShouldNotBeVisible", Justification = "Needed by event infrastructure.")]
    public static class Keywords
    {
        /// <summary>
        /// Added to Level=Verbose events that may need to be optionally enabled for additional diagnostics but are
        /// generally disabled. The BuildXL host application has command lines to optionally enable events with this
        /// keyword on a per task basis
        /// </summary>
        public const EventKeywords Diagnostics = (EventKeywords)(1 << 28);

        /// <summary>
        /// Indicates an event that will be interpreted by the BuildXL listeners.
        /// </summary>
        public const EventKeywords UserMessage = (EventKeywords)(1 << 0);

        /// <summary>
        /// This the events relevant to progress indication.
        /// </summary>
        public const EventKeywords Progress = (EventKeywords)(1 << 1);

        /// <summary>
        /// Events that are only shown as temporary status on the console. They may be overwritten by future events
        /// if supported by the console
        /// </summary>
        public const EventKeywords Overwritable = (EventKeywords)(1 << 3);

        /// <summary>
        /// Events that are only shown as temporary status on the console and are printed if the console supports overwritting.
        /// They will be overwritten by future events.
        /// <remarks>Events flagged with this keyword will never go to the text log (as opposed to events flagged with 'Overwritable')</remarks>
        /// </summary>
        public const EventKeywords OverwritableOnly = (EventKeywords)(1 << 4);

        /// <summary>
        /// Events sent to external ETW listeners only
        /// </summary>
        public const EventKeywords ExternalEtwOnly = (EventKeywords)(1 << 5);

        /// <summary>
        /// Events that are flagged as infrastructure issues
        /// </summary>
        public const EventKeywords InfrastructureIssue = (EventKeywords)(1 << 6);

        /// <summary>
        /// Error events that are flagged as User Errors
        /// </summary>
        public const EventKeywords UserError = (EventKeywords)(1 << 7);

        /// <summary>
        /// Events that should not be forwarded to the orchestrator
        /// </summary>
        public const EventKeywords NotForwardedToOrchestrator = (EventKeywords)(1 << 8);

        /// <summary>
        /// Events that should be in included only custom logs which selectively enable the event
        /// </summary>
        public const EventKeywords SelectivelyEnabled = (EventKeywords)(1 << 9);
    }

}
