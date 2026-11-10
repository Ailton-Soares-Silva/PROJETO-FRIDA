using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FridaAgent
{
    public static class FridaWorkspaceRouting
    {
        public static FridaWorkspaceSection? Classify(string request)
        {
            string text = FridaInteractionPolicy.Normalize(request);
            if (FridaRequestIntent.Classify(request).IsSoftwareDevelopment || Regex.IsMatch(text, @"\b(codigo|script|programacao|algoritmo|debug|refator|compil|repositorio|pull request|api rest|software|aplicativo)\w*\b")) return FridaWorkspaceSection.Code;
            if (Regex.IsMatch(text, @"\b(planilha|whatsapp|noticias|previsao do tempo|poema|receita|filamento|reuniao de vendas|agenda|calendario|cotacao)\b")) return FridaWorkspaceSection.Chat;
            return null;
        }
    }
    public sealed partial class FridaConversationStore
    {
        public FridaConversation Route(string id, string request, FridaWorkspaceSection defaultSection, bool meeting = false, string projectId = null)
        {
            lock (Gate)
            {
                // A conversation trashed elsewhere (or a stale voice selection) never receives new turns.
                var current = id == null ? null : TryGet(id);
                // A meeting is one continuous record: later rounds stay in it whatever their subject.
                if (current != null && current.Kind == FridaConversationKind.Meeting) return current;
                var section = current != null && current.ManualSection ? current.Section : FridaWorkspaceRouting.Classify(request) ?? (current == null ? defaultSection : current.Section);
                string title = (request ?? "Nova conversa").Replace('\r', ' ').Replace('\n', ' ').Trim(); if (title.Length > 70) title = title.Substring(0,70);
                if (title.Length == 0) title = "Nova conversa";
                if (current == null) return Create(title, section, meeting ? FridaConversationKind.Meeting : FridaConversationKind.Conversation, projectId);
                if (current.Section == section && (!meeting || current.Kind == FridaConversationKind.Meeting)) return current;
                if (MessageCount(current.Id) == 0)
                {
                    var s = Read(); var c = Find(s, current.Id, false); c.Section = section; if (meeting) c.Kind = FridaConversationKind.Meeting; Write(s); return c;
                }
                var linked = Create(title, section, meeting ? FridaConversationKind.Meeting : current.Kind, current.ProjectId, current.Id);
                var memory = LoadBrainState(current.Id);
                // Carry conversational context, never a pending action/selection from the other chat.
                SaveBrainState(linked.Id, new FridaBrainConversationState { Memory = memory.Memory, LastRequest = memory.LastRequest, LastResponse = memory.LastResponse });
                // Visible link in both histories; "link" turns never enter the model context.
                string at = Now();
                PutMessage(new FridaConversationMessage { ConversationId = current.Id, Role = "link", Author = "Vínculo", Channel = "link", AtUtc = at,
                    Text = "Este assunto continua em " + SectionName(section) + " › " + linked.Title + ". Abra pelo botão Vínculos." });
                PutMessage(new FridaConversationMessage { ConversationId = linked.Id, Role = "link", Author = "Vínculo", Channel = "link", AtUtc = at,
                    Text = "Continuação de " + SectionName(current.Section) + " › " + current.Title + ". O histórico original foi preservado." });
                return Get(linked.Id);
            }
        }
        internal static string SectionName(FridaWorkspaceSection section) { return section == FridaWorkspaceSection.Code ? "Código" : "Chat"; }
        // Origin and derived conversations, for navigation in both directions.
        public List<FridaConversation> Linked(string id)
        {
            lock (Gate)
            {
                var state = Read(); var current = Find(state, id, false);
                return state.Conversations.Where(c => c.DeletedUtc == null && (c.OriginConversationId == id || c.Id == current.OriginConversationId))
                    .OrderBy(c => c.CreatedUtc).ToList();
            }
        }
    }
}
