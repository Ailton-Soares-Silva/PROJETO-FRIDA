using System;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace FridaAgent
{
    public sealed class FridaResultForm : Form
    {
        public FridaResultForm(string request,string routing,string result,string analysis,string json)
        {
            Text="Frida — Resultado e verificacoes";Size=new Size(900,680);MinimumSize=new Size(720,500);StartPosition=FormStartPosition.CenterScreen;
            var tabs=new TabControl {Dock=DockStyle.Fill};Controls.Add(tabs);
            Add(tabs,"Resultado",string.IsNullOrWhiteSpace(result)?"Nenhum atendimento nesta sessao.":"Modo: "+routing+"\nSolicitacao: "+request+"\n\n"+result,false);
            Add(tabs,"Analise do modelo","Esta analise e gerada por I.A. e pode conter erros. Ela nao altera os estados de verificacao nem comprova que uma acao foi executada.\n\n"+(string.IsNullOrWhiteSpace(analysis)?"Nenhuma analise recebida.":analysis),false);
            Add(tabs,"Registro JSON",string.IsNullOrWhiteSpace(json)?"Nenhum registro tecnico neste atendimento.":Pretty(json),true);
        }
        private static string Pretty(string json)
        {
            var result=new StringBuilder();int level=0;bool quoted=false,escaped=false;
            foreach(char c in json)
            {
                if(quoted){result.Append(c);if(escaped)escaped=false;else if(c=='\\')escaped=true;else if(c=='"')quoted=false;continue;}
                if(c=='"'){quoted=true;result.Append(c);}
                else if(c=='{' || c=='['){result.Append(c);level++;result.AppendLine();result.Append(' ',level*2);}
                else if(c=='}' || c==']'){level=Math.Max(0,level-1);result.AppendLine();result.Append(' ',level*2);result.Append(c);}
                else if(c==','){result.Append(c);result.AppendLine();result.Append(' ',level*2);}
                else if(c==':')result.Append(": ");
                else if(!char.IsWhiteSpace(c))result.Append(c);
            }
            return result.ToString();
        }
        private static void Add(TabControl tabs,string title,string text,bool code)
        {
            var page=new TabPage(title);var content=new RichTextBox {Dock=DockStyle.Fill,ReadOnly=true,DetectUrls=!code,WordWrap=!code,
                Font=new Font(code?"Consolas":"Segoe UI",10f),BackColor=Color.FromArgb(18,27,43),ForeColor=Color.FromArgb(225,235,245),Text=text};
            if(!code)content.LinkClicked+=(s,e)=>{Uri url;if(Uri.TryCreate(e.LinkText,UriKind.Absolute,out url) && (url.Scheme=="http" || url.Scheme=="https"))try{Process.Start(new ProcessStartInfo(url.AbsoluteUri){UseShellExecute=true});}catch{}};
            page.Controls.Add(content);tabs.TabPages.Add(page);
        }
    }
}
