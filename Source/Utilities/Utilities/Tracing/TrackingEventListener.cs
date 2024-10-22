// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Instrumentation.Common;

namespace BuildXL.Utilities.Tracing
{
    /// <summary>
    /// Captures ETW data pumped through any instance derived from <see cref="Events" /> and counts the number of errors and
    /// warnings reported.
    /// </summary>
    public sealed class TrackingEventListener : BaseEventListener
    {
        private const int MaxEventIdExclusive = ushort.MaxValue;
        private const int MaxInternalWarningCount = 5;

        private int m_numAlways;
        private int m_numCriticals;
        private int m_numInformationals;
        private int m_numVerbose;
        private int m_numWarnings;
        private int m_numEventSourceInternalWarnings;

        private readonly BigBuffer<int> m_eventCounts = new BigBuffer<int>(entriesPerBufferBitWidth: 14);

        // state needed for cancelling a build early on an internal error
        private Action m_internalErrorAction = null;
        private LoggingContext m_loggingContext;
        private Task m_cancellationTask = null;
        private int m_earlyTerminationRequested;

        /// <summary>
        /// The UTC time representing time 0 for this listener
        /// </summary>
        private readonly DateTime m_baseTime;

        private readonly HashSet<string> m_internalWarnings = new HashSet<string>();

        /// <summary>
        /// List of the internal warnings. The max count is <see cref="MaxInternalWarningCount"/>.
        /// </summary>
        public IList<string> InternalWarnings => m_internalWarnings.ToList();

        /// <summary>
        /// Initializes an instance.
        /// </summary>
        /// <param name="eventSource">
        /// The event source to listen to.
        /// </param>
        /// <param name="baseTime">
        /// The starting time that events are compared against for displaying relative time for messages.
        /// </param>
        /// <param name="warningMapper">
        /// An optional delegate that is used to map warnings into errors or to suppress warnings.
        /// </param>
        public TrackingEventListener(Events eventSource, DateTime baseTime = default(DateTime), WarningMapper warningMapper = null)
            : base(
                eventSource,
                warningMapper,
                EventLevel.Verbose)
        {
            m_baseTime = baseTime == default(DateTime) ? DateTime.Now : baseTime;
            Contract.RequiresNotNull(eventSource);
            m_eventCounts.Initialize(MaxEventIdExclusive);
        }

        /// <inheritdoc/>
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            // Count all events
            if (m_eventCounts.Capacity > eventData.EventId && eventData.EventId >= 0)
            {
                var bufferPointer = m_eventCounts.GetBufferPointer(eventData.EventId);
                Interlocked.Increment(ref bufferPointer.Buffer[bufferPointer.Index]);
            }

            // then go through the event mapping and filtering logic defined in BaseEventListener
            base.OnEventWritten(eventData);
        }

        /// <summary>
        /// Gets the each event that was called and the count of times it was called
        /// </summary>
        public IEnumerable<KeyValuePair<int, int>> CountsPerEvent
        {
            get
            {
                var accessor = m_eventCounts.GetAccessor();
                for (int i = 0; i < MaxEventIdExclusive; i++)
                {
                    var eventCount = accessor[i];
                    if (eventCount != 0)
                    {
                        yield return new KeyValuePair<int, int>(i, eventCount);
                    }
                }
            }
        }

        /// <summary>
        /// Gets the number of events logged for eventId.
        /// </summary>
        /// <param name="eventId">The eventId which count to return.</param>
        /// <returns>The number of tome the iventId was raised.</returns>
        /// <remarks>If the eventId is not in the range of available eventIds, the return value is 0.</remarks>
        public int CountsPerEventId(int eventId)
        {
            var accessor = m_eventCounts.GetAccessor();

            if (eventId >= 0 && eventId < MaxEventIdExclusive)
            {
                return accessor[eventId];
            }

            return 0;
        }

        /// <summary>
        /// Gets the number of critical events written.
        /// </summary>
        public int CriticalCount => Volatile.Read(ref m_numCriticals);

        /// <summary>
        /// Gets the number of warning events written.
        /// </summary>
        public int WarningCount => Volatile.Read(ref m_numWarnings);

        /// <summary>
        /// Gets the number of meta-warnings written (warnings related to the act of writing other events).
        /// </summary>
        public int EventSourceInternalWarningCount => Volatile.Read(ref m_numEventSourceInternalWarnings);

        /// <summary>
        /// Gets the number of error events written.
        /// </summary>
        public int TotalErrorCount => InternalErrorDetails.Count + InfrastructureErrorDetails.Count + UserErrorDetails.Count;

        /// <summary>
        /// Gets the number of informational events written.
        /// </summary>
        public int InformationalCount => Volatile.Read(ref m_numInformationals);

