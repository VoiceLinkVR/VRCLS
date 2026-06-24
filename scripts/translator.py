#!/usr/bin/env python3
"""
VRCLS translator bridge (embedded process protocol)

The process stays alive and communicates with the C# app via line-delimited JSON over stdin/stdout.

Startup:
  stdout: {"status":"ready"}

Commands:
  {"cmd":"translate","text":...,"translator":...,"from_lang":...,"to_lang":...}
  -> {"success":true,"text":...} or {"success":false,"error":...}

  {"cmd":"warmup"} -> {"status":"ready"}
  {"cmd":"ping"} -> {"status":"pong"}
  {"cmd":"exit"} -> exits
"""

import builtins
import html
import json
import os
import sys
import traceback
from functools import lru_cache

_translators = None
_CACHE_SIZE = int(os.getenv("VRCLS_TRANSLATOR_CACHE", "512"))
_ORIGINAL_OPEN = builtins.open
_DEFAULT_TRANSLATORS_REGION = os.getenv("VRCLS_TRANSLATORS_DEFAULT_REGION", "CN").strip().upper() or "CN"


def _install_utf8_open_patch() -> None:
    """Force text-mode open() calls to default UTF-8 on Windows locale environments."""

    def _open_utf8_default(file, mode="r", *args, **kwargs):
        if "b" not in mode and "encoding" not in kwargs:
            kwargs["encoding"] = "utf-8"
        return _ORIGINAL_OPEN(file, mode, *args, **kwargs)

    try:
        builtins.open = _open_utf8_default
    except Exception:
        # Keep running even if monkey patching is denied.
        pass


def _force_utf8_stdio() -> None:
    """Make stdio UTF-8 on Windows locales (e.g. cp936/gbk)."""
    os.environ.setdefault("PYTHONUTF8", "1")
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")

    try:
        if hasattr(sys.stdin, "reconfigure"):
            sys.stdin.reconfigure(encoding="utf-8")
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8")
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        # Keep running even if reconfigure is unavailable.
        pass


def _emit(obj: dict) -> None:
    """Write one JSON response line using ASCII-safe escaping for stable IPC."""
    print(json.dumps(obj, ensure_ascii=True), flush=True)


def _lazy_import_translators():
    global _translators
    if _translators is None:
        os.environ.setdefault("translators_default_region", _DEFAULT_TRANSLATORS_REGION)
        import translators as ts

        _translators = ts
    return _translators


@lru_cache(maxsize=_CACHE_SIZE)
def _cached_translate(text: str, translator: str, from_lang: str, to_lang: str) -> str:
    ts = _lazy_import_translators()
    result = ts.translate_text(
        text,
        translator=translator,
        from_language=from_lang,
        to_language=to_lang,
    )
    return html.unescape(result)


def _should_bypass(text: str, from_lang: str, to_lang: str) -> bool:
    if not text:
        return True
    if not to_lang or to_lang == "none":
        return True
    if from_lang and to_lang and from_lang.lower() == to_lang.lower():
        return True
    return False


def _build_error_payload(exc: Exception) -> dict:
    tb = traceback.format_exc()
    # Mirror full traceback to stderr for line-by-line C# stderr pump logging.
    print(tb, file=sys.stderr, flush=True)
    return {
        "success": False,
        "error": str(exc),
        "error_type": type(exc).__name__,
        "traceback": tb,
    }


def _translate(text: str, translator: str, from_lang: str, to_lang: str) -> dict:
    try:
        if _should_bypass(text, from_lang, to_lang):
            return {"success": True, "text": text}

        result = _cached_translate(text, translator, from_lang, to_lang)
        return {"success": True, "text": result}
    except Exception as e:
        msg = str(e)
        # Some engines throw when translating to the same language. In that case, pass-through.
        if "should not be same" in msg:
            return {"success": True, "text": text}
        return _build_error_payload(e)


def main():
    _force_utf8_stdio()
    os.environ.setdefault("translators_default_region", _DEFAULT_TRANSLATORS_REGION)
    _install_utf8_open_patch()
    _emit({"status": "ready"})

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            req = json.loads(line)
            cmd = req.get("cmd", "translate")

            if cmd == "translate":
                out = _translate(
                    req.get("text", ""),
                    req.get("translator", "google"),
                    req.get("from_lang", "auto"),
                    req.get("to_lang", "en"),
                )
                _emit(out)
            elif cmd == "warmup":
                _lazy_import_translators()
                _emit({"status": "ready"})
            elif cmd == "ping":
                _emit({"status": "pong"})
            elif cmd == "exit":
                break
            else:
                _emit({"success": False, "error": f"unknown cmd: {cmd}"})
        except Exception as e:
            _emit(_build_error_payload(e))


if __name__ == "__main__":
    main()
