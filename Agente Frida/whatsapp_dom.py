"""WhatsApp Web UI adapter. No private APIs; all evidence comes from rendered UI.

Locators are centralized because WhatsApp can change its web client independently.
An unsupported interface is an error, never evidence that messages do not exist.
"""
import hashlib
import json
import re
import time
import unicodedata
from datetime import datetime, timedelta, timezone

SAO_PAULO = timezone(timedelta(hours=-3), "America/Sao_Paulo")


def normalized(value):
    return " ".join("".join(c for c in unicodedata.normalize("NFKD", value or "")
                            if not unicodedata.combining(c)).casefold().split())


CHAT_STOPWORDS = {"de", "da", "do", "das", "dos", "e", "o", "a", "os", "as", "no", "na", "nos", "nas", "em", "um", "uma", "grupo", "conversa", "chat", "contato", "whatsapp", "zap", "wpp"}


def significant_tokens(value):
    tokens = [t for t in re.split(r"[^a-z0-9]+", normalized(value)) if t and t not in CHAT_STOPWORDS]
    return tokens or [t for t in re.split(r"[^a-z0-9]+", normalized(value)) if t]


def chat_token_overlap(actual, expected):
    tokens = significant_tokens(expected)
    if not tokens:
        return 0, 0
    actual_tokens = [t for t in re.split(r"[^a-z0-9]+", normalized(actual)) if t]
    matched = sum(1 for t in tokens if any(at == t or at.startswith(t) for at in actual_tokens))
    return matched, len(tokens)


def chat_name_matches_strict(actual, expected):
    norm_actual = normalized(actual)
    norm_expected = normalized(expected)
    if not norm_expected:
        return True
    if norm_expected in norm_actual or norm_actual == norm_expected:
        return True
    matched, total = chat_token_overlap(actual, expected)
    return total > 0 and matched == total


def chat_name_matches(actual, expected):
    if chat_name_matches_strict(actual, expected):
        return True
    matched, total = chat_token_overlap(actual, expected)
    return total >= 3 and matched >= 2 and matched >= total - 1


def digest(value):
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def parse_time(value):
    value = (value or "").strip().strip("[]")
    for fmt in ("%H:%M, %d/%m/%Y", "%H:%M, %d/%m/%y", "%H:%M, %m/%d/%Y",
                "%I:%M %p, %m/%d/%Y", "%d/%m/%Y, %H:%M"):
        try:
            return datetime.strptime(value, fmt).replace(tzinfo=SAO_PAULO).isoformat()
        except ValueError:
            pass
    try:
        dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
        return (dt if dt.tzinfo else dt.replace(tzinfo=SAO_PAULO)).isoformat()
    except ValueError:
        return None


MESSAGES_JS = r"""
const root=document.querySelector('#main'); if(!root) return null;
const DAY=/^(hoje|ontem|today|yesterday|domingo|segunda-feira|ter[cç]a-feira|quarta-feira|quinta-feira|sexta-feira|s[aá]bado|sunday|monday|tuesday|wednesday|thursday|friday|saturday|\d{1,2}\/\d{1,2}\/\d{2,4})$/i;
// Date dividers ("Hoje", "Ontem", "sexta-feira", "28/10/2026") in document order.
const dividers=[...root.querySelectorAll('span, div')].filter(e=>e.children.length===0&&!e.closest('[data-id]')&&DAY.test((e.textContent||'').trim()));
// Delivery receipts exist only on the account's own messages, on every bubble of a sequence
// ("Você:" and the outgoing tail appear only on the first one).
const STATUS=/^(lida|entregue|enviada|pendente|read|delivered|sent|pending)$/i;
const quoted=t=>t.closest('[data-testid="quoted-message"], [aria-label="Mensagem citada"], [aria-label="Quoted message"]');
const seen=new Set(), out=[];
for(const m of root.querySelectorAll('[data-id], .message-in, .message-out, [data-testid="msg-container"]')) {
 const el=m.closest('[data-id]')||m;
 if(seen.has(el)) continue;
 const meta=el.querySelector('[data-pre-plain-text]');
 // A quoted reply is rendered before the actual message: with author/date metadata,
 // read only inside it. Media captions (documents, images, video) have no such
 // metadata; read their caption, never quoted text or file names.
 const textEl=meta ? [...meta.querySelectorAll('.selectable-text, [data-testid="msg-text"]')].find(t=>!quoted(t))
   : [...el.querySelectorAll('[data-testid*="caption"], .selectable-text')].find(t=>!quoted(t)&&t.matches('.selectable-text, [data-testid*="caption"]'));
 if(!textEl) continue; seen.add(el);
 const text=textEl.innerText.trim(); if(!text) continue;
 const outgoing=!!(el.matches('.message-out')||el.closest('.message-out')||el.querySelector('.message-out, [aria-label="Você:"], [aria-label="You:"], [data-icon="tail-out"], [data-testid="tail-out"]')
   ||[...el.querySelectorAll('[aria-label]')].some(x=>STATUS.test((x.getAttribute('aria-label')||'').trim())));
 const pre=meta?meta.getAttribute('data-pre-plain-text')||'':'';
 const match=pre.match(/^\[([^\]]+)\]\s*(.*?):\s*$/);
 let day='', clock='';
 if(!match){
  const shown=((el.querySelector('[data-testid="msg-meta"]')||{}).innerText||'').match(/\b(\d{1,2}:\d{2})\b/);
  clock=shown?shown[1]:'';
  for(const d of dividers){if(d.compareDocumentPosition(el)&Node.DOCUMENT_POSITION_FOLLOWING)day=d.textContent.trim();else break;}
 }
 out.push({message_id:el.getAttribute('data-id')||'',text:text,
 sender:match?match[2]:'',time:match?match[1]:'',day:day,clock:clock,
 direction:outgoing?'outgoing':'incoming', metadata_verified:!!match,
 metadata_source:match?'pre_plain_text':'date_divider'});
}
return out;
"""

