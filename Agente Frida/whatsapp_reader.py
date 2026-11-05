#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""Local WhatsApp reader. One final JSON on stdout; diagnostics on stderr."""
import argparse
import contextlib
import json
import os
import sqlite3
import sys
import time
from datetime import datetime, timezone

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
PROFILE_DIR = os.environ.get("FRIDA_WHATSAPP_PROFILE", os.path.join(BASE_DIR, "whatsapp_listener_profile"))
CACHE_DB_PATH = os.environ.get("FRIDA_WHATSAPP_CACHE", os.path.join(BASE_DIR, "whatsapp_cache.db"))


class ReaderFailure(Exception):
    def __init__(self, status, message):
        self.status, self.message = status, message
        super().__init__(message)


class ProfileLock:
    """OS lock released even if this process is terminated by the parent."""
    def __init__(self, profile):
        self.path, self.stream = profile + ".lock", None

    def __enter__(self):
        os.makedirs(os.path.dirname(os.path.abspath(self.path)), exist_ok=True)
        self.stream = open(self.path, "a+b")
        self.stream.seek(0, os.SEEK_END)
        if self.stream.tell() == 0:
            self.stream.write(b"0")
            self.stream.flush()
        self.stream.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            self.stream.close()
            self.stream = None
            raise ReaderFailure("profile_busy", "A sessao do leitor esta em uso ou em pareamento.")
        return self

    def __exit__(self, *exc):
        if self.stream:
            self.stream.close()


@contextlib.contextmanager
def cache_connection():
    conn = sqlite3.connect(CACHE_DB_PATH)
    try:
        with conn:
            yield conn
    finally:
        conn.close()


def init_cache_db():
    with cache_connection() as conn:
        conn.execute("CREATE TABLE IF NOT EXISTS messages (id INTEGER PRIMARY KEY AUTOINCREMENT, chat_name TEXT, sender TEXT, timestamp TEXT, text TEXT, fetched_at TEXT, UNIQUE(chat_name,sender,timestamp,text))")
        conn.execute("CREATE TABLE IF NOT EXISTS chat_summary (chat_name TEXT PRIMARY KEY, is_group INTEGER, unread_count INTEGER, last_snippet TEXT, last_time TEXT, updated_at TEXT)")


def save_messages_to_cache(chat_name, messages):
    now = datetime.now(timezone.utc).isoformat()
    with cache_connection() as conn:
        for item in messages:
            if item.get("text"):
                conn.execute("INSERT OR IGNORE INTO messages(chat_name,sender,timestamp,text,fetched_at) VALUES (?,?,?,?,?)", (chat_name,item.get("sender", "Desconhecido"),item.get("time", ""),item["text"],now))


def save_chat_summary(chats):
    now = datetime.now(timezone.utc).isoformat()
    with cache_connection() as conn:
        for item in chats:
            if item.get("name"):
                conn.execute("INSERT OR REPLACE INTO chat_summary VALUES (?,?,?,?,?,?)", (item["name"],int(item.get("is_group") is True),item.get("unread",0),item.get("snippet", ""),item.get("time", ""),now))


def query_cached_messages(chat_name=None, keyword=None, limit=15):
    sql, params = "SELECT chat_name,sender,timestamp,text,fetched_at FROM messages WHERE 1=1", []
    if chat_name:
        sql += " AND chat_name = ? COLLATE NOCASE"
        params.append(chat_name)
    if keyword:
        sql += " AND text LIKE ?"
        params.append("%" + keyword + "%")
    sql += " ORDER BY id DESC LIMIT ?"
    params.append(limit)
    with cache_connection() as conn:
        rows = conn.execute(sql,params).fetchall()
    return [dict(zip(("chat","sender","time","text","fetched_at"),row)) for row in reversed(rows)]


def close_driver(driver):
    try:
        driver.quit()
    except Exception as exc:
        print("Falha ao fechar navegador: " + type(exc).__name__, file=sys.stderr)


def get_driver(headless=True):
    try:
        from selenium import webdriver
        from selenium.webdriver.chrome.options import Options
    except ImportError:
        raise ReaderFailure("dependency_missing", "Selenium nao esta instalado no Python configurado.")
    os.makedirs(PROFILE_DIR, exist_ok=True)
    os.environ.setdefault("SE_TIMEOUT", "25")
    options = Options()
    options.page_load_strategy = "eager"
    for value in ("--user-data-dir=" + PROFILE_DIR, "--no-first-run", "--no-default-browser-check", "--window-size=1200,800"):
        options.add_argument(value)
    if headless:
        options.add_argument("--headless=new")
    chrome = os.environ.get("FRIDA_CHROME_PATH", "")
    if chrome:
        options.binary_location = chrome
    try:
        with contextlib.redirect_stdout(sys.stderr):
            driver = webdriver.Chrome(options=options)
        driver.set_page_load_timeout(15)
        driver.set_script_timeout(10)
        return driver
    except Exception as exc:
        status = "profile_busy" if "user data directory is already in use" in str(exc).lower() else "browser_error"
        raise ReaderFailure(status, "Falha ao iniciar o navegador: " + type(exc).__name__)


