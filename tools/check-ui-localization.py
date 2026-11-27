"""Check literal UI translation keys and format placeholders without launching WinUI.

Dynamic keys and nonliteral fallback strings still require runtime verification.
"""
import json
from pathlib import Path
import re
import sys


root = Path(__file__).resolve().parent.parent / "src" / "SurgeLink.App"
literal = r'"((?:\\.|[^"\\])*)"'
string_expr = f'({literal}(?:\\s*\\+\\s*{literal})*)'
literal_part = re.compile(literal)
entry_pattern = re.compile(r'\[' + literal + r'\]\s*=\s*' + string_expr)
call_pattern = re.compile(r'Str\.([TF])\(\s*' + literal + r'\s*,\s*' + string_expr)
translations = {}
findings = []
calls = 0


def unquote(value):
    return json.loads('"' + value + '"')


def unquote_expr(expr):
    return "".join(unquote(part.group(1)) for part in literal_part.finditer(expr))


def parse_composite_format(text):
    """Validate C# composite format syntax and return the set of placeholder indices."""
    indices = set()
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        if ch == "{":
            if i + 1 < n and text[i + 1] == "{":
                i += 2
                continue
            end = text.find("}", i + 1)
            if end < 0:
                return None, "unclosed '{'"
            token = text[i + 1:end]
            if "{" in token:
                return None, f"unescaped '{{' in '{{{token}}}'"
            match = re.fullmatch(r'(\d+)(?:,-?\d+)?(?::[^}]+)?', token)
            if not match:
                return None, f"invalid placeholder '{{{token}}}'"
            indices.add(int(match.group(1)))
            i = end + 1
        elif ch == "}":
            if i + 1 < n and text[i + 1] == "}":
                i += 2
                continue
            return None, "unescaped '}'"
        else:
            i += 1
    return indices, None


for path in sorted((root / "Localization" / "Zh").glob("*.cs")):
    for match in entry_pattern.finditer(path.read_text(encoding="utf-8-sig")):
        key = unquote(match.group(1))
        text = unquote_expr(match.group(2))
        if key in translations:
            findings.append(f"duplicate translation key: {key}")
        translations[key] = text

for path in sorted(root.rglob("*.cs")):
    if "obj" in path.relative_to(root).parts or "bin" in path.relative_to(root).parts:
        continue
    source = path.read_text(encoding="utf-8-sig")
    for match in call_pattern.finditer(source):
        # Documentation examples are not real translation calls.
        line_start = source.rfind("\n", 0, match.start()) + 1
        if source[line_start:match.start()].lstrip().startswith("//"):
            continue
        calls += 1
        kind = match.group(1)
        key = unquote(match.group(2))
        english = unquote_expr(match.group(3))
        location = f"{path.relative_to(root)}:{source.count(chr(10), 0, match.start()) + 1}"
        if key not in translations:
            findings.append(f"missing translation: {key} ({location})")
        elif kind == "F":
            en_indices, en_err = parse_composite_format(english)
            zh_indices, zh_err = parse_composite_format(translations[key])
            if en_err:
                findings.append(f"invalid English format string for {key} ({location}): {en_err}")
            elif zh_err:
                findings.append(f"invalid Chinese format string for {key} ({location}): {zh_err}")
            elif en_indices != zh_indices:
                findings.append(f"placeholder mismatch: {key} ({location})")

for finding in findings:
    print(finding)
print(f"Checked {calls} literal UI calls and {len(translations)} translations; {len(findings)} findings.")
sys.exit(bool(findings))