WEEKDAYS = {"segunda-feira": 0, "terca-feira": 1, "quarta-feira": 2, "quinta-feira": 3, "sexta-feira": 4,
            "sabado": 5, "domingo": 6, "monday": 0, "tuesday": 1, "wednesday": 2, "thursday": 3,
            "friday": 4, "saturday": 5, "sunday": 6}


def resolve_day(day, clock, now=None):
    """Date divider + bubble clock -> ISO timestamp (America/Sao_Paulo), or None."""
    if not day or not re.match(r"^\d{1,2}:\d{2}$", clock or ""):
        return None
    now = (now or datetime.now(SAO_PAULO)).astimezone(SAO_PAULO)
    key = normalized(day)
    if key in ("hoje", "today"):
        date = now.date()
    elif key in ("ontem", "yesterday"):
        date = now.date() - timedelta(days=1)
    elif key in WEEKDAYS:
        # WhatsApp names the weekday for the last week (2..7 days ago).
        date = now.date() - timedelta(days=(now.weekday() - WEEKDAYS[key]) % 7 or 7)
    else:
        try:
            parts = [int(x) for x in key.split("/")]
            date = datetime(parts[2] + (2000 if parts[2] < 100 else 0), parts[1], parts[0]).date()
        except (ValueError, IndexError):
            return None
    hour, minute = (int(x) for x in clock.split(":"))
    return datetime(date.year, date.month, date.day, hour, minute, tzinfo=SAO_PAULO).isoformat()

CHATS_JS = r"""
const root=document.querySelector('#pane-side'); if(!root)return null;
const rows=[...root.querySelectorAll('[role="row"], [role="listitem"], [data-testid="cell-frame-container"]')];
const out=[], seen=new Set();
for(const row of rows){
 const title=row.querySelector('span[title]'); if(!title)continue;
 const name=title.getAttribute('title')||title.innerText;
 const id=row.getAttribute('data-id')||row.querySelector('[data-id]')?.getAttribute('data-id')||'';
 const key=id||name+'|'+row.innerText; if(seen.has(key))continue;seen.add(key);
 const label=row.querySelector('[aria-label*="não lida"], [aria-label*="unread"], [data-testid="icon-unread-count"]');
 const count=label?(parseInt((label.innerText||label.getAttribute('aria-label')||'').replace(/\D/g,''))||1):0;
 out.push({name:name,chat_id:id,unread:count,snippet:row.innerText,
 is_group:arguments[0]===true,chat_type:arguments[0]===true?'group':'unknown'});
}
return out;
"""

