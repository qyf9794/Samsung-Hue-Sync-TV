using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HueTvSync
{
    internal interface ISampler : IDisposable { Rgb[] Read(IReadOnlyList<Channel> channels); }
    internal sealed class SamplePlan
    {
        public readonly List<(int X,int Y)> Points=new List<(int X,int Y)>();
        public readonly int[][] ChannelSamples;
        public SamplePlan(int count) { ChannelSamples=new int[count][]; }
        public Rgb[] Average(Rgb[] samples)
        {
            if(samples.Length!=Points.Count) throw new ArgumentException("Sample count mismatch");
            var result=new Rgb[ChannelSamples.Length];
            for(int i=0;i<result.Length;i++)
            {
                double r=0,g=0,b=0;var indices=ChannelSamples[i];
                foreach(int n in indices) { r+=samples[n].R;g+=samples[n].G;b+=samples[n].B; }
                result[i]=new Rgb(r/indices.Length,g/indices.Length,b/indices.Length);
            }
            return result;
        }
    }
    internal sealed class SamsungSampler : ISampler
    {
        [DllImport("/usr/lib/libvideoenhance.so",CallingConvention=CallingConvention.Cdecl,EntryPoint="cs_ve_get_rgb_measure_condition")]
        private static extern int Condition(IntPtr p);
        [DllImport("/usr/lib/libvideoenhance.so",CallingConvention=CallingConvention.Cdecl,EntryPoint="cs_ve_set_rgb_measure_position")]
        private static extern int Position(int index,int x,int y);
        [DllImport("/usr/lib/libvideoenhance.so",CallingConvention=CallingConvention.Cdecl,EntryPoint="cs_ve_get_rgb_measure_pixel")]
        private static extern int Pixel(int index,IntPtr p);
        private readonly int slots,dx,dy,delay,width,height;
        public SamsungSampler()
        {
            IntPtr p=Marshal.AllocHGlobal(256);
            try {
                for(int i=0;i<256;i++) Marshal.WriteByte(p,i,0);
                if(Condition(p)!=0) throw new InvalidOperationException("TV color measurement unavailable");
                slots=Marshal.ReadInt32(p,0);dx=Marshal.ReadInt32(p,4);dy=Marshal.ReadInt32(p,8);delay=Marshal.ReadInt32(p,12);width=Marshal.ReadInt32(p,16);height=Marshal.ReadInt32(p,20);
                if(slots<1||slots>16||dx<1||dy<1||delay<0||delay>200||width<dx||height<dy) throw new InvalidOperationException("Invalid TV measurement capabilities");
            } finally { Marshal.FreeHGlobal(p); }
        }
        public Rgb[] Read(IReadOnlyList<Channel> channels)
        {
            var plan=CreateSamplePlan(channels,width,height,dx,dy);
            var samples=new Rgb[plan.Points.Count];IntPtr p=Marshal.AllocHGlobal(256);
            try {
                for(int start=0;start<plan.Points.Count;start+=slots)
                {
                    int count=Math.Min(slots,plan.Points.Count-start);
                    for(int j=0;j<count;j++)
                    {
                        var (x,y)=plan.Points[start+j];
                        if(Position(j,x,y)!=0) throw new InvalidOperationException("TV rejected measurement position");
                    }
                    Thread.Sleep(Math.Max(25,delay));
                    for(int j=0;j<count;j++)
                    {
                        if(Pixel(j,p)!=0) throw new InvalidOperationException("TV color measurement failed");
                        int r=Marshal.ReadInt32(p,0),g=Marshal.ReadInt32(p,4),b=Marshal.ReadInt32(p,8);
                        if(r<0||r>1023||g<0||g>1023||b<0||b>1023) throw new InvalidOperationException("TV returned invalid color");
                        samples[start+j]=new Rgb(r,g,b);
                    }
                }
                return plan.Average(samples);
            } finally { Marshal.FreeHGlobal(p); }
        }
        internal static (int X,int Y) ScreenPosition(Channel c,int width,int height,int dx,int dy)
        {
            // Hue X runs left to right and Z bottom to top; screen Y runs top to bottom.
            double x=Math.Max(.05,Math.Min(.95,(c.X+1)/2));
            double y=Math.Max(.05,Math.Min(.95,(1-c.Z)/2));
            return ((int)(x*(width-dx)),(int)(y*(height-dy)));
        }
        internal static (int X,int Y)[] ScreenPositions(IReadOnlyList<Channel> channels,int width,int height,int dx,int dy)
        {
            var result=channels.Select(c=>ScreenPosition(c,width,height,dx,dy)).ToArray();
            foreach(var group in channels.Select((c,i)=>(c,i)).Where(p=>p.c.Group.Length>0).GroupBy(p=>p.c.Group))
            {
                var points=group.ToArray();
                if(points.Length<3) continue;
                double minX=points.Min(p=>p.c.X),maxX=points.Max(p=>p.c.X);
                // Only expand a strip whose reported channels already span both sides of the screen.
                if(minX>-.2 || maxX<.2 || maxX-minX<.6) continue;
                foreach(var p in points)
                {
                    double x=.20+.60*(p.c.X-minX)/(maxX-minX);
                    result[p.i]=((int)(x*(width-dx)),result[p.i].Y);
                }
            }
            return result;
        }
        internal static SamplePlan CreateSamplePlan(IReadOnlyList<Channel> channels,int width,int height,int dx,int dy)
        {
            var positions=ScreenPositions(channels,width,height,dx,dy);
            var plan=new SamplePlan(channels.Count);
            foreach(var group in channels.Select((c,i)=>(c,i)).Where(p=>p.c.Group.Length>0).GroupBy(p=>p.c.Group))
            {
                var ordered=group.OrderBy(p=>p.c.X).ToArray();
                if(ordered.Length!=3 || ordered[0].c.X>-.2 || ordered[2].c.X<.2 || ordered[2].c.X-ordered[0].c.X<.6) continue;
                var left=positions[ordered[0].i];var middle=positions[ordered[1].i];var right=positions[ordered[2].i];
                int start=plan.Points.Count;
                // Seven points around the existing three centers. Shared boundary points
                // contribute to both neighboring averages, while heights follow the strip.
                (int X,int Y) Between((int X,int Y) a,(int X,int Y) b,double t) =>
                    (Math.Max(0,Math.Min(width-dx,(int)Math.Round(a.X+(b.X-a.X)*t))),
                     Math.Max(0,Math.Min(height-dy,(int)Math.Round(a.Y+(b.Y-a.Y)*t))));
                plan.Points.Add(Between(left,middle,-.5));
                plan.Points.Add(left);
                plan.Points.Add(Between(left,middle,.5));
                plan.Points.Add(middle);
                plan.Points.Add(Between(middle,right,.5));
                plan.Points.Add(right);
                plan.Points.Add(Between(middle,right,1.5));
                for(int i=0;i<3;i++) plan.ChannelSamples[ordered[i].i]=new[]{start+2*i,start+2*i+1,start+2*i+2};
            }
            for(int i=0;i<channels.Count;i++) if(plan.ChannelSamples[i]==null)
            {
                plan.ChannelSamples[i]=new[]{plan.Points.Count};plan.Points.Add(positions[i]);
            }
            return plan;
        }
        public void Dispose() {}
    }
}
