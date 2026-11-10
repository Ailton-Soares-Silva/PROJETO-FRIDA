using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FridaAgent
{
    public sealed class FridaMemoryTurn { public string Role, Text; }
    public sealed class FridaBrainConversationState
    {
        public List<FridaMemoryTurn> Memory = new List<FridaMemoryTurn>();
        public string PendingNative, LastWhatsAppRoot, LastRequest, LastResponse;
        public string RoutingDescription, RoutingSource, ModelAnalysis, SpokenResponse, ExecutionReport, AuditPath;
        public long RoutingLatencyMs;
        public bool ResultOperational;
        public string[] PendingOpenCandidates;
        public DateTime PendingNativeUtc, PendingOpenUtc;
        public FridaWhatsAppPlan LastWhatsAppPlan, PendingWhatsAppChoice;
        public Dictionary<string, object>[] LastWhatsAppRows;
        public bool LastTechnical, ContinuationAllowed;
    }
    public sealed partial class FridaConversationStore
    {
        public FridaBrainConversationState LoadBrainState(string id)
        {
            lock (Gate)
            {
                Find(Read(), id, false);
                string path = Path.Combine(_root, "Conversations", id, "brain.json");
                if (File.Exists(path)) return Json().Deserialize<FridaBrainConversationState>(File.ReadAllText(path, Encoding.UTF8)) ?? new FridaBrainConversationState();
                string directory = MessageDirectory(id);
                var memory = new List<FridaMemoryTurn>();
                if (Directory.Exists(directory))
                    foreach (string file in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal).Take(32).Reverse())
                    {
                        var m = Json().Deserialize<FridaConversationMessage>(File.ReadAllText(file, Encoding.UTF8));
                        if (m != null && (m.Role == "user" || m.Role == "assistant")) memory.Add(new FridaMemoryTurn { Role = m.Role == "user" ? "user" : "model", Text = m.Text });
                    }
                var lastUser = memory.LastOrDefault(m => m.Role == "user");
                var lastReply = memory.LastOrDefault(m => m.Role == "model");
                return new FridaBrainConversationState { Memory = memory.Skip(Math.Max(0, memory.Count - 16)).ToList(),
                    LastRequest = lastUser == null ? null : lastUser.Text, LastResponse = lastReply == null ? null : lastReply.Text };
            }
        }
        public void SaveBrainState(string id, FridaBrainConversationState state)
        {
            lock (Gate)
            {
                Find(Read(), id, false);
                string directory = Path.Combine(_root, "Conversations", id); Directory.CreateDirectory(directory);
                AtomicWrite(Path.Combine(directory, "brain.json"), Json().Serialize(state));
            }
        }
    }
    public partial class FridaBrain
    {
        internal FridaConversationStore ConversationStore = new FridaConversationStore(Path.Combine(FridaIdeRuntime.DataRoot, "Workspace"));
        private string _brainConversationId;
        private FridaBrainConversationState _legacyConversationState = new FridaBrainConversationState();
        private FridaBrainConversationState CaptureBrainConversation()
        {
            lock (_memLock) return new FridaBrainConversationState {
                Memory = _chatMemory.Select(m => new FridaMemoryTurn { Role = m.Key, Text = m.Value }).ToList(),
                PendingNative = _pendingNativeRequest, PendingNativeUtc = _pendingNativeUtc, PendingOpenCandidates = _pendingOpenCandidates, PendingOpenUtc = _pendingOpenUtc,
                LastWhatsAppPlan = _lastWhatsAppPlan, PendingWhatsAppChoice = _pendingWhatsAppChoice, LastWhatsAppRoot = _lastWhatsAppRoot, LastWhatsAppRows = _lastWhatsAppRows,
                LastRequest = LastRequest, LastResponse = LastResponse, LastTechnical = LastResponseIsTechnical, ContinuationAllowed = LastConversationContinuationAllowed,
                RoutingDescription = LastRoutingDescription, RoutingSource = LastRoutingSource, RoutingLatencyMs = LastRoutingLatencyMs,
                ModelAnalysis = LastModelAnalysis, SpokenResponse = LastSpokenResponse, ExecutionReport = LastExecutionReportJson, AuditPath = LastConversationAuditPath, ResultOperational = LastResultIsOperational };
        }
        internal void SaveBrainConversation()
        {
            var state = CaptureBrainConversation();
            if (_brainConversationId == null) _legacyConversationState = state;
            else if (ConversationStore.Get(_brainConversationId, true).DeletedUtc == null) ConversationStore.SaveBrainState(_brainConversationId, state);
        }
        // Called only while holding the request admission gate. UI navigation never changes these fields.
        internal void ActivateBrainConversation(string id)
        {
            if (id != null) ConversationStore.Get(id);
            if (id == _brainConversationId) return;
            var next = id == null ? _legacyConversationState : ConversationStore.LoadBrainState(id);
            SaveBrainConversation();
            lock (_memLock)
            {
                _chatMemory.Clear();
                foreach (var m in next.Memory ?? new List<FridaMemoryTurn>()) _chatMemory.Add(new KeyValuePair<string, string>(m.Role, m.Text));
            }
            _pendingNativeRequest = next.PendingNative; _pendingNativeUtc = next.PendingNativeUtc;
            _pendingOpenCandidates = next.PendingOpenCandidates; _pendingOpenUtc = next.PendingOpenUtc;
            _lastWhatsAppPlan = next.LastWhatsAppPlan; _pendingWhatsAppChoice = next.PendingWhatsAppChoice;
            _lastWhatsAppRoot = next.LastWhatsAppRoot; _lastWhatsAppRows = next.LastWhatsAppRows;
            LastRequest = next.LastRequest; LastResponse = next.LastResponse;
            LastResponseIsTechnical = next.LastTechnical; LastConversationContinuationAllowed = next.ContinuationAllowed;
            LastRoutingDescription = next.RoutingDescription; LastRoutingSource = next.RoutingSource; LastRoutingLatencyMs = next.RoutingLatencyMs;
            LastModelAnalysis = next.ModelAnalysis; LastSpokenResponse = next.SpokenResponse; LastExecutionReportJson = next.ExecutionReport;
            LastConversationAuditPath = next.AuditPath; LastResultIsOperational = next.ResultOperational;
            _brainConversationId = id;
        }
    }
}
