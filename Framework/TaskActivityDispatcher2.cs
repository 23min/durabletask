﻿//  ----------------------------------------------------------------------------------
//  Copyright Microsoft Corporation
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  ----------------------------------------------------------------------------------

namespace DurableTask
{
    using System;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Exceptions;
    using History;
    using Microsoft.ServiceBus.Messaging;
    using Tracing;

    public sealed class TaskActivityDispatcher2 : DispatcherBase<TaskActivityWorkItem>
    {
        readonly NameVersionObjectManager<TaskActivity> objectManager;
        readonly TaskHubWorkerSettings settings;
        IOrchestrationService orchestrationService;

        internal TaskActivityDispatcher2(
            TaskHubWorkerSettings workerSettings,
            IOrchestrationService orchestrationService,
            NameVersionObjectManager<TaskActivity> objectManager)
            : base("TaskActivityDispatcher", item => item.Id)
        {
            // AFFANDAR : TODO : arg checks?
            settings = workerSettings.Clone();
            this.orchestrationService = orchestrationService;
            this.objectManager = objectManager;
            maxConcurrentWorkItems = settings.TaskActivityDispatcherSettings.MaxConcurrentActivities;
        }

        public bool IncludeDetails { get; set; }

        protected override Task<TaskActivityWorkItem> OnFetchWorkItemAsync(TimeSpan receiveTimeout)
        {
            return this.orchestrationService.LockNextTaskActivityWorkItem(receiveTimeout, CancellationToken.None);
        }

        protected override async Task OnProcessWorkItemAsync(TaskActivityWorkItem workItem)
        {
            // AFFANDAR : TODO : add this to the orchestration service impl
            //Utils.CheckAndLogDeliveryCount(message, taskHubDescription.MaxTaskActivityDeliveryCount);

            Task renewTask = null;
            var renewCancellationTokenSource = new CancellationTokenSource();

            try
            {
                TaskMessage taskMessage = workItem.TaskMessage;
                OrchestrationInstance orchestrationInstance = taskMessage.OrchestrationInstance;
                if (orchestrationInstance == null || string.IsNullOrWhiteSpace(orchestrationInstance.InstanceId))
                {
                    throw TraceHelper.TraceException(TraceEventType.Error,
                        new InvalidOperationException("Message does not contain any OrchestrationInstance information"));
                }
                if (taskMessage.Event.EventType != EventType.TaskScheduled)
                {
                    throw TraceHelper.TraceException(TraceEventType.Critical,
                        new NotSupportedException("Activity worker does not support event of type: " +
                                                  taskMessage.Event.EventType));
                }

                // call and get return message
                var scheduledEvent = (TaskScheduledEvent) taskMessage.Event;
                TaskActivity taskActivity = objectManager.GetObject(scheduledEvent.Name, scheduledEvent.Version);
                if (taskActivity == null)
                {
                    throw new TypeMissingException("TaskActivity " + scheduledEvent.Name + " version " +
                                                   scheduledEvent.Version + " was not found");
                }

                renewTask = Task.Factory.StartNew(() => RenewUntil(workItem, renewCancellationTokenSource.Token));

                // TODO : pass workflow instance data
                var context = new TaskContext(taskMessage.OrchestrationInstance);
                HistoryEvent eventToRespond = null;

                try
                {
                    string output = await taskActivity.RunAsync(context, scheduledEvent.Input);
                    eventToRespond = new TaskCompletedEvent(-1, scheduledEvent.EventId, output);
                }
                catch (TaskFailureException e)
                {
                    TraceHelper.TraceExceptionInstance(TraceEventType.Error, taskMessage.OrchestrationInstance, e);
                    string details = IncludeDetails ? e.Details : null;
                    eventToRespond = new TaskFailedEvent(-1, scheduledEvent.EventId, e.Message, details);
                }
                catch (Exception e)
                {
                    TraceHelper.TraceExceptionInstance(TraceEventType.Error, taskMessage.OrchestrationInstance, e);
                    string details = IncludeDetails
                        ? string.Format("Unhandled exception while executing task: {0}\n\t{1}", e, e.StackTrace)
                        : null;
                    eventToRespond = new TaskFailedEvent(-1, scheduledEvent.EventId, e.Message, details);
                }

                var responseTaskMessage = new TaskMessage();
                responseTaskMessage.Event = eventToRespond;
                responseTaskMessage.OrchestrationInstance = orchestrationInstance;

                await this.orchestrationService.CompleteTaskActivityWorkItemAsync(workItem, responseTaskMessage);
            }
            finally
            {
                if (renewTask != null)
                {
                    renewCancellationTokenSource.Cancel();
                    renewTask.Wait();
                }
            }
        }

