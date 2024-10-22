// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities.Core.Tracing;

namespace BuildXL.Utilities.Core.Tasks
{
    /// <summary>
    /// Static utilities related to <see cref="Task" />.
    /// </summary>
    public static class TaskUtilities
    {
        /// <summary>
        /// Returns a faulted task containing the given exception.
        /// This is the failure complement of <see cref="Task.FromResult{TResult}" />.
        /// </summary>
        public static Task<T> FromException<T>(Exception ex)
        {
            Contract.RequiresNotNull(ex);

            var failureSource = TaskSourceSlim.Create<T>();
            failureSource.SetException(ex);
            return failureSource.Task;
        }

        /// <summary>
        /// This is a variant of Task.WhenAll which ensures that all exceptions thrown by the tasks are
        /// propagated back through a single <see cref="AggregateException"/>. This is necessary because
        /// the default awaiter (as used by 'await') only takes the *first* exception inside of a task's
        /// aggregate exception. All BuildXL code should use this method instead of the standard WhenAll.
        /// </summary>
        /// <exception cref="System.AggregateException">Thrown when any of the tasks failed.</exception>
        public static async Task SafeWhenAll(IEnumerable<Task> tasks)
        {
            Contract.Requires(tasks != null);

            var whenAllTask = Task.WhenAll(tasks);

            try
            {
                await whenAllTask;
            }
            catch
            {
                if (whenAllTask.Exception != null)
                {
                    // Rethrowing the error preserving the stack trace.
                    ExceptionDispatchInfo.Capture(whenAllTask.Exception).Throw();
                }

                // whenAllTask is in the canceled state, we caught TaskCancelledException
                throw;
            }
        }

        /// <summary>
        /// This is a variant of Task.WhenAll which ensures that all exceptions thrown by the tasks are
        /// propagated back through a single <see cref="AggregateException"/>. This is necessary because
        /// the default awaiter (as used by 'await') only takes the *first* exception inside of a task's
        /// aggregate exception. All BuildXL code should use this method instead of the standard WhenAll.
        /// </summary>
        /// <exception cref="System.AggregateException">Thrown when any of the tasks failed.</exception>
        public static async Task<TResult[]> SafeWhenAll<TResult>(IEnumerable<Task<TResult>> tasks)
        {
            Contract.RequiresNotNull(tasks);

            var whenAllTask = Task.WhenAll(tasks);
            try
            {
                return await whenAllTask;
            }
            catch
            {
                if (whenAllTask.Exception != null)
                {
                    // Rethrowing the error preserving the stack trace.
                    ExceptionDispatchInfo.Capture(whenAllTask.Exception).Throw();
                }

                // whenAllTask is in the canceled state, we caught TaskCancelledException
                throw;
            }
        }

        /// <summary>
        /// This is a variant of Task.WhenAll which ensures that all exceptions thrown by the tasks are
        /// propagated back through a single <see cref="AggregateException"/>. This is necessary because
        /// the default awaiter (as used by 'await') only takes the *first* exception inside of a task's
        /// aggregate exception. All BuildXL code should use this method instead of the standard WhenAll.
        /// </summary>
        /// <exception cref="System.AggregateException">Thrown when any of the tasks failed.</exception>
        public static Task<TResult[]> SafeWhenAll<TResult>(params Task<TResult>[] tasks)
        {
            Contract.Requires(tasks != null);

            return SafeWhenAll((IEnumerable<Task<TResult>>)tasks);
        }


        /// <summary>
        /// This is a variant of Task.WhenAll which ensures that all exceptions thrown by the tasks are
        /// propagated back through a single <see cref="AggregateException"/>. This is necessary because
        /// the default awaiter (as used by 'await') only takes the *first* exception inside of a task's
        /// aggregate exception. All BuildXL code should use this method instead of the standard WhenAll.
        /// </summary>
        /// <exception cref="System.AggregateException">Thrown when any of the tasks failed.</exception>
        public static Task SafeWhenAll(params Task[] tasks)
        {
            Contract.Requires(tasks != null);

            return SafeWhenAll((IEnumerable<Task>)tasks);
        }


