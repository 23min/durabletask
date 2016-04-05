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

using System.Collections.Generic;

namespace DurableTask
{
    using System;
    using System.Runtime.Serialization;

    [DataContract]
    public class OrchestrationState
    {
        [DataMember] public DateTime CompletedTime;
        [DataMember] public long CompressedSize;
        [DataMember] public DateTime CreatedTime;
        [DataMember] public string Input;
        [DataMember] public DateTime LastUpdatedTime;
        [DataMember] public string Name;
        [DataMember] public OrchestrationInstance OrchestrationInstance;
        [DataMember] public OrchestrationStatus OrchestrationStatus;
        [DataMember] public string Output;
        [DataMember] public ParentInstance ParentInstance;
        [DataMember] public long Size;
        [DataMember] public string Status;
        [DataMember] public Dictionary<string, string> Tags;
        [DataMember] public string Version;
    }
}