SEARCH_ROWS_JS = r"""
// Text results must contain highlighted message text. Chat/contact hits are excluded.
const within=arguments[0]===true;
const roots=[...document.querySelectorAll(within?'[data-testid="drawer-right"], [data-testid="chat-search"]':'#pane-side, [data-testid="search-results"], [data-testid="search-messages-results"]')];
const rows=[], seen=new Set();
for(const root of roots) for(const mark of root.querySelectorAll('mark, .matched-text, [data-testid="search-match"], [data-testid="text-highlight"]')){
 const message=mark.closest('[data-testid^="chatlist-message-"]');
 const row=message||mark.closest('[role="listitem"], [role="row"], [data-testid="cell-frame-container"], [data-testid="search-result"]');
 if(!row||seen.has(row))continue; seen.add(row);
 const heading=[...root.querySelectorAll('h2')].find(e=>/^(mensagens|messages)$/i.test(e.innerText.trim()));
 if(!message && heading && !(heading.compareDocumentPosition(row)&Node.DOCUMENT_POSITION_FOLLOWING))continue;
 if(!within && !message && !heading && mark.closest('span[title]'))continue;
 const title=row.querySelector('span[title]');
 const messageId=message?message.getAttribute('data-testid').slice('chatlist-message-'.length):'';
 rows.push({element:row,key:messageId||row.getAttribute('data-id')||row.innerText,message_id:messageId,
 name:!within&&title?(title.getAttribute('title')||title.innerText):'',preview:row.innerText,
 text:row.querySelector('[data-testid="last-msg-status"]')?.getAttribute('title')||''});
}
return rows;
"""

CHAT_ROWS_JS = r"""
// Conversation rows of the side list. While searching, WhatsApp renders section
// headings ("Conversas", "Mensagens"...). Message hits carry the chat name in
// span[title] too, so they must never be counted as conversations.
// Because #pane-side is virtualized (transform: translateY(...)), DOM order does
// not match visual order; sort candidates by getBoundingClientRect().top first.
const pane=document.querySelector('#pane-side'); if(!pane) return null;
const norm=s=>(s||'').normalize('NFKD').replace(/[̀-ͯ]/g,'').toLowerCase().replace(/\s+/g,' ').trim();
const raw=[...pane.querySelectorAll('[role="row"], [role="listitem"], h2, [role="heading"]')];
const items=[];
let idx=0;
for(const el of raw){
 if(el.parentElement && el.parentElement.closest('[role="row"], [role="listitem"]')) continue;
 const rect=el.getBoundingClientRect ? el.getBoundingClientRect() : null;
 if(rect && rect.height===0 && rect.width===0 && el.getClientRects && el.getClientRects().length===0) continue;
 items.push({el:el, top:rect?rect.top:0, idx:idx++});
}
items.sort((a,b)=>(Math.abs(a.top-b.top)>1 ? a.top-b.top : a.idx-b.idx));
let section='list'; const headings=[], out=[], seen=new Set();
for(const item of items){
 const row=item.el;
 const title=row.querySelector ? row.querySelector('span[title]') : null;
 if(!title){
  const text=norm(row.innerText);
  if(text && text.length<40 && !/\n/.test((row.innerText||'').trim())){section=text;headings.push(text);}
  continue;
 }
 if(row.querySelector('[data-testid^="chatlist-message-"]')||row.closest('[data-testid^="chatlist-message-"]')) continue;
 if(seen.has(row)) continue; seen.add(row);
 out.push({element:row,name:title.getAttribute('title')||title.innerText,section:section,
  preview:row.innerText.split('\n').map(x=>x.trim()).filter(Boolean).slice(0,4).join(' / '),
  self:/\((voc[eê]|you)\)/i.test(row.innerText)});
}
return {headings:headings,rows:out};
"""

HEADER_JS = r"""
const h=document.querySelector('#main header'); if(!h) return null;
const t=h.querySelector('[data-testid="conversation-info-header-chat-title"]')||h.querySelector('span[title]')||h.querySelector('span[dir="auto"]');
let name='';
// Emoji are rendered as <img alt="✅">; textContent alone would drop them.
if(t){const c=t.cloneNode(true);c.querySelectorAll('img[alt]').forEach(i=>i.replaceWith(document.createTextNode(i.getAttribute('alt'))));name=c.textContent.trim();}
const labels=[...h.querySelectorAll('button,[role="button"]')].map(e=>(e.getAttribute('aria-label')||e.getAttribute('title')||'').trim()).filter(Boolean);
const sub=h.querySelector('[data-testid="chat-subtitle"]');
return {name:name,labels:labels,subtitle:sub?sub.innerText.trim():'',text:h.innerText||''};
"""

DRAWER_TEXT_JS = r"""
const d=document.querySelector('[data-testid="drawer-right"]'); return d?d.innerText:null;
"""

PROFILE_TITLES = ("dados do contato", "dados do grupo", "dados da empresa", "dados do perfil",
                  "contact info", "group info", "business info", "profile info")

