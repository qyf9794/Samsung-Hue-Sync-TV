using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace HueTvSync
{
    internal static class EngineServer
    {
        public const string DefaultConfig="/opt/usr/home/owner/apps_rw/HueEngine1/data/hue-bridge.json";
        private const string UiConfig="/opt/usr/home/owner/apps_rw/HueSyncTV1/data/hue-bridge.json";
        private const string TokenPath="/opt/usr/home/owner/apps_rw/HueEngine1/data/control-token.json";
        private const string LogPath="/opt/usr/home/owner/apps_rw/HueEngine1/data/engine-diagnostic.log";
        private const string SensitivityPath="/opt/usr/home/owner/apps_rw/HueEngine1/data/sensitivity.json";
        private static readonly object Gate=new object();
        private static string state="idle", detail="准备就绪", token="";
        private static CancellationTokenSource? cancel;
        private static Task? worker;
        private static bool messageLogged;
        private static bool tokenMissing;
        private static int sensitivity=3;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PortCallback(int id, IntPtr app, IntPtr port, [MarshalAs(UnmanagedType.I1)] bool trusted, IntPtr message, IntPtr user);
        private static readonly PortCallback Callback=OnMessage;
        [DllImport("libcapi-message-port.so.1",CallingConvention=CallingConvention.Cdecl)] private static extern int message_port_register_trusted_local_port(string port,PortCallback callback,IntPtr user);
        [DllImport("libcapi-message-port.so.1",CallingConvention=CallingConvention.Cdecl)] private static extern int message_port_send_trusted_message(string app,string port,IntPtr message);
        [DllImport("libbundle.so.0",CallingConvention=CallingConvention.Cdecl)] private static extern int bundle_get_str(IntPtr bundle,string key,out IntPtr value);
        [DllImport("libbundle.so.0",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr bundle_create();
        [DllImport("libbundle.so.0",CallingConvention=CallingConvention.Cdecl)] private static extern int bundle_add_str(IntPtr bundle,string key,[MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport("libbundle.so.0",CallingConvention=CallingConvention.Cdecl)] private static extern void bundle_free(IntPtr bundle);
        [DllImport("libglib-2.0.so.0",CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr g_main_loop_new(IntPtr context,int running);
        [DllImport("libglib-2.0.so.0",CallingConvention=CallingConvention.Cdecl)] private static extern void g_main_loop_run(IntPtr loop);
        [DllImport("libc.so.6",SetLastError=true)] private static extern int open(string path,int flags);
        [DllImport("libc.so.6",EntryPoint="open",SetLastError=true)] private static extern int open_create(string path,int flags,uint mode);
        [DllImport("libc.so.6",SetLastError=true)] private static extern int dup2(int oldfd,int newfd);
        [DllImport("libc.so.6",SetLastError=true)] private static extern int close(int fd);
        public static Task<int> RunAsync()
        {
            try
            {
                int fd=open("/dev/null",1); if(fd>=0) { dup2(fd,1); if(fd!=1) close(fd); }
                try
                {
                    using var document=JsonDocument.Parse(File.ReadAllText(TokenPath));
                    token=document.RootElement.GetProperty("token").GetString() ?? "";
                    if(token.Length!=64) throw new InvalidOperationException("Invalid local control token");
                    Hex.Decode(token);
                }
                catch(FileNotFoundException ex)
                {
                    token="";tokenMissing=true;state="error";detail="同步核心缺少控制令牌";
                    Log("control token unavailable: "+ex.GetType().Name);
                }
                catch(Exception ex)
                {
                    token="";tokenMissing=false;state="error";detail="同步核心控制令牌无效或无法读取";
                    Log("control token unavailable: "+ex.GetType().Name);
                }
                if(token.Length>0 && !EnsureBridgeConfig()) { state="error";detail="同步核心缺少 Hue Bridge 配置，无法自动恢复"; }
                if(File.Exists(SensitivityPath))
                {
                    try { using var saved=JsonDocument.Parse(File.ReadAllText(SensitivityPath));int level=saved.RootElement.GetProperty("level").GetInt32();if(level>=1 && level<=5) sensitivity=level==2?1:level==4?5:level; }
                    catch(Exception ex) { Log("sensitivity settings: "+ex.GetType().Name); }
                }
                int port=message_port_register_trusted_local_port("hue-engine",Callback,IntPtr.Zero);
                if(port<0) throw new InvalidOperationException("Message port registration: "+port);
                Log("trusted message port registered: "+port);
                var loop=g_main_loop_new(IntPtr.Zero,0);
                if(loop==IntPtr.Zero) throw new InvalidOperationException("Main loop unavailable");
                g_main_loop_run(loop);
                return Task.FromResult(0);
            }
            catch(Exception ex) { Log("engine failed: "+ex.GetType().Name+": "+ex.Message); return Task.FromResult(1); }
        }
        private static string Get(IntPtr message,string key)
        {
            if(bundle_get_str(message,key,out var p)!=0 || p==IntPtr.Zero) return "";
            return Marshal.PtrToStringUTF8(p) ?? "";
        }
        private static void OnMessage(int id,IntPtr appPtr,IntPtr portPtr,bool trusted,IntPtr message,IntPtr user)
        {
            try
            {
                string app=Marshal.PtrToStringUTF8(appPtr) ?? "", port=Marshal.PtrToStringUTF8(portPtr) ?? "";
                if(!messageLogged) { Log("message received from "+app+", trusted="+trusted+", reply port="+port);messageLogged=true; }
                if(app!="HueSyncTV1.main" || !trusted || port!="hue-ui") return;
                string command=Get(message,"command"), requestId=Get(message,"id");
                string supplied=Get(message,"token");
                if(CanProvisionControlToken(trusted,app,port,command,tokenMissing,supplied))
                {
                    try
                    {
                        SaveNewControlToken(supplied);
                        token=supplied;tokenMissing=false;
                        if(EnsureBridgeConfig()) { state="idle";detail="准备就绪"; }
                        else { state="error";detail="同步核心缺少 Hue Bridge 配置，无法自动恢复"; }
                        Log("control token restored from trusted UI");
                    }
                    catch(Exception ex) { state="error";detail="同步核心无法保存控制令牌";Log("control token restore failed: "+ex.GetType().Name); }
                }
                var a=Encoding.UTF8.GetBytes(supplied);var b=Encoding.UTF8.GetBytes(token);
                if(token.Length==0 || a.Length!=b.Length || !CryptographicOperations.FixedTimeEquals(a,b))
                {
                    if(command=="status") SendReply(app,port,requestId,"error",token.Length==0?detail:"同步核心控制令牌不匹配");
                    else Log("message authentication failed");
                    return;
                }
                if(command=="start")
                {
                    string area=Get(message,"area");if(!Guid.TryParse(area,out _)) return;
                    lock(Gate)
                    {
                        if(!EnsureBridgeConfig()) { state="error";detail="同步核心缺少 Hue Bridge 配置，无法自动恢复"; }
                        else if(worker==null || worker.IsCompleted)
                        {
                            cancel=new CancellationTokenSource();state="starting";detail="正在连接灯光";
                            var localCancel=cancel;worker=Task.Run(()=>RunSyncAsync(area,localCancel));
                        }
                    }
                }
                else if(command=="stop")
                {
                    lock(Gate) { if(cancel!=null && worker!=null && !worker.IsCompleted) { state="stopping";detail="正在停止同步";cancel.Cancel(); } }
                }
                else if(command=="sensitivity")
                {
                    if(!int.TryParse(Get(message,"level"),out var level) || (level!=1 && level!=3 && level!=5)) return;
                    File.WriteAllText(SensitivityPath,JsonSerializer.Serialize(new { level }));
                    Volatile.Write(ref sensitivity,level);
                }
                else if(command=="configure")
                {
                    string json=Get(message,"configuration");
                    try
                    {
                        if(json.Length>16384) throw new InvalidOperationException("Configuration too large");
                        Settings.Parse(json);
                        bool busy;
                        lock(Gate)
                        {
                            busy=worker!=null && !worker.IsCompleted;
                            if(!busy) { ReplacePrivateFile(DefaultConfig,json);state="idle";detail="Bridge 已配对"; }
                        }
                        if(busy) { SendReply(app,port,requestId,"error","请先停止同步，再配对 Bridge");return; }
                    }
                    catch(Exception ex) { Log("bridge configuration failed: "+ex.GetType().Name);lock(Gate) { state="error";detail="无法保存 Bridge 配置"; } }
                }
                else if(command!="status") return;
                string s,d;lock(Gate) { s=state;d=detail; }
                SendReply(app,port,requestId,s,d);
            }
            catch(Exception ex) { Log("message failed: "+ex.GetType().Name+": "+ex.Message); }
        }
        internal static bool CanProvisionControlToken(bool trusted,string app,string port,string command,bool missing,string supplied)
        {
            if(!trusted || app!="HueSyncTV1.main" || port!="hue-ui" || command!="status" || !missing || supplied.Length!=64) return false;
            foreach(char c in supplied) if(!Uri.IsHexDigit(c)) return false;
            return true;
        }
        private static void SaveNewControlToken(string supplied)
        {
            WriteNewPrivateFile(TokenPath,JsonSerializer.Serialize(new { token=supplied }));
        }
        private static bool EnsureBridgeConfig()
        {
            if(File.Exists(DefaultConfig)) return true;
            try
            {
                string json=File.ReadAllText(UiConfig);
                if(json.Length>16384) throw new InvalidOperationException("Bridge configuration too large");
                Settings.Parse(json);
                WriteNewPrivateFile(DefaultConfig,json);
                Log("bridge configuration restored from UI application data");
                return true;
            }
            catch(Exception ex) { Log("bridge configuration restore failed: "+ex.GetType().Name); return false; }
        }
        private static void WriteNewPrivateFile(string path,string content)
        {
            // O_WRONLY | O_CREAT | O_EXCL; never replace an existing file and create it with mode 0600.
            int fd=open_create(path,1|64|128,384);
            if(fd<0) throw new IOException("Cannot create private configuration",new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            using var handle=new SafeFileHandle(new IntPtr(fd),true);
            using var file=new FileStream(handle,FileAccess.Write);
            byte[] bytes=Encoding.UTF8.GetBytes(content);
            file.Write(bytes,0,bytes.Length);
            file.Flush();
        }
        private static void ReplacePrivateFile(string path,string content)
        {
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { WriteNewPrivateFile(temporary,content);File.Move(temporary,path,true); }
            finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void SendReply(string app,string port,string requestId,string status,string message)
        {
            var reply=bundle_create();if(reply==IntPtr.Zero) return;
            try {
                bundle_add_str(reply,"id",requestId);bundle_add_str(reply,"state",status);bundle_add_str(reply,"detail",message);bundle_add_str(reply,"sensitivity",Volatile.Read(ref sensitivity).ToString());
                int rc=message_port_send_trusted_message(app,port,reply);if(rc!=0) Log("message reply error: "+rc);
            } finally { bundle_free(reply); }
        }
        private static async Task RunSyncAsync(string id,CancellationTokenSource stop)
        {
            try
            {
                var settings=Settings.Load(DefaultConfig);settings.AreaId=id;
                using var bridge=new HueBridge(settings);var area=await bridge.GetAreaAsync();
                using var sampler=new SamsungSampler();
                await SyncRunner.RunAsync(bridge,settings,area,sampler,stop,0,s=>{ lock(Gate) { if(!stop.IsCancellationRequested) { state=s;detail="同步区域："+area.Name; } } },()=>Sensitivity.TimeConstant(Volatile.Read(ref sensitivity)));
                lock(Gate) { state="idle";detail="同步已停止"; }
            }
            catch(Exception ex) { Log("sync failed: "+ex.GetType().Name+": "+ex.Message);lock(Gate) { state="error";detail="无法同步，请检查灯光和网络连接"; } }
            finally { lock(Gate) { if(cancel==stop) cancel=null; } stop.Dispose(); }
        }
        private static void Log(string text)
        {
            try { if(File.Exists(LogPath)&&new FileInfo(LogPath).Length>32768) File.Delete(LogPath);File.AppendAllText(LogPath,DateTime.UtcNow.ToString("o")+" "+text+"\n"); } catch {}
        }
    }
}
