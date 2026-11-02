using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace HueTvSync
{
    internal static class SelfTests
    {
        public static int Run(bool testTransport=true)
        {
            try {
                string recoveryToken=new string('a',64);
                Require(EngineServer.CanProvisionControlToken(true,"HueSyncTV1.main","hue-ui","status",true,recoveryToken),"trusted UI can restore missing token");
                Require(!EngineServer.CanProvisionControlToken(false,"HueSyncTV1.main","hue-ui","status",true,recoveryToken) && !EngineServer.CanProvisionControlToken(true,"other.app","hue-ui","status",true,recoveryToken) && !EngineServer.CanProvisionControlToken(true,"HueSyncTV1.main","hue-ui","start",true,recoveryToken) && !EngineServer.CanProvisionControlToken(true,"HueSyncTV1.main","hue-ui","status",false,recoveryToken) && !EngineServer.CanProvisionControlToken(true,"HueSyncTV1.main","hue-ui","status",true,new string('z',64)),"token recovery rejects unauthorized or invalid requests");
                var bridgeJson="{\"host\":\"192.168.1.2\",\"username\":\"testuser\",\"clientkey\":\""+new string('a',32)+"\",\"certificate_sha256\":\""+new string('b',64)+"\"}";
                var recoveredSettings=Settings.Parse(bridgeJson);
                Require(recoveredSettings.Host=="192.168.1.2" && recoveredSettings.ClientKey.Length==32,"recovered bridge configuration validation");
                Require(Math.Abs(recoveredSettings.SpatialMix-.3)<0.0001,"default gradient blending");
                using var channelJson=JsonDocument.Parse("{\"channel_id\":7,\"position\":{\"x\":-1,\"y\":1,\"z\":-1},\"members\":[{\"service\":{\"rid\":\"gradient\"}}]}");
                var bottom=HueBridge.ParseChannel(channelJson.RootElement);
                Require(bottom.Id==7 && bottom.X==-1 && bottom.Y==1 && bottom.Z==-1 && bottom.Group=="gradient","bridge channel position");
                var bottomPoint=SamsungSampler.ScreenPosition(bottom,1000,1000,0,0);
                var topPoint=SamsungSampler.ScreenPosition(new Channel { X=1,Y=1,Z=1 },1000,1000,0,0);
                var middlePoint=SamsungSampler.ScreenPosition(new Channel { X=0,Y=-1,Z=0 },1000,1000,0,0);
                var sameHeight=SamsungSampler.ScreenPosition(new Channel { X=0,Y=1,Z=0 },1000,1000,0,0);
                Require(bottomPoint==(50,950) && topPoint==(950,50) && middlePoint==(500,500) && sameHeight==middlePoint,"screen mapping uses height, not depth");
                var channels=new[]{new Channel { Id=0,Group="a" },new Channel {Id=1,Group="b",X=-.4},new Channel {Id=2,Group="b",X=0},new Channel {Id=3,Group="b",X=.4}};
                var samplePoints=SamsungSampler.ScreenPositions(channels,1000,1000,0,0);
                Require(samplePoints[0]==(500,500) && samplePoints[1]==(200,500) && samplePoints[2]==(500,500) && samplePoints[3]==(800,500),"screen-wide gradient positions");
                var plan=SamsungSampler.CreateSamplePlan(channels,1000,1000,0,0);
                Require(plan.Points.Count==12 && plan.Points.Take(7).Select(p=>p.X).SequenceEqual(new[]{50,200,350,500,650,800,950}),"seven screen-wide points");
                Require(plan.ChannelSamples[1].SequenceEqual(new[]{0,1,2}) && plan.ChannelSamples[2].SequenceEqual(new[]{2,3,4}) && plan.ChannelSamples[3].SequenceEqual(new[]{4,5,6}) && plan.ChannelSamples[0].SequenceEqual(new[]{7,8,9,10,11}),"overlapping gradient and five-point single-light samples");
                Require(plan.Points.Skip(7).SequenceEqual(new[]{(500,500),(450,500),(550,500),(500,450),(500,550)}),"single-light samples surround its position");
                var averaged=plan.Average(Enumerable.Range(0,12).Select(i=>new Rgb(i*100,i*10,i)).ToArray());
                Require(averaged[0].R==900 && averaged[1].R==100 && averaged[2].R==300 && averaged[3].R==500 && averaged[2].G==30 && averaged[2].B==3,"gradient and single-light RGB averages");
                var edgePlan=SamsungSampler.CreateSamplePlan(new[]{new Channel {X=-1,Z=1}},1000,1000,16,16);
                Require(edgePlan.Points.All(p=>p.X>=0 && p.Y>=0 && p.X<=984 && p.Y<=984),"single-light sample points stay on screen");
                var offsetStrip=new[]{new Channel {Group="c",X=.1},new Channel {Group="c",X=.2},new Channel {Group="c",X=.3}};
                var offsetPoints=SamsungSampler.ScreenPositions(offsetStrip,1000,1000,0,0);
                Require(offsetPoints[0].X==550 && offsetPoints[1].X==600 && offsetPoints[2].X==650,"offset gradient keeps room positions");
                Require(SamsungSampler.CreateSamplePlan(offsetStrip,1000,1000,0,0).Points.Count==3,"offset lights keep individual samples");
                var raw=new[]{new Rgb(100,100,100),new Rgb(1000,0,0),new Rgb(0,0,0),new Rgb(0,0,1000)};
                var r=ColorFilter.Apply(channels,raw,null,.3,.28);
                Require(r[0].R==100 && r[1].R==700 && r[2].R==150 && r[2].B==150 && r[3].B==700,"spatial grouping");
                var blended=ColorFilter.Apply(channels,raw,null,recoveredSettings.SpatialMix,.28);
                Require(blended[0].R==100 && blended[1].R==700 && blended[2].R==150 && blended[2].B==150 && blended[3].B==700,"default blend reduces gradient color differences and preserves other lights");
                var uniform=ColorFilter.Apply(channels,new[]{new Rgb(20,30,40),new Rgb(20,30,40),new Rgb(20,30,40),new Rgb(20,30,40)},null,recoveredSettings.SpatialMix,.28);
                Require(uniform.All(c=>Math.Abs(c.R-20)<.001 && Math.Abs(c.G-30)<.001 && Math.Abs(c.B-40)<.001),"blending preserves uniform color");
                var s=ColorFilter.Apply(channels,new[]{new Rgb(0,0,0),new Rgb(0,0,0),new Rgb(0,0,0),new Rgb(0,0,0)},r,.3,.25);
                Require(s[1].R==525,"temporal smoothing");
                var reduced=ColorFilter.Apply(channels,new[]{new Rgb(1000,0,0),new Rgb(1000,0,0),new Rgb(1000,0,0),new Rgb(1000,0,0)},new[]{new Rgb(0,0,0),new Rgb(0,0,0),new Rgb(0,0,0),new Rgb(0,0,0)},.3,.4,.2);
                Require(reduced[0].R==200 && reduced[1].R==400 && reduced[2].R==400 && reduced[3].R==400,"stronger temporal smoothing only for single-channel devices");
                var previous=new[]{new Rgb(0,0,0)};var change=new[]{new Rgb(1000,0,0)};var single=new[]{new Channel()};
                double low=ColorFilter.Apply(single,change,previous,0,Sensitivity.Weight(Sensitivity.TimeConstant(1),.1))[0].R;
                double standard=ColorFilter.Apply(single,change,previous,0,Sensitivity.Weight(Sensitivity.TimeConstant(3),.1))[0].R;
                double high=ColorFilter.Apply(single,change,previous,0,Sensitivity.Weight(Sensitivity.TimeConstant(5),.1))[0].R;
                Require(low<standard && standard<high && Math.Abs(standard-329.68)<.1,"sensitivity changes reaction speed");
                double w05=Sensitivity.Weight(.25,.05),w10=Sensitivity.Weight(.25,.1);
                Require(Math.Abs((1-w05)*(1-w05)-(1-w10))<1e-12,"time smoothing is consistent across sample rates");
                Require(Math.Abs(Sensitivity.Weight(Sensitivity.FromLegacyWeight(.28),.05)-.28)<1e-12 && Sensitivity.Weight(0,.1)==1,"legacy temporal settings");
                Require(Sensitivity.Weight(.25,0)==0 && Sensitivity.Weight(.25,2)==Sensitivity.Weight(.25,.2),"smoothing caps a long gap");
                var packet=HueStream.Encode("00112233-4455-6677-8899-aabbccddeeff",9,new[]{1,3},new[]{new Rgb(1023,0,512),new Rgb(0,1023,0)});
                Require(packet.Length==66 && System.Text.Encoding.ASCII.GetString(packet,0,9)=="HueStream" && packet[9]==2 && packet[11]==9,"header");
                Require(packet[52]==1 && packet[53]==255 && packet[54]==255 && packet[55]==0 && packet[56]==0 && packet[59]==3 && packet[62]==255 && packet[63]==255,"payload encoding");
                if(testTransport) TestDtls(packet);
                Console.WriteLine("Self-test passed: channel positions, screen mapping, spatial and temporal filtering, HueStream 2 encoding"+(testTransport?", authenticated local DTLS transport":" (offline; DTLS transport not tested)"));
                return 0;
            } catch(Exception ex) { Console.Error.WriteLine("Self-test failed: "+ex.Message); return 1; }
        }
        private static void Require(bool condition,string name) { if(!condition) throw new InvalidOperationException(name); }
        private sealed class TestIdentity : TlsPskIdentityManager
        {
            private readonly byte[] key;
            public TestIdentity(byte[] key) { this.key=key; }
            public byte[] GetHint()=>Array.Empty<byte>();
            public byte[] GetPsk(byte[] identity)=>Encoding.UTF8.GetString(identity)=="test"?key:null!;
        }
        private sealed class TestServer : PskTlsServer
        {
            public TestServer(byte[] key):base(new BcTlsCrypto(),new TestIdentity(key)) {}
            protected override ProtocolVersion[] GetSupportedVersions()=>ProtocolVersion.DTLSv12.Only();
            protected override int[] GetSupportedCipherSuites()=>new[]{CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256};
            public override int GetHandshakeTimeoutMillis()=>5000;
        }
        private static void TestDtls(byte[] packet)
        {
            byte[] key=Hex.Decode("00112233445566778899aabbccddeeff");
            var server=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
            server.Bind(new IPEndPoint(IPAddress.Loopback,0));
            int port=((IPEndPoint)server.LocalEndPoint!).Port;
            var task=Task.Run(()=>{
                try {
                    server.ReceiveTimeout=5000;EndPoint peer=new IPEndPoint(IPAddress.Any,0);var peek=new byte[1500];
                    server.ReceiveFrom(peek,0,peek.Length,SocketFlags.Peek,ref peer);server.Connect(peer);
                    var wire=new UdpDatagram(server);var transport=new DtlsServerProtocol().Accept(new TestServer(key),wire);
                    var received=new byte[1500];int n=transport.Receive(received,0,received.Length,5000);
                    Require(n==packet.Length && packet.SequenceEqual(received.Take(n)),"DTLS packet integrity");
                    transport.Close();
                } finally { server.Close(); }
            });
            var udp=new UdpDatagram("127.0.0.1",port);
            try {
                var tls=new DtlsClientProtocol().Connect(new HuePskClient(Encoding.UTF8.GetBytes("test"),key),udp);
                tls.Send(packet,0,packet.Length);
                Require(task.Wait(7000),"DTLS server completion");
                tls.Close();
            } finally { udp.Close(); }
        }
    }
}
