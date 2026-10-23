import hashlib
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


spec = importlib.util.spec_from_file_location(
    "release_notes", Path(__file__).resolve().parents[1] / "release-notes.py"
)
release_notes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release_notes)


class ReleaseNotesTests(unittest.TestCase):
    def test_recent_commits_without_explanatory_sentence(self):
        for count in (3, 10):
            with self.subTest(commits=count), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                def git(*args):
                    return subprocess.check_output(["git", "-C", folder, *args], encoding="utf-8").strip()
                git("init", "--quiet")
                git("config", "user.name", "Release test")
                git("config", "user.email", "release@example.invalid")
                for index in range(count):
                    git("-c", "commit.gpgsign=false", "commit", "--allow-empty", "--quiet", "-m", f"Change {index}")
                sha = git("rev-parse", "HEAD")
                assets = root / "assets"
                assets.mkdir()
                for arch in ("x64", "arm64"):
                    exe = assets / f"SurgeLink-Setup-1.0.2.13-{arch}.exe"
                    exe.write_bytes(b"installer")
                    exe.with_suffix(".exe.sha256").write_text(hashlib.sha256(b"installer").hexdigest())
                    (assets / f"SurgeLink_1.0.2.13_{arch}.zip").write_bytes(b"archive")
                previous = Path.cwd()
                try:
                    os.chdir(root)
                    release_notes.generate("1.0.2.13", "v1.0.2.13", sha, "owner/repo", assets, root / "notes.md")
                finally:
                    os.chdir(previous)
                notes = (root / "notes.md").read_text(encoding="utf-8")
                self.assertNotIn("截至本次发布目标提交", notes)
                changes = notes.split("## 更新记录\n\n", 1)[1].split("\n---", 1)[0]
                self.assertEqual(min(count, 8), len([line for line in changes.splitlines() if line.startswith("- ")]))
                self.assertIn(f"- Change {count - 1}", changes)
                if count > 8:
                    self.assertNotIn("- Change 0", changes)
                else:
                    self.assertIn("- Change 0", changes)
                self.assertIn(f"/commit/{sha}", changes)


if __name__ == "__main__":
    unittest.main()
