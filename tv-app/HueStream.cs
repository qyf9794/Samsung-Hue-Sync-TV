using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace HueTvSync
{
    internal sealed class UdpDatagram : DatagramTransport
    {
        private readonly Socket socket;
        public UdpDatagram(string host,int port=2100) { socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp); socket.Connect(new IPEndPoint(IPAddress.Parse(host),port)); }
        public UdpDatagram(Socket connectedSocket) { socket=connectedSocket; }
        public int GetReceiveLimit()=>1500;
        public int GetSendLimit()=>1200;
        public int Receive(byte[] b,int offset,int length,int waitMillis)
        {
            socket.ReceiveTimeout=Math.Max(1,waitMillis);
            try { return socket.Receive(b,offset,length,SocketFlags.None); }
            catch(SocketException ex) when(ex.SocketErrorCode==SocketError.TimedOut || ex.SocketErrorCode==SocketError.WouldBlock) { return -1; }
        }
        public void Send(byte[] b,int offset,int length) { if(socket.Send(b,offset,length,SocketFlags.None)!=length) throw new IOException("Short UDP write"); }
        public void Close()=>socket.Close();
    }
    internal sealed class HuePskClient : PskTlsClient
    {
        public HuePskClient(byte[] identity,byte[] psk):base(new BcTlsCrypto(),identity,psk) {}
        protected override ProtocolVersion[] GetSupportedVersions()=>ProtocolVersion.DTLSv12.Only();
        protected override int[] GetSupportedCipherSuites()=>new[]{CipherSuite.TLS_PSK_WITH_AES_128_GCM_SHA256};
        public override int GetHandshakeTimeoutMillis()=>10000;
    }
    internal sealed class HueStream : IDisposable
    {
        private readonly UdpDatagram udp;
        private readonly DtlsTransport dtls;
        private readonly string area;
        private byte sequence;
        public HueStream(Settings settings,Area area,string identity)
        {
            this.area=Guid.Parse(area.Id).ToString();
            udp=new UdpDatagram(settings.Host);
            try { dtls=new DtlsClientProtocol().Connect(new HuePskClient(Encoding.UTF8.GetBytes(identity),Hex.Decode(settings.ClientKey)),udp); }
            catch { udp.Close(); throw; }
        }
        public static byte[] Encode(string area,byte sequence,int[] ids,Rgb[] colors)
        {
            if(ids.Length!=colors.Length || ids.Length>32) throw new ArgumentException("Invalid channel count");
            byte[] data=new byte[52+7*ids.Length];
            Encoding.ASCII.GetBytes("HueStream").CopyTo(data,0); data[9]=2;data[10]=0;data[11]=sequence;
            Encoding.ASCII.GetBytes(Guid.Parse(area).ToString()).CopyTo(data,16);
            for(int i=0;i<ids.Length;i++)
            {
                int off=52+i*7; data[off]=(byte)ids[i];
                Write16(data,off+1,colors[i].R);Write16(data,off+3,colors[i].G);Write16(data,off+5,colors[i].B);
            }
            return data;
        }
        private static void Write16(byte[] b,int i,double value)
        {
            int n=(int)Math.Round(Math.Max(0,Math.Min(1023,value))*65535/1023.0);
            b[i]=(byte)(n>>8); b[i+1]=(byte)n;
        }
        public void Send(int[] ids,Rgb[] colors) { var bytes=Encode(area,sequence++,ids,colors); dtls.Send(bytes,0,bytes.Length); }
        public void Dispose() { try { dtls.Close(); } finally { udp.Close(); } }
    }
}
