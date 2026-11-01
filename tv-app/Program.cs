using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.IO;

namespace HueTvSync
{
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length==0 || (args[0]!="selftest" && args[0]!="inspect" && args[0]!="list" && args[0]!="sync"))
                return await EngineServer.RunAsync();
            if (args.Length == 1 && args[0] == "selftest") return SelfTests.Run();
            if (args.Length == 2 && args[0] == "selftest" && args[1] == "--offline") return SelfTests.Run(false);
            if (args.Length < 2 || (args[0] != "inspect" && args[0] != "sync" && args[0] != "list"))
            {
                Console.Error.WriteLine("Usage: HueTvSync list|inspect|sync <config.json> [seconds] [area-id] | selftest [--offline]");
                return 2;
            }
            try
            {
                var config = Settings.Load(args[1]);
                using var client = new HueBridge(config);
                if (args[0] == "list")
                {
                    var areas=await client.ListAreasAsync();
                    Console.WriteLine(JsonSerializer.Serialize(areas));
                    return 0;
                }
                if(args.Length>3) { if(!Guid.TryParse(args[3],out _)) throw new ArgumentException("Invalid area ID"); config.AreaId=args[3]; }
                var area = await client.GetAreaAsync();
                Console.WriteLine("Area: " + area.Name + "; channels: " + area.Channels.Count + "; status: " + area.Status);
                if (args[0] == "inspect")
                {
                    var identity=await client.GetApplicationIdAsync();
                    if(string.IsNullOrWhiteSpace(identity)) throw new InvalidOperationException("Bridge identity is missing");
                    var status=await client.GetStatusAsync(area);
                    if(status.Status!="inactive" && status.Status!="active") throw new InvalidOperationException("Invalid entertainment status");
                    Console.WriteLine("Authenticated bridge identity available");
                    return 0;
                }
                int seconds = args.Length > 2 ? int.Parse(args[2]) : 0;
                if (seconds < 0 || seconds > 86400) throw new ArgumentException("Duration must be 0..86400 seconds");
                using var stop = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Cancel(); };
                using var sampler = new SamsungSampler();
                await SyncRunner.RunAsync(client, config, area, sampler, stop, seconds);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
    }
}
