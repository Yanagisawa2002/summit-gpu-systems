using System;
using System.Collections.Generic;
using System.IO;
using Summit.GpuTimestamps;

namespace Summit.PublicIntegration
{
    public static class QueueObservations
    {
        public static IEnumerable<Observation> Enumerate(QueueResult result)
        {
            var source=new ObservationSource(Identity(result.config.sourceSha),Identity(result.buildGuid),
                Identity(result.config.observationDeviceDriverId),"Unity-verified-readback","stopwatch-relative","host-consumer",result.processId);
            foreach(var row in result.jobs)
            {
                if(row==null||row.submitTicks<=0)continue;
                yield return Completion(source,"job:"+row.job,row.submitTicks,row.verifiedTicks,result.clockFrequency,row.sourceFrame,row.observedFrame,row.verified);
            }
        }
        public static IEnumerable<Observation> Enumerate(BoundaryReport result)
        {
            var source=new ObservationSource(Identity(result.config.sourceSha),Identity(result.buildGuid),
                Identity(result.config.observationDeviceDriverId),"Unity-readback-and-route","stopwatch-relative","host-consumer",result.processId);
            for(int i=0;i<result.samples.Count;i++)
            {
                var row=result.samples[i];
                yield return Completion(source,"batch:"+i,row.startTicks,row.endTicks,result.frequency,row.sourceFrame,row.observedFrame,row.verified);
            }
            foreach(var row in result.orders)
                if(row.submitTicks>0)yield return Completion(source,"route:"+row.job,row.submitTicks,row.decisionTicks,result.frequency,row.sourceFrame,row.observedFrame,row.verified);
        }
        static Observation Completion(ObservationSource source,string id,long start,long end,long frequency,int sourceFrame,int observedFrame,bool verified)
        {
            bool valid=verified&&sourceFrame>=0&&start>0&&end>=start&&frequency>0;
            return new Observation(source,id,ObservationScope.QueueCompletion,ObservationUnit.Milliseconds,
                valid?ObservationState.Available:ObservationState.Pending,valid?(double?)((end-start)*1000.0/frequency):null,
                valid?"":"completion-not-verified-or-unattributed",sourceFrame,observedFrame,
                valid?(ulong)start:0,valid?(ulong)end:0,valid?(ulong)frequency:0);
        }
        static string Identity(string value)=>string.IsNullOrWhiteSpace(value)?"unknown":value;
    }
}
