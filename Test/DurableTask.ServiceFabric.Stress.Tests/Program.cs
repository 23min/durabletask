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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DurableTask.Test.Orchestrations.Stress;
using Microsoft.ServiceFabric.Services.Client;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using TestApplication.Common;

namespace DurableTask.ServiceFabric.Stress.Tests
{
    class Program
    {
        static ConcurrentBag<OrchestrationInstance> instances = new ConcurrentBag<OrchestrationInstance>();
        static IRemoteClient serviceClient = ServiceProxy.Create<IRemoteClient>(new Uri("fabric:/TestFabricApplication/TestStatefulService"), new ServicePartitionKey(1));
        static Dictionary<string, long> outcomeFrequencies = new Dictionary<string, long>();

        static void Main(string[] args)
        {
            CancellationTokenSource cts1 = new CancellationTokenSource();
            CancellationTokenSource cts2 = new CancellationTokenSource();

            var programTask = RunOrchestrations(cts1.Token);
            var statusTask = PollState(cts2.Token);

            Console.WriteLine("Press any key to stop running orchestrations and print results");
            Console.ReadKey();

            cts1.Cancel();
            programTask.Wait();

            cts2.Cancel();
            statusTask.Wait();
        }

        static async Task PollState(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await PrintStatusAggregation();
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
            await PrintStatusAggregation();
        }

        static async Task PrintStatusAggregation()
        {
            Console.WriteLine($"Numer of instances so far : {instances.Count}");
            foreach (var instance in instances)
            {
                var state = await serviceClient.GetOrchestrationState(instance);
                var key = state != null ? state.OrchestrationStatus.ToString() : "NullState";

                if (!outcomeFrequencies.ContainsKey(key))
                {
                    outcomeFrequencies.Add(key, 0);
                }

                outcomeFrequencies[key]++;
            }

            foreach (var kvp in outcomeFrequencies)
            {
                Console.WriteLine($"{kvp.Key} : {kvp.Value}");
            }

            outcomeFrequencies.Clear();
        }

        static async Task RunOrchestrations(CancellationToken cancellationToken)
        {
            int totalRequests = 0;
            List<Task> tasks = new List<Task>();
            Console.WriteLine("Starting orchestrations");

            while (!cancellationToken.IsCancellationRequested)
            {
                var newOrchData = new TestOrchestrationData()
                {
                    NumberOfParallelTasks = 15,
                    NumberOfSerialTasks = 5,
                    MaxDelayInMinutes = 0
                };

                totalRequests++;
                var instance = await serviceClient.StartTestOrchestrationAsync(newOrchData);
                instances.Add(instance);
                var waitTask = serviceClient.WaitForOrchestration(instance, TimeSpan.FromMinutes(2));

                tasks.Add(waitTask);

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            Console.WriteLine($"Total orchestrations : {totalRequests}");
            Console.WriteLine("Waiting for pending orchestrations");
            await Task.WhenAll(tasks);

            Console.WriteLine("Done");
        }
    }
}
