"""
Collects the English text HaloBar shows, for translating.

Looks through src/HaloBar for string literals and interpolated strings in the places text reaches
the screen (drawing calls, HUDs, panel titles, tile names, settings rows, menus, statuses) and
writes src/HaloBar/Languages/en.txt: one entry per line, interpolation holes as {0}, {1}…

Run from the repository root:  python tools/extract_strings.py
"""
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[1] / "src" / "HaloBar"

# Lines that never show text to the user.
SKIP_LINE = re.compile(
    r"Log\.Write|DllImport|\bGuid\(|Regex|StringComparison\.Ordinal\)|\bcase\s+\"|=>\s*\"[a-z0-9_.-]*\"\s*[,;]|"
    r"Path\.Combine|GetEnvironmentVariable|FromFamilyName|ParseAdd|Registry|\"application/|Content-Type|"
    r"\bthrow\b|Process\.Start|ProcessStartInfo|WebTools\.Run|\bnameof\(|JsonPropertyName|\[\"|\.Contains\(\"|"
    r"StartsWith\(\"|EndsWith\(\"|Equals\(\"|TryGetProperty\(|GetProperty\(|Attr\(|\bId\s*=>|\bIcon\s*=|q\[\""
)
LITERAL = re.compile(r'(\$?)"((?:[^"\\\n]|\\.)*)"')


def template(raw: str, interpolated: bool) -> str:
    text = raw.replace('\\"', '"').replace("\\n", "\n").replace("\\\\", "\\")
    if not interpolated:
        return text
    # Replace each {expression[:format]} hole with {0}, {1}…, keeping literal {{ }} out of the way.
    out, n, i = [], 0, 0
    while i < len(text):
        if text.startswith("{{", i):
            out.append("{"); i += 2; continue
        if text.startswith("}}", i):
            out.append("}"); i += 2; continue
        if text[i] == "{":
            depth, j = 1, i + 1
            while j < len(text) and depth:
                depth += {"{": 1, "}": -1}.get(text[j], 0)
                j += 1
            out.append("{" + str(n) + "}"); n += 1; i = j; continue
        out.append(text[i]); i += 1
    return "".join(out)


def is_text(s: str) -> bool:
    words = re.sub(r"\{\d+\}", "", s)
    if not re.search(r"[A-Za-z]{2,}", words): return False          # needs a real word
    if re.fullmatch(r"[a-z0-9_.:/\\-]+", s): return False            # ids, keys, paths
    if re.match(r"^(https?:|[A-Za-z]:\\|\\\\|\.|/|#|%|\{\d+\}$)", s): return False
    if re.fullmatch(r"[A-Z0-9_]+", s): return False                  # constants
    if re.search(r"\.(cs|json|png|jpg|mp4|ics|exe|dll|txt|ini|toml)\b", s): return False
    if re.fullmatch(r"[A-Za-z]+\.[A-Za-z.]+", s): return False       # Type.Names
    if re.fullmatch(r"[a-z]+[A-Z][A-Za-z]*", s): return False         # camelCase ids
    if s.strip() != s and not s.startswith("  "): return False
    if "<" in s or "=" in s or "&" in s or "?" in s and s.startswith("?"): return False   # HTML, queries
    if re.fullmatch(r"[A-Z][a-z]+(?:[A-Z][a-z]+)+", s): return False   # PascalCase names: WebFetch, GetRecordStatus
    if s.endswith(" Standard Time") or s.endswith(" Time") and len(s.split()) > 2: return False  # Windows time zone ids
    if re.fullmatch(r"[dMyHhmsft:./ -]+", s): return False            # date formats: MMM d, HH:mm
    if re.fullmatch(r"(Key|Num|F)\{0\}", s) or s.startswith("Segoe"): return False
    return True


def main():
    found = {}
    for path in sorted(ROOT.rglob("*.cs")):
        if "obj" in path.parts or "bin" in path.parts: continue
        for line in path.read_text(encoding="utf-8").splitlines():
            code = line.strip()
            if code.startswith("//") or code.startswith("///") or SKIP_LINE.search(code): continue
            if '"""' in code or '@"' in code: continue
            # The language list shows each language in its own name; only its first entry is ours.
            if path.name == "Lang.cs" and code.startswith('("'):
                if '"Windows language"' in code: found.setdefault("Windows language", "UI/Lang.cs")
                continue
            for m in LITERAL.finditer(code):
                s = template(m.group(2), m.group(1) == "$")
                if is_text(s): found.setdefault(s, path.relative_to(ROOT).as_posix())
    out = ROOT / "Languages"
    out.mkdir(exist_ok=True)
    entries = sorted(found)
    (out / "en.txt").write_text("\n".join(e.replace("\n", "\\n") for e in entries) + "\n", encoding="utf-8")
    print(f"{len(entries)} strings")


if __name__ == "__main__":
    main()
