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
    using System.Diagnostics;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using DurableTask.Tracking;
    using Microsoft.ServiceFabric.Data;
    using Microsoft.ServiceFabric.Data.Collections;

    // Potential todo:
    //   - Support for querying state across executions (makes sense only after ContinuedAsNew is supported)
    //   - Support writing multiple state events for a given orchestration instance/execution (?)
    //   - Support writing/querying/purging history events
    class FabricOrchestrationInstanceStore : IFabricOrchestrationServiceInstanceStore
    {
        const string InstanceStoreCollectionNamePrefix = Constants.CollectionNameUniquenessPrefix + "InstSt_";
        const string TimeFormatString =  "yyyy-MM-dd-HH";
        const string TimeFormatStringPrefix = "yyyy-MM-dd-";
        readonly IReliableStateManager stateManager;

        CancellationTokenSource cancellationTokenSource;
        IReliableDictionary<string, OrchestrationState> instanceStore;
        IReliableDictionary<string, string> executionIdStore;

        public FabricOrchestrationInstanceStore(IReliableStateManager stateManager)
        {
            this.stateManager = stateManager;
        }

        public Task InitializeStoreAsync(bool recreate)
        {
            if (recreate)
            {
                return DeleteStoreAsync();
            }

            return CompletedTask.Default;
        }

        public async Task StartAsync()
        {
            this.cancellationTokenSource = new CancellationTokenSource();
            await EnsureStoreInitialized();
            var nowait = CleanupOldDictionaries();
        }

        public Task StopAsync(bool isForced)
        {
            this.cancellationTokenSource.Cancel();
            return CompletedTask.Default;
        }

        public async Task DeleteStoreAsync()
        {
            await this.stateManager.RemoveAsync(Constants.InstanceStoreDictionaryName);
            await this.stateManager.RemoveAsync(Constants.ExecutionStoreDictionaryName);
        }

        public async Task WriteEntitesAsync(ITransaction transaction, IEnumerable<InstanceEntityBase> entities)
        {
            await EnsureStoreInitialized();
            foreach (var entity in entities)
            {
                var state = entity as OrchestrationStateInstanceEntity;
                if (state != null && state.State != null)
                {
                    var instance = state.State.OrchestrationInstance;
                    string key = GetKey(instance.InstanceId, instance.ExecutionId);

                    if (state.State.OrchestrationStatus.IsRunningOrPending())
                    {
                        if (state.State.OrchestrationStatus == OrchestrationStatus.Pending)
                        {
                            await this.executionIdStore.AddOrUpdateAsync(transaction, instance.InstanceId, instance.ExecutionId, (k, old) => instance.ExecutionId);
                        }
                        await this.instanceStore.AddOrUpdateAsync(transaction, key, state.State,
                            (k, oldValue) => state.State);
                    }
                    else
                    {
                        var backupDictionaryName = GetDictionaryKeyFromTimeFormat(DateTime.UtcNow);
                        // It's intentional to not pass 'transaction' parameter to this call, this API doesn't seem to follow
                        // see-what-you-commit-within-the-transaction rules. If we add it within the transaction and immediately
                        // try to AddOrUpdateAsync an entry in dictionary that doesn't work.
                        var backupDictionary = await this.stateManager.GetOrAddAsync<IReliableDictionary<string, OrchestrationState>>(backupDictionaryName);
                        await backupDictionary.AddOrUpdateAsync(transaction, key, state.State,
                            (k, oldValue) => state.State);
                        await this.instanceStore.TryRemoveAsync(transaction, key);
                    }
                }
                else
                {
                    throw new NotSupportedException();
                }
            }
        }

        public async Task<IList<OrchestrationStateInstanceEntity>> GetOrchestrationStateAsync(string instanceId, bool allInstances)
        {
            if (allInstances)
            {
                throw new NotImplementedException("Querying for state across all executions for an orchestration is not supported, only the latest execution can be queried");
            }

            await EnsureStoreInitialized();

            string latestExecutionId = null;
            using (var tx = this.stateManager.CreateTransaction())
            {
                var executionIdValue = await this.executionIdStore.TryGetValueAsync(tx, instanceId);
                if (executionIdValue.HasValue)
                {
                    latestExecutionId = executionIdValue.Value;
                }
            }

            if (latestExecutionId != null)
            {
                var state = await GetOrchestrationStateAsync(instanceId, latestExecutionId);
                if (state != null)
                {
                    return new List<OrchestrationStateInstanceEntity>() { state };
                }
            }

            return ImmutableList<OrchestrationStateInstanceEntity>.Empty;
        }

        public async Task<OrchestrationStateInstanceEntity> GetOrchestrationStateAsync(string instanceId, string executionId)
        {
            await EnsureStoreInitialized();
            var queryKey = this.GetKey(instanceId, executionId);
            using (var txn = this.stateManager.CreateTransaction())
            {
                var state = await this.instanceStore.TryGetValueAsync(txn, queryKey);
                if (state.HasValue)
                {
                    return new OrchestrationStateInstanceEntity()
                    {
                        State = state.Value,
                    };
                }
            }

            // If querying for orchestration which completed an hour ago, we won't return the results.
            var now = DateTime.UtcNow;
            for (int i = 0; i < 2; i++)
            {
                var backupDictionaryName = GetDictionaryKeyFromTimeFormat(now - TimeSpan.FromHours(i));
                var backupDictionary = await this.stateManager.TryGetAsync<IReliableDictionary<string, OrchestrationState>>(backupDictionaryName);

                if (backupDictionary.HasValue)
                {
                    using (var txn = this.stateManager.CreateTransaction())
                    {
                        var state = await backupDictionary.Value.TryGetValueAsync(txn, queryKey);
                        if (state.HasValue)
                        {
                            return new OrchestrationStateInstanceEntity()
                            {
                                State = state.Value,
                            };
                        }
                    }
                }
            }

            return null;
        }

        public Task<IEnumerable<OrchestrationWorkItemInstanceEntity>> GetOrchestrationHistoryEventsAsync(string instanceId, string executionId)
        {
            throw new NotImplementedException();
        }

        // Todo: This is incomplete and inaccurate implemenation done for testing purposes.
        // The method will cleanup state for every orchestration happening in the hour time window of given time,
        // for example, if the given time is 9.35, it will delete state for all orchestrations that
        // are completed between 9.00 to 9.59!!!
        public Task PurgeOrchestrationHistoryEventsAsync(DateTime threshholdHourlyDateTimeUtc)
        {
            return this.stateManager.RemoveAsync(GetDictionaryKeyFromTimeFormat(threshholdHourlyDateTimeUtc));
        }

        string GetKey(string instanceId, string executionId)
        {
            return string.Concat(instanceId, "_", executionId);
        }

        string GetDictionaryKeyFromTimeFormat(DateTime time)
        {
            return GetInstanceStoreBackupDictionaryKey(time, TimeFormatString);
        }

        string GetDictionaryKeyFromTimePrefixFormat(DateTime time)
        {
            return GetInstanceStoreBackupDictionaryKey(time, TimeFormatStringPrefix);
        }

        string GetInstanceStoreBackupDictionaryKey(DateTime time, string formatString)
        {
            return InstanceStoreCollectionNamePrefix + time.ToString(formatString);
        }

        Task CleanupDayOldDictionaries()
        {
            return Utils.RunBackgroundJob(async () =>
            {
                var purgeTime = GetDictionaryKeyFromTimePrefixFormat(DateTime.UtcNow - TimeSpan.FromDays(1));

                for (int i = 0; i < 24; i++)
                {
                    await this.stateManager.RemoveAsync($"{purgeTime}{i:D2}");
                }
            }, initialDelay: TimeSpan.FromMinutes(5), delayOnSuccess: TimeSpan.FromHours(12), delayOnException: TimeSpan.FromHours(1), actionName: $"{nameof(CleanupDayOldDictionaries)}", token: this.cancellationTokenSource.Token);
        }

        Task CleanupOldDictionaries()
        {
            return Utils.RunBackgroundJob(async () =>
            {
                List<string> toDelete = new List<string>();
                List<string> toKeep = new List<string>();
                var currentTime = DateTime.UtcNow;
                var ttl = TimeSpan.FromDays(1);

                var enumerationTime = await Utils.MeasureAsync(async () =>
                {
                    var enumerator = this.stateManager.GetAsyncEnumerator();
                    while (await enumerator.MoveNextAsync(this.cancellationTokenSource.Token))
                    {
                        var storeName = enumerator.Current.Name.AbsolutePath.Trim('/');
                        if (storeName.StartsWith(InstanceStoreCollectionNamePrefix))
                        {
                            DateTime storeTime;
                            if (DateTime.TryParseExact(storeName.Substring(InstanceStoreCollectionNamePrefix.Length), TimeFormatString, CultureInfo.InvariantCulture, DateTimeStyles.None, out storeTime)
                                && (currentTime - storeTime > ttl))
                            {
                                toDelete.Add(storeName);
                                continue;
                            }
                        }

                        toKeep.Add(storeName);
                    }
                });
                ProviderEventSource.Log.LogTimeTaken($"Enumerating all reliable states (count: {toDelete.Count + toKeep.Count})", enumerationTime.TotalMilliseconds);

                ProviderEventSource.Log.ReliableStateManagement($"Deleting {toDelete.Count} stores", String.Join(",", toDelete));
                ProviderEventSource.Log.ReliableStateManagement($"All remaining {toKeep.Count} stores", String.Join(",", toKeep));

                foreach (var storeName in toDelete)
                {
                    var deleteTime = await Utils.MeasureAsync(async () =>
                    {
                        await this.stateManager.RemoveAsync(storeName);
                    });
                    ProviderEventSource.Log.LogTimeTaken($"Deleting reliable state {storeName}", deleteTime.TotalMilliseconds);
                }
            }, initialDelay: TimeSpan.FromMinutes(5), delayOnSuccess: TimeSpan.FromHours(1), delayOnException: TimeSpan.FromMinutes(10), actionName: $"{nameof(CleanupOldDictionaries)}", token: this.cancellationTokenSource.Token);
        }

        async Task EnsureStoreInitialized()
        {
            if (this.instanceStore == null)
            {
                this.instanceStore = await this.stateManager.GetOrAddAsync<IReliableDictionary<string, OrchestrationState>>(Constants.InstanceStoreDictionaryName);
            }
            if (this.executionIdStore == null)
            {
                this.executionIdStore = await this.stateManager.GetOrAddAsync<IReliableDictionary<string, string>>(Constants.ExecutionStoreDictionaryName);
            }
        }
    }
}
