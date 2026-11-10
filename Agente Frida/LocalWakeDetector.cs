using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    // Vosk recebe o PCM do microfone ja selecionado. Nao abre outra captura nem acessa a rede.
    public sealed class LocalWakeDetector : IDisposable
    {
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ModelNew(byte[] path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeObject(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr RecognizerNew(IntPtr model,float rate,byte[] grammar);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Accept(IntPtr recognizer,byte[] bytes,int length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Result(IntPtr recognizer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void LogLevel(int level);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int FindWord(IntPtr model,byte[] word);
        private IntPtr _module,_model,_recognizer;
        private FreeObject _freeModel,_freeRecognizer,_reset;
        private Accept _accept;
        private Result _result,_partial;
        private readonly BlockingCollection<byte[]> _queue=new BlockingCollection<byte[]>(32);
        private Thread _worker;
        private volatile bool _disposed;
        private int _resetRequested;
        private string _pendingPartial;
        private DateTime _pendingSince;
        private readonly Action<string> _onWake;
        public volatile bool IsSpeakingPlayback;
        public string LastText {get;private set;}
        public bool SupportsOffKeyword {get;private set;}
        public bool Ready {get {return _recognizer!=IntPtr.Zero && !_disposed;}}
        private T Bind<T>(string name) where T:class
        {
            IntPtr p=GetProcAddress(_module,name);if(p==IntPtr.Zero)throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(p,typeof(T)) as T;
        }
        private static byte[] Utf8(string value) {return Encoding.UTF8.GetBytes(value+"\0");}
        private static string Text(IntPtr pointer)
        {
            if(pointer==IntPtr.Zero)return "";int n=0;while(Marshal.ReadByte(pointer,n)!=0)n++;
            var bytes=new byte[n];Marshal.Copy(pointer,bytes,0,n);return Encoding.UTF8.GetString(bytes);
        }
        public LocalWakeDetector(string folder,Action<string> onWake)
        {
            _onWake=onWake;
            try
            {
                _module=LoadLibraryEx(Path.Combine(folder,"native","libvosk.dll"),IntPtr.Zero,8);
                if(_module==IntPtr.Zero)throw new DllNotFoundException("Detector local: "+Marshal.GetLastWin32Error());
                Bind<LogLevel>("vosk_set_log_level")(-1);
                _freeModel=Bind<FreeObject>("vosk_model_free");_freeRecognizer=Bind<FreeObject>("vosk_recognizer_free");
                _model=Bind<ModelNew>("vosk_model_new")(Utf8(Path.Combine(folder,"vosk-model-small-pt-0.3")));
                if(_model==IntPtr.Zero)throw new InvalidDataException("Modelo local de chamada indisponivel");
                FindWord find=Bind<FindWord>("vosk_model_find_word");
                SupportsOffKeyword=find(_model,Utf8("off"))>=0;
                var phrases=new List<string>{"[unk]"};
                foreach(string name in new string[]{"frida","frita","ferida"})
                    if(find(_model,Utf8(name))>=0)
                    {
                        if(name=="frida")phrases.Add(name);
                        foreach(string greeting in new string[]{"olá","ola","oi","ei"})
                            if(find(_model,Utf8(greeting))>=0)phrases.Add(greeting+" "+name);
                    }
                foreach(string control in FridaSessionControl.SpokenControls)
                {
                    bool known=true;foreach(string word in control.Split(' '))if(find(_model,Utf8(word))<0){known=false;break;}
                    if(!known)continue;
                    phrases.Add(control);
                    foreach(string name in new[]{"frida","frita","ferida"})if(find(_model,Utf8(name))>=0)phrases.Add(name+" "+control);
                }
                foreach(string negative in new[]{"não pare","não parar","não para","como parar","como para","para parar"})
                    if(negative.Split(' ').All(word=>find(_model,Utf8(word))>=0))phrases.Add(negative);
                if(phrases.Count==1)throw new InvalidDataException("Vocabulario local sem variantes da chamada");
                _recognizer=Bind<RecognizerNew>("vosk_recognizer_new_grm")(_model,16000,new JavaScriptSerializer().Serialize(phrases).ToUtf8());
                if(_recognizer==IntPtr.Zero)throw new InvalidDataException("Reconhecedor local indisponivel");
                _accept=Bind<Accept>("vosk_recognizer_accept_waveform");_result=Bind<Result>("vosk_recognizer_result");
                _partial=Bind<Result>("vosk_recognizer_partial_result");_reset=Bind<FreeObject>("vosk_recognizer_reset");
                _worker=new Thread(Work){IsBackground=true,Name="FridaLocalWake"};_worker.Start();
            }
            catch {Dispose();throw;}
        }
        public void Push(byte[] pcm)
        {
            if(!Ready || pcm==null)return;
            var copy=(byte[])pcm.Clone();
            try {if(!_queue.TryAdd(copy)){byte[] old;_queue.TryTake(out old);_queue.TryAdd(copy);}}catch(InvalidOperationException){}
        }
        private void Work()
        {
            var json=new JavaScriptSerializer();
            try
            {
                foreach(byte[] pcm in _queue.GetConsumingEnumerable())
                {
                    if(_disposed)break;
                    if(Interlocked.Exchange(ref _resetRequested,0)!=0)_reset(_recognizer);
                    int complete=_accept(_recognizer,pcm,pcm.Length);
                    var value=json.Deserialize<Dictionary<string,object>>(Text((complete!=0?_result:_partial)(_recognizer)));
                    string key=complete!=0?"text":"partial";
                    string text=value!=null && value.ContainsKey(key)?Convert.ToString(value[key]):"";
                    if (!string.IsNullOrWhiteSpace(text)) LastText=text;
                    string cleaned;bool isControl;
                    if(ShouldAcceptLocalHypothesis(text,complete!=0,IsSpeakingPlayback,out cleaned,out isControl))
                    {
                        // Aguarda estabilidade: nao consome 'Frida' no primeiro fragmento de audio.
                        if(complete==0)
                        {
                            if(_pendingPartial!=cleaned){_pendingPartial=cleaned;_pendingSince=DateTime.UtcNow;continue;}
                            if((DateTime.UtcNow-_pendingSince).TotalMilliseconds<300)continue;
                        }
                        _pendingPartial=null;
                        _reset(_recognizer);
                        byte[] old;while(_queue.TryTake(out old)){}
                        if(_onWake!=null)_onWake(cleaned);
                    }
                    else _pendingPartial=null;
                }
            }
            catch(Exception ex) {if(!_disposed)BootLogger.Log("Detector local interrompido: "+ex.GetType().Name);}
        }
        internal static bool ShouldAcceptLocalHypothesis(string rawVoskText,bool complete,bool isSpeakingPlayback,out string cleanedText,out bool isControl)
        {
            cleanedText="";isControl=false;
            string raw=(rawVoskText ?? "").Trim();
            if(string.IsNullOrWhiteSpace(raw))return false;
            bool hadUnk=raw.IndexOf("[unk]",StringComparison.OrdinalIgnoreCase)>=0;
            cleanedText=System.Text.RegularExpressions.Regex.Replace(raw,@"^(?:\[unk\]\s*)+","").Trim();
            if(string.IsNullOrWhiteSpace(cleanedText))return false;
            FridaControlCommand cmd=FridaSessionControl.Parse(cleanedText);
            if(cmd!=FridaControlCommand.None)
            {
                // Parar e reversivel; OFF exige chamada explicita. Desconhecidas/negacoes nunca sao cortadas.
                if(hadUnk)return false;
                if(cmd==FridaControlCommand.Off && (!complete || !FridaSessionControl.IsAddressedControl(cleanedText)))return false;
                if(cmd==FridaControlCommand.Stop && !complete && !FridaSessionControl.IsExplicitCompoundOrWakeControl(cleanedText))return false;
                if(string.Equals(FridaInteractionPolicy.Normalize(cleanedText),"para",StringComparison.Ordinal) && hadUnk)
                    cmd=FridaControlCommand.None;
                else if(isSpeakingPlayback && !complete && !FridaSessionControl.IsExplicitCompoundOrWakeControl(cleanedText))
                    cmd=FridaControlCommand.None;
            }
            isControl=cmd!=FridaControlCommand.None;
            if(isControl)return true;
            if(isSpeakingPlayback)return false;
            string remainder;
            return FridaInteractionPolicy.TryStripWake(cleanedText,out remainder);
        }
        public void Reset()
        {
            Interlocked.Exchange(ref _resetRequested,1);
            byte[] old;while(_queue.TryTake(out old)){}
        }
        public void Dispose()
        {
            if(_disposed)return;_disposed=true;_queue.CompleteAdding();
            if(_worker!=null && Thread.CurrentThread!=_worker)_worker.Join();
            if(_recognizer!=IntPtr.Zero && _freeRecognizer!=null)_freeRecognizer(_recognizer);_recognizer=IntPtr.Zero;
            if(_model!=IntPtr.Zero && _freeModel!=null)_freeModel(_model);_model=IntPtr.Zero;
            if(_module!=IntPtr.Zero)FreeLibrary(_module);_module=IntPtr.Zero;
        }
    }
    internal static class WakeEncoding
    {
        public static byte[] ToUtf8(this string value) {return Encoding.UTF8.GetBytes(value+"\0");}
    }
}
