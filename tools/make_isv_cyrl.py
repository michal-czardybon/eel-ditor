"""Generates src/EelDitor.App/Lang/isv-Cyrl.json (Interslavic in Cyrillic) from isv.json.

Run: py tools/make_isv_cyrl.py
isv.json stays the source of truth: edit it, then regenerate. The letter table is the official Interslavic
Cyrillic alphabet, the same one as Transliteration.Interslavic in EelDitor.Core. Technical terms (git, Markdown,
key names, commands, placeholders) stay in Latin letters.
"""
import json
import pathlib
import re

LANG = pathlib.Path(__file__).resolve().parent.parent / "src" / "EelDitor.App" / "Lang"

PAIRS = ("dž=дж lj=љ nj=њ ě=є y=ы j=ј č=ч š=ш ž=ж a=а b=б v=в g=г d=д e=е z=з i=и k=к l=л m=м n=н o=о p=п r=р s=с "
         "t=т u=у f=ф h=х c=ц ć=ћ đ=ђ ę=ѧ ų=ѫ ȯ=ъ å=а ė=е ĺ=љ ľ=љ ń=њ ŕ=р ť=т ď=д ś=с ź=з").split()
TABLE = dict(p.split("=") for p in PAIRS)
MAXLEN = max(len(k) for k in TABLE)

# Kept in Latin letters: placeholders, escapes, key chords, git terms, product names, Markdown syntax.
PROTECTED = re.compile(r"""
    \{\d+\}
  | \\[nrtx]
  | \(git[^)]*\)
  | (?:Ctrl|Alt|Shift)(?:\+(?:Ctrl|Alt|Shift|Enter|Tab|Esc|[A-Za-z0-9]\b))+
  | \b(?:Explorer|[Gg]it|[Cc]ommit)\w*
  | \b(?:Ctrl|Alt|Shift|Enter|Esc|Tab|OK|MB|CR\ LF|CR|LF|TSV|HTML|PDF|PATH|UTF-8|HEAD|Markdown|Windows|Unix|Mac|Word
        |Microsoft|Edge|Google|Chrome|Chromium|Diff|push|Push|upstream|stage|unstage|revert)\b
  | Eel-ditor
  | --word-diff | --cached
  | ==tekst== | H~2~O | x\^2\^ | </?su[bp]>
  | !\[ime\]\(put\)
""", re.X)

# Keys that explain letters: the examples stay Latin, the prose is converted.
LETTER_KEYS = {
    "Cyrillic.Tip.isv": ["ě = є, y = ы, j = ј, lj = љ, nj = њ, dž = дж", "ę ų ȯ ć đ…"],
    "Cyrillic.Tip.ru": ["š č ž x šč ja ju ë è y, ʹ = ь, ʺ = ъ (h i ' takože dějut)"],
    "Cyrillic.Tip.sr": ["lj = љ, nj = њ, dž = џ, đ = ђ, ć = ћ, j = ј"],
    "Cyrillic.Tip.uk": ["h = г, g = ґ, y = и, i = і, je = є, ji = ї, ja ju šč, ʹ = ь, ʼ = apostrof"],
}
# Polish A/B variants are letters of the label, not Slavic words.
AB = re.compile(r"\b[AB]\b")


def convert(text: str) -> str:
    out, i = [], 0
    while i < len(text):
        for n in range(min(MAXLEN, len(text) - i), 0, -1):
            chunk = text[i:i + n]
            cyr = TABLE.get(chunk.lower())
            if cyr is None:
                continue
            letters = [c for c in chunk if c.isalpha()]
            if letters and letters[0].isupper():
                nxt = text[i + n] if i + n < len(text) else ""
                prev = text[i - 1] if i > 0 else ""
                caps = (all(c.isupper() for c in letters) if len(letters) > 1
                        else nxt.isalpha() and nxt.isupper() if nxt.isalpha()
                        else prev.isalpha() and prev.isupper())
                cyr = cyr.upper() if caps else cyr[0].upper() + cyr[1:]
            out.append(cyr)
            i += n
            break
        else:
            out.append(text[i])
            i += 1
    return "".join(out)


def to_cyrillic(key: str, text: str) -> str:
    spans = [m.span() for m in PROTECTED.finditer(text)]
    for example in LETTER_KEYS.get(key, []):
        start = text.index(example)
        spans.append((start, start + len(example)))
    if key.startswith("Cyrillic."):
        spans += [m.span() for m in AB.finditer(text)]
    spans.sort()
    out, pos = [], 0
    for a, b in spans:
        if a < pos:
            continue
        out.append(convert(text[pos:a]))
        out.append(text[a:b])
        pos = b
    out.append(convert(text[pos:]))
    return "".join(out)


def main() -> None:
    isv = json.loads((LANG / "isv.json").read_text(encoding="utf-8"))
    cyr = {k: to_cyrillic(k, v) for k, v in isv.items()}
    text = json.dumps(cyr, ensure_ascii=False, indent=2) + "\n"
    (LANG / "isv-Cyrl.json").write_text(text, encoding="utf-8", newline="\n")
    print(f"{len(cyr)} keys -> isv-Cyrl.json")


if __name__ == "__main__":
    main()
