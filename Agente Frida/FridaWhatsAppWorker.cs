using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    // One owned Python process; stdout JSONL is never mixed with stderr diagnostics.
    public sealed class FridaWhatsAppWorker : IDisposable
    {
        private Process _process;
        // Process.StandardInput uses Console.InputEncoding (BOM/ANSI); the protocol is UTF-8 JSONL.
        private StreamWriter _input;
        private BlockingCollection<string> _output;
        private readonly Queue<string> _errors=new Queue<string>();
        private readonly object _sync=new object();
        private FridaSettings _settings;
        private static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=32*1024*1024};}
        public FridaWhatsAppWorker(FridaSettings settings){_settings=settings;}
        private static string Quote(string value){return "\""+value.Replace("\"","\\\"")+"\"";}
        private string Python()
        {
            string path=string.IsNullOrWhiteSpace(_settings.whatsapp_python)?Environment.GetEnvironmentVariable("FRIDA_PYTHON"):_settings.whatsapp_python;
            if(!string.IsNullOrWhiteSpace(path))return Environment.ExpandEnvironmentVariables(path);
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Python");
            if(Directory.Exists(root))foreach(string dir in Directory.GetDirectories(root).OrderByDescending(x=>x)){string exe=Path.Combine(dir,"python.exe");if(File.Exists(exe))return exe;}
            foreach(string dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)){try{string exe=Path.Combine(dir.Trim('"'),"python.exe");if(File.Exists(exe))return exe;}catch(ArgumentException){}}
            return null;
        }
        private void Start()
        {
            if(_process!=null&&!_process.HasExited)return;
            DisposeProcess();
            string python=Python(),root=AppDomain.CurrentDomain.BaseDirectory,script=Path.Combine(root,"whatsapp_reader.py");
            if(!File.Exists(python)||!File.Exists(script)||!File.Exists(Path.Combine(root,"whatsapp_runtime.py")))throw new FileNotFoundException("Python ou modulos do leitor nao encontrados.");
            var info=new ProcessStartInfo(python,"-u "+Quote(script)+" --serve") {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            info.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";info.EnvironmentVariables["PYTHONUTF8"]="1";
            if(!string.IsNullOrWhiteSpace(_settings.whatsapp_profile))info.EnvironmentVariables["FRIDA_WHATSAPP_PROFILE"]=Path.GetFullPath(Environment.ExpandEnvironmentVariables(_settings.whatsapp_profile));
            if(!string.IsNullOrWhiteSpace(_settings.whatsapp_chrome))info.EnvironmentVariables["FRIDA_CHROME_PATH"]=Environment.ExpandEnvironmentVariables(_settings.whatsapp_chrome);
            if(!string.IsNullOrWhiteSpace(_settings.whatsapp_cache))info.EnvironmentVariables["FRIDA_WHATSAPP_CACHE"]=Path.GetFullPath(Environment.ExpandEnvironmentVariables(_settings.whatsapp_cache));
            if(!string.IsNullOrWhiteSpace(_settings.whatsapp_chromedriver))info.EnvironmentVariables["FRIDA_CHROMEDRIVER"]=Environment.ExpandEnvironmentVariables(_settings.whatsapp_chromedriver);
            _output=new BlockingCollection<string>();var output=_output;
            lock(_errors)_errors.Clear();
            _process=new Process{StartInfo=info,EnableRaisingEvents=true};
            _process.OutputDataReceived+=(s,e)=>{if(e.Data!=null)try{output.Add(e.Data);}catch(InvalidOperationException){}};
            _process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(_errors){_errors.Enqueue(e.Data.Length>1000?e.Data.Substring(0,1000):e.Data);while(_errors.Count>12)_errors.Dequeue();}};
            _process.Start();_process.BeginOutputReadLine();_process.BeginErrorReadLine();
            _input=new StreamWriter(_process.StandardInput.BaseStream,new UTF8Encoding(false)){AutoFlush=true,NewLine="\n"};
        }
        private void Send(string line){_input.WriteLine(line);_input.Flush();}
        public Dictionary<string,object> Execute(Dictionary<string,object> args,Action<string> progress)
        {
            lock(_sync)
            {
                string id=Guid.NewGuid().ToString("N"),stage="worker_start";var json=Json();var stageWatch=Stopwatch.StartNew();var total=Stopwatch.StartNew();int timeout=45;
                var batches=new Dictionary<string,Dictionary<string,object>>();bool cancelSent=false;
                try
                {
                    Start();var request=new Dictionary<string,object>(args);request["request_id"]=id;
                    Send(json.Serialize(request));
                    while(true)
                    {
                        if(FridaWorkContext.Token.IsCancellationRequested&&!cancelSent)
                        {cancelSent=true;Send(json.Serialize(new{operation="cancel",request_id=id}));stageWatch.Restart();timeout=5;}
                        string line;
                        if(_output.TryTake(out line,100))
                        {
                            Dictionary<string,object> item;
                            try{item=json.Deserialize<Dictionary<string,object>>(line);}catch{continue;}
                            if(item==null)continue;
                            string kind=Value(item,"event");
                            // Requests are serialized: an unparseable line can only be the one in flight.
                            if(kind=="protocol_error")throw new InvalidDataException("O leitor rejeitou o pedido: "+Value(item,"message"));
                            if(Value(item,"request_id")!=id)continue;
                            if(kind=="qr") { FridaWhatsAppSource.PublishQr(Value(item,"png_base64")); continue; }
                            if(kind=="progress")
                            {
                                stage=Value(item,"stage");if(!cancelSent){int seconds;timeout=int.TryParse(Value(item,"timeout_seconds"),out seconds)?Math.Min(240,seconds)+15:135;stageWatch.Restart();}
                                if(progress!=null)progress(Value(item,"message"));
                                BootLogger.Log("WhatsApp etapa="+stage+"; request_id="+id);
                            }
                            if(kind=="batch")
                            {
                                object raw;if(item.TryGetValue("messages",out raw)&&raw is IEnumerable)foreach(var row in ((IEnumerable)raw).OfType<Dictionary<string,object>>())batches[Value(row,"reference")]=row;
                                if(progress!=null)progress("WhatsApp: "+batches.Count+" mensagens encontradas; continuando a busca.");
                            }
                            if(kind=="result")
                            {var result=item["result"] as Dictionary<string,object>;if(result==null)throw new InvalidDataException("Resultado JSON invalido.");result["worker_pid"]=_process.Id;result["stderr"]=Errors();return result;}
                        }
                        if(_process.HasExited)throw new IOException("O leitor encerrou antes do resultado.");
                        if(stageWatch.Elapsed.TotalSeconds>timeout)throw new TimeoutException("Prazo excedido na etapa "+stage);
                    }
                }
                catch(Exception ex)
                {
                    string error=cancelSent?"user_cancelled":ex is TimeoutException?"reader_timeout":ex is FileNotFoundException?"dependency_missing":"reader_process_error";
                    var result=new Dictionary<string,object>{{"schema_version",2},{"request_id",id},{"stage",stage},{"status",batches.Count>0?"partial":error},{"message",ex.Message},{"stderr",Errors()},{"elapsed_ms",total.ElapsedMilliseconds},{"source","browser_dom_snapshot"},{"coverage",new{complete=false,reason=error}},{"results",batches.Values.ToArray()},{"messages",batches.Values.ToArray()}};
                    DisposeProcess();return result;
                }
            }
        }
        internal static string Value(IDictionary<string,object> obj,string key){object value;return obj!=null&&obj.TryGetValue(key,out value)?Convert.ToString(value):"";}
        private string Errors(){lock(_errors)return string.Join("\n",_errors.ToArray());}
        private void DisposeProcess()
        {
            _input=null;
            if(_process!=null){try{if(!_process.HasExited)FridaVerifiedTools.KillOwnedProcessTree(_process);}catch(InvalidOperationException){}finally{_process.Dispose();_process=null;}}
        }
        public void Dispose(){lock(_sync)DisposeProcess();}
    }
}
