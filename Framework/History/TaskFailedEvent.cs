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
    public class TaskFailedEvent : HistoryEvent
    {
        public TaskFailedEvent(int eventId, int taskScheduledId, string reason, string details)
            : base(eventId)
        {
            TaskScheduledId = taskScheduledId;
            Reason = reason;
            Details = details;
        }

        public override EventType EventType
        {
            get { return EventType.TaskFailed; }
        }

        [DataMember]
        public int TaskScheduledId { get; private set; }

        [DataMember]
        public string Reason { get; private set; }

        [DataMember]
        public string Details { get; private set; }
    }
}