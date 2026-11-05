"""DOM-contract tests in an isolated real Chromium, without WhatsApp/network data."""
import tempfile
import unittest
from urllib.parse import quote
from selenium import webdriver
from selenium.webdriver.chrome.options import Options
from whatsapp_dom import MESSAGES_JS, SEARCH_ROWS_JS, CHAT_ROWS_JS, HEADER_JS, WebUI


class MessageDomTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        options = Options()
        options.add_argument('--headless=new')
        options.add_argument('--user-data-dir='+cls.temp.name)
        options.add_argument('--no-first-run')
        cls.driver = webdriver.Chrome(options=options)

    @classmethod
    def tearDownClass(cls):
        cls.driver.quit()
        cls.temp.cleanup()

    def extract(self, html):
        self.driver.get('data:text/html;charset=utf-8,'+quote('<div id="main">'+html+'</div>'))
        return self.driver.execute_script(MESSAGES_JS)

    def test_reply_extracts_body_not_quoted_message(self):
        records = self.extract('''<div data-id="actual"><span aria-label="Você:"></span>
          <div data-pre-plain-text="[17:02, 04/11/2026] Operador: ">
            <div data-testid="quoted-message"><span class="selectable-text">2514 reparo</span></div>
            <span class="selectable-text">Acompanhando 3213 e 3225 <span class="selectable-text">@Paulo</span></span>
          </div></div>''')
        self.assertEqual(len(records),1)
        self.assertEqual(records[0]['text'],'Acompanhando 3213 e 3225 @Paulo')
        self.assertEqual(records[0]['message_id'],'actual')
        self.assertEqual(records[0]['direction'],'outgoing')
        self.assertEqual(records[0]['sender'],'Operador')

    def test_incoming_message_and_quoted_only_record(self):
        records=self.extract('''<div data-id="other" class="message-in"><div data-pre-plain-text="[10:00, 03/11/2026] Ana: "><span class="selectable-text">Entrega confirmada</span></div></div>
          <div data-id="quoted-only"><div data-pre-plain-text="[10:01, 03/11/2026] Ana: "><div data-testid="quoted-message"><span class="selectable-text">Não é o corpo</span></div></div></div>''')
        self.assertEqual(len(records),1)
        self.assertEqual(records[0]['direction'],'incoming')
        self.assertEqual(records[0]['text'],'Entrega confirmada')

    def test_message_search_result_survives_virtualized_heading(self):
        self.driver.get('data:text/html;charset=utf-8,'+quote('''<div id="pane-side">
          <div data-testid="cell-frame-container"><span title="3213 Contato"><span data-testid="text-highlight">3213</span></span></div>
          <div data-testid="chatlist-message-real-id"><span title="Equipe">Equipe</span><span title="Pedido 3213"><span data-testid="text-highlight">3213</span></span></div>
          </div>'''))
        rows=self.driver.execute_script(SEARCH_ROWS_JS)
        self.assertEqual(len(rows),1)
        self.assertEqual(rows[0]['message_id'],'real-id')
        self.assertEqual(rows[0]['name'],'Equipe')

    def test_in_chat_search_ignores_empty_state_in_global_search(self):
        self.driver.get('data:text/html;charset=utf-8,'+quote('''<div id="pane-side"><div>Nenhuma mensagem encontrada</div></div>
          <div data-testid="drawer-right"><input role="textbox" value="3213"><div data-testid="cell-frame-container"><span data-testid="last-msg-status" title="Pedido 3213"><span data-testid="text-highlight">3213</span></span></div></div>'''))
        ui=WebUI(self.driver,RuntimeError,lambda:None,lambda *args:None)
        self.assertFalse(ui.empty_state(within=True))
        self.assertTrue(ui.empty_state(within=False))
        rows=self.driver.execute_script(SEARCH_ROWS_JS,True)
        self.assertEqual(len(rows),1)
        self.assertEqual(rows[0]['name'],'')
        self.assertEqual(rows[0]['text'],'Pedido 3213')

    def page(self, html):
        self.driver.get('data:text/html;charset=utf-8,'+quote(html))
        return WebUI(self.driver,RuntimeError,lambda:None,lambda *args:None)

    def test_message_hit_with_chat_title_is_not_a_homonymous_conversation(self):
        # Observed live: a document message in the self chat repeated the chat name.
        self.page('''<div id="pane-side"><div role="grid">
          <div role="row">Conversas</div>
          <div role="row"><div data-testid="cell-frame-container"><span title="+55 62 9968-5616">+55 62 9968-5616</span><span>(você)</span></div></div>
          <div role="row">Mensagens</div>
          <div role="row"><div data-testid="chatlist-message-ABC"><span title="+55 62 9968-5616">+55 62 9968-5616</span><span>Conversa.zip</span></div></div>
          <div role="row"><div><span title="Outro grupo">Outro grupo</span></div></div>
          </div></div>''')
        data=self.driver.execute_script(CHAT_ROWS_JS)
        self.assertEqual([(r['name'],r['section']) for r in data['rows']],[('+55 62 9968-5616','conversas'),('Outro grupo','mensagens')])
        self.assertTrue(data['rows'][0]['self'])
        self.assertEqual(data['headings'],['conversas','mensagens'])

    def test_groups_in_common_are_not_name_matches(self):
        # Observed live for "PCP": the same group reappeared under "Grupos em comum".
        ui=self.page('''<div id="side"><input role="textbox" value="PCP"></div><div id="pane-side"><div role="grid">
          <div role="row">Conversas</div>
          <div role="row"><div><span title="DEMANDAS PCP">DEMANDAS PCP</span></div></div>
          <div role="row"><div><span title="Mônica PCP">Mônica PCP</span></div></div>
          <div role="row">Grupos em comum</div>
          <div role="row"><div><span title="DEMANDAS PCP">DEMANDAS PCP</span><span>Mônica PCP está no grupo</span></div></div>
          </div></div>''')
        ui.reset_search=lambda:None
        found=ui.find_chats('PCP')
        self.assertEqual([r['name'] for r in found],['DEMANDAS PCP','Mônica PCP'])

    def test_header_name_keeps_emoji_and_kind_comes_from_controls(self):
        cases=[('<span data-testid="conversation-info-header-chat-title">ENTREGA POP<img alt="✅"></span><div data-testid="chat-subtitle">Somente admins podem enviar mensagens</div><button aria-label="Pesquisar"></button>','ENTREGA POP✅','group'),
               ('<span data-testid="conversation-info-header-chat-title">Equipe</span><div data-testid="chat-subtitle">Ana, Bruno, Você</div><button aria-label="Ligação de vídeo em grupo"></button>','Equipe','group'),
               ('<span data-testid="conversation-info-header-chat-title">Carol RH</span><div data-testid="chat-subtitle">Conta comercial</div><button aria-label="Ligação de vídeo"></button><button aria-label="Ligação de voz"></button>','Carol RH','contact'),
               ('<span data-testid="conversation-info-header-chat-title">+55 62 9968-5616</span><span>(você)</span><div data-testid="chat-subtitle">Mensagens para mim</div>','+55 62 9968-5616','contact'),
               ('<span data-testid="conversation-info-header-chat-title">Sem pistas</span><button aria-label="Pesquisar"></button>','Sem pistas','unknown')]
        for html,name,kind in cases:
            ui=self.page('<div id="main"><header>'+html+'</header></div>')
            header=ui.header()
            self.assertEqual(header['name'],name)
            self.assertEqual(WebUI.chat_kind(header),kind,name)

    def test_empty_state_requires_exact_interface_text(self):
        ui=self.page('<div id="pane-side"><span>Nenhuma conversa, contato ou mensagem encontrada</span></div>')
        self.assertTrue(ui.empty_state())
        ui=self.page('<div id="pane-side"><span title="Equipe">Equipe</span><span>Nenhuma conversa hoje, ok?</span></div>')
        self.assertFalse(ui.empty_state())

    def test_direction_uses_receipts_on_every_bubble_of_a_sequence(self):
        # Observed live: "Você:" and the outgoing tail exist only on the first bubble.
        records=self.extract('''
          <div data-id="o1"><span aria-label="Você:"></span><span data-icon="tail-out"></span><div data-pre-plain-text="[08:25, 29/10/2026] Ailton: "><span class="selectable-text">primeira</span></div><div data-testid="msg-meta">08:25<span aria-label=" Lida "></span></div></div>
          <div data-id="o2"><div data-pre-plain-text="[08:26, 29/10/2026] Ailton: "><span class="selectable-text">segunda</span></div><div data-testid="msg-meta">08:26<span aria-label=" Entregue "></span></div></div>
          <div data-id="i1"><div data-pre-plain-text="[08:27, 29/10/2026] Ana: "><span class="selectable-text">resposta</span></div><div data-testid="msg-meta">08:27</div></div>''')
        self.assertEqual([(r['message_id'],r['direction']) for r in records],[('o1','outgoing'),('o2','outgoing'),('i1','incoming')])

    def test_media_caption_dated_by_divider_and_quoted_text_ignored(self):
        records=self.extract('''<div><span>Ontem</span></div>
          <div data-id="doc1"><div data-testid="quoted-message"><span class="selectable-text">citada</span></div>
            <div data-testid="document-thumb">Solicitacao.pdf</div><span class="selectable-text" data-testid="document-caption">Trilho da tampa</span>
            <div data-testid="msg-meta">16:58<span aria-label=" Lida "></span></div></div>
          <div><span>sexta-feira</span></div>
          <div data-id="sys1"><span class="selectable-text">Ana entrou usando o link</span></div>''')
        doc=[r for r in records if r['message_id']=='doc1'][0]
        self.assertEqual((doc['text'],doc['day'],doc['clock'],doc['direction'],doc['metadata_verified']),('Trilho da tampa','Ontem','16:58','outgoing',False))
        ui=WebUI(self.driver,RuntimeError,lambda:None,lambda *args:None)
        rows=ui.messages({'chat_id':'ui:x','chat':'Equipe','chat_type':'group','source_verified':True})
        self.assertEqual([r['message_id'] for r in rows],['doc1'])  # undated system line dropped
        self.assertEqual(rows[0]['sender'],'Você')
        self.assertTrue(rows[0]['timestamp'].endswith('T16:58:00-03:00'))

    def test_chat_search_footer_is_not_a_sync_in_progress(self):
        # Observed live: this footer is permanent on linked devices ("Ver progresso" only
        # says to keep the phone online), so it must not make every search partial.
        footer='<div data-testid="drawer-right"><div>Sincronizando mensagens mais antigas. Ver progresso</div></div>'
        preview='<div id="side"><div role="row"><span title="Equipe">Equipe</span><span>sincronizando mensagens do ERP</span></div></div>'
        self.assertFalse(self.page(footer+preview).sync_in_progress())
        self.assertTrue(self.page('<div id="side"><div><span>Sincronizando mensagens</span></div></div>').sync_in_progress())

    def test_selected_filter_is_not_clicked_again(self):
        ui=self.page('<button aria-label="Tudo" aria-selected="true" onclick="document.title=\'clicked\'">Tudo</button>')
        self.assertTrue(ui.click_label(['Tudo'],False,skip_selected=True))
        self.assertNotEqual(self.driver.title,'clicked')


if __name__=='__main__':unittest.main()
