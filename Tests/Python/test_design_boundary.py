"""Offline design-scope checks; no repository mutation or GitHub calls."""
import importlib.util
from pathlib import Path
import sys
import unittest

PATH = Path(__file__).resolve().parents[2] / "Tools/check-design-boundary.py"
SPEC = importlib.util.spec_from_file_location("design_boundary_guard", PATH)
guard = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = guard
SPEC.loader.exec_module(guard)


class DesignBoundaryTests(unittest.TestCase):
    def check_diff(self, payload):
        return guard.check_changes(guard.parse_name_status(payload))

    def test_owned_asset_and_designer_document_are_allowed(self):
        self.assertEqual(self.check_diff(b"A\0Assets/DesignerWorkspace/Prefabs/Fox.prefab\0M\0Design/Preview.md\0A\0Dashboard/src/App.tsx\0"), [])

    def test_rename_into_owned_folder_cannot_hide_protected_source(self):
        errors = self.check_diff(b"R100\0Python/lego_ocr_adapter.py\0Dashboard/reader.py\0")
        self.assertTrue(any("Python/lego_ocr_adapter.py" in error for error in errors))

    def test_rename_out_of_owned_folder_cannot_modify_protected_destination(self):
        errors = self.check_diff(b"R100\0Assets/DesignerWorkspace/example.cs\0Assets/BrailleSoundDemo/Runtime/ConversationFlow.cs\0")
        self.assertTrue(any("ConversationFlow.cs" in error for error in errors))

    def test_copy_source_and_protected_deletion_are_checked(self):
        self.assertTrue(self.check_diff(b"C100\0ProjectSettings/ProjectSettings.asset\0Dashboard/settings.asset\0"))
        self.assertTrue(self.check_diff(b"D\0Assets/Scenes/PhysicalStoryDemo.unity\0"))

    def test_owned_rename_preserves_whitespace_names(self):
        diff = b"R100\0Design/Old name\nline.txt\0Design/New name.txt\0"
        self.assertEqual(self.check_diff(diff), [])
        self.assertIn("\n", guard.parse_name_status(diff)[0].paths[0])

    def test_self_policy_and_workflow_changes_are_protected(self):
        for path in ("Assets/DesignerWorkspace/AGENTS.md", ".github/workflows/design-boundary.yml", "Tools/check-design-boundary.py"):
            self.assertTrue(self.check_diff(b"M\0" + path.encode() + b"\0"))

    def test_metadata_deletion_and_guid_replacement_are_rejected(self):
        self.assertTrue(self.check_diff(b"D\0Assets/DesignerWorkspace/Fox.png.meta\0"))
        changes = guard.parse_name_status(b"R100\0Assets/DesignerWorkspace/Old.png.meta\0Assets/DesignerWorkspace/New.png.meta\0")
        original = b"fileFormatVersion: 2\nguid: " + b"a" * 32 + b"\n"
        replaced = b"fileFormatVersion: 2\nguid: " + b"b" * 32 + b"\n"
        self.assertEqual(guard.check_meta_guids(changes, lambda side, path: original), [])
        self.assertTrue(guard.check_meta_guids(changes, lambda side, path: original if side == "base" else replaced))
        copies = [guard.Change("C100", ("Assets/DesignerWorkspace/Old.png.meta", "Assets/DesignerWorkspace/New.png.meta"))]
        self.assertTrue(guard.check_meta_guids(copies, lambda side, path: original))
        self.assertEqual(guard.check_meta_guids(copies, lambda side, path: original if side == "base" else replaced), [])

    def test_invalid_paths_and_truncated_renames_fail_closed(self):
        for path in ("Dashboard/../Python/file.py", "/Design/file", "Assets/DesignerWorkspace\\escape", "Design/./file"):
            self.assertFalse(guard.allowed_path(path))
        with self.assertRaises(ValueError):
            guard.parse_name_status(b"R100\0Design/source\0")
        with self.assertRaises(ValueError):
            guard.parse_name_status(b"M\0Dashboard/unterminated")

    def test_design_branch_detection(self):
        self.assertTrue(guard.is_design_branch("design/fox-art"))
        self.assertTrue(guard.is_design_branch("refs/heads/design/fox-art"))
        self.assertFalse(guard.is_design_branch("feature/authorized-core-change"))


if __name__ == "__main__":
    unittest.main()
