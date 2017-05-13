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
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;

namespace DurableTask.ServiceFabric.UnitTests
{
    class Measure
    {
        public static T DataContractSerialization<T>(T testObject)
        {
            return (T)DataContractSerialization(typeof(T), testObject);
        }

        public static object DataContractSerialization(Type testType, object testObject)
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractSerializer(testType);
                Stopwatch timer = Stopwatch.StartNew();
                serializer.WriteObject(stream, testObject);
                timer.Stop();
                Console.WriteLine($"Time for serialization : {timer.ElapsedMilliseconds} ms");
                Console.WriteLine($"Size of serialized stream : {stream.Length} bytes.");

                stream.Position = 0;
                timer.Restart();
                var deserialized = serializer.ReadObject(stream);
                timer.Stop();
                Console.WriteLine($"Time for deserialization : {timer.ElapsedMilliseconds} ms");
                return deserialized;
            }
        }
    }
}
