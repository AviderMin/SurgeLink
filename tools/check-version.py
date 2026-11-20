"""Verify that the documented release version matches Directory.Build.props.

The version is repeated in README.md and README-FULL.md for end users. A stale
copy has shipped before, so this check fails the build instead of relying on a
reviewer noticing.
"""
from pathlib import Path
import re
import sys

root = Path(__file__).resolve().parent.parent
props = (root / "Directory.Build.props").read_text(encoding="utf-8-sig")
match = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", props)
if not match:
    print("Directory.Build.props has no <Version> element.")
    raise SystemExit(1)
version = match.group(1)

checks = {
    "README.md": r"当前版本：\*\*([^*]+)\*\*",
    "README-FULL.md": r"当前源码版本：\*\*([^*]+)\*\*",
}
findings = []
for name, pattern in checks.items():
    text = (root / name).read_text(encoding="utf-8-sig")
    found = re.search(pattern, text)
    if not found:
        findings.append(f"{name}: documented version line not found")
    elif found.group(1) != version:
        findings.append(f"{name}: documents {found.group(1)}, Directory.Build.props has {version}")

for finding in findings:
    print(finding)
print(f"Checked documented version against {version}; {len(findings)} findings.")
raise SystemExit(bool(findings))
