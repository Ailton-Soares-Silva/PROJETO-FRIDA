using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace FridaAgent
{
    internal sealed class FridaProviderBudget
    {
        private readonly HashSet<string> _providers=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool TryEnter(string provider){if(_providers.Contains(provider))return true;if(_providers.Count>=2)return false;_providers.Add(provider);return true;}
        public void Clear(){_providers.Clear();}
    }
    public sealed class FridaExpectedOperation
    {
        public string Kind, Text, RawText, ExplicitTarget, Operation;
        public bool Matches(FridaToolReceipt r)
        {
            if(Kind=="research")return r.Tool=="web_research";
            if(r.Status==FridaResultStatus.ModelAnalysis)return false;
            if(!string.IsNullOrEmpty(ExplicitTarget) && !string.Equals(ExplicitTarget.TrimEnd('\\'),(r.Target ?? "").TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return false;
            if(!string.IsNullOrEmpty(Operation) && r.Operation!=Operation && !(Operation=="write" && r.Operation=="append"))return false;
            if(Kind=="whatsapp")return r.Tool=="read_whatsapp";
            if(Kind=="open")return r.Tool=="open_program_or_url" || r.Tool=="native_terminal";
            if(Kind=="power")return r.Tool=="native_power";
            if(Kind=="read")return !r.IsAction && (r.Tool=="manage_files" || r.Tool=="verify_artifact");
            if(Kind=="review")return !r.IsAction && (r.Tool=="manage_files" || r.Tool=="verify_artifact");
            if(Kind=="write")return r.IsAction && r.Tool=="manage_files";
            if(Kind=="research")return r.Tool=="web_research";
            if(Kind=="inspect")return r.Tool=="run_powershell";
            return r.Tool=="run_powershell" || r.Tool=="secretary_memory";
        }
    }

    public sealed class FridaRequestIntent
    {
        public bool IsExplanation, RequiresTools, IsNegated;
        public readonly List<FridaExpectedOperation> Operations=new List<FridaExpectedOperation>();
        public static FridaRequestIntent Classify(string request)
        {
            var result=new FridaRequestIntent();string rest;
            string raw=FridaInteractionPolicy.TryStripWake(request,out rest)?rest:request ?? "";
            string t=FridaInteractionPolicy.Normalize(raw);
            if(Regex.IsMatch(t,@"^(?:por favor )?nao (?:execute|abra|programe|agende|reinicie|desligue|crie|salve|apague|exclua|quero que|faca)\b"))
            {result.IsNegated=true;return result;}
            bool explanatory=Regex.IsMatch(t,@"^(?:por favor )?(?:(?:voce )?pode(?:ria)? )?(?:me )?(como|explique|explicar|o que|por que|mostre como|quero saber|gostaria de saber|descreva|detalhe)\b");
            bool observation=Regex.IsMatch(t,@"^(o que tem|o que ha|o que contem|como esta)\b") && Regex.IsMatch(t,@"\b(pasta|arquivo|diretorio|whatsapp|cpu|memoria|processos)\b");
            if(observation)explanatory=false;
            if(Regex.IsMatch(t,@"\b(piada|poema|poesia|conto|historia|rima)\b") && !Regex.IsMatch(t,@"\b(salve|grave|arquivo|documento|pasta)\b"))return result;
            var parts=Regex.Split(t,@"\b(?:e|depois|em seguida)\s+(?=(?:tambem )?(?:abra|abre|leia|liste|consulte|crie|salve|execute|rode|programe|agende|desligue|reinicie|cancele|pesquise)\b)");
            var rawParts=Regex.Split(raw,@"\b(?:e|depois|em seguida)\s+(?=(?:tamb[eé]m )?(?:abra|abre|leia|liste|consulte|crie|salve|execute|rode|programe|agende|desligue|reinicie|cancele|pesquise)\b)",RegexOptions.IgnoreCase);
            result.IsExplanation=explanatory && parts.Length==1;
            if(result.IsExplanation)return result;
            for(int index=0;index<parts.Length;index++)
            {
                string part=parts[index];string rawPart=rawParts.Length==parts.Length?rawParts[index]:raw;
                if(explanatory && part==parts[0])continue;
                string kind=null;
                if(Regex.IsMatch(part,@"\b(whatsapp|whats|wpp|zap)\b") && Regex.IsMatch(part,@"\b(grupos?|mensagens?|status|conectado|pareie|parear|conecte|conectar|vincule|novidades|nao lidas)\b"))kind="whatsapp";
                else if(Regex.IsMatch(part,@"\b(desligar|desligue|desligamento|reiniciar|reinicie|reinicializacao)\b"))kind="power";
                else if(Regex.IsMatch(part,@"\b(abra|abre|abrir|inicie|iniciar)\b"))kind="open";
                else if(Regex.IsMatch(part,@"\b(liste|listar|leia|ler|consulte|consultar|conteudo|quais arquivos|quais pastas)\b") && Regex.IsMatch(part,@"\b(arquivo|arquivos|pasta|pastas|diretorio|diretorios|documento|documentos|logs|conteudo)\b"))kind="read";
                else if(Regex.IsMatch(part,@"\b(crie|criar|gere|gerar|salve|salvar|grave|gravar|escreva|escrever|copie|mova|renomeie|apague|exclua)\b"))kind="write";
                else if(Regex.IsMatch(part,@"\b(analise|revise|confira|verifique|audite|valide|diagnostique)\b") && Regex.IsMatch(part,@"\b(arquivo|documento|pasta|projeto|repositorio|logs)\b"))kind="review";
                else if(Regex.IsMatch(part,@"\b(execute|executar|rode|rodar|use|utilize|programe|agende|configure|instale|corrija|implemente|construa|desenvolva|feche|fechar|minimize|maximize|restaure|limpe|limpar|otimize|remova|suspenda|suspender|hiberne|hibernar|ajuste|aumente|diminua|silencie|ative|desative|mate|finalize|encerre|lembre|registre|anote|compile|teste|organize|repare|conserte|atualize)\b"))kind="execute";
                else if(Regex.IsMatch(part,@"\b(pesquise|pesquisar|busque|procure|investigue)\b") || FridaEconomyRouting.NeedsFreshData(raw))kind="research";
                else if(Regex.IsMatch(part,@"\b(cpu|memoria|processos|hardware|temperatura|consumo|espaco livre)\b"))kind="inspect";
                else if(Regex.IsMatch(part,@"\b(quero|preciso|necessito|gostaria)\b") && Regex.IsMatch(part,@"\b(um programa|uma aplicacao|um aplicativo|um sistema|um script|um site|um dashboard|uma ferramenta)\b"))kind="execute";
                if(kind!=null)
                {
                    var target=Regex.Match(rawPart,"\"([a-zA-Z]:[\\\\/][^\"]+)\"|([a-zA-Z]:[\\\\/][^\\s]+)");
                    string operation=null;
                    if(kind=="write")operation=Regex.IsMatch(part,@"\b(apague|exclua|remova)\b")?"delete":Regex.IsMatch(part,@"\b(pasta|diretorio)\b")?"mkdir":"write";
                    if(kind=="whatsapp")operation=Regex.IsMatch(part,@"\b(pareie|parear|conecte|conectar|vincule)\b")?"pair":Regex.IsMatch(part,@"\b(status|conectado)\b")?"status":Regex.IsMatch(part,@"\b(nao lidas|mensagens novas|novidades)\b")?"read_unread":Regex.IsMatch(part,@"\b(grupos|quais grupos|liste grupos)\b")?"list_groups":null;
                    result.Operations.Add(new FridaExpectedOperation{Kind=kind,Text=part,RawText=rawPart,Operation=operation,ExplicitTarget=target.Success?(target.Groups[1].Success?target.Groups[1].Value:target.Groups[2].Value):null});
                }
            }
            result.RequiresTools=result.Operations.Count>0;return result;
        }
        public IList<FridaExpectedOperation> Missing(IEnumerable<FridaToolReceipt> receipts)
        {
            var available=receipts.Where(r=>r.SupersededBy==null).ToList();var missing=new List<FridaExpectedOperation>();
            foreach(var op in Operations)
            {
                // Match a receipt once. Failed attempts still cover a step, but cannot prove success.
                int i=available.FindIndex(op.Matches);
                if(i<0)missing.Add(op);else available.RemoveAt(i);
            }
            return missing;
        }
    }

    public static class FridaOperationalReply
    {
        public static string Finalize(FridaRequestIntent intent,FridaExecutionLedger ledger,string modelReply)
        {
            ledger.Revalidate();ledger.SetIntent(intent);
            if(!intent.RequiresTools)return modelReply;
            var receipts=ledger.Receipts.Where(r=>(r.Status!=FridaResultStatus.ModelAnalysis || r.Tool=="web_research") && r.SupersededBy==null).ToArray();
            if(receipts.Length==0)return "A operacao solicitada nao foi executada. "+(ledger.StopMessage ?? "Nenhum resultado de ferramenta foi registrado.");
            var lines=new List<string>();
            foreach(var r in receipts)
            {
                if(r.Tool=="read_whatsapp")lines.Add(FridaWhatsAppSource.Reply(r,""));
                else if(r.Tool=="web_research")
                {
                    lines.Add(r.Output);object sources;
                    if(r.Data.TryGetValue("sources",out sources))lines.Add(string.Join("\n",((System.Collections.IEnumerable)sources).Cast<object>().Select(Convert.ToString).ToArray()));
                }
                else if(!r.IsAction && (r.Status==FridaResultStatus.Verified || r.Status==FridaResultStatus.Partial) && r.Tool=="manage_files")lines.Add(r.Output);
                else if(r.Tool=="run_powershell")lines.Add(r.Summary+"\n"+r.Output);
                else lines.Add(r.Summary);
            }
            var missing=intent.Missing(receipts);
            if(missing.Count>0)lines.Add("Etapas pendentes: "+string.Join("; ",missing.Select(x=>x.Text).ToArray())+".");
            if(missing.Count==0 && intent.Operations.Any(x=>x.Kind=="review") && receipts.Any(r=>r.Status==FridaResultStatus.Verified && !r.IsAction))
            {
                // Keep the useful analysis, while confirmations still come solely from adapter receipts.
                string analysis=string.Join("\n",Regex.Split(modelReply ?? "",@"(?<=[.!?])\s+|[\r\n]+").Where(x=>!FridaReliability.HasCompletionClaim(x) && FridaReliability.GroundActivityClaims(x,false)==x).ToArray());
                if(!string.IsNullOrWhiteSpace(analysis))lines.Add(analysis);
            }
            if(ledger.StopMessage!=null)lines.Add(ledger.StopMessage);
            return string.Join("\n",lines.Distinct().ToArray());
        }
    }

    public static class FridaDuration
    {
        private static string Numbers(string text)
        {
            var words=new Dictionary<string,string>{{"uma","1"},{"um","1"},{"duas","2"},{"dois","2"},{"tres","3"},{"quatro","4"},{"cinco","5"},{"seis","6"},{"sete","7"},{"oito","8"},{"nove","9"},{"dez","10"},{"quinze","15"},{"vinte","20"},{"trinta","30"},{"meia","0.5"}};
            text=Regex.Replace(text,@"\b(uma?|duas?|dois|tres|\d+)\s+horas?\s+e\s+meia\b","$1 horas e 30 minutos");
            foreach(var pair in words)text=Regex.Replace(text,@"\b"+pair.Key+@"\b",pair.Value);
            text=Regex.Replace(text,@"\b(20|30)\s+e\s+([1-9])\b",m=>(int.Parse(m.Groups[1].Value)+int.Parse(m.Groups[2].Value)).ToString(CultureInfo.InvariantCulture));
            return text;
        }
        public static bool TryParse(string raw,out int seconds)
        {
            seconds=0;
            // Keep decimal punctuation; Normalize() intentionally removes it.
            string text=Numbers((raw ?? "").ToLowerInvariant().Replace("três","tres").Replace(',','.'));
            if(Regex.IsMatch(text,@"(?<!\w)-\s*\d|\b(menos|dias?|semanas?|meses|anos?)\b") || Regex.IsMatch(text,@"\b(amanha|amanhã|hoje|às|as)\s*\d"))return false;
            text=Regex.Replace(text,@"(\d+)\s*h\s*(\d{1,2})(?!\d)","$1 horas $2 minutos");
            var matches=Regex.Matches(text,@"(?<![\d.])(\d+(?:\.\d+)?)\s*(horas?|h|minutos?|min|m|segundos?|seg|s)\b");
            if(matches.Count==0)return false;
            string remaining=Regex.Replace(text,@"(?<![\d.])(\d+(?:\.\d+)?)\s*(horas?|h|minutos?|min|m|segundos?|seg|s)\b","");
            if(Regex.IsMatch(remaining,@"\b\d+(?:\.\d+)?\b"))return false;
            double total=0;
            foreach(Match m in matches)
            {
                double value;if(!double.TryParse(m.Groups[1].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value))return false;
                string unit=m.Groups[2].Value;total+=value*(unit.StartsWith("h")?3600:unit.StartsWith("m")?60:1);
            }
            if(total<1 || total>315360000 || Math.Floor(total)!=total)return false;
            seconds=(int)total;return true;
        }
    }

    public static class FridaNativeCommands
    {
        public static FridaToolReceipt Execute(string request,Func<string,string,FridaToolReceipt> run)
        {
            var intent=FridaRequestIntent.Classify(request);
            if(intent.IsNegated)return new FridaToolReceipt("request_control","no_execution","",false).Finish(FridaResultStatus.ProcessCompleted,"no_execution","Certo, nenhum comando sera executado para esse pedido.");
            if(!intent.RequiresTools || intent.Operations.Count!=1)return null;
            request=intent.Operations[0].RawText ?? request;
            string t=FridaInteractionPolicy.Normalize(request);
            if(intent.Operations[0].Kind=="power")
            {
                bool cancel=Regex.IsMatch(t,@"\b(cancele|cancelar|cancela|aborte|abortar|nao desligar|interromper)\b");
                int seconds;
                if(!cancel && !FridaDuration.TryParse(request,out seconds))return new FridaToolReceipt("native_power","schedule","Windows",true).Fail("missing_duration","Em quanto tempo devo desligar ou reiniciar o computador? Informe horas, minutos ou segundos.");
                seconds=0;if(!cancel)FridaDuration.TryParse(request,out seconds);
                bool restart=Regex.IsMatch(t,@"\b(reiniciar|reinicie|reinicializacao)\b");
                string args=cancel?"/a":(restart?"/r":"/s")+" /t "+seconds;
                var r=run("shutdown.exe",args);r.Tool="native_power";r.Operation=cancel?"cancel":restart?"restart":"shutdown";r.Target="Windows";
                r.Put("arguments",args);
                object code;if(r.Data.TryGetValue("exit_code",out code) && Convert.ToInt32(code)==1116 && cancel)return r.Fail("no_shutdown_pending","Nao ha desligamento ou reinicializacao agendado para cancelar.");
                if(r.Status==FridaResultStatus.ProcessCompleted)
                    return r.Finish(FridaResultStatus.Submitted,"windows_accepted",cancel?"O Windows aceitou o cancelamento do agendamento.":"O Windows aceitou o agendamento de "+(restart?"reinicializacao":"desligamento")+" para daqui a "+seconds+" segundos.",r.Output);
                return r;
            }
            if(Regex.IsMatch(t,@"\b(cmd|prompt|terminal|powershell)\b") && Regex.IsMatch(t,@"\b(abra|abrir|abre|inicie|iniciar)\b")
                && !Regex.IsMatch(t,@"\b(para executar|para rodar|e execute|e rode)\b"))
                return run(t.Contains("powershell")?"powershell.exe":"cmd.exe","__visible_terminal__");
            return null;
        }
        public static FridaToolReceipt Run(string exe,string args)
        {
            if(args=="__visible_terminal__")
            {
                var r=new FridaToolReceipt("native_terminal","open",exe,true);
                try
                {
                    using(var p=Process.Start(new ProcessStartInfo(exe){UseShellExecute=true}))
                    {
                        if(p==null)return r.Finish(FridaResultStatus.Submitted,"shell_dispatch","A abertura do terminal foi solicitada ao Windows.");
                        r.Put("process_id",p.Id);var clock=Stopwatch.StartNew();
                        while(clock.ElapsedMilliseconds<2500 && !p.HasExited){p.Refresh();if(p.MainWindowHandle!=IntPtr.Zero){r.Check("window_exists",true,"Janela do terminal","Janela encontrada","process_window");return r.Finish(FridaResultStatus.Verified,"visible_window","Terminal aberto: "+exe+".");}Thread.Sleep(100);}
                        return r.Finish(FridaResultStatus.Submitted,"shell_dispatch","A abertura do terminal foi solicitada; a janela ainda nao foi confirmada.");
                    }
                }
                catch(Exception ex){return r.Fail(ex.GetType().Name,"Falha ao abrir o terminal: "+ex.Message);}
            }
            // Arguments are constructed exclusively by the native parser, never from model text.
            return FridaVerifiedTools.RunPowerShell("& \""+Path.Combine(Environment.SystemDirectory,exe)+"\" "+args,10000);
        }
    }
}
