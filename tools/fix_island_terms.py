"""
Re-translates strings that mention the island or the pill but came back with a literal word
(ostrov, isla, 섬, गोली…) instead of the language's own Dynamic Island term.

The word is swapped for "HaloBar", which translators leave as it is, and the language's term is
put back afterwards. Only lines missing the term are touched, so hand corrections stay.

Run from the repository root:  python tools/fix_island_terms.py
"""
import json
import re
import sys

sys.path.insert(0, str(__import__("pathlib").Path(__file__).parent))
from translate import FOLDER, LANGUAGES, PLACEHOLDER, same_placeholders, translate  # noqa: E402

# Each language's word for HaloBar's island, as its older strings use it.
TERMS = {
    "ar": "Dynamic Island", "cs": "lišta", "de": "Insel", "es": "Island", "fr": "Island", "gu": "Island", "hi": "आइलैंड",
    "id": "Island", "it": "Island", "ja": "Island", "ko": "아일랜드", "nl": "Island", "pl": "Island", "pt-BR": "Island",
    "ru": "Dynamic Island", "th": "Dynamic Island", "tr": "Dynamic Island", "uk": "Dynamic Island", "vi": "Dynamic Island",
    "zh-Hans": "灵动岛", "zh-Hant": "動態島",
}
WORD = re.compile(r"\b(?:the\s+(?:closed\s+|open\s+)?)?(island|pill)s?\b", re.IGNORECASE)


def main():
    english = [l for l in (FOLDER / "en.txt").read_text(encoding="utf-8").splitlines() if l.strip()]
    mentions = [e for e in english if WORD.search(e)]
    for code, term in TERMS.items():
        path = FOLDER / f"{code}.json"
        table = json.loads(path.read_text(encoding="utf-8"))
        # Lithuanian-style endings aside, Czech declines "lišta"; its stem finds every form.
        stem = term[:-1] if code == "cs" else term
        broken = [e for e in mentions if e in table and stem.lower() not in table[e].lower()]
        if not broken:
            continue
        protected = [WORD.sub(lambda m: m.group(0)[: m.start(1) - m.start(0)] + "HaloBar", e) for e in broken]
        out = translate(protected, LANGUAGES[code])
        fixed = 0
        for en, line in zip(broken, out):
            line = PLACEHOLDER.sub(lambda m: "{" + m.group(1) + "}", line.strip())
            # Gujarati and Hindi write the name in their own script.
            for spelled in ("HaloBar", "હેલોબાર", "हेलोबार"):
                line = line.replace(spelled, term)
            if line and same_placeholders(en, line) and term in line:
                table[en] = line
                fixed += 1
        path.write_text(json.dumps(dict(sorted(table.items())), ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        print(f"{code}: {fixed} of {len(broken)} fixed")


if __name__ == "__main__":
    main()