        /// <summary>
        /// Creates a task that will complete when all of the <see cref="T:System.Threading.Tasks.Task" /> objects in an enumerable collection have completed or when the <paramref name="cancellationToken"/> is triggered.
        /// </summary>
        /// <exception cref="OperationCanceledException">The exception is thrown if the <paramref name="cancellationToken"/> is canceled before the completion of <paramref name="tasks"/></exception>
        public static Task WhenAllWithCancellationAsync(IEnumerable<Task> tasks, CancellationToken cancellationToken)
        {
            // If one of the tasks passed here fails, we want to make sure that the task created by 'Task.WhenAll(tasks)' is observed
            // in order to avoid unobserved task errors.
            return AwaitWithCancellationAsync(Task.WhenAll(tasks), cancellationToken);
        }

        /// <summary>
        /// Creates a task that will complete when either <paramref name="task"/> is completed, or when <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        public static async Task AwaitWithCancellationAsync(Task task, CancellationToken cancellationToken)
        {
            var completedTask = await Task.WhenAny(
                Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                task);

            // We have one of two cases here: either the task is done or the cancellation was requested.

            // If the cancellation is requested we need to make sure we observe the result of the when all task created earlier.
            task.Forget();

            // Now, we can trigger 'OperationCancelledException' if the token is canceled.
            // (Yes, its possible that all the tasks are done already, but this is a natural race condition for this pattern).
            cancellationToken.ThrowIfCancellationRequested();

            // The cancellation was not requested, but one of the tasks may fail.
            // Re-throwing the error in this case by awaiting already completed task.
            await completedTask;
        }

        /// <summary>
        /// Gets <see cref="CancellationTokenAwaitable"/> from a given <paramref name="token"/> that can be used in async methods to await the cancellation.
        /// </summary>
        /// <remarks>
        /// The method returns a special disposable type instead of just returning a Task.
        /// This is important, because the client code need to "unregister" the callback from the token when some other operations are done and the cancellation is no longer relevant.
        /// Just returning a task on a token that is never trigerred will effectively cause a memory leak.
        /// Here is a previous implementation of this method:
        /// <code>public static async Task ToAwaitable(this CancellationToken token) { try {await Task.Delay(Timeout.Infinite, token);} catch(TaskCanceledException) {} }</code>
        /// The `Delay` impelmentaiton checks if the timeout is infinite and won't start the timer, but it still will create a `DelayPromise` instance
        /// and will register for the cancellation.
        /// It means that if we call such a method many times with the same cancellation token, the registration list will grow indefinitely causing potential performance issues.
        /// </remarks>
        public static CancellationTokenAwaitable ToAwaitable(this CancellationToken token)
        {
            if (!token.CanBeCanceled)
            {
                // If the token can not be canceled, return a special global instance with a task that will never be finished.
                return CancellationTokenAwaitable.NonCancellableAwaitable;
            }

            var tcs = TaskSourceSlim.Create<object>();
            var registration = token.Register(static tcs => ((TaskSourceSlim<object>)tcs).SetResult(null), tcs);
            return new CancellationTokenAwaitable(tcs.Task, registration);
        }

        /// <nodoc />
        public readonly struct CancellationTokenAwaitable : IDisposable
        {
            private readonly CancellationTokenRegistration? m_registration;

            /// <nodoc />
            public CancellationTokenAwaitable(Task completionTask, CancellationTokenRegistration? registration)
            {
                m_registration = registration;
                CompletionTask = completionTask;
            }

            /// <nodoc />
            public static CancellationTokenAwaitable NonCancellableAwaitable { get; } = new CancellationTokenAwaitable(TaskSourceSlim.Create<object>().Task, registration: null);

            /// <nodoc />
            public Task CompletionTask { get; }

            /// <inheritdoc />
            void IDisposable.Dispose()
            {
                m_registration?.Dispose();
            }
        }

        /// <summary>
        /// Provides await functionality for ordinary <see cref="WaitHandle"/>s.
        /// </summary>
        /// <param name="handle">The handle to wait on.</param>
        /// <returns>The awaiter.</returns>
        public static TaskAwaiter GetAwaiter(this WaitHandle handle)
        {
            Contract.RequiresNotNull(handle);

            return handle.ToTask().GetAwaiter();
        }

