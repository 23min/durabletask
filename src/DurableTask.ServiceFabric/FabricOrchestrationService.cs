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

namespace DurableTask.ServiceFabric
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Threading;
    using System.Threading.Tasks;
    using DurableTask.Common;
    using DurableTask.Tracking;
    using Microsoft.ServiceFabric.Data;
    using Microsoft.ServiceFabric.Data.Collections;

    public class FabricOrchestrationService : IOrchestrationService
    {
        IReliableStateManager stateManager;
        IFabricOrchestrationServiceInstanceStore instanceStore;

        SessionsProvider orchestrationProvider;
        ActivitiesProvider activitiesProvider;

        public FabricOrchestrationService(IReliableStateManager stateManager, IFabricOrchestrationServiceInstanceStore instanceStore)
        {
            if (stateManager == null)
            {
                throw new ArgumentNullException(nameof(stateManager));
            }

            this.stateManager = stateManager;
            this.instanceStore = instanceStore;
        }

        public async Task StartAsync()
        {
            var orchestrations = await this.stateManager.GetOrAddAsync<IReliableDictionary<string, PersistentSession>>(Constants.OrchestrationDictionaryName);
            this.activitiesProvider = new ActivitiesProvider(this.stateManager);
            this.orchestrationProvider = new SessionsProvider(stateManager, orchestrations);
            await this.instanceStore.StartAsync();
            await this.orchestrationProvider.StartAsync();
            await this.activitiesProvider.StartAsync();
        }

        public Task StopAsync()
        {
            return StopAsync(false);
        }

        public async Task StopAsync(bool isForced)
        {
            this.activitiesProvider.Stop();
            this.orchestrationProvider.Stop();
            await this.instanceStore.StopAsync(isForced);
        }

        public Task CreateAsync()
        {
            return CreateAsync(true);
        }

        public async Task CreateAsync(bool recreateInstanceStore)
        {
            await DeleteAsync(deleteInstanceStore: recreateInstanceStore);
            // Actual creation will be done on demand when we call GetOrAddAsync in StartAsync method.
        }

        public Task CreateIfNotExistsAsync()
        {
            return Task.FromResult<object>(null);
        }

        public Task DeleteAsync()
        {
            return DeleteAsync(true);
        }

        public async Task DeleteAsync(bool deleteInstanceStore)
        {
            await this.stateManager.RemoveAsync(Constants.OrchestrationDictionaryName);
            await this.stateManager.RemoveAsync(Constants.ActivitiesQueueName);
        }

        public bool IsMaxMessageCountExceeded(int currentMessageCount, OrchestrationRuntimeState runtimeState)
        {
            //Todo: Do we need to enforce a limit here?
            return false;
        }

        public int GetDelayInSecondsAfterOnProcessException(Exception exception)
        {
            //Todo: Need to fine tune
            if (exception is TimeoutException)
            {
                return 1;
            }

            return 0;
        }

        public int GetDelayInSecondsAfterOnFetchException(Exception exception)
        {
            //Todo: Need to fine tune
            if (exception is TimeoutException)
            {
                return 1;
            }

            return 0;
        }

        public int TaskOrchestrationDispatcherCount => 1;
        public int MaxConcurrentTaskOrchestrationWorkItems => 1;

        // Note: Do not rely on cancellationToken parameter to this method because the top layer does not yet implement any cancellation.
        public async Task<TaskOrchestrationWorkItem> LockNextTaskOrchestrationWorkItemAsync(TimeSpan receiveTimeout, CancellationToken cancellationToken)
        {
            var currentSession = await this.orchestrationProvider.AcceptSessionAsync(receiveTimeout);

            if (currentSession == null)
            {
                return null;
            }

            var newMessages = this.orchestrationProvider.GetSessionMessages(currentSession);
            return new TaskOrchestrationWorkItem()
            {
                NewMessages = newMessages,
                InstanceId = currentSession.SessionId,
                OrchestrationRuntimeState = new OrchestrationRuntimeState(currentSession.SessionState.ToImmutableList())
            };
        }

        public Task RenewTaskOrchestrationWorkItemLockAsync(TaskOrchestrationWorkItem workItem)
        {
            throw new NotImplementedException();
        }

        public async Task CompleteTaskOrchestrationWorkItemAsync(
            TaskOrchestrationWorkItem workItem,
            OrchestrationRuntimeState newOrchestrationRuntimeState,
            IList<TaskMessage> outboundMessages,
            IList<TaskMessage> orchestratorMessages,
            IList<TaskMessage> timerMessages,
            TaskMessage continuedAsNewMessage,
            OrchestrationState orchestrationState)
        {
            if (continuedAsNewMessage != null)
            {
                throw new Exception("ContinueAsNew is not supported yet");
            }

            using (var txn = this.stateManager.CreateTransaction())
            {
                await this.activitiesProvider.AppendBatch(txn, outboundMessages);

                await this.orchestrationProvider.CompleteAndUpdateSession(txn, workItem.InstanceId, newOrchestrationRuntimeState, timerMessages);

                if (orchestratorMessages?.Count > 0)
                {
                    await this.orchestrationProvider.AppendMessageBatchAsync(txn, orchestratorMessages);
                }

                if (this.instanceStore != null)
                {
                    await this.instanceStore.WriteEntitesAsync(txn, new InstanceEntityBase[]
                    {
                        new OrchestrationStateInstanceEntity()
                        {
                            State = Utils.BuildOrchestrationState(workItem.OrchestrationRuntimeState)
                        }
                    });
                }

                await txn.CommitAsync();
            }
        }

        public Task AbandonTaskOrchestrationWorkItemAsync(TaskOrchestrationWorkItem workItem)
        {
            return Task.FromResult<object>(null);
        }

        public async Task ReleaseTaskOrchestrationWorkItemAsync(TaskOrchestrationWorkItem workItem)
        {
            if (workItem.OrchestrationRuntimeState.OrchestrationStatus.IsTerminalState())
            {
                using (var txn = this.stateManager.CreateTransaction())
                {
                    await this.orchestrationProvider.ReleaseSession(txn, workItem.InstanceId);
                    await txn.CommitAsync();
                    ProviderEventSource.Instance.LogOrchestrationFinished(workItem.InstanceId,
                        workItem.OrchestrationRuntimeState.OrchestrationStatus.ToString(),
                        (workItem.OrchestrationRuntimeState.CompletedTime - workItem.OrchestrationRuntimeState.CreatedTime).TotalSeconds,
                        workItem.OrchestrationRuntimeState.Output);
                }
            }
        }

        public int TaskActivityDispatcherCount => 1;
        public int MaxConcurrentTaskActivityWorkItems => 1;

        // Note: Do not rely on cancellationToken parameter to this method because the top layer does not yet implement any cancellation.
        public async Task<TaskActivityWorkItem> LockNextTaskActivityWorkItem(TimeSpan receiveTimeout, CancellationToken cancellationToken)
        {
            var currentActivity = await this.activitiesProvider.GetNextWorkItem(receiveTimeout);

            if (currentActivity != null)
            {
                return new TaskActivityWorkItem()
                {
                    Id = Guid.NewGuid().ToString(), //Todo: Do we need to persist this in activity queue?
                    TaskMessage = currentActivity
                };
            }

            return null;
        }

        public async Task CompleteTaskActivityWorkItemAsync(TaskActivityWorkItem workItem, TaskMessage responseMessage)
        {
            using (var txn = this.stateManager.CreateTransaction())
            {
                await this.activitiesProvider.CompleteWorkItem(txn, workItem.TaskMessage);
                var sessionId = workItem.TaskMessage.OrchestrationInstance.InstanceId;
                await this.orchestrationProvider.AppendMessageAsync(txn, responseMessage);
                await txn.CommitAsync();
            }
        }

        public Task AbandonTaskActivityWorkItemAsync(TaskActivityWorkItem workItem)
        {
            return Task.FromResult<object>(null);
        }

        public bool ProcessWorkItemSynchronously => true;

        public Task<TaskActivityWorkItem> RenewTaskActivityWorkItemLockAsync(TaskActivityWorkItem workItem)
        {
            return Task.FromResult(workItem);
        }
    }
}
