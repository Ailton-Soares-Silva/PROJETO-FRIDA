using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public sealed class FridaDiagnosticEvent
    {
        public string ConversationId, RequestId, ProjectId, OperationId, AtUtc, Kind, Text, Destination, Directory;
        public override string ToString() { return FridaIdeRuntime.DisplayTime(AtUtc) + " · " + Destination + " · " + Directory + "\r\n" + Text; }
    }
    public static class FridaDiagnostics
    {
        public static event Action<FridaDiagnosticEvent> Published;
        public static void Publish(string kind, string text, string operationId = null)
        {
            var c = FridaIdeRuntime.Context; if (c == null || c.ConversationId == null) return;
            var e = new FridaDiagnosticEvent { ConversationId = c.ConversationId, RequestId = c.RequestId, ProjectId = c.ProjectId, OperationId = operationId,
                AtUtc = DateTime.UtcNow.ToString("o"), Kind = kind, Text = FridaIdeRuntime.Redact(text), Directory = FridaIdeRuntime.DefaultDirectory,
                Destination = c.Project == null || string.IsNullOrEmpty(c.Project.SshAlias) ? "Computador" : c.Project.SshAlias };
            try { FridaMeetingWorkspace.Store(c).AppendDiagnostic(e); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Persistência de diagnóstico: " + ex.GetType().Name); }
            var handler = Published; if (handler == null) return;
            foreach (Action<FridaDiagnosticEvent> observer in handler.GetInvocationList())
                try { observer(e); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.GetType().Name); }
        }
    }
    public sealed partial class FridaConversationStore
    {
        public bool IsRunning(string id) { lock (Gate) return id != null && Active.ContainsKey(ActiveKey(id)); }
        public void AppendDiagnostic(FridaDiagnosticEvent item)
        {
            lock (Gate)
            {
                // Streaming output publishes per line: validate the identity without parsing the whole catalog.
                CheckId(item.ConversationId);
                string directory = Path.Combine(_root, "Conversations", item.ConversationId); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "diagnostics.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024)
                { string previous = path + ".previous"; if (File.Exists(previous)) File.Delete(previous); File.Move(path, previous); }
                item.Text = FridaIdeRuntime.Redact(item.Text);
                File.AppendAllText(path, Json().Serialize(item) + "\n", new UTF8Encoding(false));
            }
        }
        public List<FridaDiagnosticEvent> Diagnostics(string id, int maximum = 500)
        {
            lock (Gate)
            {
                Find(Read(), id, false); var queue = new Queue<FridaDiagnosticEvent>();
                string path = Path.Combine(_root, "Conversations", id, "diagnostics.jsonl");
                foreach (string file in new[] { path + ".previous", path })
                    if (File.Exists(file)) foreach (string row in File.ReadLines(file))
                        try { var e = Json().Deserialize<FridaDiagnosticEvent>(row); if (e != null && e.ConversationId == id) { queue.Enqueue(e); if (queue.Count > maximum) queue.Dequeue(); } }
                        catch (ArgumentException) { } // A partial final JSONL row cannot hide earlier diagnostics.
                return queue.ToList();
            }
        }
    }
}