SCROLL_JS = r"""
const selector=arguments[0], direction=arguments[1];
let root=document.querySelector(selector); if(!root)return null;
let candidates=[root,...root.querySelectorAll('div')].filter(x=>x.scrollHeight>x.clientHeight+5 && x.clientHeight>80 && /auto|scroll/.test(getComputedStyle(x).overflowY));
let el=candidates.sort((a,b)=>b.clientHeight-a.clientHeight)[0]; if(!el)return {moved:false,boundary:true};
let old=el.scrollTop;el.scrollTop+=direction*Math.max(200,el.clientHeight*.8);
return {moved:Math.abs(old-el.scrollTop)>1,boundary:direction<0?el.scrollTop<=1:el.scrollTop+el.clientHeight>=el.scrollHeight-2};
"""


class WebUI:
    def __init__(self, driver, failure, checkpoint, progress):
        self.driver, self.Failure = driver, failure
        self.checkpoint, self.progress = checkpoint, progress

    def wait(self, probe, seconds=12, label="interface"):
        deadline = time.monotonic() + seconds
        last = None
        while time.monotonic() < deadline:
            self.checkpoint()
            try:
                value = probe()
                if value:
                    return value
            except Exception as exc:
                if type(exc).__name__ not in ("StaleElementReferenceException", "NoSuchElementException", "ElementClickInterceptedException", "ElementNotInteractableException"):
                    raise
                last = type(exc).__name__
            time.sleep(.15)
        raise self.Failure("dom_changed", "A interface nao confirmou " + label + (": " + last if last else ""))

    def visible(self, selector):
        return [x for x in self.driver.find_elements("css selector", selector) if x.is_displayed()]

    def _safe_click(self, element):
        try:
            element.click()
        except Exception as exc:
            if type(exc).__name__ in ("ElementClickInterceptedException", "ElementNotInteractableException", "MoveTargetOutOfBoundsException"):
                self.driver.execute_script("arguments[0].scrollIntoView({block:'center'}); arguments[0].click();", element)
            else:
                raise
        return True

    def click_label(self, labels, required=True, skip_selected=False):
        values = json.dumps([normalized(x) for x in labels])
        script = """const names=JSON.parse(arguments[0]);const norm=s=>(s||'').normalize('NFD').replace(/[\\u0300-\\u036f]/g,'').toLowerCase().trim();
        const found=[...document.querySelectorAll('button,[role="button"]')].filter(e=>e.getClientRects().length).find(e=>names.includes(norm(e.getAttribute('aria-label')))||names.includes(norm(e.innerText)))||null;
        if(found&&arguments[1]&&(found.getAttribute('aria-selected')==='true'||found.getAttribute('aria-pressed')==='true'))return 'selected';
        return found;"""
        button = self.driver.execute_script(script, values, skip_selected)
        if button == "selected":
            return True
        if button:
            try:
                button.click()
            except Exception as exc:
                # Transient popovers (recent searches) can cover the control.
                if type(exc).__name__ != "ElementClickInterceptedException":
                    raise
                self.driver.execute_script("arguments[0].click();", button)
            return True
        if required:
            raise self.Failure("dom_changed", "Controle nao localizado: " + "/".join(labels))
        return False

    def reset_search(self):
        # Observed first-login release-notes modal; do not dismiss unrelated dialogs.
        for dialog in self.visible('[role="dialog"]'):
            if normalized(dialog.text).startswith(('novidades do whatsapp web', "what's new in whatsapp web")):
                close = dialog.find_elements('css selector', 'button[aria-label="Fechar"], button[aria-label="Close"]')
                if close:
                    self._safe_click(close[0])
                    self.wait(lambda: not self.visible('[role="dialog"]'), label="fechamento das novidades")
        close = self.visible('[data-testid="drawer-right"] button[aria-label="Fechar"], [data-testid="drawer-right"] button[aria-label="Close"]')
        if close:
            self._safe_click(close[0])
            self.wait(lambda: not self.visible('[data-testid="drawer-right"]'), label="fechamento da pesquisa lateral")
        self.click_label(["Fechar pesquisa", "Close search", "Voltar", "Back"], False)
        clear_btns = self.visible('#side [data-icon="x-alt"], #side [data-testid="x-alt"], #side button[aria-label*="Cancelar"], #side button[aria-label*="Limpar"], #side button[aria-label*="Clear"]')
        if clear_btns:
            try:
                self._safe_click(clear_btns[0])
            except Exception:
                pass
        boxes = self.visible('#side [contenteditable="true"][role="textbox"], #side input[role="textbox"]')
        if boxes:
            self._clear_box(boxes[0], close_popover=True)
        self.click_label(["Tudo", "Todas", "All"], False, skip_selected=True)
        try:
            self.driver.execute_script("const p=document.querySelector('#pane-side'); if(p) p.scrollTop=0;")
        except Exception:
            pass

    def _clear_box(self, box, close_popover=False):
        self._safe_click(box)
        box.send_keys("\ue009a")
        box.send_keys("\ue003")
        raw = box.get_attribute("value") or box.text
        if isinstance(raw, str) and normalized(raw):
            try:
                self.driver.execute_script(
                    "const el=arguments[0]; if(el){if(el.focus)el.focus(); if(document.execCommand){document.execCommand('selectAll',false,null);document.execCommand('delete',false,null);} if('value' in el)el.value='';}",
                    box)
            except Exception:
                pass
        if close_popover:
            box.send_keys("\ue00c")

    EMPTY_TEXTS = ("Nenhum resultado encontrado", "Nenhuma mensagem encontrada", "Nenhuma conversa",
                   "Nenhuma conversa, contato ou mensagem encontrada", "No results found", "No messages found",
                   "No chats", "No chats, contacts or messages found")

    def empty_state(self, within=False):
        selector = '[data-testid="drawer-right"], [data-testid="chat-search"]' if within else '#pane-side'
        # Exact texts only: a message preview that merely starts with "Nenhum..." is not an empty state.
        condition = " or ".join("normalize-space(.)='" + text + "'" for text in self.EMPTY_TEXTS)
        for root in self.visible(selector):
            matches = root.find_elements("xpath", ".//*[self::span or self::div][" + condition + "]")
            if any(element.is_displayed() for element in matches):
                return True
        return False

    SYNC_JS = r"""
    // A real history download shows a status in the side panel, outside the chat rows.
    // The chat-search footer "Sincronizando mensagens mais antigas · Ver progresso" is
    // permanent on linked devices (it means "older history stays on the phone") and is
    // deliberately NOT treated as a sync in progress.
    const side=document.querySelector('#side'); if(!side) return false;
    const re=/sincronizando mensagens|syncing (older )?messages/i;
    for(const el of side.querySelectorAll('span, div')){
     if(el.children.length || el.closest('[role="row"], [role="listitem"], [data-testid="cell-frame-container"]')) continue;
     if(re.test(el.textContent||'') && el.getClientRects().length) return true;
    }
    return false;
    """

    def sync_in_progress(self):
        """True only for an initial history download indicator in the side panel."""
        return bool(self.driver.execute_script(self.SYNC_JS))

    def search_active(self, query, within=False):
        selector = '[data-testid="drawer-right"] input[role="textbox"], [data-testid="chat-search"] [role="textbox"], [role="textbox"][data-tab="6"]' if within else '#side input[role="textbox"], #side [contenteditable="true"][role="textbox"]'
        target = normalized(query)
        for box in self.visible(selector):
            raw = box.get_attribute('value') or box.text
            val = normalized(raw if isinstance(raw, str) else "")
            if val == target or (target and target in val):
                return True
        return False

    def search(self, query, within=False):
        if within:
            # The search button must be inside the currently verified chat header.
            buttons = self.visible('#main header button[aria-label*="Pesquisar"], #main header button[aria-label*="Search"]') or self.visible('#main header [data-icon="search"]')
            if not buttons:
                raise self.Failure("dom_changed", "Pesquisa dentro da conversa indisponivel.")
            self.wait(lambda: self._safe_click(buttons[0]), label="abertura da busca na conversa")
            boxes = self.wait(lambda: self.visible('[data-testid="drawer-right"] input[role="textbox"], [data-testid="chat-search"] [role="textbox"], [role="textbox"][aria-label*="Pesquisar mensagem"], [role="textbox"][aria-label*="Search message"], [role="textbox"][data-tab="6"]'), label="campo de busca da conversa")
        else:
            self.reset_search()
            boxes = self.wait(lambda: self.visible('#side [contenteditable="true"][role="textbox"], #side input[type="text"]'), label="campo de busca")
        self._safe_click(boxes[0])
        boxes[0].send_keys("\ue009a")
        boxes[0].send_keys("\ue003")
        boxes[0].send_keys(query)
        # Do not press Enter: this must never target a message composer.
        empty_since = [None]
        def settled_results():
            rows = self.driver.execute_script(SEARCH_ROWS_JS, within) or []
            if rows:
                empty_since[0] = None
                return rows
            root = '[data-testid="drawer-right"]' if within else '#side'
            busy = self.visible(root+' [role="progressbar"], '+root+' [data-testid="spinner"]')
            if busy or not self.empty_state(within) or not self.search_active(query, within):
                empty_since[0] = None
                return False
            # The UI briefly renders "no messages" while its debounced query is
            # still starting. Require a stable, idle empty state before accepting it.
            if empty_since[0] is None:
                empty_since[0] = time.monotonic()
            return time.monotonic() - empty_since[0] >= 2
        self.wait(settled_results, seconds=20, label="resultados de mensagens")

    def header(self):
        return self.driver.execute_script(HEADER_JS)

    @staticmethod
    def chat_kind(header):
        """Conversation type from rendered header controls, never from the name."""
        labels = [normalized(x) for x in header.get("labels") or []]
        hint = normalized((header.get("subtitle") or "") + " " + (header.get("text") or ""))
        if any(("grupo" in x or "group" in x) and ("ligacao" in x or "chamada" in x or "call" in x or "seletor" in x or "picker" in x) for x in labels) \
                or any(x in hint for x in ("dados do grupo", "group info", "somente admins", "only admins")):
            return "group"
        if any(x.startswith(("ligacao de voz", "ligacao de video", "voice call", "video call")) for x in labels) \
                or any(x in hint for x in ("dados do contato", "contact info", "conta comercial", "business account", "si mesmo",
                                           "message yourself", "mensagens para mim", "(voce)", "(you)")):
            return "contact"
        return "unknown"

    def chat_identity(self, expected=None, expected_kind="all", discriminator=""):
        header = self.header()
        if not header or not header.get("name"):
            raise self.Failure("chat_mismatch", "Cabecalho da conversa nao confirmado.")
        name = header["name"]
        if expected and normalized(name) != normalized(expected):
            raise self.Failure("chat_mismatch", "O cabecalho nao corresponde ao alvo selecionado.")
        # Older clients rendered message data-id with the remote JID; keep using it when present.
        ids = self.driver.execute_script("return [...document.querySelectorAll('#main [data-id]')].map(e=>e.getAttribute('data-id'));") or []
        jids = set()
        for value in ids:
            match = re.search(r'(?:true|false)_([^_]+@(?:g\.us|c\.us|s\.whatsapp\.net|lid))_', value or "")
            if match:
                jids.add(match.group(1))
        if len(jids) > 1:
            raise self.Failure("identity_unverified", "Identidades divergentes na conversa.")
        if jids:
            jid = next(iter(jids))
            kind = "group" if jid.endswith("@g.us") else "contact"
            identity_scope = "remote_id"
        else:
            # The current client exposes no JID. Identity is the verified display name
            # and type (stable across sessions/scroll position); homonymous chats get a
            # discriminator read from their profile panel.
            kind = self.chat_kind(header)
            if kind == "unknown":
                raise self.Failure("identity_unverified", "Tipo da conversa nao confirmado pela interface.")
            jid = "ui:" + digest(normalized(name) + "|" + kind + ("|" + discriminator if discriminator else ""))
            identity_scope = "profile_details" if discriminator else "display_name"
        if expected_kind not in ("all", kind):
            raise self.Failure("chat_type_mismatch", "O tipo da conversa nao corresponde ao filtro.")
        return {"chat_id": jid, "chat": name, "chat_type": kind, "source_verified": True, "identity_scope": identity_scope}

    def _query_chat_rows(self, query_text):
        self.reset_search()
        boxes = self.wait(lambda: self.visible('#side [contenteditable="true"][role="textbox"], #side input[role="textbox"]'), label="localizador de conversa")
        self._clear_box(boxes[0], close_popover=False)
        boxes[0].send_keys(query_text)
        tokens = significant_tokens(query_text)
        started = time.monotonic()
        state = {"signature": None, "since": 0.0}
        def settled():
            if not self.search_active(query_text):
                return None
            data = self.driver.execute_script(CHAT_ROWS_JS) or {}
            rows = data.get("rows") or []
            has_headings = bool(data.get("headings"))
            is_empty = self.empty_state()
            has_matching_row = bool(rows) and any(
                any(t in normalized(r.get("name", "")) for t in tokens)
                for r in rows
            )
            now = time.monotonic()
            if not has_headings and not is_empty and not has_matching_row and (now - started) < 1.2:
                return None  # still the unfiltered chat list
            signature = tuple((r["name"], r["preview"]) for r in rows)
            if signature != state["signature"]:
                state["signature"], state["since"] = signature, now
                return None
            # Results render incrementally; accept only a stable list.
            return {"rows": rows} if now - state["since"] >= .6 else None
        return self.wait(settled, label="conversas correspondentes")["rows"]

    @staticmethod
    def _chat_section_rows(rows):
        return [r for r in rows if r["section"] in ("list", "conversas", "chats")] or \
               [r for r in rows if r["section"] in ("contatos", "contacts")] or list(rows)

    def find_chats(self, name):
        """Conversation rows matching a name: exact matches, else partial/token matches."""
        rows = self._query_chat_rows(name)
        target = normalized(name)
        # Name matches live under "Conversas"; "Contatos" lists address-book entries.
        # "Grupos em comum" (participant matches) and "Mensagens" are never name matches.
        chats = self._chat_section_rows(rows)
        seen_candidates = {}
        for r in chats:
            seen_candidates.setdefault(normalized(r["name"]), r)
        exact = [r for r in chats if normalized(r["name"]) == target]
        for r in exact:
            r["exact"] = True
        if exact:
            return exact
        partial = [r for r in chats if target and (target in normalized(r["name"]) or chat_name_matches_strict(r["name"], name))]
        tokens = significant_tokens(name)
        if not partial:
            compact = " ".join(tokens)
            if compact and compact != target:
                rows = self._query_chat_rows(compact)
                chats = self._chat_section_rows(rows)
                for r in chats:
                    seen_candidates.setdefault(normalized(r["name"]), r)
                partial = [r for r in chats if chat_name_matches_strict(r["name"], name)]
            if not partial and len(tokens) >= 2:
                rows = self._query_chat_rows(tokens[0] + " " + tokens[1])
                chats = self._chat_section_rows(rows)
                for r in chats:
                    seen_candidates.setdefault(normalized(r["name"]), r)
                partial = [r for r in chats if chat_name_matches_strict(r["name"], name)]
        if not partial and seen_candidates:
            # Relaxed token-overlap fallback (e.g. "qualidade corte a laser" -> "Qualidade Corte e Dobra")
            scored = []
            for r in seen_candidates.values():
                matched, total = chat_token_overlap(r["name"], name)
                if chat_name_matches(r["name"], name) or (total >= 2 and matched >= 2):
                    scored.append((matched, r))
            scored.sort(key=lambda item: -item[0])
            partial = [item[1] for item in scored]
        for r in partial:
            r["exact"] = False
        return partial

    def profile_discriminator(self, self_marker=False):
        """Distinctive, rendered profile evidence (phone, group creator) for homonyms."""
        parts = ["self"] if self_marker else []
        opener = self.visible('#main header [data-testid="conversation-info-header"]') or self.visible('#main header [role="button"][title="Dados do perfil"], #main header [role="button"][title="Profile details"]')
        if not opener:
            raise self.Failure("identity_unverified", "Dados da conversa indisponiveis para distinguir homonimos.")
        self.wait(lambda: self._safe_click(opener[0]), label="abertura dos dados da conversa")
        text = self.wait(lambda: (lambda t: t if t and normalized(t).startswith(PROFILE_TITLES) else None)(self.driver.execute_script(DRAWER_TEXT_JS)), seconds=8, label="dados da conversa")
        phone = re.search(r'\+\d[\d\s().-]{6,}\d', text)
        if phone:
            parts.append("tel:" + re.sub(r'\D', '', phone.group(0)))
        creator = re.search(r'(?:grupo criado por|group created by)\s+([^,\n]+)', text, re.I)
        if creator:
            parts.append("criador:" + normalized(creator.group(1)))
        close = self.visible('[data-testid="drawer-right"] button[aria-label="Fechar"], [data-testid="drawer-right"] button[aria-label="Close"]')
        if close:
            self._safe_click(close[0])
            self.wait(lambda: not self.visible('[data-testid="drawer-right"]'), label="fechamento dos dados da conversa")
        if not parts:
            raise self.Failure("identity_unverified", "Conversas homonimas sem dado distintivo visivel.")
        return "|".join(parts)

    def open_row(self, row, name, kind, discriminate=False):
        self.wait(lambda: self._safe_click(row["element"]), label="abertura da conversa")
        self.wait(lambda: (lambda h: h and normalized(h.get("name")) == normalized(name))(self.header()), label="cabecalho da conversa")
        # A chat may be empty or media-only; wait briefly for rendering, do not require text.
        try:
            self.wait(lambda: self.driver.find_elements("css selector", "#main [data-id]"), seconds=4, label="mensagens da conversa")
        except self.Failure:
            pass
        discriminator = self.profile_discriminator(row.get("self")) if discriminate else ""
        return self.chat_identity(name, kind, discriminator)

    def open_chat(self, name, chat_id="", kind="all", candidate=0):
        found = self.find_chats(name)
        if not found:
            raise self.Failure("not_found", "Conversa nao encontrada: " + name)
        names = {normalized(r["name"]) for r in found}
        # A saved chat_id (resume) is matched against every candidate instead of asking again.
        if len(found) > 1 and not candidate and not chat_id:
            reason = "Mais de uma conversa tem esse nome." if len(names) == 1 else "Mais de uma conversa corresponde a esse nome."
            raise self.Failure("ambiguous_chat", reason + " Responda com o numero da opcao.",
                               matches=[{"candidate": i + 1, "name": r["name"], "preview": r["preview"], "self": r["self"]} for i, r in enumerate(found)])
        if candidate and candidate > len(found):
            raise self.Failure("not_found", "A opcao " + str(candidate) + " nao existe; ha " + str(len(found)) + " conversas.")
        order = [candidate - 1] if candidate else list(range(len(found)))
        if chat_id and not candidate:
            # Name-based ids can be checked without opening: try the likely row first.
            def likely(i):
                key = normalized(found[i]["name"])
                return any("ui:" + digest(key + "|" + k) == chat_id for k in ("group", "contact"))
            order.sort(key=lambda i: not likely(i))
        for attempt, index in enumerate(order):
            rows = found
            if attempt:
                # Opening a chat re-renders the list; re-acquire the same row.
                rows = self.find_chats(name)
                same = [r for r in rows if r["name"] == found[index]["name"] and r["preview"] == found[index]["preview"]]
                row = same[0] if same else rows[index] if index < len(rows) else None
            else:
                row = rows[index]
            if row is None:
                continue
            twins = sum(normalized(r["name"]) == normalized(row["name"]) for r in rows) > 1
            identity = self.open_row(row, row["name"], kind, discriminate=twins)
            if not chat_id or identity["chat_id"] == chat_id:
                return identity
            if len(found) == 1:
                # The saved id may come from an earlier homonym choice; the twin may be gone.
                try:
                    if self.chat_identity(row["name"], kind, self.profile_discriminator(row.get("self")))["chat_id"] == chat_id:
                        return dict(identity, chat_id=chat_id, identity_scope="profile_details")
                except self.Failure:
                    pass
        raise self.Failure("chat_mismatch", "A identidade da conversa mudou.")

    def messages(self, identity):
        raw = self.driver.execute_script(MESSAGES_JS)
        if not isinstance(raw, list):
            raise self.Failure("dom_changed", "Painel de mensagens nao reconhecido.")
        result = []
        own = next((x["sender"] for x in raw if x.get("metadata_verified") and x.get("direction") == "outgoing" and x.get("sender")), "")
        for item in raw:
            if not item.get("message_id"):
                continue
            if item.get("metadata_verified"):
                item["timestamp"] = parse_time(item.get("time"))
            else:
                # Media caption: date divider + bubble clock; sender only when it is certain.
                item["timestamp"] = resolve_day(item.get("day"), item.get("clock"))
                if not item["timestamp"]:
                    continue
                stamp = datetime.fromisoformat(item["timestamp"])
                item["time"] = stamp.strftime("%H:%M, %d/%m/%Y")
                if item.get("direction") == "outgoing":
                    item["sender"] = own or "Você"
                elif identity.get("chat_type") == "contact":
                    item["sender"] = identity.get("chat", "")
                else:
                    item["sender"] = "remetente não identificado"
                item["sender_verified"] = item["sender"] != "remetente não identificado"
            for key in ("day", "clock"):
                item.pop(key, None)
            item.update(identity)
            item["reference"] = "M" + digest(identity["chat_id"] + "|" + item["message_id"])[:16]
            item["fetched_at"] = datetime.now(timezone.utc).isoformat()
            result.append(item)
        if raw and not result:
            raise self.Failure("dom_changed", "Mensagens sem identificacao verificavel.")
        return result

    def scroll(self, selector, direction):
        return self.driver.execute_script(SCROLL_JS, selector, direction)
