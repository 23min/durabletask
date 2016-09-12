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

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DurableTask;
using DurableTask.ServiceFabric;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using TestApplication.Common;
using TestStatefulService.DebugHelper;
using TestStatefulService.TestOrchestrations;
using Microsoft.ServiceFabric.Services.Remoting.Runtime;

namespace TestStatefulService
{
    /// <summary>
    /// An instance of this class is created for each service replica by the Service Fabric runtime.
    /// </summary>
    internal sealed class TestStatefulService : StatefulService, IRemoteClient
    {
        private TaskHubWorker worker;
        private TaskHubClient client;
        private ReplicaRole currentRole;
        private TestExecutor testExecutor;

        public TestStatefulService(StatefulServiceContext context)
            : base(context)
        {
            var instanceStore = new FabricOrchestrationInstanceStore(this.StateManager);
            this.worker = new TaskHubWorker(new FabricOrchestrationService(this.StateManager, instanceStore));
            this.client = new TaskHubClient(new FabricOrchestrationServiceClient(this.StateManager, instanceStore));
            this.testExecutor = new TestExecutor(this.client);
        }

        /// <summary>
        /// Optional override to create listeners (e.g., HTTP, Service Remoting, WCF, etc.) for this service replica to handle client or user requests.
        /// </summary>
        /// <remarks>
        /// For more information on service communication, see http://aka.ms/servicefabricservicecommunication
        /// </remarks>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new[]
            {
                //new ServiceReplicaListener(initParams => new OwinCommunicationListener("TestStatefulService", new Startup(), initParams))
                new ServiceReplicaListener(this.CreateServiceRemotingListener)
            };
        }

        /// <summary>
        /// This is the main entry point for your service replica.
        /// This method executes when this replica of your service becomes primary and has write status.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service replica.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            await this.worker
                .AddTaskOrchestrations(KnownOrchestrationTypeNames.Values.ToArray())
                .AddTaskActivities(typeof(GetUserTask), typeof(GreetUserTask), typeof(GenerationBasicTask))
                .StartAsync();

            //await this.testExecutor.StartAsync();
        }

        protected override async Task OnChangeRoleAsync(ReplicaRole newRole, CancellationToken cancellationToken)
        {
            if (newRole != ReplicaRole.Primary && this.currentRole == ReplicaRole.Primary)
            {
                //await this.testExecutor.StopAsync();
                await this.worker.StopAsync();
            }
            this.currentRole = newRole;
        }

        public async Task<OrchestrationState> RunOrchestrationAsync(string orchestrationTypeName, object input, TimeSpan waitTimeout)
        {
            var instance = await client.CreateOrchestrationInstanceAsync(GetOrchestrationType(orchestrationTypeName), input);
            return await client.WaitForOrchestrationAsync(instance, waitTimeout);
        }

        Type GetOrchestrationType(string typeName)
        {
            if (!KnownOrchestrationTypeNames.ContainsKey(typeName))
            {
                throw new Exception($"Unknown Orchestration Type Name : {typeName}");
            }

            return KnownOrchestrationTypeNames.First(kvp => string.Equals(typeName, kvp.Key)).Value;
        }

        private static Dictionary<string, Type> KnownOrchestrationTypeNames = new Dictionary<string, Type>
        {
            { typeof(SimpleOrchestrationWithTasks).Name, typeof(SimpleOrchestrationWithTasks) },
            { typeof(SimpleOrchestrationWithTimer).Name, typeof(SimpleOrchestrationWithTimer) },
            { typeof(GenerationBasicOrchestration).Name, typeof(GenerationBasicOrchestration) },
        };
    }
}