def navigate(driver):
    try:
        driver.get("https://web.whatsapp.com")
    except Exception as exc:
        # SPA service workers may keep navigation pending after the QR/pane has loaded.
        if "Timeout" not in type(exc).__name__:
            raise


def check_login_status(driver, timeout=12):
    navigate(driver)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if driver.find_elements("id", "pane-side"):
            return "LOGGED_IN"
        if driver.find_elements("css selector", "canvas[aria-label*='Scan'], canvas[aria-label*='QR'], div[data-ref]"):
            return "NEEDS_QR"
        time.sleep(.25)
    return "TIMEOUT"


def require_login(driver):
    status = check_login_status(driver)
    if status != "LOGGED_IN":
        raise ReaderFailure("needs_auth" if status == "NEEDS_QR" else "timeout", "Aguardando pareamento." if status == "NEEDS_QR" else "O WhatsApp nao carregou no prazo.")


def run_pair():
    print("Abrindo QR Code; aguardando o operador por ate 120 segundos.", file=sys.stderr, flush=True)
    driver = get_driver(headless=False)
    try:
        navigate(driver)
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            if driver.find_elements("id", "pane-side"):
                return {"status":"success", "authenticated":True, "message":"WhatsApp pareado; perfil salvo."}
            time.sleep(.5)
        raise ReaderFailure("timeout", "Tempo de pareamento esgotado.")
    finally:
        close_driver(driver)


def select_groups_filter(driver):
    buttons = driver.find_elements("xpath", "//*[@role='button' or self::button][normalize-space(.)='Grupos' or normalize-space(.)='Groups' or @aria-label='Grupos' or @aria-label='Groups']")
    if not buttons:
        raise ReaderFailure("dom_changed", "O filtro de grupos nao foi localizado na interface.")
    button = buttons[0]
    button.click()
    deadline = time.monotonic() + 3
    while time.monotonic() < deadline:
        if button.get_attribute("aria-pressed") == "true" or button.get_attribute("aria-selected") == "true" or button.get_attribute("data-state") in ("active", "on"):
            return True
        time.sleep(.1)
    raise ReaderFailure("dom_changed", "Nao foi possivel confirmar o filtro de grupos selecionado.")


def collect_chats(driver):
    select_groups_filter(driver)
    chats = driver.execute_script(JS_EXTRACT_CHATS, True)
    if not isinstance(chats, list):
        raise ReaderFailure("dom_changed", "A interface nao retornou uma lista valida.")
    if not chats and not driver.find_elements("xpath", "//*[contains(text(),'Nenhuma conversa') or contains(text(),'No chats') or contains(text(),'Nenhum grupo')]"):
        raise ReaderFailure("dom_changed", "A lista nao carregou ou os seletores mudaram; lista vazia nao confirmada.")
    return [item for item in chats if item.get("is_group") is True]


JS_EXTRACT_CHATS = """
const items = document.querySelectorAll('#pane-side div[role="listitem"], #pane-side div[data-testid="cell-frame-container"]');
const results = [];
const seen = new Set();

items.forEach(el => {
    // Title / Group name
    const titleEl = el.querySelector('span[title]') || el.querySelector('span[dir="auto"]');
    const name = titleEl ? (titleEl.getAttribute('title') || titleEl.innerText) : '';
    if (!name || seen.has(name)) return;
    seen.add(name);

    // Unread count
    let unread = 0;
    const unreadEl = el.querySelector('span[aria-label*="não lida"], span[data-testid="icon-unread-count"]');
    if (unreadEl) {
        const txt = unreadEl.innerText || unreadEl.getAttribute('aria-label') || '';
        const num = parseInt(txt.replace(/\\D/g, ''), 10);
        if (!isNaN(num)) unread = num;
        else unread = 1;
    }

    // Time
    let timeStr = '';
    const timeEl = el.querySelector('div[class*="_ak8i"], div[class*="timestamp"], div[class*="_ak8j"]');
    if (timeEl) timeStr = timeEl.innerText.trim();

    // Last message preview snippet
    let snippet = '';
    const snippetEl = el.querySelector('span[title][class*="_ao3e"], span[class*="_ao3e"]');
    if (snippetEl) snippet = snippetEl.innerText.trim();

    results.push({
        name: name,
        unread: unread,
        time: timeStr,
        snippet: snippet,
        is_group: arguments[0] === true
    });
});
return results;
"""

