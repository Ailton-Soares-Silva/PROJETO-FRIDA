using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

namespace FridaAgent
{
    public static class FridaVerifiedTools
    {
        private static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        public static string Sha256(byte[] bytes)
        {using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        public static string FileSha256(string path)
        {using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        private static string RequiredPath(string path)
        {if(string.IsNullOrWhiteSpace(path))throw new ArgumentException("Informe um caminho explicito.");return Path.GetFullPath(path);}
        private static bool BinaryDocument(string path)
        {return System.Text.RegularExpressions.Regex.IsMatch(Path.GetExtension(path),@"(?i)^\.(doc|xlsx|xls|pdf|pptx|ppt|exe|dll|zip|png|jpg|jpeg|gif|wav|mp3|dwg|sldprt|sldasm)$");}
        private static string NormalizeLines(string value){return (value ?? "").Replace("\r\n","\n").Replace('\r','\n');}
        private static void ValidateTextFormat(string path,string content)
        {
            string ext=Path.GetExtension(path).ToLowerInvariant();
            if(ext==".json")new JavaScriptSerializer {MaxJsonLength=32*1024*1024}.DeserializeObject(content);
            if(ext==".xml" || ext==".svg")using(var reader=XmlReader.Create(new StringReader(content),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))while(reader.Read()){}
        }
        public static FridaToolReceipt WriteFile(string path,string content,bool append)
        {
            FridaWorkContext.ThrowIfCancelled();
            var receipt=new FridaToolReceipt("manage_files",append?"append":"write",path,true);
            string temporary=null;
            try
            {
                path=RequiredPath(path);receipt.Target=path;content=content ?? "";
                bool docx=Path.GetExtension(path).Equals(".docx",StringComparison.OrdinalIgnoreCase);
                if(BinaryDocument(path) || (append && docx))return receipt.Fail("unsupported_format","Formato exige uma ferramenta de geracao apropriada; o arquivo nao foi gravado.");
                string before=File.Exists(path)?FileSha256(path):null;
                receipt.Put("before_sha256",before);
                string parent=Path.GetDirectoryName(path);Directory.CreateDirectory(parent);
                if(docx)
                {
                    FridaDocumentIO.WriteDocx(path,content);
                    string read=FridaDocumentIO.ReadDocx(path,0,int.MaxValue);
                    string expectedText=NormalizeLines(content)+"\n";
                    bool matches=NormalizeLines(read)==expectedText;
                    receipt.Check("docx_content_roundtrip",matches,Sha256(Utf8.GetBytes(expectedText)),Sha256(Utf8.GetBytes(NormalizeLines(read))),"docx_xml_reader");
                    if(!matches)return receipt.Fail("content_mismatch","O DOCX foi gravado, mas a conferencia do conteudo falhou.");
                }
                else
                {
                    byte[] bytes=Utf8.GetBytes(content);
                    if(!append)ValidateTextFormat(path,content);
                    temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                    using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {
                        if(append && before!=null)using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))input.CopyTo(output);
                        output.Write(bytes,0,bytes.Length);output.Flush(true);
                    }
                    if(append && (Path.GetExtension(path).Equals(".json",StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".xml",StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".svg",StringComparison.OrdinalIgnoreCase)))
                        ValidateTextFormat(path,File.ReadAllText(temporary,Utf8));
                    string expected=FileSha256(temporary);
                    if(before!=(File.Exists(path)?FileSha256(path):null))return receipt.Fail("concurrent_change","O arquivo mudou durante a preparacao. A substituicao nao foi realizada.");
                    FridaWorkContext.ThrowIfCancelled();
                    if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
                    string actual=FileSha256(path);
                    receipt.Check("file_bytes_sha256",expected==actual,expected,actual,"sha256_readback");
                    if(expected!=actual)return receipt.Fail("content_mismatch","O conteudo lido apos a gravacao difere do conteudo preparado.");
                }
                receipt.Check("file_exists",File.Exists(path),"true",File.Exists(path).ToString(),"filesystem");
                receipt.MonitorKind="file";receipt.MonitorHash=FileSha256(path);
                receipt.Put("sha256",receipt.MonitorHash);receipt.Put("bytes",new FileInfo(path).Length);
                return receipt.Finish(FridaResultStatus.Verified,"file_content_at_timestamp",(append?"Conteudo anexado":"Arquivo gravado")+" e conferido por releitura"+(docx?" do DOCX":" e SHA-256")+": "+Path.GetFileName(path)+".");
            }
            catch(Exception ex){return receipt.Fail(ex.GetType().Name,"Falha ao gravar ou verificar o arquivo: "+ex.Message);}
            finally {if(temporary!=null && File.Exists(temporary))try{File.Delete(temporary);}catch{}}
        }
        public static FridaToolReceipt ReadFile(string path,int offset,int limit)
        {
            FridaWorkContext.ThrowIfCancelled();
            var receipt=new FridaToolReceipt("manage_files","read",path,false);
            try
            {
                path=RequiredPath(path);receipt.Target=path;string before=FileSha256(path);
                var snapshot=FridaDocumentIO.ReadSnapshot(path,offset,limit);string text=snapshot.Describe();string after=FileSha256(path);
                receipt.Check("stable_read",before==after,before,after,"sha256_before_after_read");
                receipt.Put("sha256",after);receipt.Put("offset",offset);receipt.Put("limit",limit);
                if(before!=after)return receipt.Finish(FridaResultStatus.Unconfirmed,"read_snapshot","O arquivo mudou durante a leitura.",text);
                bool partial=snapshot.HasMore;receipt.Put("has_more",partial);receipt.Put("characters",snapshot.Text.Length);
                return receipt.Finish(partial?FridaResultStatus.Partial:FridaResultStatus.Verified,"read_snapshot",(partial?"Trecho parcial lido":"Leitura realizada")+": "+Path.GetFileName(path)+". Isso nao valida a interpretacao do conteudo.",text);
            }
            catch(Exception ex){return receipt.Fail(ex.GetType().Name,"Falha na leitura do arquivo: "+ex.Message);}
        }
        public static FridaToolReceipt DirectoryOperation(string operation,string path,string pattern)
        {
            FridaWorkContext.ThrowIfCancelled();
            var receipt=new FridaToolReceipt("manage_files",operation,path,operation=="mkdir" || operation=="delete");
            try
            {
                path=RequiredPath(path);receipt.Target=path;
                if(operation=="mkdir")
                {
                    Directory.CreateDirectory(path);bool exists=Directory.Exists(path);
                    receipt.Check("directory_exists",exists,"true",exists.ToString(),"filesystem");receipt.MonitorKind="directory";
                    return receipt.Finish(FridaResultStatus.Verified,"directory_exists","Pasta criada e existencia conferida: "+new DirectoryInfo(path).Name+".");
                }
                if(operation=="delete")
                {
                    if(string.Equals(path.TrimEnd('\\'),Path.GetPathRoot(path).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return receipt.Fail("invalid_target","Informe um alvo de remocao que nao seja a raiz da unidade.");
                    if(File.Exists(path))File.Delete(path);else if(Directory.Exists(path))Directory.Delete(path,true);
                    else return receipt.Fail("not_found","Alvo nao encontrado para remocao.");
                    bool absent=!File.Exists(path) && !Directory.Exists(path);
                    receipt.Check("target_absent",absent,"true",absent.ToString(),"filesystem");receipt.MonitorKind="absent";
                    return receipt.Finish(FridaResultStatus.Verified,"target_absent","Remocao realizada e ausencia do alvo conferida: "+Path.GetFileName(path)+".");
                }
                if(operation!="list" && operation!="search")return receipt.Fail("unknown_operation","Operacao de arquivo desconhecida.");
                if(!Directory.Exists(path))return receipt.Fail("not_found","Diretorio nao encontrado.");
                var text=new StringBuilder();int count=0;bool partial=false;
                try
                {
                    if(operation=="list")foreach(string dir in Directory.EnumerateDirectories(path)){FridaWorkContext.ThrowIfCancelled();text.AppendLine("[PASTA] "+dir);if(++count>=80){partial=true;break;}}
                    if(!partial)foreach(string file in Directory.EnumerateFiles(path,string.IsNullOrWhiteSpace(pattern)?"*":pattern,operation=="search"?SearchOption.AllDirectories:SearchOption.TopDirectoryOnly))
                    {FridaWorkContext.ThrowIfCancelled();text.AppendLine(file);if(++count>=80){partial=true;break;}}
                }
                catch(UnauthorizedAccessException){partial=true;}
                catch(IOException){partial=true;}
                receipt.Check("directory_accessed",true,"Diretorio acessivel","true","filesystem");receipt.Put("items",count);receipt.Put("complete",!partial);
                return receipt.Finish(partial?FridaResultStatus.Partial:FridaResultStatus.Verified,"directory_snapshot",(partial?"Listagem parcial":"Listagem concluida no escopo percorrido")+"; "+count+" itens.",text.ToString());
            }
            catch(Exception ex){return receipt.Fail(ex.GetType().Name,"Falha na operacao de diretorio: "+ex.Message);}
        }
        public static FridaToolReceipt VerifyArtifact(string path,string kind,string expectedHash)
        {
            FridaWorkContext.ThrowIfCancelled();
            var receipt=new FridaToolReceipt("verify_artifact",kind,path,false);
            try
            {
                path=RequiredPath(path);receipt.Target=path;bool exists=File.Exists(path);
                receipt.Check("file_exists",exists,"true",exists.ToString(),"filesystem");
                if(!exists)return receipt.Fail("not_found","Arquivo nao encontrado para verificacao.");
                string hash=FileSha256(path);receipt.Put("sha256",hash);receipt.Put("bytes",new FileInfo(path).Length);
                if(kind=="sha256")
                {
                    bool match=string.Equals(hash,expectedHash,StringComparison.OrdinalIgnoreCase);
                    receipt.Check("sha256_matches",match,expectedHash,hash,"sha256_readback");
                    if(!match)return receipt.Fail("hash_mismatch","O SHA-256 do arquivo difere do esperado.");
                }
                else if(kind=="json" || kind=="xml")
                {
                    if(new FileInfo(path).Length>16*1024*1024)return receipt.Fail("too_large","Arquivo excede o limite deste validador de formato.");
                    string content=File.ReadAllText(path,Utf8);
                    if(kind=="json")new JavaScriptSerializer {MaxJsonLength=32*1024*1024}.DeserializeObject(content);
                    else using(var reader=XmlReader.Create(new StringReader(content),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))while(reader.Read()){}
                    receipt.Check("format_parse",true,kind,"parse_ok","local_parser");
                }
                else if(kind=="docx"){FridaDocumentIO.ReadDocx(path,0,1);receipt.Check("docx_readable",true,"word/document.xml","parse_ok","docx_xml_reader");}
                else if(kind!="exists")return receipt.Fail("unsupported_verifier","Verificador desconhecido. Use exists, sha256, json, xml ou docx.");
                string after=FileSha256(path);receipt.Check("stable_verification",hash==after,hash,after,"sha256_before_after");
                receipt.MonitorKind="file";receipt.MonitorHash=after;
                return receipt.Finish(FridaResultStatus.Verified,"artifact_"+kind,"Verificacao "+kind+" aprovada para "+Path.GetFileName(path)+". O funcionamento do programa nao foi certificado.");
            }
            catch(Exception ex){return receipt.Fail(ex.GetType().Name,"Falha na verificacao do artefato: "+ex.Message);}
        }
        private sealed class Captured {public string Text;public bool Truncated;}
        private static Captured Drain(StreamReader reader)
        {
            var text=new StringBuilder();char[] buffer=new char[4096];int count;bool truncated=false;
            while((count=reader.Read(buffer,0,buffer.Length))>0){int keep=Math.Min(count,65536-text.Length);if(keep>0)text.Append(buffer,0,keep);if(keep<count)truncated=true;}
            return new Captured {Text=text.ToString(),Truncated=truncated};
        }
        public static FridaToolReceipt RunPowerShell(string command,int timeoutMs)
        {
            var receipt=new FridaToolReceipt("run_powershell","execute","powershell.exe",true);
            var cancellation=FridaWorkContext.Token;
            if(cancellation.IsCancellationRequested)return receipt.Finish(FridaResultStatus.Unconfirmed,"user_cancelled","Execucao cancelada antes de iniciar o processo.");
            if(string.IsNullOrWhiteSpace(command))return receipt.Fail("empty_command","Comando vazio; nenhum processo executado.");
            try
            {
                string wrapped="[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $ErrorActionPreference = 'Stop'; "+command+"; if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }";
                var info=new ProcessStartInfo("powershell.exe","-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped))) {
                    UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
                using(var process=Process.Start(info))
                {
                    receipt.Put("process_id",process.Id);receipt.Put("command_sha256",Sha256(Utf8.GetBytes(command)));
                    var stdout=Task.Run(()=>Drain(process.StandardOutput));var stderr=Task.Run(()=>Drain(process.StandardError));
                    var wait=Stopwatch.StartNew();bool exited=false;
                    while(!(exited=process.WaitForExit(100)) && !cancellation.IsCancellationRequested && wait.ElapsedMilliseconds<timeoutMs){}
                    if(cancellation.IsCancellationRequested)
                    {
                        KillOwnedProcessTree(process);
                        receipt.Check("operator_cancelled",false,"Execucao concluida","Cancelada pelo operador","cancellation_token");
                        return receipt.Finish(FridaResultStatus.Unconfirmed,"user_cancelled","Processo interrompido pelo operador. Efeitos anteriores nao sao desfeitos automaticamente.");
                    }
                    if(!exited)
                    {
                        KillOwnedProcessTree(process);
                        receipt.Check("process_exited_in_time",false,"<= "+timeoutMs+" ms","timeout","process_handle");
                        return receipt.Finish(FridaResultStatus.Unconfirmed,"process_timeout","O processo excedeu o tempo limite. Resultado e eventuais efeitos parciais nao confirmados.");
                    }
                    bool streamsComplete=Task.WaitAll(new Task[]{stdout,stderr},2000);
                    int code=process.ExitCode;receipt.Put("exit_code",code);receipt.Put("output_complete",streamsComplete);
                    receipt.Check("process_exit_code",code==0,"0",code.ToString(),"process_handle");
                    if(stdout.IsCompleted && !stdout.IsFaulted){receipt.Put("stdout",stdout.Result.Text);receipt.Put("stdout_truncated",stdout.Result.Truncated);}
                    if(stderr.IsCompleted && !stderr.IsFaulted){receipt.Put("stderr",stderr.Result.Text);receipt.Put("stderr_truncated",stderr.Result.Truncated);}
                    string output="Codigo de saida: "+code+"\n"+(stdout.IsCompleted && !stdout.IsFaulted?stdout.Result.Text:"")+"\n[STDERR]: "+(stderr.IsCompleted && !stderr.IsFaulted?stderr.Result.Text:"");
                    return receipt.Finish(code==0?FridaResultStatus.ProcessCompleted:FridaResultStatus.Failed,"process_exit_only",code==0?"Processo encerrado com codigo zero. O resultado funcional ainda precisa de verificacao.":"Processo falhou com codigo de saida "+code+". A tarefa nao foi confirmada.",output);
                }
            }
            catch(Exception ex){return receipt.Fail(ex.GetType().Name,"Falha ao executar o processo: "+ex.Message);}
        }
        internal static void KillOwnedProcessTree(Process process)
        {
            try
            {
                if(process.HasExited)return;
                int id=process.Id;DateTime started=process.StartTime;
                using(var current=Process.GetProcessById(id))
                    if(current.StartTime!=started)return;
                using(var kill=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"taskkill.exe"),"/PID "+id+" /T /F"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}))
                {kill.WaitForExit(1500);}
                if(!process.HasExited)process.Kill();
            }
            catch {try{if(!process.HasExited)process.Kill();}catch{}}
        }
        internal static FridaToolReceipt Dispatch(ProcessStartInfo info,string target)
        {
            var receipt=new FridaToolReceipt("open_program_or_url","launch",target,true);
            try
            {
                using(var process=Process.Start(info))if(process!=null)try{receipt.Put("process_id",process.Id);}catch(InvalidOperationException){}
                receipt.Check("shell_dispatch",true,"Process.Start sem erro","accepted","windows_process_api");
                return receipt.Finish(FridaResultStatus.Submitted,"launch_dispatch_only","Solicitacao de abertura enviada ao Windows. A janela e o carregamento do arquivo nao foram verificados.");
            }
            catch(Exception ex){return receipt.Fail(ex.GetType().Name,"Falha na solicitacao de abertura: "+ex.Message);}
        }
        internal static FridaToolReceipt Analysis(string tool,string operation,string text,string target)
        {
            var receipt=new FridaToolReceipt(tool,operation,target,false);
            receipt.Put("text",text);
            return receipt.Finish(FridaResultStatus.ModelAnalysis,"model_interpretation_only","Interpretacao recebida do modelo; fatos e conclusoes nao foram validados automaticamente.",text);
        }
    }
}