        /// <summary>
        /// Gets the number of verbose events written.
        /// </summary>
        public int VerboseCount => Volatile.Read(ref m_numVerbose);

        /// <summary>
        /// Gets the number of 'always' events written.
        /// </summary>
        public int AlwaysCount => Volatile.Read(ref m_numAlways);

        /// <summary>
        /// The name of the first user error encountered
        /// </summary>
        public string FirstUserErrorName { get; private set; }

        /// <summary>
        /// Details about UserErrors that have been logged
        /// </summary>
        public ErrorCagetoryDetails UserErrorDetails = new ErrorCagetoryDetails();

        /// <summary>
        /// Details about InfrastructureErrors that have been logged
        /// </summary>
        public ErrorCagetoryDetails InfrastructureErrorDetails = new ErrorCagetoryDetails();

        /// <summary>
        /// Details about InternalErrors that have been logged
        /// </summary>
        public ErrorCagetoryDetails InternalErrorDetails = new ErrorCagetoryDetails();

        /// <summary>
        /// Gets whether any errors were written.
        /// </summary>
        public bool HasFailures => TotalErrorCount > 0 || CriticalCount > 0;

        /// <summary>
        /// Gets whether any errors or warnings were written.
        /// </summary>
        /// <remarks>
        /// This is useful in unit tests to ensure no error was logged.
        /// </remarks>
        public bool HasFailuresOrWarnings => TotalErrorCount > 0 || CriticalCount > 0 || WarningCount > 0;

        /// <summary>
        /// Registers an action to take place after an internal or infrastructure error has been logged and the LoggingContext has been updated
        /// </summary>
        /// <remarks>
        /// Only a single action is supported. Unset an action by passing null. Only the first internal error will trigger the action
        /// </remarks>
        public void RegisterInternalErrorAction(LoggingContext loggingContext, Action internalErrorAction)
        {
            m_internalErrorAction = internalErrorAction;
            m_loggingContext = loggingContext;
        }

        /// <inheritdoc />
        protected override void OnCritical(EventWrittenEventArgs eventData)
        {
            Contract.Assume(eventData.Level == EventLevel.Critical);
            long keywords = (long)eventData.Keywords;
            string eventName = eventData.EventName;

            string eventMessage = FormattingEventListener.CreateFullMessageString(eventData, "error", eventData.Message, m_baseTime, useCustomPipDescription: false);
            BucketError(keywords, eventName, eventMessage);
            Interlocked.Increment(ref m_numCriticals);
        }

        /// <inheritdoc />
        protected override void OnWarning(EventWrittenEventArgs eventData)
        {
            Contract.Assume(eventData.Level == EventLevel.Warning);
            Interlocked.Increment(ref m_numWarnings);
            CollectInternalWarning(eventData);
        }

        /// <inheritdoc />
        protected override void OnEventSourceInternalWarning(EventWrittenEventArgs eventData)
        {
            Interlocked.Increment(ref m_numEventSourceInternalWarnings);
        }