JS_EXTRACT_MESSAGES = """
const containers = document.querySelectorAll('#main div[data-testid="msg-container"]');
const msgs = [];
containers.forEach(el => {
    // Text
    const textEl = el.querySelector('span.selectable-text, div[data-testid="msg-text"]');
    const text = textEl ? textEl.innerText.trim() : '';
    if (!text) return;

    // Sender
    let sender = 'Outro';
    if (el.classList.contains('message-out')) {
        sender = 'Operador (Eu)';
    } else {
        const authorEl = el.querySelector('span[data-testid="author"], span[class*="copyable-text"][data-pre-plain-text]');
        if (authorEl) {
            const pre = authorEl.getAttribute('data-pre-plain-text');
            if (pre) {
                // Format: [14:02, 03/11/2026] Carlos: 
                const match = pre.match(/\\]\\s*([^:]+):/);
                if (match) sender = match[1].trim();
            } else {
                sender = authorEl.innerText.trim();
            }
        } else {
            const nameEl = el.querySelector('span[dir="auto"]');
            if (nameEl && nameEl.innerText) sender = nameEl.innerText.trim();
        }
    }

    // Timestamp
    let timeStr = '';
    const metadataEl = el.querySelector('[data-pre-plain-text]');
    const metadata = metadataEl ? metadataEl.getAttribute('data-pre-plain-text') || '' : '';
    const closeBracket = metadata.indexOf(']');
    const timeEl = el.querySelector('div[data-testid="msg-meta"] span');
    if (metadata.startsWith('[') && closeBracket > 0) timeStr = metadata.slice(1, closeBracket);
    else if (timeEl) timeStr = timeEl.innerText.trim();

    msgs.push({
        sender: sender,
        time: timeStr,
        text: text
    });
});
return msgs;
"""


def run_list_groups(limit_count=30):
    driver = get_driver()
    try:
        require_login(driver)
        chats = collect_chats(driver)
        save_chat_summary(chats)
        return {"status":"success", "groups_filter_verified":True, "limited_to_loaded":True, "total_chats_found":len(chats), "chats":chats[:limit_count]}
    finally:
        close_driver(driver)


def run_read_group(target_group, limit=15):
    driver = get_driver()
    try:
        # A QR requirement must reach the parent so pairing can be offered automatically.
        require_login(driver)
        select_groups_filter(driver)
        boxes = driver.find_elements("css selector", "#side div[contenteditable='true'][role='textbox'], div[contenteditable='true'][data-tab='3']")
        if boxes:
            boxes[0].click()
            boxes[0].send_keys("\ue009" + "a")
            boxes[0].send_keys(target_group)
            time.sleep(.8)
        items = driver.find_elements("css selector", "#pane-side div[role='listitem'], #pane-side div[data-testid='cell-frame-container']")
        candidates = {}
        for item in items:
            titles = item.find_elements("css selector", "span[title]")
            if titles:
                name = (titles[0].get_attribute("title") or titles[0].text).strip()
                if target_group.casefold() in name.casefold():
                    candidates.setdefault(name, []).append(item)
        exact = [name for name in candidates if name.casefold() == target_group.casefold()]
        names = exact or list(candidates)
        if len(names) > 1 or (names and len(candidates[names[0]]) > 1):
            return {"status":"ambiguous_group", "matches":names}
        if not names:
            return {"status":"not_found", "group":target_group}
        name = names[0]
        candidates[name][0].click()
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            headers = driver.find_elements("css selector", "#main header span[title]")
            if any((n.get_attribute("title") or "").strip().casefold() == name.casefold() for n in headers):
                break
            time.sleep(.2)
        else:
            return {"status":"group_mismatch", "group":name}
        messages = driver.execute_script(JS_EXTRACT_MESSAGES)
        if not isinstance(messages,list):
            raise ReaderFailure("dom_changed", "Painel de mensagens nao reconhecido.")
        messages = messages[-limit:]
        save_messages_to_cache(name,messages)
        return {"status":"success", "group":name, "group_header_verified":True, "groups_filter_verified":True, "limited_to_loaded":True, "messages":messages}
    finally:
        close_driver(driver)


def run_read_unread():
    driver = get_driver()
    try:
        require_login(driver)
        chats = collect_chats(driver)
        save_chat_summary(chats)
        unread = [item for item in chats if item.get("unread",0) > 0]
        return {"status":"success", "groups_filter_verified":True, "limited_to_loaded":True, "total_unread_chats":len(unread), "unread_chats":unread}
    finally:
        close_driver(driver)


def main(argv=None):
    from whatsapp_runtime import main as run
    return run(argv)


if __name__ == "__main__":
    # CLI redirects on Windows must use the same encoding as the JSONL worker.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    result = main()
    if result is not None:
        print(json.dumps(result, ensure_ascii=False))
