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

namespace DurableTask.History
{
    using System.Runtime.Serialization;

    [DataContract]
    public class SubOrchestrationInstanceStartFailedEvent : HistoryEvent
    {
        public SubOrchestrationInstanceStartFailedEvent(int eventId, int taskScheduledId, OrchestrationInstanceStartFailureCause cause)
            : base(eventId)
        {
            TaskScheduledId = taskScheduledId;
            Cause = cause;
        }

        public override EventType EventType
        {
            get { return EventType.SubOrchestrationInstanceStartFailed; }
        }

        [DataMember]
        public int TaskScheduledId { get; private set; }

        [DataMember]
        public OrchestrationInstanceStartFailureCause Cause { get; private set; }
    }
}
