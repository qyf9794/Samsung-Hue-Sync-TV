using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace HueTvSync
{
    internal sealed class HueBridge : IDisposable
    {
        private readonly Settings config;
        private readonly HttpClient http;
        public HueBridge(Settings config)
        {
            this.config=config;
            var expected=Hex.Decode(config.CertificateSha256);
            var handler=new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback=(request,certificate,chain,errors) => {
                if(certificate==null) return false;
                using var sha=SHA256.Create();
                return CryptographicOperations.FixedTimeEquals(sha.ComputeHash(certificate.GetRawCertData()),expected);
            };
            http=new HttpClient(handler) { BaseAddress=new Uri("https://"+config.Host), Timeout=TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Add("hue-application-key", config.Username);
        }
        public async Task<Area> GetAreaAsync()
        {
            using var response=await http.GetAsync("/clip/v2/resource/entertainment_configuration");
            using var doc=await ReadAsync(response);
            var all=doc.RootElement.GetProperty("data").EnumerateArray().ToList();
            var matched=all.Where(a=>config.AreaId.Length>0 ? a.GetProperty("id").GetString()==config.AreaId : a.GetProperty("metadata").GetProperty("name").GetString()==config.AreaName).ToList();
            if(matched.Count!=1) throw new InvalidOperationException("Area name is missing or ambiguous");
            var j=matched[0];
            var a=new Area { Id=j.GetProperty("id").GetString() ?? "", Name=j.GetProperty("metadata").GetProperty("name").GetString() ?? "", Status=j.GetProperty("status").GetString() ?? "" };
            foreach(var c in j.GetProperty("channels").EnumerateArray())
            {
                a.Channels.Add(ParseChannel(c));
            }
            if (!Guid.TryParse(a.Id,out _) || a.Channels.Count==0 || a.Channels.Count>32 || a.Channels.Select(x=>x.Id).Distinct().Count()!=a.Channels.Count) throw new InvalidOperationException("Invalid area configuration");
            if (all.Any(x=>x.GetProperty("status").GetString()=="active")) a.Status="active";
            return a;
        }
        internal static Channel ParseChannel(JsonElement c)
        {
            var pos=c.GetProperty("position");
            var ch=new Channel { Id=c.GetProperty("channel_id").GetInt32(), X=pos.GetProperty("x").GetDouble(), Y=pos.GetProperty("y").GetDouble(), Z=pos.GetProperty("z").GetDouble() };
            if(c.TryGetProperty("members",out var members) && members.GetArrayLength()>0 && members[0].TryGetProperty("service",out var service)) ch.Group=service.GetProperty("rid").GetString() ?? "";
            if(ch.Id<0||ch.Id>255||!double.IsFinite(ch.X)||!double.IsFinite(ch.Y)||!double.IsFinite(ch.Z)||Math.Abs(ch.X)>2||Math.Abs(ch.Y)>2||Math.Abs(ch.Z)>2) throw new InvalidOperationException("Invalid channel configuration");
            return ch;
        }
        public async Task<object[]> ListAreasAsync()
        {
            using var response=await http.GetAsync("/clip/v2/resource/entertainment_configuration");
            using var doc=await ReadAsync(response);
            return doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(a=>new { id=a.GetProperty("id").GetString(), name=a.GetProperty("metadata").GetProperty("name").GetString(), status=a.GetProperty("status").GetString() })
                .Where(a=>Guid.TryParse(a.id,out _) && !string.IsNullOrWhiteSpace(a.name)).Cast<object>().ToArray();
        }
        public async Task<string> GetApplicationIdAsync()
        {
            using var r=await http.GetAsync("/auth/v1");
            r.EnsureSuccessStatusCode();
            if (!r.Headers.TryGetValues("hue-application-id",out var values)) throw new InvalidOperationException("Bridge did not return application identity");
            return values.First();
        }
        public async Task<(string Status,string Owner)> GetStatusAsync(Area area)
        {
            using var response=await http.GetAsync("/clip/v2/resource/entertainment_configuration/"+area.Id);
            using var doc=await ReadAsync(response);
            var data=doc.RootElement.GetProperty("data");
            if(data.GetArrayLength()!=1) throw new InvalidOperationException("Area disappeared");
            var item=data[0];
            var status=item.GetProperty("status").GetString() ?? "";
            string owner="";
            if(item.TryGetProperty("active_streamer",out var streamer) && streamer.ValueKind==JsonValueKind.Object && streamer.TryGetProperty("rid",out var rid)) owner=rid.GetString() ?? "";
            return (status,owner);
        }
        public async Task ActionAsync(Area a,string action)
        {
            using var content=new StringContent(JsonSerializer.Serialize(new { action }),Encoding.UTF8,"application/json");
            using var response=await http.PutAsync("/clip/v2/resource/entertainment_configuration/"+a.Id,content);
            using var _=await ReadAsync(response);
        }
        private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
            var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if(doc.RootElement.TryGetProperty("errors",out var errors) && errors.GetArrayLength()>0) { doc.Dispose(); throw new InvalidOperationException("Bridge reported an API error"); }
            return doc;
        }
        public void Dispose()=>http.Dispose();
    }
}
