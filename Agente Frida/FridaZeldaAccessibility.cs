using System;
using System.Windows.Forms;

namespace FridaAgent
{
    public partial class ZeldaExecutiveLlmDashboard
    {
        private void ConfigureDashboardMenu()
        {
            AccessibleName = "Zelda Manager. Alt+1 a Alt+7 selecionam salas. Menu de contexto oferece modelos e ações.";
            TabStop = true;
            var menu = new ContextMenuStrip();
            var rooms = new ToolStripMenuItem("Salas");
            string[] names = { "Visão geral", "Hall", "Escritório", "Sala de jogos", "Descanso", "Reunião", "Consumo" };
            for (int i = 0; i < names.Length; i++) { int room = i; rooms.DropDownItems.Add(names[i], null, delegate { SwitchRoom((DashboardRoom)room); }); }
            var models = new ToolStripMenuItem("Modelos");
            menu.Items.Add(rooms); menu.Items.Add(models);
            menu.Items.Add("Testar modelo selecionado", null, delegate { TestAccessibleModel(SelectedStats()); });
            menu.Items.Add("Preferir modelo selecionado", null, delegate { PreferAccessibleModel(SelectedStats()); });
            menu.Items.Add("Atualizar cota", null, delegate { var s = SelectedStats(); if (s != null) ModelQuotaGuard.RefreshQuotaLimits(s.Bucket, s.ModelId); Invalidate(); });
            menu.Items.Add("Ativar/desativar modelo", null, delegate { var s = SelectedStats(); if (s != null) ModelQuotaGuard.SetDisabled(s.Bucket, s.ModelId, !s.IsDisabled); Invalidate(); });
            menu.Items.Add("Zerar contadores de hoje", null, delegate { if (FridaDialog.Confirm(FindForm(), "Zerar contadores?", "Os contadores locais de hoje voltam a zero. O histórico de consumo continua salvo.", "Zerar", true)) { ModelQuotaGuard.ResetTodayCounters(); Invalidate(); } });
            menu.Opening += delegate {
                models.DropDownItems.Clear();
                foreach (var s in AccessibleModels()) { var selected = s; models.DropDownItems.Add(s.FriendlyName + " · " + s.ModelId, null, delegate { SelectAccessibleModel(selected.Bucket, selected.ModelId); }); }
            };
            FridaTheme.Style(menu);
            ContextMenuStrip = menu; Disposed += delegate { menu.Dispose(); };
            MouseDown += delegate { Focus(); };
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (_embeddedLayout && (keyData & Keys.Alt) == Keys.Alt)
            {
                Keys key = keyData & Keys.KeyCode;
                if (key >= Keys.D1 && key <= Keys.D7) { SwitchRoom((DashboardRoom)((int)key - (int)Keys.D1)); return true; }
            }
            return base.ProcessCmdKey(ref message, keyData);
        }
    }
}