        /// <summary>
        /// Provides await functionality for an array of ordinary <see cref="WaitHandle"/>s.
        /// </summary>
        /// <param name="handles">The handles to wait on.</param>
        /// <returns>The awaiter.</returns>
        public static TaskAwaiter<int> GetAwaiter(this WaitHandle[] handles)
        {
            Contract.RequiresNotNull(handles);
            Contract.RequiresForAll(handles, handle => handles != null);

            return handles.ToTask().GetAwaiter();
        }

        /// <summary>
        /// Creates a TPL Task that is marked as completed when a <see cref="WaitHandle"/> is signaled.
        /// </summary>
        /// <param name="handle">The handle whose signal triggers the task to be completed.  Do not use a <see cref="Mutex"/> here.</param>
        /// <param name="timeout">The timeout (in milliseconds) after which the task will fault with a <see cref="TimeoutException"/> if the handle is not signaled by that time.</param>
        /// <returns>A Task that is completed after the handle is signaled.</returns>
        /// <remarks>
        /// There is a (brief) time delay between when the handle is signaled and when the task is marked as completed.
        /// </remarks>
        public static Task ToTask(this WaitHandle handle, int timeout = Timeout.Infinite)
        {
            Contract.RequiresNotNull(handle);

            return ToTask(new WaitHandle[1] { handle }, timeout);
        }

        /// <summary>
        /// Creates a TPL Task that is marked as completed when any <see cref="WaitHandle"/> in the array is signaled.
        /// </summary>
        /// <param name="handles">The handles whose signals triggers the task to be completed.  Do not use a <see cref="Mutex"/> here.</param>
        /// <param name="timeout">The timeout (in milliseconds) after which the task will return a value of WaitTimeout.</param>
        /// <returns>A Task that is completed after any handle is signaled.</returns>
        /// <remarks>
        /// There is a (brief) time delay between when the handles are signaled and when the task is marked as completed.
        /// </remarks>
        public static Task<int> ToTask(this WaitHandle[] handles, int timeout = Timeout.Infinite)
        {
            Contract.RequiresNotNull(handles);
            Contract.RequiresForAll(handles, handle => handles != null);

            var tcs = TaskSourceSlim.Create<int>();
            int signalledHandle = WaitHandle.WaitAny(handles, 0);
            if (signalledHandle != WaitHandle.WaitTimeout)
            {
                // An optimization for if the handle is already signaled
                // to return a completed task.
                tcs.SetResult(signalledHandle);
            }
            else
            {
                var localVariableInitLock = new object();
                lock (localVariableInitLock)
                {
                    RegisteredWaitHandle[] callbackHandles = new RegisteredWaitHandle[handles.Length];
                    for (int i = 0; i < handles.Length; i++)
                    {
                        callbackHandles[i] = ThreadPool.RegisterWaitForSingleObject(
                            handles[i],
                            (state, timedOut) =>
                            {
                                int handleIndex = (int)state;
                                if (timedOut)
                                {
                                    tcs.TrySetResult(WaitHandle.WaitTimeout);
                                }
                                else
                                {
                                    tcs.TrySetResult(handleIndex);
                                }

                                // We take a lock here to make sure the outer method has completed setting the local variable callbackHandles contents.
                                lock (localVariableInitLock)
                                {
                                    foreach (var handle in callbackHandles)
                                    {
                                        handle.Unregister(null);
                                    }
                                }
                            },
                            state: i,
                            millisecondsTimeOutInterval: timeout,
                            executeOnlyOnce: true);
                    }
                }
            }

            return tcs.Task;
        }

        /// <summary>
        /// Creates a new <see cref="SemaphoreSlim"/> representing a mutex which can only be entered once.
        /// </summary>
        /// <returns>the semaphore</returns>
        public static SemaphoreSlim CreateMutex(bool taken = false)
        {
            return new SemaphoreSlim(initialCount: taken ? 0 : 1, maxCount: 1);
        }

