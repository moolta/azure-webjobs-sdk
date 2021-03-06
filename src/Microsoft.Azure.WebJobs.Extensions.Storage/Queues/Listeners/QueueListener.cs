﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Queue;
using Microsoft.Azure.WebJobs.Extensions.Storage;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Listeners;
using Microsoft.Azure.WebJobs.Host.Protocols;
using Microsoft.Azure.WebJobs.Host.Scale;
using Microsoft.Azure.WebJobs.Host.Timers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Host.Queues.Listeners
{
    internal sealed partial class QueueListener : IListener, ITaskSeriesCommand, INotificationCommand, IScaleMonitor<QueueTriggerMetrics>
    {
        const int NumberOfSamplesToConsider = 5;

        internal static readonly QueueRequestOptions DefaultQueueRequestOptions = new QueueRequestOptions { NetworkTimeout = TimeSpan.FromSeconds(100) };

        private readonly ITaskSeriesTimer _timer;
        private readonly IDelayStrategy _delayStrategy;
        private readonly CloudQueue _queue;
        private readonly CloudQueue _poisonQueue;
        private readonly ITriggerExecutor<CloudQueueMessage> _triggerExecutor;
        private readonly IWebJobsExceptionHandler _exceptionHandler;
        private readonly IMessageEnqueuedWatcher _sharedWatcher;
        private readonly List<Task> _processing = new List<Task>();
        private readonly object _stopWaitingTaskSourceLock = new object();
        private readonly QueuesOptions _queueOptions;
        private readonly QueueProcessor _queueProcessor;
        private readonly TimeSpan _visibilityTimeout;
        private readonly ILogger<QueueListener> _logger;
        private readonly FunctionDescriptor _functionDescriptor;
        private readonly string _functionId;
        private readonly ScaleMonitorDescriptor _scaleMonitorDescriptor;
        private readonly CancellationTokenSource _shutdownCancellationTokenSource;

        private bool? _queueExists;
        private bool _foundMessageSinceLastDelay;
        private bool _disposed;
        private TaskCompletionSource<object> _stopWaitingTaskSource;

        // for mock testing only
        internal QueueListener()
        {
        }

        public QueueListener(CloudQueue queue,
            CloudQueue poisonQueue,
            ITriggerExecutor<CloudQueueMessage> triggerExecutor,
            IWebJobsExceptionHandler exceptionHandler,
            ILoggerFactory loggerFactory,
            SharedQueueWatcher sharedWatcher,
            QueuesOptions queueOptions,
            IQueueProcessorFactory queueProcessorFactory,
            FunctionDescriptor functionDescriptor,
            string functionId = null,
            TimeSpan? maxPollingInterval = null)
        {
            if (queueOptions == null)
            {
                throw new ArgumentNullException(nameof(queueOptions));
            }

            if (queueProcessorFactory == null)
            {
                throw new ArgumentNullException(nameof(queueProcessorFactory));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            if (queueOptions.BatchSize <= 0)
            {
                throw new ArgumentException("BatchSize must be greater than zero.");
            }

            if (queueOptions.MaxDequeueCount <= 0)
            {
                throw new ArgumentException("MaxDequeueCount must be greater than zero.");
            }

            _timer = new TaskSeriesTimer(this, exceptionHandler, Task.Delay(0));
            _queue = queue;
            _poisonQueue = poisonQueue;
            _triggerExecutor = triggerExecutor;
            _exceptionHandler = exceptionHandler;
            _queueOptions = queueOptions;
            _logger = loggerFactory.CreateLogger<QueueListener>();
            _functionDescriptor = functionDescriptor ?? throw new ArgumentNullException(nameof(functionDescriptor));
            _functionId = functionId ?? _functionDescriptor.Id;

            // if the function runs longer than this, the invisibility will be updated
            // on a timer periodically for the duration of the function execution
            _visibilityTimeout = TimeSpan.FromMinutes(10);

            if (sharedWatcher != null)
            {
                // Call Notify whenever a function adds a message to this queue.
                sharedWatcher.Register(queue.Name, this);
                _sharedWatcher = sharedWatcher;
            }

            EventHandler<PoisonMessageEventArgs> poisonMessageEventHandler = _sharedWatcher != null ? OnMessageAddedToPoisonQueue : (EventHandler<PoisonMessageEventArgs>)null;
            _queueProcessor = CreateQueueProcessor(_queue, _poisonQueue, loggerFactory, queueProcessorFactory, _queueOptions, poisonMessageEventHandler);

            TimeSpan maximumInterval = _queueProcessor.MaxPollingInterval;
            if (maxPollingInterval.HasValue && maximumInterval > maxPollingInterval.Value)
            {
                // enforce the maximum polling interval if specified
                maximumInterval = maxPollingInterval.Value;
            }

            _delayStrategy = new RandomizedExponentialBackoffStrategy(QueuePollingIntervals.Minimum, maximumInterval);

            _scaleMonitorDescriptor = new ScaleMonitorDescriptor($"{_functionId}-QueueTrigger-{_queue.Name}".ToLower());
            _shutdownCancellationTokenSource = new CancellationTokenSource();
        }

        // for testing
        internal TimeSpan MinimumVisibilityRenewalInterval { get; set; } = TimeSpan.FromMinutes(1);

        public void Cancel()
        {
            ThrowIfDisposed();
            _timer.Cancel();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            _timer.Start();
            return Task.FromResult(0);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(() => _shutdownCancellationTokenSource.Cancel()))
            {
                ThrowIfDisposed();
                _timer.Cancel();
                await Task.WhenAll(_processing);
                await _timer.StopAsync(cancellationToken);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _timer.Dispose();
                _shutdownCancellationTokenSource.Dispose();
                _disposed = true;
            }
        }

        public async Task<TaskSeriesCommandResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            lock (_stopWaitingTaskSourceLock)
            {
                if (_stopWaitingTaskSource != null)
                {
                    _stopWaitingTaskSource.TrySetResult(null);
                }

                _stopWaitingTaskSource = new TaskCompletionSource<object>();
            }

            IEnumerable<CloudQueueMessage> batch = null;
            string clientRequestId = Guid.NewGuid().ToString();
            Stopwatch sw = null;
            try
            {
                if (!_queueExists.HasValue || !_queueExists.Value)
                {
                    // Before querying the queue, determine if it exists. This
                    // avoids generating unecessary exceptions (which pollute AppInsights logs)
                    // Once we establish the queue exists, we won't do the existence
                    // check anymore (steady state).
                    // However the queue can always be deleted from underneath us, in which case
                    // we need to recheck. That is handled below.
                    _queueExists = await _queue.ExistsAsync();
                }

                if (_queueExists.Value)
                {
                    sw = Stopwatch.StartNew();
                    OperationContext context = new OperationContext { ClientRequestID = clientRequestId };

                    batch = await TimeoutHandler.ExecuteWithTimeout(nameof(CloudQueue.GetMessageAsync), context.ClientRequestID,
                        _exceptionHandler, _logger, cancellationToken, () =>
                        {
                            return _queue.GetMessagesAsync(_queueProcessor.BatchSize,
                                _visibilityTimeout,
                                options: DefaultQueueRequestOptions,
                                operationContext: context,
                                cancellationToken: cancellationToken);
                        });

                    int count = batch?.Count() ?? -1;
                    Logger.GetMessages(_logger, _functionDescriptor.LogName, _queue.Name, context.ClientRequestID, count, sw.ElapsedMilliseconds);
                }
            }
            catch (StorageException exception)
            {
                // if we get ANY errors querying the queue reset our existence check
                // doing this on all errors rather than trying to special case not
                // found, because correctness is the most important thing here
                _queueExists = null;

                if (exception.IsNotFoundQueueNotFound() ||
                    exception.IsConflictQueueBeingDeletedOrDisabled() ||
                    exception.IsServerSideError())
                {
                    long pollLatency = sw?.ElapsedMilliseconds ?? -1;
                    Logger.HandlingStorageException(_logger, _functionDescriptor.LogName, _queue.Name, clientRequestId, pollLatency, exception);

                    // Back off when no message is available, or when
                    // transient errors occur
                    return CreateBackoffResult();
                }
                else
                {
                    throw;
                }
            }

            if (batch == null)
            {
                return CreateBackoffResult();
            }

            bool foundMessage = false;
            foreach (var message in batch)
            {
                if (message == null)
                {
                    continue;
                }

                foundMessage = true;

                // Note: Capturing the cancellationToken passed here on a task that continues to run is a slight abuse
                // of the cancellation token contract. However, the timer implementation would not dispose of the
                // cancellation token source until it has stopped and perhaps also disposed, and we wait for all
                // outstanding tasks to complete before stopping the timer.
                Task task = ProcessMessageAsync(message, _visibilityTimeout, cancellationToken);

                // Having both WaitForNewBatchThreshold and this method mutate _processing is safe because the timer
                // contract is serial: it only calls ExecuteAsync once the wait expires (and the wait won't expire until
                // WaitForNewBatchThreshold has finished mutating _processing).
                _processing.Add(task);
            }

            // Back off when no message was found.
            if (!foundMessage)
            {
                return CreateBackoffResult();
            }

            _foundMessageSinceLastDelay = true;
            return CreateSucceededResult();
        }

        public void Notify()
        {
            lock (_stopWaitingTaskSourceLock)
            {
                if (_stopWaitingTaskSource != null)
                {
                    _stopWaitingTaskSource.TrySetResult(null);
                }
            }
        }

        private Task CreateDelayWithNotificationTask()
        {
            TimeSpan nextDelay = _delayStrategy.GetNextDelay(executionSucceeded: _foundMessageSinceLastDelay);
            Task normalDelay = Task.Delay(nextDelay);
            _foundMessageSinceLastDelay = false;

            Logger.BackoffDelay(_logger, _functionDescriptor.LogName, _queue.Name, nextDelay.TotalMilliseconds);

            return Task.WhenAny(_stopWaitingTaskSource.Task, normalDelay);
        }

        private TaskSeriesCommandResult CreateBackoffResult()
        {
            return new TaskSeriesCommandResult(wait: CreateDelayWithNotificationTask());
        }

        private TaskSeriesCommandResult CreateSucceededResult()
        {
            Task wait = WaitForNewBatchThreshold();
            return new TaskSeriesCommandResult(wait);
        }

        private async Task WaitForNewBatchThreshold()
        {
            while (_processing.Count > _queueProcessor.NewBatchThreshold)
            {
                Task processed = await Task.WhenAny(_processing);
                _processing.Remove(processed);
            }
        }

        internal async Task ProcessMessageAsync(CloudQueueMessage message, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
        {
            try
            {
                if (!await _queueProcessor.BeginProcessingMessageAsync(message, cancellationToken))
                {
                    return;
                }

                FunctionResult result = null;
                using (ITaskSeriesTimer timer = CreateUpdateMessageVisibilityTimer(_queue, message, visibilityTimeout, _exceptionHandler))
                {
                    timer.Start();

                    result = await _triggerExecutor.ExecuteAsync(message, cancellationToken);

                    await timer.StopAsync(cancellationToken);
                }

                // Use a different cancellation token for shutdown to allow graceful shutdown.
                // Specifically, don't cancel the completion or update of the message itself during graceful shutdown.
                // Only cancel completion or update of the message if a non-graceful shutdown is requested via _shutdownCancellationTokenSource.
                await _queueProcessor.CompleteProcessingMessageAsync(message, result, _shutdownCancellationTokenSource.Token);
            }
            catch (StorageException ex) when (ex.IsTaskCanceled())
            {
                // TaskCanceledExceptions may be wrapped in StorageException.
            }
            catch (OperationCanceledException)
            {
                // Don't fail the top-level task when an inner task cancels.
            }
            catch (Exception exception)
            {
                // Immediately report any unhandled exception from this background task.
                // (Don't capture the exception as a fault of this Task; that would delay any exception reporting until
                // Stop is called, which might never happen.)
                _exceptionHandler.OnUnhandledExceptionAsync(ExceptionDispatchInfo.Capture(exception)).GetAwaiter().GetResult();
            }
        }

        private void OnMessageAddedToPoisonQueue(object sender, PoisonMessageEventArgs e)
        {
            // TODO: this is assuming that the poison queue is in the same
            // storage account
            _sharedWatcher.Notify(e.PoisonQueue.Name);
        }

        private ITaskSeriesTimer CreateUpdateMessageVisibilityTimer(CloudQueue queue,
            CloudQueueMessage message, TimeSpan visibilityTimeout,
            IWebJobsExceptionHandler exceptionHandler)
        {
            // Update a message's visibility when it is halfway to expiring.
            TimeSpan normalUpdateInterval = new TimeSpan(visibilityTimeout.Ticks / 2);

            IDelayStrategy speedupStrategy = new LinearSpeedupStrategy(normalUpdateInterval, MinimumVisibilityRenewalInterval);
            ITaskSeriesCommand command = new UpdateQueueMessageVisibilityCommand(queue, message, visibilityTimeout, speedupStrategy);
            return new TaskSeriesTimer(command, exceptionHandler, Task.Delay(normalUpdateInterval));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(null);
            }
        }

        internal static QueueProcessor CreateQueueProcessor(CloudQueue queue, CloudQueue poisonQueue, ILoggerFactory loggerFactory, IQueueProcessorFactory queueProcessorFactory,
            QueuesOptions queuesOptions, EventHandler<PoisonMessageEventArgs> poisonQueueMessageAddedHandler)
        {
            QueueProcessorFactoryContext context = new QueueProcessorFactoryContext(queue, loggerFactory, queuesOptions, poisonQueue);

            QueueProcessor queueProcessor = null;
            if (HostQueueNames.IsHostQueue(queue.Name) &&
                string.Compare(queue.Uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) != 0)
            {
                // We only delegate to the processor factory for application queues,
                // not our built in control queues
                // We bypass this check for local testing though
                queueProcessor = new QueueProcessor(context);
            }
            else
            {
                queueProcessor = queueProcessorFactory.Create(context);
            }

            if (poisonQueueMessageAddedHandler != null)
            {
                queueProcessor.MessageAddedToPoisonQueue += poisonQueueMessageAddedHandler;
            }

            return queueProcessor;
        }

        public ScaleMonitorDescriptor Descriptor
        {
            get
            {
                return _scaleMonitorDescriptor;
            }
        }

        async Task<ScaleMetrics> IScaleMonitor.GetMetricsAsync()
        {
            return await GetMetricsAsync();
        }

        public async Task<QueueTriggerMetrics> GetMetricsAsync()
        {
            int queueLength = 0;
            TimeSpan queueTime = TimeSpan.Zero;

            try
            {
                await _queue.FetchAttributesAsync();
                queueLength = _queue.ApproximateMessageCount.GetValueOrDefault();

                if (queueLength > 0)
                {
                    CloudQueueMessage message = await _queue.PeekMessageAsync();
                    if (message != null)
                    {
                        if (message.InsertionTime.HasValue)
                        {
                            queueTime = DateTime.UtcNow.Subtract(message.InsertionTime.Value.DateTime);
                        }
                    }
                    else
                    {
                        // ApproximateMessageCount often returns a stale value,
                        // especially when the queue is empty.
                        queueLength = 0;
                    }
                }
            }
            catch (StorageException ex)
            {
                if (ex.IsNotFoundQueueNotFound() ||
                    ex.IsConflictQueueBeingDeletedOrDisabled() ||
                    ex.IsServerSideError())
                {
                    // ignore transient errors, and return default metrics
                    // E.g. if the queue doesn't exist, we'll return a zero queue length
                    // and scale in
                    _logger.LogWarning($"Error querying for queue scale status: {ex.Message}");
                }
            }

            return new QueueTriggerMetrics
            {
                QueueLength = queueLength,
                QueueTime = queueTime,
                Timestamp = DateTime.UtcNow
            };
        }

        ScaleStatus IScaleMonitor.GetScaleStatus(ScaleStatusContext context)
        {
            return GetScaleStatusCore(context.WorkerCount, context.Metrics?.Cast<QueueTriggerMetrics>().ToArray());
        }

        public ScaleStatus GetScaleStatus(ScaleStatusContext<QueueTriggerMetrics> context)
        {
            return GetScaleStatusCore(context.WorkerCount, context.Metrics?.ToArray());
        }

        private ScaleStatus GetScaleStatusCore(int workerCount, QueueTriggerMetrics[] metrics)
        {
            ScaleStatus status = new ScaleStatus
            {
                Vote = ScaleVote.None
            };

            // verify we have enough samples to make a scale decision.
            if (metrics == null || (metrics.Length < NumberOfSamplesToConsider))
            {
                return status;
            }

            // Maintain a minimum ratio of 1 worker per 1,000 queue messages.
            long latestQueueLength = metrics.Last().QueueLength;
            if (latestQueueLength > workerCount * 1000)
            {
                status.Vote = ScaleVote.ScaleOut;
                _logger.LogInformation($"QueueLength ({latestQueueLength}) > workerCount ({workerCount}) * 1,000");
                _logger.LogInformation($"Length of queue ({_queue.Name}, {latestQueueLength}) is too high relative to the number of instances ({workerCount}).");
                return status;
            }

            // Check to see if the queue has been empty for a while.
            bool queueIsIdle = metrics.All(p => p.QueueLength == 0);
            if (queueIsIdle)
            {
                status.Vote = ScaleVote.ScaleIn;
                _logger.LogInformation($"Queue '{_queue.Name}' is idle");
                return status;
            }

            // Samples are in chronological order. Check for a continuous increase in time or length.
            // If detected, this results in an automatic scale out.
            if (metrics[0].QueueLength > 0)
            {
                bool queueLengthIncreasing =
                IsTrueForLastN(
                    metrics,
                    NumberOfSamplesToConsider,
                    (prev, next) => prev.QueueLength < next.QueueLength);
                if (queueLengthIncreasing)
                {
                    status.Vote = ScaleVote.ScaleOut;
                    _logger.LogInformation($"Queue length is increasing for '{_queue.Name}'");
                    return status;
                }
            }

            if (metrics[0].QueueTime > TimeSpan.Zero && metrics[0].QueueTime < metrics[NumberOfSamplesToConsider - 1].QueueTime)
            {
                bool queueTimeIncreasing =
                    IsTrueForLastN(
                        metrics,
                        NumberOfSamplesToConsider,
                        (prev, next) => prev.QueueTime <= next.QueueTime);
                if (queueTimeIncreasing)
                {
                    status.Vote = ScaleVote.ScaleOut;
                    _logger.LogInformation($"Queue time is increasing for '{_queue.Name}'");
                    return status;
                }
            }

            bool queueLengthDecreasing =
                IsTrueForLastN(
                    metrics,
                    NumberOfSamplesToConsider,
                    (prev, next) => prev.QueueLength > next.QueueLength);
            if (queueLengthDecreasing)
            {
                status.Vote = ScaleVote.ScaleIn;
                _logger.LogInformation($"Queue length is decreasing for '{_queue.Name}'");
                return status;
            }

            bool queueTimeDecreasing = IsTrueForLastN(
                metrics,
                NumberOfSamplesToConsider,
                (prev, next) => prev.QueueTime > next.QueueTime);
            if (queueTimeDecreasing)
            {
                status.Vote = ScaleVote.ScaleIn;
                _logger.LogInformation($"Queue time is decreasing for '{_queue.Name}'");
                return status;
            }

            _logger.LogInformation($"Queue '{_queue.Name}' is steady");

            return status;
        }

        private static bool IsTrueForLastN(IList<QueueTriggerMetrics> samples, int count, Func<QueueTriggerMetrics, QueueTriggerMetrics, bool> predicate)
        {
            Debug.Assert(count > 1, "count must be greater than 1.");
            Debug.Assert(count <= samples.Count, "count must be less than or equal to the list size.");

            // Walks through the list from left to right starting at len(samples) - count.
            for (int i = samples.Count - count; i < samples.Count - 1; i++)
            {
                if (!predicate(samples[i], samples[i + 1]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