        /// <inheritdoc />
        protected override void OnError(EventWrittenEventArgs eventData)
        {
            var level = eventData.Level;
            bool wasInfrastructureOrInternal = false;
            if (level == EventLevel.Error)
            {
                long keywords = (long)eventData.Keywords;
                string eventName = eventData.EventName;
                string eventMessage = FormattingEventListener.CreateFullMessageString(eventData, "error", eventData.Message, m_baseTime, useCustomPipDescription: false);

                // Errors replayed from workers should respect their original event name and keywords
                if (eventData.EventId == (int)SharedLogEventId.DistributionWorkerForwardedError)
                {
                    eventMessage = (string)eventData.Payload[0];
                    eventName = (string)eventData.Payload[2];
                    keywords = (long)eventData.Payload[3];
                }

                // If it's an IPC pip failure, check if we can make massage the message a bit.
                // Use eventName instead of eventData.EventId in case this an error sent from a worker.
                if (eventName == SharedLogEventId.PipIpcFailed.ToString())
                {
                    // IPC pip failure message is too verbose and too long, so let's shorten it a bit. The message has the following format:
                    // [{pipDescription}] IPC operation '{operation}' could not be executed via IPC moniker '{moniker}'.  Reason: {reason}. Error: {message}
                    // For the purpose of creating a message for tracking/bucketing, the only useful component here is 'reason'.
                    // Both 'operation' and 'message' can be arbitrarily long and have limited value.
                    // If regex cannot find a match (this will happen if an error was not handled by a daemon and instead was handled by IServer),
                    // it's not a big deal, we'll just fall back to using the full error message.
                    try
                    {
                        var regex = new Regex(@" Reason: (?<reason>\w+)\. ", RegexOptions.IgnoreCase);
                        var match = regex.Match(eventMessage);
                        if (match.Success)
                        {
                            // If there is a match, create a new bucket message.
                            eventMessage = $"{SharedLogEventId.PipIpcFailed}.{match.Groups["reason"].Value}";
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // If there is a timeout, just fall back to using the full error message.
                    }
                }

                wasInfrastructureOrInternal = BucketError(keywords, eventName, eventMessage);
            }
            else
            {
                // OnError() called for an EventLevel other than error?!? How can this be? It is possible for the
                // WarningMapper to upconvert a lower priority event to an error
                Contract.Assert((level == EventLevel.Warning) || (level == EventLevel.Informational) || (level == EventLevel.Verbose));

                string eventMessage = FormattingEventListener.CreateFullMessageString(eventData, "error", eventData.Message, DateTime.Now, useCustomPipDescription: false);
                // The configuration promoted a warning to an error. That's a user error
                UserErrorDetails.RegisterError(eventData.EventName, eventMessage);
            }

            if (m_loggingContext != null && m_internalErrorAction != null)
            {
                if (wasInfrastructureOrInternal &&
                    // These timeout errors are infrastrucure errors but also trigger a shutdown. So do not re-trigger another
                    // internal error early shutdown. Doing so would create confusion in logging
                    eventData.EventId != (int)SharedLogEventId.TimeoutReached && eventData.EventId != (int)SharedLogEventId.TimeoutTooLow)
                {
                    TriggerInternalErrorAction();
                }
            }
        }

        /// <summary>
        /// Calls the <see cref="m_internalErrorAction"/> once the LoggingContext signals that an error was logged
        /// </summary>
        /// <remarks>
        /// The primary purpose of the <see cref="m_internalErrorAction"/> is to terminate the build on an internal error.
        /// It is important that the originating error has an opportunity to be logged before that termination is requested.
        /// So this method kicks off a background task to trigger that action only after the LoggingContext has signaled
        /// that the original error has been logged.
        /// This method also guards that the action function can only be triggered a single time.
        /// </remarks>
        private void TriggerInternalErrorAction()
        {
            if (m_earlyTerminationRequested == 0 && Interlocked.Increment(ref m_earlyTerminationRequested) == 1)
            {
                m_cancellationTask = Task.Run(async () =>
                {
                    while (!m_loggingContext.ErrorWasLogged)
                    {
                        await Task.Delay(10);
                    }

                    m_internalErrorAction();
                });
            }
        }

        /// <summary>
        /// Buckets and tracks an error into user, infrastructure, internal
        /// </summary>
        /// <returns>True if the error is an internal or infrastructure error</returns>
        private bool BucketError(long keywords, string eventName, string errorMessage)
        {
            if ((keywords & (long)Keywords.UserError) > 0)
            {
                UserErrorDetails.RegisterError(eventName, errorMessage);
                return false;
            }
            else if ((keywords & (long)Keywords.InfrastructureIssue) > 0)
            {
                InfrastructureErrorDetails.RegisterError(eventName, errorMessage);
            }
            else
            {
                InternalErrorDetails.RegisterError(eventName, errorMessage);
            }

            return true;
        }

        /// <inheritdoc />
        protected override void OnInformational(EventWrittenEventArgs eventData)
        {
            Contract.Assume(eventData.Level == EventLevel.Informational);
            Interlocked.Increment(ref m_numInformationals);
            CollectInternalWarning(eventData);
        }

        /// <inheritdoc />
        protected override void OnVerbose(EventWrittenEventArgs eventData)
        {
            Contract.Assume(eventData.Level == EventLevel.Verbose);
            Interlocked.Increment(ref m_numVerbose);
            CollectInternalWarning(eventData);
        }

        /// <inheritdoc />
        protected override void OnAlways(EventWrittenEventArgs eventData)
        {
            Contract.Assume(eventData.Level == EventLevel.LogAlways);
            Interlocked.Increment(ref m_numAlways);
        }

        /// <summary>
        /// We want to log some infrastructure issues as less-than-warning to not
        /// surface them to the users, but at the same time keep track
        /// of them: we include them in the internal warnings that we monitor.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CollectInternalWarning(EventWrittenEventArgs eventData)
        {
            long keywords = (long)eventData.Keywords;
            if ((keywords & (long)Keywords.InfrastructureIssue) > 0 &&
                m_internalWarnings.Count < MaxInternalWarningCount)
            {
                m_internalWarnings.Add(eventData.EventName);
            }
        }

        /// <summary>
        /// Returns a dictionary of the number of times each event was encountered.
        /// </summary>
        /// <returns></returns>
        public Dictionary<string, int> ToEventCountDictionary()
        {
            Dictionary<string, int> d = new Dictionary<string, int>();
            foreach (var entry in CountsPerEvent)
            {
                d.Add(entry.Key.ToString(CultureInfo.InvariantCulture), entry.Value);
            }

            return d;
        }
    }
}