        /// <summary>
        /// Asynchronously acquire a semaphore
        /// </summary>
        /// <param name="semaphore">The semaphore to acquire</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>A disposable which will release the semaphore when it is disposed.</returns>
        public static async Task<SemaphoreReleaser> AcquireAsync(this SemaphoreSlim semaphore, CancellationToken cancellationToken = default(CancellationToken))
        {
            Contract.Requires(semaphore != null);

            var stopwatch = StopwatchSlim.Start();
            await semaphore.WaitAsync(cancellationToken);
            return new SemaphoreReleaser(semaphore, stopwatch.Elapsed);
        }

        /// <summary>
        /// Asynchronously acquire a semaphore
        /// </summary>
        /// <param name="semaphore">The semaphore to acquire</param>
        /// <param name="timeout">The timeout for acquiring the semaphore</param>
        /// <param name="cancellationToken">The cancellation token</param>
        /// <returns>A disposable which will release the semaphore when it is disposed.</returns>
        public static async Task<SemaphoreReleaser> AcquireAsync(this SemaphoreSlim semaphore, TimeSpan timeout, CancellationToken cancellationToken = default(CancellationToken))
        {
            Contract.Requires(semaphore != null);

            var stopwatch = StopwatchSlim.Start();
            var acquired = await semaphore.WaitAsync(timeout, cancellationToken);
            return new SemaphoreReleaser(acquired ? semaphore : null, stopwatch.Elapsed);
        }

        /// <summary>
        /// Synchronously acquire a semaphore
        /// </summary>
        /// <param name="semaphore">The semaphore to acquire</param>
        public static SemaphoreReleaser AcquireSemaphore(this SemaphoreSlim semaphore)
        {
            Contract.Requires(semaphore != null);
            var stopwatch = StopwatchSlim.Start();

            semaphore.Wait();
            return new SemaphoreReleaser(semaphore, stopwatch.Elapsed);
        }

