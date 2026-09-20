#!/usr/bin/env python3
"""Check curated presentation docs only: UTF-8, inline local links and headings.

No network requests, Unity imports, GPU execution or performance validation.
This deliberately supports the inline-link/ATX-heading subset used by these pages,
not arbitrary Markdown, reference links or the complete historical document tree.
"""
from __future__ import annotations
import argparse
import re
import tempfile
import unittest
from pathlib import Path
from urllib.parse import unquote, urlsplit

PAGES = (
    "README.md", "Evidence/README.md", "Docs/NO_COPY_VISIBLE_TILES.md",
    "Docs/RUNNING.md", "Docs/portfolio/README.md",
    "Docs/REVIEW_2026-09-20.md",
)
LINK = re.compile(r"\]\(([^)\n]+)\)")

def prose(text: str) -> str:
    result, fence = [], None
    for line in text.splitlines():
        marker = re.match(r"^\s*(`{3,}|~{3,})", line)
        if marker:
            token = marker.group(1)
            if fence is None:
                fence = token
            elif token[0] == fence[0] and len(token) >= len(fence):
                fence = None
            result.append("")
        else:
            result.append(line if fence is None else "")
    return "\n".join(result)

def anchors(text: str) -> set[str]:
    found, counts = set(), {}
    for heading in re.findall(r"^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$", prose(text), re.M):
        slug = re.sub(r"[^\w\- ]", "", heading.lower()).replace(" ", "-")
        number = counts.get(slug, 0)
        counts[slug] = number + 1
        found.add(slug if number == 0 else f"{slug}-{number}")
    found.update(re.findall(r"\bid=[\"']([^\"']+)[\"']", text))
    return found

def check(root: Path, pages: tuple[str, ...] = PAGES) -> tuple[list[str], int, int]:
    root = root.resolve()
    errors, local_count, external_count = [], 0, 0
    for name in pages:
        page = root / name
        try:
            text = page.read_bytes().decode("utf-8", errors="strict")
            if "\ufffd" in text:
                raise ValueError("Unicode replacement character found")
        except (OSError, UnicodeError, ValueError) as exc:
            errors.append(f"{name}: {exc}")
            continue
        for match in LINK.finditer(prose(text)):
            raw = match.group(1).strip().strip("<>")
            link = urlsplit(raw)
            if link.scheme or link.netloc:
                external_count += 1
                continue  # External availability is deliberately not asserted.
            local_count += 1
            target = (page.parent / unquote(link.path)).resolve() if link.path else page.resolve()
            label = f"{name}: {raw}"
            if not target.is_relative_to(root):
                errors.append(f"{label}: escapes repository")
            elif not target.exists():
                errors.append(f"{label}: target missing")
            elif link.fragment and target.suffix.lower() == ".md":
                try:
                    content = target.read_bytes().decode("utf-8", errors="strict")
                    if unquote(link.fragment) not in anchors(content):
                        errors.append(f"{label}: heading missing")
                except (OSError, UnicodeError) as exc:
                    errors.append(f"{label}: {exc}")
    return errors, local_count, external_count

class Checks(unittest.TestCase):
    def evaluate(self, body: bytes, target: bytes = b"# Target\n"):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "README.md").write_bytes(body)
            (root / "target.md").write_bytes(target)
            return check(root, ("README.md",))
    def test_valid_local_and_anchor(self):
        self.assertEqual(self.evaluate(b"[ok](target.md#target)")[0], [])
    def test_missing_file(self):
        self.assertIn("target missing", self.evaluate(b"[bad](absent.md)")[0][0])
    def test_missing_heading(self):
        self.assertIn("heading missing", self.evaluate(b"[bad](target.md#absent)")[0][0])
    def test_invalid_utf8(self):
        self.assertTrue(self.evaluate(b"\xff")[0])
    def test_external_not_fetched(self):
        self.assertEqual(self.evaluate(b"[remote](https://invalid.example/file)"), ([], 0, 1))
    def test_fenced_example_ignored(self):
        self.assertEqual(self.evaluate(b"```text\n[example](missing.md)\n```\n"), ([], 0, 0))
    def test_nested_image_links(self):
        self.assertEqual(self.evaluate(b"[![image](target.md)](target.md#target)"), ([], 2, 0))
    def test_path_escape(self):
        self.assertIn("escapes repository", self.evaluate(b"[bad](../outside.md)")[0][0])
    def test_repeated_headings(self):
        self.assertEqual(anchors("# Same\n# Same\n"), {"same", "same-1"})

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(Checks))
        return 0 if result.wasSuccessful() else 1
    errors, local_count, external_count = check(args.root)
    for error in errors:
        print(f"ERROR: {error}")
    print(f"Checked {len(PAGES)} curated pages and {local_count} local links; "
          f"{external_count} external links not fetched; {len(errors)} errors.")
    return 1 if errors else 0

if __name__ == "__main__":
    raise SystemExit(main())
