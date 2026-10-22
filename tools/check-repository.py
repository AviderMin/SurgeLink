"""Scan tracked files, or all reachable Git history, without printing secret values.

Usage: python tools/check-repository.py [--history]
This is a heuristic check, not a guarantee that a repository contains no secrets.
"""
import argparse
from pathlib import Path
import re
import subprocess
import sys


def git(*args):
    return subprocess.check_output(["git", *args])


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--history", action="store_true")
args = parser.parse_args()
rules = {
    "private-key": rb"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----",
    "github-token": rb"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,})",
    "aws-access-key": rb"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b",
    "openai-token": rb"\bsk-(?:proj-|svcacct-)[A-Za-z0-9_-]{30,}",
    "credential-url": rb"(?:https?|ssh)://[^\s/:]+:[^\s/@]+@",
}
compiled = {name: re.compile(pattern) for name, pattern in rules.items()}
forbidden = re.compile(r"(?:^|/)(?:id_rsa|id_dsa|id_ecdsa|id_ed25519|credentials\.json|\.env(?:\.(?!example$).*)?)$|\.(?:pfx|p12|pem|key|snk|dmp|appx|msix|zip|pdb)$", re.I)
findings = 0
scanned = 0
if args.history:
    entries = []
    for line in git("rev-list", "--objects", "--all").decode().splitlines():
        oid, _, path = line.partition(" ")
        entries.append((oid, path))
else:
    entries = [(None, p) for p in git("ls-files", "--cached", "--others", "--exclude-standard", "-z").decode().split("\0") if p and Path(p).is_file()]

for oid, path in entries:
    if oid:
        if git("cat-file", "-t", oid).strip() != b"blob":
            continue
        data = git("cat-file", "blob", oid)
    else:
        with open(path, "rb") as source:
            data = source.read()
    scanned += 1
    label = f"{oid[:12]}:{path}" if oid else path
    if forbidden.search(path):
        print(f"sensitive/generated filename: {label}")
        findings += 1
    if b"\0" in data:
        continue
    for name, pattern in compiled.items():
        if pattern.search(data):
            print(f"{name}: {label}")
            findings += 1
print(f"Scanned {scanned} {'historical blobs' if args.history else 'tracked files'}; {findings} findings.")
sys.exit(1 if findings else 0)
