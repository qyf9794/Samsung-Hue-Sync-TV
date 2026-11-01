using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;

namespace HueTvSync
{
    internal sealed class Settings
    {
        public string Host = "", Username = "", ClientKey = "", CertificateSha256 = "", AreaName = "TV area", AreaId = "";
        // Blend neighboring channels more gently while keeping the sampled color dominant.
        public double SpatialMix = 0.30, TemporalMix = 0.28;
        public static Settings Load(string path) => Parse(File.ReadAllText(path));
        internal static Settings Parse(string content)
        {
            using var document = JsonDocument.Parse(content);
            var json = document.RootElement;
            var s = new Settings { Host = Required(json,"host"), Username = Required(json,"username"), ClientKey = Required(json,"clientkey"), CertificateSha256 = Required(json,"certificate_sha256") };
            if (json.TryGetProperty("area_name",out var area)) s.AreaName = area.GetString() ?? "";
            if (json.TryGetProperty("spatial_mix",out var spatial)) s.SpatialMix = spatial.GetDouble();
            if (json.TryGetProperty("temporal_mix",out var temporal)) s.TemporalMix = temporal.GetDouble();
            if (!IPAddress.TryParse(s.Host,out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new ArgumentException("Bridge host must be an IPv4 address");
            if (s.CertificateSha256.Length != 64 || s.ClientKey.Length != 32) throw new ArgumentException("Invalid certificate fingerprint or client key length");
            Hex.Decode(s.CertificateSha256); Hex.Decode(s.ClientKey);
            if (s.Username.Length < 8 || s.AreaName.Length == 0 || s.SpatialMix < 0 || s.SpatialMix > 0.5 || s.TemporalMix <= 0 || s.TemporalMix > 1) throw new ArgumentException("Invalid configuration");
            return s;
        }
        private static string Required(JsonElement obj, string name) => obj.GetProperty(name).GetString() ?? throw new ArgumentException("Missing " + name);
    }
    internal static class Hex
    {
        public static byte[] Decode(string input)
        {
            if (input.Length % 2 != 0) throw new FormatException("Invalid hex");
            var bytes = new byte[input.Length / 2];
            for (int i=0;i<bytes.Length;i++) bytes[i] = System.Convert.ToByte(input.Substring(i*2,2),16);
            return bytes;
        }
    }
    internal sealed class Channel
    {
        public int Id; public double X,Y,Z; public string Group = "";
    }
    internal sealed class Area
    {
        public string Id="", Name="", Status=""; public List<Channel> Channels=new List<Channel>();
    }
    internal readonly struct Rgb
    {
        public readonly double R,G,B;
        public Rgb(double r,double g,double b) { R=r; G=g; B=b; }
        public static Rgb Mix(Rgb a,Rgb b,double w) => new Rgb(a.R*(1-w)+b.R*w,a.G*(1-w)+b.G*w,a.B*(1-w)+b.B*w);
    }
}
