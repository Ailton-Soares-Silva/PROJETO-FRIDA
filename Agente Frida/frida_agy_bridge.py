# language: Python, file: frida_agy_bridge.py, runtime: Python 3.12, target: Windows 11
"""JSONL stdio bridge connecting Frida C# Voice/Orb Shell to Google Antigravity SDK."""
import asyncio
import json
import os
import re
import sys
import threading
from pathlib import Path

from google.antigravity import (
    Agent,
    CapabilitiesConfig,
    LocalAgentConfig,
)
from google.antigravity.hooks import policy

BASE_DIR = Path(__file__).resolve().parent
CONFIG_PATH = BASE_DIR / "config.json"
IDE_DATA_DIR = BASE_DIR / "IdeData"
SAVE_DIR = IDE_DATA_DIR / "agy_sessions"
STATE_FILE = IDE_DATA_DIR / "agy_voice_state.json"
SKILLS_DIR = Path.home() / ".gemini" / "config" / "skills"

SYSTEM_INSTRUCTIONS = (
    "Voce e a FRIDA operando sobre o motor Google Antigravity no Windows 11 para Ailton (dj). "
    "Atue como engenheira senior de software/automacao/CAD e secretaria executiva com acesso total a maquina. "
    "Use as skills frida-whatsapp, frida-planilhas-base e frida-secretaria-sistema sempre que o pedido envolver "
    "WhatsApp, planilhas do Google Drive, memoria/lembretes, telemetria de hardware, abertura de programas ou energia. "
    "Nas respostas faladas: va direto ao ponto em portugues brasileiro claro, sem preambulos, sem sermoes e sem avisos "
    "nao solicitados. Quando gerar codigo extenso ou tabelas grandes, coloque um resumo executivo curto nas 2 primeiras "
    "frases seguido do bloco tecnico completo."
)

_stdout_lock = threading.Lock()


def emit(payload: dict) -> None:
    with _stdout_lock:
        sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
        sys.stdout.flush()


def load_gemini_api_key() -> str:
    env_key = (os.environ.get("GEMINI_API_KEY") or "").strip()
    if env_key:
        return env_key
    if CONFIG_PATH.exists():
        try:
            cfg = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
            raw = (cfg.get("api_key") or "").strip()
            if raw:
                for token in re.split(r"[,;\r\n]+", raw):
                    cleaned = token.strip()
                    if cleaned:
                        return cleaned
        except Exception:
            pass
    return ""


def load_saved_conversation_id() -> str | None:
    if STATE_FILE.exists():
        try:
            data = json.loads(STATE_FILE.read_text(encoding="utf-8"))
            cid = (data.get("conversation_id") or "").strip()
            return cid or None
        except Exception:
            return None
    return None


