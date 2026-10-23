"""Check literal UI translation keys and format placeholders without launching WinUI.

Dynamic keys and nonliteral fallback strings still require runtime verification.
"""
import json
from pathlib import Path
import re
import sys


root = Path(__file__).resolve().parent.parent / "src" / "SurgeLink.App"
literal = r'"((?:\\.|[^"\\])*)"'
entry_pattern = re.compile(r'\[' + literal + r'\]\s*=\s*' + literal)
call_pattern = re.compile(r'Str\.[TF]\(\s*' + literal + r'\s*,\s*' + literal)
placeholder_pattern = re.compile(r'(?<!\{)\{(\d+)(?:,[^{}:]+)?(?::[^{}]+)?\}(?!\})')
translations = {}
findings = []
calls = 0


def unquote(value):
    return json.loads('"' + value + '"')


for path in sorted((root / "Localization" / "Zh").glob("*.cs")):
    for match in entry_pattern.finditer(path.read_text(encoding="utf-8-sig")):
        key, text = map(unquote, match.groups())
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
        key, english = map(unquote, match.groups())
        location = f"{path.relative_to(root)}:{source.count(chr(10), 0, match.start()) + 1}"
        if key not in translations:
            findings.append(f"missing translation: {key} ({location})")
        elif set(placeholder_pattern.findall(english)) != set(placeholder_pattern.findall(translations[key])):
            findings.append(f"placeholder mismatch: {key} ({location})")

for finding in findings:
    print(finding)
print(f"Checked {calls} literal UI calls and {len(translations)} translations; {len(findings)} findings.")
sys.exit(bool(findings))
