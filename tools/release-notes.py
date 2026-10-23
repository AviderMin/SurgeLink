"""Validate release assets and generate a Markdown download page; performs no publishing."""
import argparse
import hashlib
import html
from pathlib import Path
import re
import subprocess
from urllib.parse import quote


def generate(version, tag, sha, repository, assets, output):
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:\.\d+)?", version):
        raise ValueError("Invalid numeric version")
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid repository")
    base = f"https://github.com/{repository}"
    download = f"{base}/releases/download/{quote(tag, safe='')}"
    rows = []
    required = []
    for arch, devices in [("x64", "Intel / AMD 64 位电脑"), ("arm64", "Windows on ARM 设备")]:
        exe = f"SurgeLink-Setup-{version}-{arch}.exe"
        appx = f"SurgeLink_{version}_{arch}.zip"
        required += [exe, exe + ".sha256", appx]
        rows.append(f"| **{arch}** · {devices} | [下载安装程序]({download}/{exe}) | [下载侧载包]({download}/{appx}) |")
    for name in required:
        path = assets / name
        if not path.is_file() or path.stat().st_size == 0:
            downloaded = sorted(str(p.relative_to(assets)) for p in assets.rglob("*") if p.is_file())
            raise ValueError(f"Missing or empty release asset: {name}. Downloaded files: {downloaded}")
    unexpected = {p.name for p in assets.iterdir() if p.is_file()} - set(required) - {"SHA256SUMS.txt"}
    if unexpected:
        raise ValueError(f"Unexpected release assets: {sorted(unexpected)}")
    sums = []
    for name in required:
        if name.endswith(".sha256"):
            continue
        with (assets / name).open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        if name.endswith(".exe"):
            expected = (assets / (name + ".sha256")).read_text(encoding="utf-8-sig").strip()
            if digest.lower() != expected.lower():
                raise ValueError(f"SHA256 mismatch: {name}")
        sums.append(f"{digest}  {name}")
    (assets / "SHA256SUMS.txt").write_text("\n".join(sums) + "\n", encoding="utf-8")
    commits = subprocess.check_output(
        ["git", "log", "-8", "--format=%H%x09%s", sha, "--"], encoding="utf-8"
    ).splitlines()
    changelog = []
    for commit in commits:
        commit_sha, subject = commit.split("\t", 1)
        # Commit subjects are data, never release Markdown or HTML instructions.
        subject = html.escape(subject)
        subject = re.sub(r"([\\`*_{}\[\]()#+.!|>~-])", r"\\\1", subject)
        changelog.append(f"- {subject} ([`{commit_sha[:7]}`]({base}/commit/{commit_sha}))")
    notes = f"""# SurgeLink {version}

SSH 终端 · SFTP 文件管理 · 远程性能监控

## 下载

选择设备架构和安装方式。日常使用推荐 **安装程序**。

| 设备架构 | 安装程序 · EXE（推荐） | APPX · 侧载 ZIP |
| :--- | :--- | :--- |
{chr(10).join(rows)}

**系统要求：** Windows 10 21H2 或更高版本。安装包包含 .NET 和 Windows App SDK 运行库。

- **EXE：** 双击安装，支持开始菜单、可选桌面快捷方式和卸载；目前未进行代码签名。
- **APPX ZIP：** 解压后按包内 `Install.ps1` 安装，包含配套开发证书。
- **ARM64：** 已交叉编译，仍需在 ARM64 设备上验证。

[SHA256 校验清单]({download}/SHA256SUMS.txt) · [使用说明]({base}/blob/{sha}/README.md) · [对应源代码]({base}/tree/{sha})

## 更新记录

{chr(10).join(changelog)}

---

版本 `{version}` · 提交 [`{sha[:7]}`]({base}/commit/{sha})
"""
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(notes, encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for key in ("version", "tag", "sha", "repository"):
        parser.add_argument("--" + key, required=True)
    parser.add_argument("--assets", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    generate(args.version, args.tag, args.sha, args.repository, args.assets, args.output)
