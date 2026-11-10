using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaMeetingArtifact
    {
        public string Id, ConversationId, RequestId, Title, Kind, Markdown, Author, CreatedUtc, SkillId, SkillState;
        // Structured content behind the Markdown (the decision report as JSON); null for other kinds.
        public string Data;
        public int Version;
        public override string ToString() { return Title + " · v" + Version + (SkillState == null ? "" : " · " + SkillState); }
    }
    public sealed partial class FridaConversationStore
    {
        public FridaMeetingArtifact SaveArtifact(string conversationId, string requestId, string title, string kind, string markdown, string author, string skillId = null, string skillState = null, string data = null)
        {
            lock (Gate)
            {
                Find(Read(), conversationId, false);
                string id = StableId(conversationId + ":" + kind + ":" + title);
                var previous = Artifacts(conversationId).Where(a => a.Id == id).OrderByDescending(a => a.Version).FirstOrDefault();
                markdown = FridaIdeRuntime.Redact(markdown);
                if (data != null) data = FridaIdeRuntime.Redact(data);
                if (previous != null && previous.Markdown == markdown && previous.SkillState == skillState && previous.Data == data) return previous;
                var artifact = new FridaMeetingArtifact { Id = id, ConversationId = conversationId, RequestId = requestId, Title = title, Kind = kind,
                    Markdown = markdown, Author = author, CreatedUtc = Now(), Version = previous == null ? 1 : previous.Version + 1, SkillId = skillId, SkillState = skillState, Data = data };
                string directory = Path.Combine(_root, "Conversations", conversationId, "Artifacts"); Directory.CreateDirectory(directory);
                AtomicWrite(Path.Combine(directory, id + "-" + artifact.Version.ToString("D6") + ".json"), Json().Serialize(artifact));
                return artifact;
            }
        }
        public List<FridaMeetingArtifact> Artifacts(string id)
        {
            lock (Gate)
            {
                Find(Read(), id, false); string directory = Path.Combine(_root, "Conversations", id, "Artifacts");
                if (!Directory.Exists(directory)) return new List<FridaMeetingArtifact>();
                return Directory.EnumerateFiles(directory, "*.json").Select(p => Json().Deserialize<FridaMeetingArtifact>(File.ReadAllText(p, Encoding.UTF8)))
                    .Where(a => a != null).OrderByDescending(a => a.CreatedUtc).ToList();
            }
        }
    }
    public static class FridaMeetingWorkspace
    {
        public static event Action<string> Changed;
        public static FridaConversationStore Store(FridaIdeContext context) { return context.Store ?? new FridaConversationStore(Path.Combine(FridaIdeRuntime.DataRoot, "Workspace")); }
        public static void Notify(string id)
        {
            var callback = Changed; if (callback == null) return;
            foreach (Action<string> observer in callback.GetInvocationList()) try { observer(id); } catch (Exception ex) { BootLogger.Log("Evento de reunião: " + ex.GetType().Name); }
        }
        public static void Message(TeamMessage message)
        {
            var context = FridaIdeRuntime.Context; if (context == null || context.ConversationId == null) return;
            Store(context).PutMessage(new FridaConversationMessage { Id = message.Id, ConversationId = context.ConversationId, RequestId = context.RequestId,
                Author = message.Title, Role = message.Role.ToString(), Model = message.Model, Text = message.Text, AtUtc = message.AtUtc.ToString("o"),
                Channel = "meeting", Status = message.Success ? "complete" : "unavailable" });
            Notify(context.ConversationId);
        }
        private static string MarkdownValue(object value)
        {
            if (value == null) return "";
            if (value is string) return (string)value;
            var dictionary = value as Dictionary<string, object>;
            if (dictionary != null) return string.Join("\n", dictionary.Select(p => "- **" + p.Key + "**: " + MarkdownValue(p.Value)));
            var list = value as IEnumerable;
            return list == null ? Convert.ToString(value) : string.Join("\n", list.Cast<object>().Select(v => "- " + MarkdownValue(v)));
        }
        public static void Plan(string response)
        {
            var context = FridaIdeRuntime.Context; if (context == null || context.ConversationId == null || string.IsNullOrWhiteSpace(response)) return;
            Dictionary<string, object> parsed = null;
            try { int start = response.IndexOf('{'), end = response.LastIndexOf('}'); if (start >= 0 && end > start) parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(response.Substring(start, end - start + 1)); } catch (ArgumentException) { }
            var md = new StringBuilder("# Plano da reunião\n\n");
            object value;
            if (parsed != null && parsed.TryGetValue("plan", out value))
            {
                md.AppendLine(MarkdownValue(value));
                foreach (string key in new[] { "missing_requirements", "delegated_roles", "acceptance_criteria", "research_queries" })
                    if (parsed.TryGetValue(key, out value)) md.AppendLine("\n## " + key + "\n\n" + MarkdownValue(value));
                if (parsed.TryGetValue("workflow", out value) && !string.IsNullOrWhiteSpace(MarkdownValue(value)))
                    Store(context).SaveArtifact(context.ConversationId, context.RequestId, "Fluxo", "flow", "# Fluxo\n\n" + MarkdownValue(value), "Gestor");
                if (parsed.TryGetValue("proposed_skills", out value)) ProposeSkills(context, value);
            }
            else md.AppendLine(response);
            Store(context).SaveArtifact(context.ConversationId, context.RequestId, "Plano", "plan", md.ToString(), "Gestor"); Notify(context.ConversationId);
        }
        // A skill named in the debate is only a proposal; it never overrides a saved or verified version.
        private static void ProposeSkills(FridaIdeContext context, object value)
        {
            var items = value as IEnumerable; if (items == null || value is string) return;
            var store = Store(context);
            foreach (object item in items.Cast<object>().Take(10))
            {
                var fields = item as Dictionary<string, object>;
                string name = fields == null ? Convert.ToString(item) : fields.ContainsKey("name") ? Convert.ToString(fields["name"]) : null;
                string purpose = fields != null && fields.ContainsKey("purpose") ? Convert.ToString(fields["purpose"]) : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                name = name.Trim(); if (name.Length > 80) name = name.Substring(0, 80);
                string id = FridaConversationStore.StableId(context.ConversationId + ":skill:" + name);
                var latest = store.Artifacts(context.ConversationId).Where(a => a.Id == id).OrderByDescending(a => a.Version).FirstOrDefault();
                if (latest != null && latest.SkillState != "proposta") continue;
                store.SaveArtifact(context.ConversationId, context.RequestId, name, "skill", "# " + name + "\n\n" + purpose +
                    "\n\n_Proposta do Gestor nesta reunião. Ainda não registrada no catálogo de skills da Frida._", "Gestor", null, "proposta");
            }
        }
        public static void Report(string text)
        {
            var c = FridaIdeRuntime.Context; if (c == null || c.ConversationId == null || string.IsNullOrWhiteSpace(text)) return;
            Store(c).SaveArtifact(c.ConversationId, c.RequestId, "Conclusões", "report", "# Conclusões\n\n" + text, "Gestor"); Notify(c.ConversationId);
        }
        // The secretary's final decision: a versioned "Decisão final" artifact (Markdown + JSON) and a
        // card in the conversation pointing to that exact version ("<artifact id>:<version>").
        public static FridaMeetingArtifact Decision(FridaTeamDecisionReport report)
        {
            var c = FridaIdeRuntime.Context; if (c == null || c.ConversationId == null || report == null) return null;
            var store = Store(c);
            var artifact = store.SaveArtifact(c.ConversationId, c.RequestId, "Decisão final", "decision", report.ToMarkdown(), "Secretaria Frida", null, null,
                new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(report));
            var secretary = FridaTeamState.Member(TeamRole.Secretary);
            store.PutMessage(new FridaConversationMessage { ConversationId = c.ConversationId, RequestId = c.RequestId, Author = secretary.Name, Role = "decision", Model = secretary.Model,
                Text = report.CardText(), Channel = "meeting", ArtifactId = artifact.Id + ":" + artifact.Version });
            Notify(c.ConversationId);
            return artifact;
        }
        public static FridaTeamDecisionReport LoadDecision(FridaConversationStore store, string conversationId, string artifactRef)
        {
            if (store == null || string.IsNullOrEmpty(artifactRef)) return null;
            string[] parts = artifactRef.Split(':'); int version;
            var artifact = store.Artifacts(conversationId).Where(a => a.Kind == "decision" && a.Id == parts[0] && (parts.Length < 2 || !int.TryParse(parts[1], out version) || a.Version == version))
                .OrderByDescending(a => a.Version).FirstOrDefault();
            return DecisionOf(artifact);
        }
        public static FridaTeamDecisionReport DecisionOf(FridaMeetingArtifact artifact)
        {
            if (artifact == null || string.IsNullOrEmpty(artifact.Data)) return null;
            try { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Deserialize<FridaTeamDecisionReport>(artifact.Data); }
            catch (ArgumentException) { return null; } catch (InvalidOperationException) { return null; }
        }
        public static void Skill(FridaSkillEntry skill)
        {
            var c = FridaIdeRuntime.Context; if (c == null || c.ConversationId == null || skill == null) return;
            string path = Path.Combine(skill.SkillDirectory, "SKILL.md"); if (!File.Exists(path)) return;
            Store(c).SaveArtifact(c.ConversationId, c.RequestId, skill.Name, "skill", File.ReadAllText(path, Encoding.UTF8).Replace(FridaSkillRegistry.GeneratedMarker, "").TrimEnd(), "Equipe", skill.Id, skill.Verified ? "verificada" : "salva"); Notify(c.ConversationId);
        }
    }
}