        /// <summary>
        /// Consumes a task and doesn't do anything with it.  Useful for fire-and-forget calls to async methods within async methods.
        /// </summary>
        /// <param name="task">The task whose result is to be ignored.</param>
        /// <param name="unobservedExceptionHandler">Optional handler for the task's unobserved exception (if any).</param>
        public static void Forget(this Task task, Action<Exception> unobservedExceptionHandler = null)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Analysis.IgnoreArgument(t.Exception);
                    var e = (t.Exception as AggregateException)?.InnerException ?? t.Exception;
                    unobservedExceptionHandler?.Invoke(e);
                }
            });
        }

        /// <summary>
        /// "Swallow" an exception that happen in fire-and-forget task.
        /// </summary>
        public static Task IgnoreErrorsAndReturnCompletion(this Task task)
        {
            return task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // Ignore the exception if task if faulted
                    Analysis.IgnoreArgument(t.Exception);
                }
            });
        }

        /// <summary>
        /// Convenience method for creating a task with a result after a given task completes
        /// </summary>
        public static async Task<T> WithResultAsync<T>(this Task task, T result)
        {
            await task;
            return result;
        }

        /// <summary>
        /// Waits for the given task to complete within the given timeout, throwing a <see cref="TimeoutException"/> if the timeout expires before the task completes
        /// </summary>
        public static Task WithTimeoutAsync(this Task task, TimeSpan timeout)
        {
            return WithTimeoutAsync(async ct =>
            {
                await task;
                return Unit.Void;
            }, timeout);

        }

        /// <summary>
        /// Waits for the given task to complete within the given timeout, throwing a <see cref="TimeoutException"/> if the timeout expires before the task completes
        /// </summary>
        public static async Task<T> WithTimeoutAsync<T>(this Task<T> task, TimeSpan timeout, CancellationToken token = default)
        {
            await WithTimeoutAsync(ct => task, timeout, token);
            return await task;
        }

        /// <summary>
        /// Waits for the given task to complete within the given timeout, throwing a <see cref="TimeoutException"/> if the timeout expires before the task completes
        /// Very elaborate logic to ensure we log the "right" thing
        /// </summary>
        public static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> taskFactory, TimeSpan timeout, CancellationToken token = default)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                return await taskFactory(token);
            }

            using (var timeoutTokenSource = new CancellationTokenSource(timeout))
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(timeoutTokenSource.Token, token))
            {
#pragma warning disable AsyncFixer04 // A disposable object used in a fire & forget async call
                var task = taskFactory(cts.Token);
#pragma warning restore AsyncFixer04 // A disposable object used in a fire & forget async call
                Analysis.IgnoreResult(await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token)));

                if (!task.IsCompleted || (task.IsCanceled && timeoutTokenSource.IsCancellationRequested))
                {
                    // The user's task is not completed or it is canceled (possibly due to timeoutTokenSource)

                    if (!token.IsCancellationRequested)
                    {
                        // Throw TimeoutException only when the original token is not canceled.
                        observeTaskAndThrow(task);
                    }

                    // Need to wait with timeout again to ensure that cancellation of a non-responding task will time out.
                    Analysis.IgnoreResult(await Task.WhenAny(task, Task.Delay(Timeout.Infinite, timeoutTokenSource.Token)));
                    if (!task.IsCompleted && timeoutTokenSource.IsCancellationRequested)
                    {
                        observeTaskAndThrow(task);
                    }
                }

                return await task;
            }

            void observeTaskAndThrow(Task task)
            {
                // Task created by the task factory is unreachable by the client of this method.
                // So we need to "observe" potential error to prevent a (possible) application crash
                // due to TaskUnobservedException.
                task.Forget();
                throw new TimeoutException($"The operation has timed out. Timeout is '{timeout}'.");
            }
        }

        /// <summary>
        /// Allows an IDisposable-conforming release of an acquired semaphore
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1815:OverrideEqualsAndOperatorEqualsOnValueTypes")]
        public readonly struct SemaphoreReleaser : IDisposable
        {
            private readonly SemaphoreSlim m_semaphore;

            /// <nodoc />
            public TimeSpan LockAcquisitionDuration { get; }

            /// <summary>
            /// Creates a new releaser.
            /// </summary>
            /// <param name="semaphore">The semaphore to release when Dispose is invoked.</param>
            /// <param name="lockAcquisitionDuration">The time it took to acquire the lock.</param>
            /// <remarks>
            /// Assumes the semaphore is already acquired.
            /// </remarks>
            internal SemaphoreReleaser(SemaphoreSlim semaphore, TimeSpan lockAcquisitionDuration)
            {
                m_semaphore = semaphore;
                LockAcquisitionDuration = lockAcquisitionDuration;
            }

            /// <summary>
            /// IDispoaable.Dispose()
            /// </summary>
            public void Dispose()
            {
                m_semaphore?.Release();
            }

            /// <summary>
            /// Whether this semaphore releaser is valid (and not the default value)
            /// </summary>
            public bool IsValid => m_semaphore != null;

            /// <summary>
            /// Gets the number of threads that will be allowed to enter the semaphore.
            /// </summary>
            public int CurrentCount => m_semaphore?.CurrentCount ?? -1;
        }

        // Starting .Net8, CancellationTokenSource has CancelAsync() method. To make AsyncFixer happy and to keep our
        // code readable, we add this extension method. Though it has 'async' in the name it's not an async method
        // in .net versions prior to .Net8.
#if NET8_0_OR_GREATER
        /// <summary>
        /// If async cancellation is supported by the runtime, communicates request for cancellation asynchronously.
        /// Otherwise, communicates the request synchronously.
        /// </summary>
        /// <remarks>
        /// Use this extension method when you need to cancel a CancellationTokenSource inside of an async method.
        /// The return value must be awaited.
        /// </remarks>
        public static async ValueTask CancelTokenAsyncIfSupported(this CancellationTokenSource cts)
        {
            await cts.CancelAsync();
        }
#else
        /// <summary>
        /// If async cancellation is supported by the runtime, communicates request for cancellation asynchronously.
        /// Otherwise, communicates the request synchronously.
        /// </summary>
        /// <remarks>
        /// Use this extension method when you need to cancel a CancellationTokenSource inside of an async method.
        /// The return value must be awaited.
        /// </remarks>
        public static ValueTask CancelTokenAsyncIfSupported(this CancellationTokenSource cts)
        {
            cts.Cancel();
            return default;
        }
#endif
    }
}
