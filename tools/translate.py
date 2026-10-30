"""
Machine-translates src/HaloBar/Languages/en.txt into one JSON file per language
(src/HaloBar/Languages/<code>.json, {"English": "translation"}).

Entries already in a file are kept, so hand corrections survive a re-run; only new strings are
sent. A translation whose {0}-style placeholders don't match the English is dropped, and the
app shows English for it. Uses Google's free web endpoint; only HaloBar's own UI text is sent.

Run from the repository root:  python tools/translate.py [code ...]
"""
import json
import pathlib
import re
import sys
import time
import urllib.parse
import urllib.request

FOLDER = pathlib.Path(__file__).resolve().parents[1] / "src" / "HaloBar" / "Languages"
# HaloBar's code → Google's code.
LANGUAGES = {
    "ar": "ar", "cs": "cs", "de": "de", "es": "es", "fr": "fr", "gu": "gu", "hi": "hi", "id": "id", "it": "it",
    "ja": "ja", "ko": "ko", "nl": "nl", "pl": "pl", "pt-BR": "pt", "ru": "ru", "th": "th", "tr": "tr", "uk": "uk",
    "vi": "vi", "zh-Hans": "zh-CN", "zh-Hant": "zh-TW",
}
PLACEHOLDER = re.compile(r"\{\s*(\d+)\s*\}")


def google(lines, target):
    query = urllib.parse.urlencode({"client": "gtx", "dt": "t", "sl": "en", "tl": target, "q": "\n".join(lines)})
    request = urllib.request.Request("https://translate.googleapis.com/translate_a/single?" + query,
                                     headers={"User-Agent": "Mozilla/5.0 HaloBar-translate"})
    with urllib.request.urlopen(request, timeout=30) as response:
        data = json.loads(response.read().decode("utf-8"))
    return "".join(segment[0] for segment in data[0] if segment[0]).split("\n")


def translate(lines, target):
    """Translates in one go when the line count survives, else halves the batch."""
    try:
        out = google(lines, target)
    except Exception as e:  # network hiccup: wait and halve
        print(f"  retrying {len(lines)} lines after: {e}")
        time.sleep(2)
        out = []
    if len(out) == len(lines):
        return out
    if len(lines) == 1:
        return [lines[0]]
    mid = len(lines) // 2
    return translate(lines[:mid], target) + translate(lines[mid:], target)


def same_placeholders(a, b):
    return sorted(PLACEHOLDER.findall(a)) == sorted(PLACEHOLDER.findall(b))


def main():
    english = [l.rstrip("\n") for l in (FOLDER / "en.txt").read_text(encoding="utf-8").splitlines() if l.strip()]
    codes = sys.argv[1:] or list(LANGUAGES)
    for code in codes:
        path = FOLDER / f"{code}.json"
        table = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
        todo = [e for e in english if e not in table]
        print(f"{code}: {len(todo)} new of {len(english)}")
        batch, size = [], 0
        def flush():
            nonlocal batch, size
            if not batch: return
            for en, out in zip(batch, translate(batch, LANGUAGES[code])):
                out = PLACEHOLDER.sub(lambda m: "{" + m.group(1) + "}", out.strip())
                if out and same_placeholders(en, out): table[en] = out
            batch, size = [], 0
            time.sleep(0.4)
        for e in todo:
            if size + len(e) > 1800: flush()
            batch.append(e.replace("\\n", " "))
            size += len(e) + 1
        flush()
        # Only strings still in en.txt, sorted, so the files diff cleanly.
        kept = {k: table[k] for k in sorted(table) if k in english}
        path.write_text(json.dumps(kept, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        print(f"  {len(kept)} translated")


if __name__ == "__main__":
    main()
