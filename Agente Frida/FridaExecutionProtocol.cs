using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public enum FridaResultStatus { Verified, ProcessCompleted, Submitted, Partial, Failed, Unconfirmed, ModelAnalysis }

    public sealed class FridaVerification
    {
        public string Name { get; internal set; }
        public bool Passed { get; internal set; }
        public string Expected { get; internal set; }
        public string Observed { get; internal set; }
        public string Verifier { get; internal set; }
    }

    // Created by local adapters. Model text and tool arguments are never deserialized into this type.
    public sealed class FridaToolReceipt
    {
        private readonly List<FridaVerification> _checks = new List<FridaVerification>();
        private readonly Dictionary<string,object> _data = new Dictionary<string,object>();
        public int SchemaVersion { get { return 1; } }
        public string RequestId { get; internal set; }
        public string OperationId { get; private set; }
        public string Tool { get; internal set; }
        public string Operation { get; internal set; }
        public string Target { get; internal set; }
        public string Reference { get; internal set; }
        public string ParametersSha256 { get; internal set; }
        public DateTime StartedUtc { get; private set; }
        public DateTime FinishedUtc { get; private set; }
        public FridaResultStatus Status { get; private set; }
        public string Scope { get; internal set; }
        public string Summary { get; internal set; }
        public string Output { get; internal set; }
        public string ErrorCode { get; internal set; }
        public bool IsAction { get; internal set; }
        public string SupersededBy { get; internal set; }
        public string StepId { get; internal set; }
        public int Attempt { get; internal set; }
        public string ProjectRevision { get; internal set; }
        private readonly List<string> _validationEvidenceRefs = new List<string>();
        public IList<string> ValidationEvidenceRefs { get { return _validationEvidenceRefs.AsReadOnly(); } }
        public IList<FridaVerification> Checks { get { return _checks.AsReadOnly(); } }
        public IDictionary<string,object> Data { get { return new Dictionary<string,object>(_data); } }
        internal string MonitorKind, MonitorHash;
        internal FridaToolReceipt(string tool,string operation,string target,bool action)
        {
            OperationId=Guid.NewGuid().ToString("N"); StartedUtc=DateTime.UtcNow;
            Tool=tool;Operation=operation;Target=target ?? "";IsAction=action;
            Attempt=1;StepId=(tool ?? "op")+":"+(operation ?? "run")+"#1";ProjectRevision="rev-0";
            Status=FridaResultStatus.Unconfirmed;Scope="none";Output="";Summary="Resultado nao verificado.";
        }
        internal void AddValidationEvidenceRef(string operationId)
        {
            if(!string.IsNullOrWhiteSpace(operationId) && !_validationEvidenceRefs.Contains(operationId))
                _validationEvidenceRefs.Add(operationId);
        }
        internal void Check(string name,bool passed,string expected,string observed,string verifier)
        { _checks.Add(new FridaVerification {Name=name,Passed=passed,Expected=expected,Observed=observed,Verifier=verifier}); }
        internal void Put(string key,object value) { _data[key]=value; }
        internal FridaToolReceipt Finish(FridaResultStatus status,string scope,string summary,string output=null)
        {
            if(status==FridaResultStatus.Verified && (_checks.Count==0 || _checks.Any(x=>!x.Passed)))
                status=FridaResultStatus.Unconfirmed;
            Status=status;Scope=scope;Summary=summary;Output=output ?? summary;FinishedUtc=DateTime.UtcNow;
            if ((status == FridaResultStatus.Verified || status == FridaResultStatus.Partial) && (Tool == "manage_files" || Tool == "read_code" || Tool == "inspect_project" || Tool == "apply_patch"))
                FridaIdeRuntime.RecordArtifact(Target, Operation);
            FridaIdeRuntime.RecordExecution(this);
            FridaIdeRuntime.Publish(Tool + " · " + Operation + " · " + Target + " · " + Status + "\r\n" + Summary);
            return this;
        }
        internal FridaToolReceipt Fail(string code,string message)
        {ErrorCode=code;return Finish(FridaResultStatus.Failed,"none",message,message);}
        internal Dictionary<string,object> Export(bool includeOutput)
        {
            var result=new Dictionary<string,object> {
                {"schema_version",SchemaVersion},{"request_id",RequestId},{"operation_id",OperationId},
                {"step_id",StepId ?? ""},{"attempt",Attempt>0?Attempt:1},{"project_revision",ProjectRevision ?? "rev-0"},
                {"tool",Tool},{"operation",Operation},{"target",Target},{"reference",Reference},
                {"parameters_sha256",ParametersSha256},{"started_utc",StartedUtc.ToString("o")},{"finished_utc",FinishedUtc.ToString("o")},
                {"status",Status.ToString()},{"verified_scope",Scope},{"is_action",IsAction},{"summary",Summary},
                {"checks",_checks.Select(x=>new {name=x.Name,passed=x.Passed,expected=x.Expected,observed=x.Observed,verifier=x.Verifier}).ToArray()},
                {"validation_evidence_refs",_validationEvidenceRefs.ToArray()},
                {"error_code",ErrorCode},{"superseded_by",SupersededBy}
            };
            // Output/data can contain arbitrary source text. Kept separate from the trusted status.
            if(includeOutput){result["untrusted_output"]=Output;result["data"]=_data;}
            else result["data"]=_data.Where(x=>x.Key!="stdout" && x.Key!="stderr" && x.Key!="text" && x.Key!="sources").ToDictionary(x=>x.Key,x=>x.Value);
            return result;
        }
        public string ToJson() { return new JavaScriptSerializer {MaxJsonLength=8*1024*1024}.Serialize(Export(true)); }
    }

    public sealed class FridaExecutionLedger
    {
        private readonly List<FridaToolReceipt> _receipts=new List<FridaToolReceipt>();
        public string RequestId {get;private set;}
        public DateTime StartedUtc {get;private set;}
        public DateTime PublishedUtc {get;private set;}
        public string PersistenceError {get;private set;}
        public string StopCode {get;private set;}
        public string StopMessage {get;private set;}
        public int RevisionCounter {get;private set;}
        public string CurrentProjectRevision {get;private set;}
        public FridaDevelopmentTaskState DevTaskState {get;internal set;}
        public IList<FridaToolReceipt> Receipts {get{return _receipts.AsReadOnly();}}
        private FridaRequestIntent _intent;
        internal void SetIntent(FridaRequestIntent intent){_intent=intent;}

        public bool FileSaved
        {
            get
            {
                return _receipts.Any(r=>r.SupersededBy==null && r.IsAction && (r.Tool=="manage_files" || r.Tool=="apply_patch")
                    && (r.Operation=="write" || r.Operation=="append" || r.Operation=="patch") && r.Status==FridaResultStatus.Verified);
            }
        }
        public bool ProcessCompleted
        {
            get
            {
                return _receipts.Any(r=>r.SupersededBy==null && (r.Status==FridaResultStatus.ProcessCompleted
                    || r.Scope=="process_exit_only" || r.Scope=="process_completed"
                    || r.Scope=="build_passed" || r.Scope=="tests_passed" || r.Scope=="tests_passed_with_compile"));
            }
        }
        public bool CompilationApproved
        {
            get
            {
                return _receipts.Any(r=>r.SupersededBy==null && r.Status==FridaResultStatus.Verified
                    && (r.Scope=="build_passed" || r.Scope=="tests_passed_with_compile")
                    && string.Equals(r.ProjectRevision ?? "rev-0",CurrentProjectRevision ?? "rev-0",StringComparison.OrdinalIgnoreCase));
            }
        }
        public bool TestsApproved
        {
            get
            {
                return _receipts.Any(r=>r.SupersededBy==null && r.Status==FridaResultStatus.Verified
                    && (r.Scope=="tests_passed" || r.Scope=="tests_passed_with_compile")
                    && string.Equals(r.ProjectRevision ?? "rev-0",CurrentProjectRevision ?? "rev-0",StringComparison.OrdinalIgnoreCase));
            }
        }
        public bool RequestFulfilled { get { return TaskCompletionVerified; } }

        public bool TaskCompletionVerified
        {
            get
            {
                bool baseVerified = _intent!=null && _intent.RequiresTools
                    && !_intent.Operations.Any(op=>op.Kind=="review" || op.Kind=="research" || op.Kind=="inspect")
                    && StopCode==null && _intent.Missing(_receipts).Count==0
                    && _receipts.Any()
                    && _receipts.Where(r=>r.SupersededBy==null).All(r=>r.Status==FridaResultStatus.Verified && r.Checks.Count>0 && r.Checks.All(c=>c.Passed));
                if(!baseVerified)return false;
                if(_intent.IsSoftwareDevelopment)
                {
                    if(!FileSaved || !CompilationApproved || !TestsApproved)return false;
                    if(DevTaskState!=null && DevTaskState.HasUnresolvedReviewFindings)return false;
                }
                return true;
            }
        }
        public FridaExecutionLedger(){RequestId=Guid.NewGuid().ToString("N");StartedUtc=DateTime.UtcNow;RevisionCounter=0;CurrentProjectRevision="rev-0";}
        internal void Stop(string code,string message){StopCode=code;StopMessage=message;}
        internal bool Add(FridaToolReceipt receipt)
        {
            if(receipt==null)throw new ArgumentNullException("receipt");
            if(receipt.RequestId!=null && receipt.RequestId!=RequestId)throw new InvalidOperationException("Resultado pertence a outro atendimento.");
            if(_receipts.Any(x=>x.OperationId==receipt.OperationId))return false;

            int priorSame = _receipts.Count(x=>string.Equals(x.Tool,receipt.Tool,StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Operation,receipt.Operation,StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(receipt.Target) || string.Equals(x.Target,receipt.Target,StringComparison.OrdinalIgnoreCase)));
            receipt.Attempt = priorSame + 1;
            receipt.StepId = (receipt.Tool ?? "op") + ":" + (receipt.Operation ?? "run") + "#" + receipt.Attempt;

            bool isCodeModification = receipt.IsAction
                && (receipt.Tool=="manage_files" || receipt.Tool=="apply_patch")
                && (receipt.Operation=="write" || receipt.Operation=="append" || receipt.Operation=="patch")
                && receipt.Status==FridaResultStatus.Verified;

            if(isCodeModification)
            {
                RevisionCounter++;
                string shortHash = !string.IsNullOrEmpty(receipt.MonitorHash) && receipt.MonitorHash.Length>=8
                    ? receipt.MonitorHash.Substring(0,8)
                    : RevisionCounter.ToString();
                CurrentProjectRevision = "rev-" + RevisionCounter + "-" + shortHash;
                receipt.ProjectRevision = CurrentProjectRevision;

                // Alterar o codigo invalida qualquer validacao de compilacao/teste anterior
                foreach(var prev in _receipts)
                {
                    if(prev.SupersededBy!=null)continue;
                    bool isPriorValidation = prev.Tool=="run_project_validation"
                        || prev.Scope=="build_passed" || prev.Scope=="tests_passed" || prev.Scope=="tests_passed_with_compile"
                        || prev.Scope=="build_failed" || prev.Scope=="tests_failed"
                        || (prev.Tool=="run_powershell" && FridaCodingTools.IsValidationCommand(prev.Target));
                    if(isPriorValidation)
                    {
                        prev.SupersededBy = receipt.OperationId;
                    }
                    else if(prev.Status==FridaResultStatus.Failed && prev.IsAction
                        && (prev.Tool=="manage_files" || prev.Tool=="apply_patch")
                        && string.Equals(prev.Target,receipt.Target,StringComparison.OrdinalIgnoreCase))
                    {
                        prev.SupersededBy = receipt.OperationId;
                        receipt.AddValidationEvidenceRef(prev.OperationId);
                    }
                }
            }
            else
            {
                receipt.ProjectRevision = CurrentProjectRevision;
                bool isValidationPass = receipt.Status==FridaResultStatus.Verified
                    && (receipt.Scope=="build_passed" || receipt.Scope=="tests_passed" || receipt.Scope=="tests_passed_with_compile");

                if(receipt.Status==FridaResultStatus.Verified || receipt.Status==FridaResultStatus.ProcessCompleted)
                {
                    foreach(var prev in _receipts)
                    {
                        if(prev.SupersededBy!=null)continue;
                        if(prev.Status==FridaResultStatus.Failed)
                        {
                            bool sameOp = string.Equals(prev.Tool,receipt.Tool,StringComparison.OrdinalIgnoreCase)
                                && string.Equals(prev.Operation,receipt.Operation,StringComparison.OrdinalIgnoreCase);
                            bool resolvesFailedValidation = isValidationPass
                                && (prev.Tool=="run_project_validation" || prev.Scope=="build_failed" || prev.Scope=="tests_failed" || prev.Tool=="run_powershell");
                            if(sameOp || resolvesFailedValidation)
                            {
                                prev.SupersededBy = receipt.OperationId;
                                receipt.AddValidationEvidenceRef(prev.OperationId);
                            }
                        }
                    }
                }

                if(isValidationPass)
                {
                    foreach(var mod in _receipts.Where(r=>r.SupersededBy==null && r.IsAction && (r.Tool=="manage_files" || r.Tool=="apply_patch") && r.Status==FridaResultStatus.Verified))
                    {
                        receipt.AddValidationEvidenceRef(mod.OperationId);
                    }
                }
            }

            receipt.RequestId=RequestId;_receipts.Add(receipt);return true;
        }
        public void Revalidate()
        {
            var monitored = _receipts.Where(receipt => receipt.MonitorKind != null && receipt.SupersededBy == null).ToList();
            for (int index = 0; index < monitored.Count; index++)
            {
                var current = monitored[index];
                if (!current.IsAction) continue;
                foreach (var previous in monitored.Take(index).Where(previous => previous.SupersededBy == null && string.Equals(previous.Target, current.Target, StringComparison.OrdinalIgnoreCase)))
                    previous.SupersededBy = current.OperationId;
            }
            foreach(var item in monitored.Where(receipt => receipt.SupersededBy == null))
            {
                try
                {
                    bool valid=item.MonitorKind=="file" ? File.Exists(item.Target) && FridaVerifiedTools.FileSha256(item.Target)==item.MonitorHash
                        : item.MonitorKind=="absent" ? !File.Exists(item.Target) && !Directory.Exists(item.Target)
                        : Directory.Exists(item.Target);
                    item.Check("state_at_publication",valid,"Estado final igual ao verificado",valid?"Igual":"Estado alterado","filesystem_recheck");
                    if(!valid)item.Finish(FridaResultStatus.Unconfirmed,item.Scope,"O estado do alvo mudou antes da publicacao; resultado atual nao confirmado.",item.Output);
                }
                catch(Exception ex)
                {
                    item.Check("state_at_publication",false,"Alvo acessivel",ex.GetType().Name,"filesystem_recheck");
                    item.Finish(FridaResultStatus.Unconfirmed,item.Scope,"Nao foi possivel reconferir o alvo antes da publicacao.",item.Output);
                }
            }
            PublishedUtc=DateTime.UtcNow;
        }
        public string Summary(bool normative)
        {
            var lines=new List<string>();
            if(StopMessage!=null)lines.Add("Atendimento interrompido: "+StopMessage);
            foreach(var receipt in _receipts.Take(16))
            {
                string status=receipt.Status==FridaResultStatus.Verified?"[OK]":receipt.Status==FridaResultStatus.ProcessCompleted?"[CONCLUIDO]":receipt.Status==FridaResultStatus.Submitted?"[ENVIADO]":receipt.Status==FridaResultStatus.Failed?"[FALHA]":receipt.Status==FridaResultStatus.Partial?"[PARCIAL]":receipt.Status==FridaResultStatus.ModelAnalysis?"[INFO]":"[PENDENTE]";
                lines.Add(status+" "+receipt.Summary+(receipt.SupersededBy==null?"":" (substituido)"));
                if(!string.IsNullOrWhiteSpace(receipt.Target))lines.Add("  Alvo: "+receipt.Target);
            }
            if(PersistenceError!=null)lines.Add("Registro salvo em memoria.");
            return string.Join("\n",lines.ToArray());
        }
        public string SpokenSummary()
        {
            if(StopMessage!=null)return StopMessage;
            if(_receipts.Count==0)return "";
            if(_receipts.Count==1)return _receipts[0].Summary;
            var okItems=_receipts.Where(x=>x.Status==FridaResultStatus.Verified && x.SupersededBy==null).Select(x=>x.Summary).Take(2).ToArray();
            if(okItems.Length>0)return string.Join(". ", okItems)+".";
            return _receipts.Last().Summary;
        }
        public string ToJson(bool includeOutput)
        {
            return new JavaScriptSerializer {MaxJsonLength=16*1024*1024}.Serialize(new {
                schema_version=1,request_id=RequestId,started_utc=StartedUtc.ToString("o"),published_utc=PublishedUtc.ToString("o"),
                project_revision=CurrentProjectRevision,
                deliverable_states=new {
                    file_saved=FileSaved,
                    process_completed=ProcessCompleted,
                    build_passed=CompilationApproved,
                    tests_passed=TestsApproved,
                    request_fulfilled=TaskCompletionVerified
                },
                task_completion_verified=TaskCompletionVerified,completion_scope=TaskCompletionVerified?"identified_request_steps":"individual_checks_only",persistence_error=PersistenceError,
                stop_code=StopCode,stop_message=StopMessage,
                operations=_receipts.Select(x=>x.Export(includeOutput)).ToArray()
            });
        }
        public void Save(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string path=Path.Combine(directory,StartedUtc.ToString("yyyyMMdd-HHmmss-fff")+"-"+RequestId+".json");
                string temporary=path+".tmp";
                try
                {
                    PersistenceError=null;
                    string json=ToJson(false);File.WriteAllText(temporary,json,new UTF8Encoding(false));
                    if(File.ReadAllText(temporary,Encoding.UTF8)!=json)throw new IOException("Verificacao do registro falhou.");
                    if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
                }
                finally {if(File.Exists(temporary))File.Delete(temporary);}
            }
            catch(Exception ex){PersistenceError=ex.GetType().Name;}
        }
    }
}