def save_conversation_id(cid: str | None) -> None:
    IDE_DATA_DIR.mkdir(parents=True, exist_ok=True)
    STATE_FILE.write_text(
        json.dumps({"conversation_id": cid or ""}, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def build_spoken_summary(full_text: str) -> tuple[str, bool]:
    text = (full_text or "").strip()
    if not text:
        return "", False
    has_code = "```" in text
    has_table = bool(re.search(r"^\|.+\|\s*$", text, re.MULTILINE))
    rich = has_code or has_table or len(text) > 650

    cleaned = re.sub(r"```[\s\S]*?```", " ", text)
    cleaned = re.sub(r"^\|.*\|$", " ", cleaned, flags=re.MULTILINE)
    cleaned = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", cleaned)
    cleaned = re.sub(r"[*_`#>~]+", " ", cleaned)
    cleaned = re.sub(r"https?://\S+", "na referencia indicada", cleaned)
    cleaned = re.sub(r"\s+", " ", cleaned).strip()

    if not rich:
        return cleaned, False

    sentences = re.split(r"(?<=[.!?])\s+", cleaned)
    summary_parts: list[str] = []
    char_count = 0
    for s in sentences:
        if not s:
            continue
        summary_parts.append(s)
        char_count += len(s)
        if char_count >= 340 or len(summary_parts) >= 3:
            break
    spoken = " ".join(summary_parts).strip()
    if not spoken:
        spoken = "Concluido."
    if has_code or has_table or len(cleaned) > len(spoken) + 80:
        spoken += " Os detalhes completos estao na tela."
    return spoken, True


def make_agent_config(api_key: str, resume_id: str | None) -> LocalAgentConfig:
    SAVE_DIR.mkdir(parents=True, exist_ok=True)
    skills = [str(SKILLS_DIR)] if SKILLS_DIR.exists() else []
    kwargs: dict = {
        "system_instructions": SYSTEM_INSTRUCTIONS,
        "capabilities": CapabilitiesConfig(),
        "policies": [policy.allow_all()],
        "save_dir": str(SAVE_DIR),
    }
    if skills:
        kwargs["skills_paths"] = skills
    if api_key:
        kwargs["api_key"] = api_key
    if resume_id:
        kwargs["conversation_id"] = resume_id
    return LocalAgentConfig(**kwargs)


async def bridge_loop() -> None:
    api_key = load_gemini_api_key()
    if not api_key:
        emit({
            "event": "error",
            "fatal": True,
            "message": "Chave Gemini ausente em config.json ou GEMINI_API_KEY.",
        })
        return

    cmd_queue: asyncio.Queue[dict | None] = asyncio.Queue()
    loop = asyncio.get_running_loop()
    active_turn_holder: dict[str, object | None] = {"resp": None, "task": None}

    def stdin_reader() -> None:
        for raw in sys.stdin.buffer:
            try:
                line = raw.decode("utf-8-sig", errors="replace").strip()
                if not line:
                    continue
                msg = json.loads(line)
                if not isinstance(msg, dict):
                    continue
                if msg.get("op") == "cancel":
                    resp = active_turn_holder.get("resp")
                    if resp is not None and hasattr(resp, "cancel"):
                        asyncio.run_coroutine_threadsafe(resp.cancel(), loop)
                    turn_task = active_turn_holder.get("task")
                    if isinstance(turn_task, asyncio.Task) and not turn_task.done():
                        loop.call_soon_threadsafe(turn_task.cancel)
                    continue
                loop.call_soon_threadsafe(cmd_queue.put_nowait, msg)
            except Exception as exc:
                emit({"event": "protocol_error", "message": str(exc)})
        loop.call_soon_threadsafe(cmd_queue.put_nowait, None)

    threading.Thread(target=stdin_reader, daemon=True).start()

    resume_id = load_saved_conversation_id()
    while True:
        try:
            cfg = make_agent_config(api_key, resume_id)
            async with Agent(cfg) as agent:
                save_conversation_id(agent.conversation_id)
                emit({
                    "event": "ready",
                    "conversation_id": agent.conversation_id,
                    "skills_loaded": SKILLS_DIR.exists(),
                })

                reset_requested = False
                while not reset_requested:
                    item = await cmd_queue.get()
                    if item is None:
                        return
                    op = item.get("op", "chat")
                    if op == "ping":
                        emit({"event": "pong", "conversation_id": agent.conversation_id})
                        continue
                    if op == "reset":
                        save_conversation_id(None)
                        resume_id = None
                        reset_requested = True
                        emit({"event": "session_reset"})
                        break

                    prompt = (item.get("text") or "").strip()
                    req_id = item.get("request_id") or ""
                    channel = item.get("channel") or "voice"
                    if not prompt:
                        continue

                    framed_prompt = (
                        f"[Canal: {channel}] {prompt}"
                        if channel == "voice"
                        else prompt
                    )

                    async def execute_turn() -> tuple[str, str, bool]:
                        response = await agent.chat(framed_prompt)
                        active_turn_holder["resp"] = response

                        async def watch_tools(resp_obj: object) -> None:
                            try:
                                async for call in resp_obj.tool_calls:
                                    emit({
                                        "event": "tool",
                                        "request_id": req_id,
                                        "tool": getattr(call, "name", "ferramenta"),
                                    })
                            except (asyncio.CancelledError, Exception):
                                pass

                        tool_task = asyncio.create_task(watch_tools(response))
                        chunks: list[str] = []
                        try:
                            async for token in response:
                                chunks.append(token)
                        finally:
                            active_turn_holder["resp"] = None
                            try:
                                await asyncio.wait_for(tool_task, timeout=0.5)
                            except Exception:
                                tool_task.cancel()

                        full_reply = "".join(chunks).strip()
                        spoken_part, is_rich = build_spoken_summary(full_reply)
                        return full_reply, spoken_part, is_rich

                    turn_task = asyncio.create_task(execute_turn())
                    active_turn_holder["task"] = turn_task
                    try:
                        full_reply, spoken, rich = await turn_task
                        save_conversation_id(agent.conversation_id)
                        emit({
                            "event": "reply",
                            "request_id": req_id,
                            "conversation_id": agent.conversation_id,
                            "text": full_reply,
                            "spoken": spoken,
                            "has_rich_output": rich,
                        })
                    except asyncio.CancelledError:
                        emit({"event": "cancelled", "request_id": req_id})
                    except Exception as turn_exc:
                        emit({
                            "event": "error",
                            "request_id": req_id,
                            "fatal": False,
                            "message": f"{type(turn_exc).__name__}: {turn_exc}",
                        })
                    finally:
                        active_turn_holder["resp"] = None
                        active_turn_holder["task"] = None
        except Exception as boot_exc:
            if resume_id is not None:
                # Corrupted or expired saved trajectory: clear and retry once cleanly.
                resume_id = None
                save_conversation_id(None)
                continue
            emit({
                "event": "error",
                "fatal": True,
                "message": f"Falha ao iniciar Antigravity Agent: {type(boot_exc).__name__}: {boot_exc}",
            })
            return


def main() -> None:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    asyncio.run(bridge_loop())


if __name__ == "__main__":
    main()
