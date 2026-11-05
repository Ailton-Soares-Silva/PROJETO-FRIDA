using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace FridaAgent
{
    public sealed class FridaSearchResult
    {
        public string Status="not_found", Query;
        public readonly List<string> Candidates=new List<string>();
        public readonly List<string> Errors=new List<string>();
        public string Message
        {
            get
            {
                if(Status=="ambiguous")return "Encontrei mais de um alvo. Qual devo abrir? "+string.Join("; ",Candidates.Select((path,index)=>(index+1)+". "+path).ToArray());
                if(Status=="incomplete")return "A busca ficou incompleta (limite de tempo, acesso ou quantidade). Informe o caminho mais especifico.";
                return "Arquivo ou pasta nao encontrado: "+Query+". Informe o caminho correto.";
            }
        }
    }
    public sealed class FridaSearchException:IOException
    {
        public readonly FridaSearchResult Result;
        public FridaSearchException(FridaSearchResult result):base(result.Message){Result=result;}
    }
    public static class FridaFileSearch
    {
        public static string NameKey(string text){return FridaInteractionPolicy.Normalize(text).Replace('_',' ');}
        public static string QueryKey(string text)
        {
            string t=NameKey(text);
            t=Regex.Replace(t,@"\b(?:disco|drive|unidade)\s+[a-z]\b","");
            t=Regex.Replace(t,@"\b(abra|abre|abrir|a|o|as|os|do|da|de|no|na|meu|minha|pasta|arquivo|diretorio|disco|drive|google|por favor)\b","");
            return Regex.Replace(t,@"\s+"," ").Trim();
        }
        public static string[] Roots(string request)
        {
            var drive=Regex.Match(request ?? "",@"(?i)\b(?:disco|drive|unidade)\s+([a-z])\b|\b([a-z]):[\\/]");
            if(drive.Success)
            {
                string root=(drive.Groups[1].Success?drive.Groups[1].Value:drive.Groups[2].Value).ToUpperInvariant()+":\\";
                string mine=Path.Combine(root,"Meu Drive");return new[]{Directory.Exists(mine)?mine:root};
            }
            if(Regex.IsMatch(NameKey(request),@"\b(drive|google)\b"))
            {
                var mounts=new List<string>();
                foreach(var d in DriveInfo.GetDrives())try{string p=Path.Combine(d.RootDirectory.FullName,"Meu Drive");if(d.IsReady && Directory.Exists(p))mounts.Add(p);}catch(IOException){}
                return mounts.ToArray();
            }
            return new[]{Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads")};
        }
        public static FridaSearchResult Find(string request,IEnumerable<string> roots,TimeSpan budget,CancellationToken token,
            Func<string,IEnumerable<string>> enumerate,Func<string,FileAttributes> attributes)
        {
            var result=new FridaSearchResult{Query=QueryKey(request)};
            if(string.IsNullOrEmpty(result.Query))return result;
            var watch=Stopwatch.StartNew();var queue=new Queue<KeyValuePair<string,int>>();
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var partial=new List<string>();int visited=0;
            foreach(string root in roots)queue.Enqueue(new KeyValuePair<string,int>(root,0));
            bool limited=false;int bestDepth=int.MaxValue;
            while(queue.Count>0)
            {
                token.ThrowIfCancellationRequested();
                if(watch.Elapsed>=budget || visited>=100000){limited=true;break;}
                var node=queue.Dequeue();if(node.Value>bestDepth || !seen.Add(node.Key))continue;
                try
                {
                    foreach(string path in enumerate(node.Key))
                    {
                        token.ThrowIfCancellationRequested();
                        if(watch.Elapsed>=budget || ++visited>=100000){limited=true;break;}
                        FileAttributes a;
                        try{a=attributes(path);}catch(IOException ex){result.Errors.Add(ex.GetType().Name);continue;}catch(UnauthorizedAccessException ex){result.Errors.Add(ex.GetType().Name);continue;}
                        if((a&FileAttributes.ReparsePoint)!=0)continue;
                        string name=NameKey(Path.GetFileName(path));
                        if(name==result.Query){result.Candidates.Add(path);bestDepth=node.Value;}
                        else if(name.Contains(result.Query))partial.Add(path);
                        if(result.Candidates.Count>=64 || partial.Count>=256){limited=true;break;}
                        if((a&FileAttributes.Directory)!=0)
                        {if(node.Value<bestDepth && node.Value<8)queue.Enqueue(new KeyValuePair<string,int>(path,node.Value+1));else if(node.Value>=8 && bestDepth==int.MaxValue)limited=true;}
                    }
                }
                catch(IOException ex){result.Errors.Add(node.Key+": "+ex.GetType().Name);}
                catch(UnauthorizedAccessException ex){result.Errors.Add(node.Key+": "+ex.GetType().Name);}
                if(limited && (watch.Elapsed>=budget || visited>=100000 || result.Candidates.Count>=64 || partial.Count>=256))break;
            }
            if(result.Candidates.Count==0)result.Candidates.AddRange(partial.Distinct(StringComparer.OrdinalIgnoreCase).Take(64));
            if(limited || result.Errors.Count>0)result.Status="incomplete";
            else result.Status=result.Candidates.Count>1?"ambiguous":result.Candidates.Count==1?"found":"not_found";
            return result;
        }
        public static string Resolve(string request)
        {
            var r=Find(request,Roots(request),TimeSpan.FromSeconds(10),FridaWorkContext.Token,
                p=>Directory.EnumerateFileSystemEntries(p),File.GetAttributes);
            if(r.Status=="found")return r.Candidates[0];
            if(r.Status!="not_found")throw new FridaSearchException(r);
            return null;
        }
        public static FridaToolReceipt DispatchFound(string path,string arguments)
        {
            var r=new FridaToolReceipt("open_program_or_url","launch",path,true);
            bool exists=File.Exists(path)||Directory.Exists(path);
            r.Check("target_exists",exists,"Alvo existente",path,"filesystem");
            if(!exists)return r.Fail("target_disappeared","O alvo deixou de existir antes da abertura: "+path);
            var dispatched=FridaVerifiedTools.Dispatch(new ProcessStartInfo(path,arguments ?? ""){UseShellExecute=true},path);
            dispatched.Check("target_exists_at_dispatch",true,"Alvo existente",path,"filesystem");
            dispatched.Put("resolved_path",path);return dispatched;
        }
    }
}
