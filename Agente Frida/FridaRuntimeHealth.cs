using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public static class FridaRuntimeHealth
    {
        private static readonly object Sync=new object();
        private static Timer _timer;
        private static string _directory,_state="iniciando",_stage="boot",_request;
        private static DateTime _ui=DateTime.UtcNow;
        private static bool _clean;
        private static int _inactivityCycles;
        private static readonly Dictionary<string,string> Pending=new Dictionary<string,string>();
        public static bool UiStale(DateTime now,DateTime last){return (now-last).TotalSeconds>45;}
        public static void Start(string directory)
        {
            lock(Sync)
            {
                _directory=directory;_clean=false;_ui=DateTime.UtcNow;
                string path=Path.Combine(directory,"frida_heartbeat.json");
                try
                {
                    if(File.Exists(path))
                    {
                        var previous=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
                        object clean;
                        if(previous!=null && previous.TryGetValue("clean_shutdown",out clean) && clean is bool && !(bool)clean)
                        {
                            previous["recovery_status"]="interrupted_requires_reconciliation";
                            AtomicWrite(Path.Combine(directory,"frida_previous_interruption.json"),new JavaScriptSerializer().Serialize(previous));
                            BootLogger.Log("Execucao anterior interrompida. Operacoes pendentes exigem reconciliacao; nao serao repetidas automaticamente.");
                        }
                    }
                }
                catch(Exception ex){Crash(ex,"read_previous_heartbeat");}
                _timer=new Timer(delegate{Pulse();},null,0,15000);
            }
        }
        public static void State(string state,string stage)
        {
            lock(Sync)
            {
                if(_state!=state)BootLogger.Log("Estado: "+state+"; etapa="+stage);
                _state=state;_stage=stage;
            }
        }
        public static void Inactivity(){lock(Sync)_inactivityCycles++;}
        public static void UiPulse(){lock(Sync)_ui=DateTime.UtcNow;}
        public static void BeginRequest(string id){lock(Sync){_request=id;_stage="classificando";Pending.Clear();}Pulse();}
        public static void EndRequest(){lock(Sync){_request=null;Pending.Clear();}Pulse();}
        public static void OperationStarted(string key,string description){lock(Sync){Pending[key]=description;_stage=description;}Pulse();}
        public static void OperationFinished(string key){lock(Sync)Pending.Remove(key);Pulse();}
        public static void AtomicWrite(string path,string json)
        {
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                File.WriteAllText(temporary,json,new UTF8Encoding(false));
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        public static void Pulse()
        {
            lock(Sync)
            {
                if(_directory==null)return;
                try
                {
                    using(var process=Process.GetCurrentProcess())
                    AtomicWrite(Path.Combine(_directory,"frida_heartbeat.json"),new JavaScriptSerializer().Serialize(new {
                        schema_version=1,pid=process.Id,executable=process.MainModule.FileName,version=typeof(FridaRuntimeHealth).Assembly.GetName().Version.ToString(),
                        heartbeat_utc=DateTime.UtcNow.ToString("o"),last_ui_utc=_ui.ToString("o"),ui_stale=UiStale(DateTime.UtcNow,_ui),
                        state=_state,stage=_stage,request_id=_request,clean_shutdown=_clean,inactivity_cycles=_inactivityCycles,pending_operations=Pending
                    }));
                }
                catch(Exception ex){BootLogger.Log("Heartbeat nao persistido: "+ex.GetType().Name);}
            }
        }
        public static void Shutdown()
        {
            lock(Sync){_clean=Pending.Count==0 && _request==null;_state="encerrando";if(_timer!=null){_timer.Dispose();_timer=null;}}
            Pulse();
        }
        public static void Crash(Exception ex,string stage)
        {
            lock(Sync)
            {
                string line=DateTime.UtcNow.ToString("o")+" PID="+Process.GetCurrentProcess().Id+" version="+typeof(FridaRuntimeHealth).Assembly.GetName().Version+
                    " request="+(_request ?? "none")+" state="+_state+" stage="+stage+Environment.NewLine+ex+Environment.NewLine;
                try{File.AppendAllText(Path.Combine(_directory ?? AppDomain.CurrentDomain.BaseDirectory,"frida_crash.log"),line,Encoding.UTF8);}catch{}
                BootLogger.Log(line);
            }
        }
        public static bool Recover(int pid,string expectedPath,DateTime expectedStartUtc)
        {
            // Explicit recovery only; validate PID, start time and executable before stopping it.
            using(var p=Process.GetProcessById(pid))
            {
                if(p.Id==Process.GetCurrentProcess().Id || p.StartTime.ToUniversalTime()!=expectedStartUtc ||
                    !string.Equals(Path.GetFullPath(p.MainModule.FileName),Path.GetFullPath(expectedPath),StringComparison.OrdinalIgnoreCase))return false;
                p.Kill();return p.WaitForExit(5000);
            }
        }
    }
}
