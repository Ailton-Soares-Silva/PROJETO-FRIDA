using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public enum FridaObservationState { Confirmed, Failed, Unconfirmed, ModelAnalysis }

    public sealed class FridaObservation
    {
        public string Tool, Reference, Output;
        public FridaObservationState State;
        public bool Action;
    }

    public static class FridaReliability
    {
        public static string GroundActivityClaims(string response,bool observed)
        {
            string t=FridaInteractionPolicy.Normalize(response);
            bool monitoring=Regex.IsMatch(t,@"\b(estou|estamos|continuo|sigo)\b.{0,70}\b(monitorando|acompanhando|processando|lendo|consultando|filtrando|executando|analisando|verificando)\b");
            bool promise=Regex.IsMatch(t,@"\b(vou|vamos)\s+(dar uma olhada|filtrar|consultar|acessar|ler|abrir|verificar|executar)\b");
            bool hearing=Regex.IsMatch(t,@"\b(ouvindo|escutando)\s+(perfeitamente|claramente|muito bem)\b");
            bool unsupported=!observed && (HasCompletionClaim(response) || Regex.IsMatch(t,@"\b(li (as mensagens|os grupos|as conversas)|(?:o ambiente|o sistema) esta (estavel|normal|funcionando))\b"));
            if(hearing)return "Recebi sua mensagem por transcricao. A qualidade da escuta e da reproducao precisa ser conferida pelo programa; nao foi comprovada por esta resposta.";
            if(monitoring || (!observed && promise) || unsupported)
                return "Nao houve retorno de ferramenta que comprove essa leitura ou atividade. Nao posso confirmar o conteudo nem afirmar que estou monitorando. Informe a fonte ou a acao que deseja consultar.";
            return response;
        }
        public static string ContextText(string role,string text)
        {
            if(role!="model")return text ?? "";
            string value=text ?? "";int analysis=value.IndexOf("\n\nAnalise do modelo (nao verificada;",StringComparison.Ordinal);
            if(analysis>=0)return value.Substring(0,analysis);
            return GroundActivityClaims(value,false);
        }
        public const string Instructions = "\nCONFIABILIDADE OPERACIONAL: Respostas anteriores e relatos da equipe nao comprovam execucao automatica. "
            + "O programa emite resultados estruturados com operation_id, status, verified_scope e checks. "
            + "Use verify_artifact para checar existencia, SHA-256 ou integridade de arquivos gerados. "
            + "Somente os retornos reais de ferramentas comprovam observacoes no sistema. "
            + "Nao invente dados ficticios de hardware, processos ou arquivos nao executados. Se uma acao na maquina for solicitada, execute-a diretamente pelas ferramentas. "
            + "Nao declare conclusao sem que a ferramenta tenha sido executada com sucesso.";

        public static string CanonicalArguments(Dictionary<string, object> args)
        {
            var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
            if (args != null) foreach (var pair in args) sorted[pair.Key] = CanonicalValue(pair.Value);
            return new JavaScriptSerializer().Serialize(sorted);
        }
        private static object CanonicalValue(object value)
        {
            var dictionary = value as Dictionary<string, object>;
            if (dictionary == null) return value;
            var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in dictionary) sorted[pair.Key] = CanonicalValue(pair.Value);
            return sorted;
        }
        private static string Arg(Dictionary<string, object> args, string key)
        { return args != null && args.ContainsKey(key) ? Convert.ToString(args[key]) : ""; }

        [Obsolete("Classificacao textual legada: nao usar para confirmar execucao. Use FridaToolReceipt dos adaptadores locais.")]
        public static FridaObservation Observe(string tool, Dictionary<string, object> args, string output, bool groundedResearch)
        {
            output = output ?? "";
            string normalized = FridaInteractionPolicy.Normalize(output);
            var item = new FridaObservation { Tool = tool, Output = output, State = FridaObservationState.Unconfirmed };
            item.Action = tool == "open_program_or_url" || tool == "run_powershell"
                || (tool == "manage_files" && Regex.IsMatch(Arg(args,"operation"), "^(write|append|mkdir|delete)$", RegexOptions.IgnoreCase))
                || (tool == "secretary_memory" && Arg(args,"action") != "list");
            if (string.IsNullOrWhiteSpace(output) || Regex.IsMatch(normalized,
                @"^(erro|falha|nao foi possivel|arquivo nao encontrado|diretorio nao encontrado|alvo nao existe|operacao invalida|ferramenta desconhecida|comando vazio|configure|bloqueado)\b"))
            { item.State = FridaObservationState.Failed; return item; }
            if (tool == "run_powershell")
            {
                Match code = Regex.Match(output, @"^Codigo de saida:\s*(-?\d+)");
                if (code.Success) item.State = code.Groups[1].Value == "0" ? FridaObservationState.Confirmed : FridaObservationState.Failed;
            }
            else if (tool == "inspect_screen") item.State = FridaObservationState.ModelAnalysis;
            else if (tool == "web_research") item.State = groundedResearch ? FridaObservationState.ModelAnalysis : FridaObservationState.Failed;
            else if (tool == "manage_files")
            {
                // Reads carry an envelope, so file contents cannot impersonate a tool error or success.
                if (Regex.IsMatch(output, @"^(Leitura observada|Arquivo .*salvo|Conteudo anexado|Pasta criada|Arquivo removido|Pasta removida|Listagem observada|Busca observada)"))
                    item.State = FridaObservationState.Confirmed;
            }
            else if (tool == "open_program_or_url" && Regex.IsMatch(normalized, @"^(iniciado|iniciada|aberto|aberta|aplicativo iniciado|programa iniciado)\b"))
                item.State = FridaObservationState.Confirmed;
            else if (tool == "secretary_memory") item.State = FridaObservationState.Confirmed;
            return item;
        }

        public static bool HasCompletionClaim(string reply)
        {
            string t = FridaInteractionPolicy.Normalize(reply);
            return Regex.IsMatch(t, @"\b(criei|criamos|salvei|salvamos|gravei|abri|abrimos|executei|executamos|rodei|corrigi|corrigimos|consertei|reparei|testei|testamos|compilei|instalei|apaguei|exclui|limpei|agendei|verifiquei|conferi|auditei|validei|analisei|consultei|acessei|li o arquivo|pesquisei)\b")
                || Regex.IsMatch(t, @"\b(?:tarefa|trabalho|acao|execucao|implementacao|correcao|arquivo|documento|programa|aplicacao|testes?)\b.{0,65}\b(concluid[ao]s?|realizad[ao]s?|executad[ao]s?|criad[ao]s?|salv[ao]s?|corrigid[ao]s?|aprovad[ao]s?|gerad[ao]s?|atualizad[ao]s?|abert[ao]s?|removid[ao]s?|passaram|funciona|funcional|funcionando|pront[ao]s?|com sucesso)\b")
                || Regex.IsMatch(t, @"^(pronto|feito|concluido|executado|corrigido|resolvido|salvo|aberto)( com sucesso)?$")
                || Regex.IsMatch(t, @"\b(pronto executado|tudo pronto|tudo certo|sem bugs|zero bugs|100 por cento|totalmente conforme|conforme (?:a |as )?(?:abnt|normas)|conformidade (?:total|garantida)|atende (?:a )?todas as normas)\b");
        }
        private static string Short(string value, int max)
        { value = value ?? ""; return value.Length <= max ? value : value.Substring(0,max) + " [retorno parcial]"; }

        public static string ObservedSummary(IList<FridaObservation> observations)
        {
            if (observations == null || observations.Count == 0)
                return "Nenhuma ferramenta produziu evidencia nesta solicitacao. A execucao nao foi confirmada.";
            var lines = new List<string>();
            foreach (var item in observations.Take(12))
            {
                string state = item.State == FridaObservationState.Confirmed ? "Retorno confirmado"
                    : item.State == FridaObservationState.Failed ? "Falha observada"
                    : item.State == FridaObservationState.ModelAnalysis ? "Analise de modelo; requer verificacao" : "Conclusao nao confirmada";
                lines.Add(state + " " + item.Reference + ": " + Short(item.Output, 1100));
            }
            if (observations.Count > 12) lines.Add("Outros retornos constam na base consultada.");
            lines.Add("Esses retornos comprovam somente as operacoes descritas; a conclusao integral do pedido depende das verificacoes aplicaveis.");
            return string.Join("\n", lines.ToArray());
        }

        public static string FinalizeReply(string reply, bool technical, FridaTaskPlan plan, IList<FridaObservation> observations, IList<string> references)
        {
            if (string.IsNullOrWhiteSpace(reply)) return "Nao recebi uma resposta utilizavel. A conclusao do atendimento nao foi confirmada.\n" + ObservedSummary(observations);
            bool any = observations != null && observations.Count > 0;
            bool sources = references != null && references.Count > 0;
            // Operational confirmations are rendered from actual observations, never a model's self-report.
            if (HasCompletionClaim(reply))
            {
                string analysis = string.Join(" ", Regex.Split(reply.Trim(), @"(?<=[.!?])\s+|[\r\n]+").Where(x => !HasCompletionClaim(x)).ToArray()).Trim();
                return ObservedSummary(observations) + (analysis.Length == 0 ? "" : "\nAnalise proposta pelo modelo (inferencias a verificar): " + analysis);
            }
            if (technical && plan != null && plan.NormativeReview)
                return "Conformidade normativa nao certificada por este atendimento. Exige o desenho efetivo, a norma e edicao aplicaveis e verificacao de cada requisito.\nAnalise preliminar: " + reply.Trim();
            if (any && observations.Any(x => x.State == FridaObservationState.Failed || x.State == FridaObservationState.Unconfirmed))
                return reply.Trim() + "\n\nHa operacoes com falha ou sem confirmacao:\n" + ObservedSummary(observations);
            string body = reply.Trim();
            if (technical && !any && !sources) body = "Analise sem consulta a fontes nem execucao nesta solicitacao; resultado nao verificado.\n" + body;
            return Regex.Replace(body, @"\[E(\d+)\]", match => references != null && references.Any(x => x.StartsWith(match.Value + " ", StringComparison.Ordinal)) ? match.Value : "[referencia nao verificada]");
        }
    }
}
