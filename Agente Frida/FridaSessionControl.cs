using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace FridaAgent
{
    public enum FridaControlCommand { None, Stop, Off }
    public static class FridaSessionControl
    {
        public const int IdleSeconds=30;
        public static readonly string[] SpokenControls={"pare","parar","pause","pausar","cancele","cancelar","pare tudo","pare de falar","parar de falar","fique em off","ficar em off","fica em off","modo off","fique em silencio","ficar em silencio","desativar","desative","fique inativa"};
        public static FridaControlCommand Parse(string text)
        {
            string command;string value=FridaInteractionPolicy.Normalize(FridaInteractionPolicy.TryStripWake(text,out command)?command:text);
            value=Regex.Replace(value,@"^(?:(?:ola|oi|ei|hey)\s+)?(?:frida|firda|fridda|freeda|frita|ferida)\s+","");
            value=Regex.Replace(value,@"^(?:(?:por favor|voce|pode|poderia)\s+)+","");
            value=Regex.Replace(value,@"\s+(?:por favor|agora|frida|fridda|freeda|frita|ferida)$","").Trim();
            if(Regex.IsMatch(value,@"^(fique em off|ficar em off|fica em off|modo off|off|fique em silencio|ficar em silencio|desative|desativar|fique inativa|fique offline|modo offline)$"))return FridaControlCommand.Off;
            if(Regex.IsMatch(value,@"^(pare|parar|pause|pausar|pausa|cancele|cancelar|pare tudo|parar tudo|pare de falar|parar de falar|pode parar|interrompa|interromper|chega)$"))return FridaControlCommand.Stop;
            return FridaControlCommand.None;
        }
    }

    // Per-async-operation cancellation, shared by HTTP, teams, tools and speech helpers.
    public static class FridaWorkContext
    {
        private static readonly AsyncLocal<CancellationToken> Current=new AsyncLocal<CancellationToken>();
        public static CancellationToken Token {get{return Current.Value;}}
        public static IDisposable Enter(CancellationToken token)
        {var scope=new Scope(Current.Value);Current.Value=token;return scope;}
        public static void ThrowIfCancelled(){Token.ThrowIfCancellationRequested();}
        public static Task RunIndependent(Func<Task> operation)
        {
            return Task.Run(async delegate
            {
                using(Enter(CancellationToken.None))await operation();
            });
        }
        private sealed class Scope:IDisposable
        {
            private readonly CancellationToken _previous;
            internal Scope(CancellationToken previous){_previous=previous;}
            public void Dispose(){Current.Value=_previous;}
        }
    }
}