        async void RenewUntil(TaskActivityWorkItem workItem, CancellationToken cancellationToken)
        {
            try
            {
                if (workItem.LockedUntilUtc < DateTime.UtcNow)
                {
                    return;
                }

                DateTime renewAt = workItem.LockedUntilUtc.Subtract(TimeSpan.FromSeconds(30));

                // service bus clock sku can really mess us up so just always renew every 30 secs regardless of 
                // what the message.LockedUntilUtc says. if the sku is negative then in the worst case we will be
                // renewing every 5 secs
                //
                renewAt = AdjustRenewAt(renewAt);

                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));

                    if (DateTime.UtcNow >= renewAt)
                    {
                        try
                        {
                            TraceHelper.Trace(TraceEventType.Information, "Renewing lock for workitem id {0}",
                                workItem.Id);
                            workItem = await this.orchestrationService.RenewTaskActivityWorkItemLockAsync(workItem);
                            renewAt = workItem.LockedUntilUtc.Subtract(TimeSpan.FromSeconds(30));
                            renewAt = AdjustRenewAt(renewAt);
                            TraceHelper.Trace(TraceEventType.Information, "Next renew for workitem id '{0}' at '{1}'",
                                workItem.Id, renewAt);
                        }
                        catch (Exception exception)
                        {
                            // might have been completed
                            TraceHelper.TraceException(TraceEventType.Information, exception,
                                "Failed to renew lock for workitem {0}", workItem.Id);
                            break;
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // brokeredmessage is already disposed probably through 
                // a complete call in the main dispatcher thread
            }
        }

        DateTime AdjustRenewAt(DateTime renewAt)
        {
            DateTime maxRenewAt = DateTime.UtcNow.Add(TimeSpan.FromSeconds(30));

            if (renewAt > maxRenewAt)
            {
                return maxRenewAt;
            }

            return renewAt;
        }

        // AFFANDAR : TODO : all of this crap has to go away, have to redo dispatcher base
        protected override void OnStart()
        {
        }

        protected override void OnStopping(bool isForced)
        {
        }

        protected override void OnStopped(bool isForced)
        {
        }

        protected override Task SafeReleaseWorkItemAsync(TaskActivityWorkItem workItem)
        {
            return Task.FromResult<object>(null);
        }

        protected override Task AbortWorkItemAsync(TaskActivityWorkItem workItem)
        {
            return this.orchestrationService.AbandonTaskActivityWorkItemAsync(workItem);
        }

        protected override int GetDelayInSecondsAfterOnProcessException(Exception exception)
        {
            if (exception is MessagingException)
            {
                return settings.TaskActivityDispatcherSettings.TransientErrorBackOffSecs;
            }

            return 0;
        }

        protected override int GetDelayInSecondsAfterOnFetchException(Exception exception)
        {
            int delay = settings.TaskActivityDispatcherSettings.NonTransientErrorBackOffSecs;
            if (exception is MessagingException && (exception as MessagingException).IsTransient)
            {
                delay = settings.TaskActivityDispatcherSettings.TransientErrorBackOffSecs;
            }
            return delay;
        }
    }
}