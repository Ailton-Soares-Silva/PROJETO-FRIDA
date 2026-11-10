using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FridaAgent
{
    public sealed class FridaMigrationResult
    {
        public int Reports, Meetings, Projects;
        public List<string> Warnings = new List<string>();
    }

    public sealed partial class FridaConversationStore
    {
        internal static string StableId(string key)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant().Substring(0, 32);
        }
        private static string Timestamp(string value, string fallback)
        {
            DateTime parsed;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed) ? parsed.ToUniversalTime().ToString("o") : fallback;
        }
        private string ImportProject(string name, string root, string destination)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            string alias = string.IsNullOrEmpty(destination) || destination == "Computador" ? null : destination;
            string canonical = alias == null ? Path.GetFullPath(root) : root;
            var existing = Read().Projects.FirstOrDefault(p => (p.SshAlias ?? "") == (alias ?? "") && string.Equals(p.Root.TrimEnd('\\', '/'), canonical.TrimEnd('\\', '/'), alias == null ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            return existing != null ? existing.Id : RegisterProject(name ?? Path.GetFileName(root), root, alias).Id;
        }
        private FridaConversation EnsureImported(string key, string title, string at, string projectId, FridaConversationKind kind, FridaWorkspaceSection section)
        {
            var state = Read(); string id;
            if (state.Imported.TryGetValue(key, out id)) return Find(state, id, true);
            id = StableId("conversation:" + key);
            var item = state.Conversations.FirstOrDefault(c => c.Id == id);
            if (item == null)
            {
                string clean = (title ?? "Conversa importada").Replace('\r', ' ').Replace('\n', ' ').Trim();
                item = new FridaConversation { Id = id, Title = clean.Length > 90 ? clean.Substring(0, 90) + "…" : clean, CreatedUtc = at,
                    UpdatedUtc = at, ProjectId = projectId, Kind = kind, Section = section };
                state.Conversations.Add(item);
            }
            // Commit identity before messages; a retry uses the same conversation and message IDs.
            state.Imported[key] = id; Write(state); return item;
        }
        public string RecordReport(FridaConversationEntry report)
        {
            if (report == null || string.IsNullOrWhiteSpace(report.request_id)) throw new InvalidDataException("Atendimento sem identificador.");
            lock (Gate)
            {
                string at = Timestamp(report.started_utc, Now());
                FridaConversation c;
                if (!string.IsNullOrEmpty(report.conversation_id)) c = Get(report.conversation_id, true);
                else c = EnsureImported("request:" + report.request_id, report.request, at,
                    ImportProject(report.project_name, report.project_root, report.destination), FridaConversationKind.Conversation,
                    FridaRequestIntent.Classify(report.request).IsSoftwareDevelopment ? FridaWorkspaceSection.Code : FridaWorkspaceSection.Chat);
                var catalog = Read(); string mapped;
                if (!catalog.Imported.TryGetValue("request:" + report.request_id, out mapped) || mapped != c.Id) { catalog.Imported["request:" + report.request_id] = c.Id; Write(catalog); }
                if (c.DeletedUtc != null) return c.Id;
                PutMessage(new FridaConversationMessage { Id = StableId(report.request_id + ":user"), ConversationId = c.Id, RequestId = report.request_id,
                    Author = "Você", Role = "user", Text = report.request, Channel = report.input_channel, AtUtc = at });
                if (!string.IsNullOrEmpty(report.response))
                    PutMessage(new FridaConversationMessage { Id = StableId(report.request_id + ":assistant"), ConversationId = c.Id, RequestId = report.request_id,
                        Author = "Frida", Role = "assistant", Text = report.response, Channel = report.input_channel, Model = report.routing_model,
                        AtUtc = Timestamp(report.finished_utc, DateTime.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddTicks(1).ToString("o")), Status = report.status });
                return c.Id;
            }
        }
        public string RecordMeeting(FridaIdeMeeting meeting)
        {
            if (meeting == null || string.IsNullOrEmpty(meeting.Id)) throw new InvalidDataException("Reunião sem identificador.");
            lock (Gate)
            {
                string at = Timestamp(meeting.StartedUtc, Now());
                string key = string.IsNullOrEmpty(meeting.RequestId) ? "meeting:" + meeting.Id : "request:" + meeting.RequestId;
                string projectId = meeting.Project == null ? null : ImportProject(meeting.Project.Name, meeting.Project.Root, meeting.Project.SshAlias);
                var c = !string.IsNullOrEmpty(meeting.ConversationId) ? Get(meeting.ConversationId, true) : EnsureImported(key, meeting.Topic, at, projectId,
                    FridaConversationKind.Meeting, FridaRequestIntent.Classify(meeting.Topic).IsSoftwareDevelopment ? FridaWorkspaceSection.Code : FridaWorkspaceSection.Chat);
                if (c.DeletedUtc != null) return c.Id;
                var s = Read(); var stored = Find(s, c.Id, false); stored.Kind = FridaConversationKind.Meeting;
                if (stored.ProjectId == null) stored.ProjectId = projectId;
                s.Imported["meeting:" + meeting.Id] = c.Id; Write(s);
                int index = 0;
                foreach (var message in meeting.Messages ?? new List<TeamMessage>())
                {
                    int position = index++; if (message == null) continue;
                    PutMessage(new FridaConversationMessage { Id = message.Id ?? StableId(meeting.Id + ":message:" + position), ConversationId = c.Id, RequestId = meeting.RequestId,
                        Author = message.Title, Role = message.Role.ToString(), Model = message.Model, Text = message.Text,
                        AtUtc = message.AtUtc == default(DateTime) ? DateTime.Parse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).AddTicks(position + 1).ToString("o") : message.AtUtc.ToUniversalTime().ToString("o"),
                        Status = message.Success ? "complete" : "unavailable", Channel = "meeting" });
                }
                if (!string.IsNullOrEmpty(meeting.Report) && string.IsNullOrEmpty(meeting.ConversationId))
                    PutMessage(new FridaConversationMessage { Id = StableId(meeting.Id + ":report"), ConversationId = c.Id, RequestId = meeting.RequestId, Author = "Gestor",
                        Role = "report", Text = meeting.Report, AtUtc = Timestamp(meeting.FinishedUtc, at), Channel = "meeting", Status = meeting.FinishedUtc == null ? "interrupted" : "complete" });
                return c.Id;
            }
        }
        public FridaMigrationResult ImportLegacy(string reportsDirectory, string projectsFile, string meetingsDirectory)
        {
            var result = new FridaMigrationResult();
            lock (Gate)
            {
                // Catalog failures must stop migration, not be mistaken for bad source files.
                var snapshot = Read();
                _batch = snapshot; _batchDirty = false;
                try { ImportLegacyBatch(reportsDirectory, projectsFile, meetingsDirectory, result); }
                finally { FlushBatch(); _batch = null; _batchDirty = false; }
                return result;
            }
        }
        private void ImportLegacyBatch(string reportsDirectory, string projectsFile, string meetingsDirectory, FridaMigrationResult result)
        {
                var snapshot = Read(); int pending = 0;
                var known = new Dictionary<string, HashSet<string>>();
                if (File.Exists(projectsFile))
                    ImportOne(projectsFile, result, () =>
                    {
                        foreach (var p in Json().Deserialize<List<FridaIdeProject>>(File.ReadAllText(projectsFile, Encoding.UTF8)) ?? new List<FridaIdeProject>())
                            if (p != null && !string.IsNullOrWhiteSpace(p.Root)) { ImportProject(p.Name, p.Root, p.SshAlias); result.Projects++; }
                    });
                snapshot = Read();
                // The IDE runs this on every start. A record whose identity and messages are already
                // present is skipped without touching the catalog; a partial import is completed.
                if (Directory.Exists(reportsDirectory))
                    foreach (string path in Directory.EnumerateFiles(reportsDirectory, "*.json").OrderBy(x => x))
                        ImportOne(path, result, () =>
                        {
                            var report = Json().Deserialize<FridaConversationEntry>(File.ReadAllText(path, Encoding.UTF8));
                            if (report == null || !ReportImported(snapshot, known, report))
                            { known.Remove(RecordReport(report)); snapshot = Read(); if (++pending % 100 == 0) FlushBatch(); }
                            result.Reports++;
                        });
                if (Directory.Exists(meetingsDirectory))
                    foreach (string path in Directory.EnumerateFiles(meetingsDirectory, "*.json").OrderBy(x => x))
                        ImportOne(path, result, () =>
                        {
                            var meeting = Json().Deserialize<FridaIdeMeeting>(File.ReadAllText(path, Encoding.UTF8));
                            if (meeting == null || !MeetingImported(snapshot, known, meeting))
                            { known.Remove(RecordMeeting(meeting)); snapshot = Read(); if (++pending % 100 == 0) FlushBatch(); }
                            result.Meetings++;
                        });
        }
        private HashSet<string> MessageIds(Dictionary<string, HashSet<string>> known, string conversationId)
        {
            HashSet<string> ids;
            if (known.TryGetValue(conversationId, out ids)) return ids;
            ids = new HashSet<string>(StringComparer.Ordinal);
            string directory = MessageDirectory(conversationId);
            if (Directory.Exists(directory))
                foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file); int dash = name.IndexOf('-');
                    if (dash > 0) ids.Add(name.Substring(dash + 1));
                }
            known[conversationId] = ids; return ids;
        }
        private bool ReportImported(FridaWorkspaceCatalog snapshot, Dictionary<string, HashSet<string>> known, FridaConversationEntry report)
        {
            if (string.IsNullOrWhiteSpace(report.request_id)) return false;
            string id;
            if (!snapshot.Imported.TryGetValue("request:" + report.request_id, out id)) return false;
            if (!string.IsNullOrEmpty(report.conversation_id) && report.conversation_id != id) return false;
            var c = snapshot.Conversations.FirstOrDefault(x => x.Id == id);
            if (c == null) return false;
            if (c.DeletedUtc != null) return true;
            var ids = MessageIds(known, id);
            return ids.Contains(StableId(report.request_id + ":user")) && (string.IsNullOrEmpty(report.response) || ids.Contains(StableId(report.request_id + ":assistant")));
        }
        private bool MeetingImported(FridaWorkspaceCatalog snapshot, Dictionary<string, HashSet<string>> known, FridaIdeMeeting meeting)
        {
            if (string.IsNullOrEmpty(meeting.Id)) return false;
            string id;
            if (!snapshot.Imported.TryGetValue("meeting:" + meeting.Id, out id)) return false;
            if (!string.IsNullOrEmpty(meeting.ConversationId) && meeting.ConversationId != id) return false;
            var c = snapshot.Conversations.FirstOrDefault(x => x.Id == id);
            if (c == null) return false;
            if (c.DeletedUtc != null) return true;
            if (c.Kind != FridaConversationKind.Meeting) return false;
            var ids = MessageIds(known, id); int position = 0;
            foreach (var message in meeting.Messages ?? new List<TeamMessage>())
            {
                int index = position++; if (message == null) continue;
                if (!ids.Contains(message.Id ?? StableId(meeting.Id + ":message:" + index))) return false;
            }
            return string.IsNullOrEmpty(meeting.Report) || !string.IsNullOrEmpty(meeting.ConversationId) || ids.Contains(StableId(meeting.Id + ":report"));
        }
        private static void ImportOne(string path, FridaMigrationResult result, Action import)
        {
            try { import(); }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is InvalidDataException) && !(ex is InvalidOperationException)) throw;
                result.Warnings.Add(Path.GetFileName(path) + ": " + ex.GetType().Name + ". Original preservado.");
            }
        }
    }
}
