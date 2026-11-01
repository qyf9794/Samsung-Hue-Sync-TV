using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HueTvSync
{
    internal static class Sensitivity
    {
        public static double TimeConstant(int level) => level switch
        {
            1 => 0.55, 3 => 0.25, 5 => 0.12,
            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };
        // Preserve the meaning of an explicitly configured legacy per-frame weight
        // at the original nominal 20 Hz when running without a sensitivity control.
        public static double FromLegacyWeight(double weight) => weight>=1 ? 0 : -.05/Math.Log(1-weight);
        public static double Weight(double timeConstant,double elapsedSeconds)
        {
            if(timeConstant<=0) return 1;
            double elapsed=Math.Max(0,Math.Min(.2,elapsedSeconds));
            return 1-Math.Exp(-elapsed/timeConstant);
        }
    }
    internal static class ColorFilter
    {
        public static Rgb[] Apply(IReadOnlyList<Channel> channels,Rgb[] raw,Rgb[]? previous,double spatial,double temporal)
        {
            if(channels.Count!=raw.Length || (previous!=null&&previous.Length!=raw.Length)) throw new ArgumentException("Channel count mismatch");
            var result=(Rgb[])raw.Clone();
            foreach(var group in channels.Select((c,i)=>(c,i)).Where(x=>x.c.Group.Length>0).GroupBy(x=>x.c.Group))
            {
                var indices=group.OrderBy(x=>x.c.X).Select(x=>x.i).ToArray();
                if(indices.Length<2) continue;
                for(int i=0;i<indices.Length;i++)
                {
                    var center=raw[indices[i]];
                    Rgb neighbor=i==0?raw[indices[1]]:i==indices.Length-1?raw[indices[i-1]]:Rgb.Mix(raw[indices[i-1]],raw[indices[i+1]],.5);
                    result[indices[i]]=Rgb.Mix(center,neighbor,spatial);
                }
            }
            if(previous!=null) for(int i=0;i<result.Length;i++) result[i]=Rgb.Mix(previous[i],result[i],temporal);
            return result;
        }
    }
    internal static class SyncRunner
    {
        public static async Task RunAsync(HueBridge bridge,Settings config,Area area,ISampler sampler,CancellationTokenSource stop,int seconds,Action<string>? stateChanged=null,Func<double>? timeConstant=null)
        {
            if(area.Status!="inactive") throw new InvalidOperationException("An entertainment area is active; refusing to interrupt it");
            var first=sampler.Read(area.Channels);
            var identity=await bridge.GetApplicationIdAsync();
            var current=await bridge.GetAreaAsync();
            if(current.Status!="inactive" || current.Id!=area.Id) throw new InvalidOperationException("Entertainment area changed or is active");
            bool started=false;
            try {
                await bridge.ActionAsync(area,"start"); started=true;
                using var stream=new HueStream(config,area,identity);
                int[] ids=area.Channels.Select(c=>c.Id).ToArray();
                Rgb[] filtered=ColorFilter.Apply(area.Channels,first,null,config.SpatialMix,config.TemporalMix);
                long lastSample=Stopwatch.GetTimestamp();
                var nextStatus=DateTime.UtcNow.AddSeconds(2);
                if(seconds>0) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
                int frames=0;
                Console.WriteLine("Streaming started");
                stateChanged?.Invoke("running");
                while(!stop.Token.IsCancellationRequested)
                {
                    if(DateTime.UtcNow>=nextStatus)
                    {
                        var status=await bridge.GetStatusAsync(area);
                        if(status.Status!="active" || status.Owner!=identity) { Console.WriteLine("Entertainment session ended or changed owner"); break; }
                        nextStatus=DateTime.UtcNow.AddSeconds(2);
                    }
                    stream.Send(ids,filtered);
                    frames++;
                    var next=sampler.Read(area.Channels);
                    long now=Stopwatch.GetTimestamp();
                    double elapsed=(now-lastSample)/(double)Stopwatch.Frequency;
                    lastSample=now;
                    double tau=timeConstant?.Invoke() ?? Sensitivity.FromLegacyWeight(config.TemporalMix);
                    filtered=ColorFilter.Apply(area.Channels,next,filtered,config.SpatialMix,Sensitivity.Weight(tau,elapsed));
                }
                Console.WriteLine("Frames sent: "+frames);
            } finally {
                if(started) { try { var status=await bridge.GetStatusAsync(area); if(status.Status=="active"&&status.Owner==identity) { await bridge.ActionAsync(area,"stop"); Console.WriteLine("Entertainment stopped"); } } catch(Exception ex) { Console.Error.WriteLine("Could not verify or stop entertainment: "+ex.Message); } }
            }
        }
    }
}
