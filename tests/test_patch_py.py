"""Tests for patch.py's apply/preview orchestration around the C# patcher.

The C# patcher itself is replaced by a stand-in with the same on-disk contract
(read <managed>/<dll>, SKIP when its marker is present, otherwise back up and
write); everything else — manifest, .bak handling, restore decisions — is the
real patch.py code.

Run from the repo root:  python3 -m unittest tests/test_patch_py.py
"""

import contextlib
import hashlib
import io
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import patch  # noqa: E402

ORIGINAL = b"orig"          # what Steam ships
OLD_PATCH = b"orig+v1"      # what an older patcher wrote (marker "+v" present)
NEW_PATCH = b"orig+v2"      # what the current patcher writes from the original


def sha(data):
    return hashlib.sha256(data).hexdigest()


class FakePatcher:
    """Stand-in for cs2patcher with its contract: a DLL that already carries the
    marker is SKIPped (Applied == 0); an original is backed up and rewritten."""

    def __init__(self):
        self.calls = []   # (apply, {dll: bytes seen}) per invocation

    def __call__(self, dotnet, managed_dir, apply):
        seen = {}
        for dll in patch.DLLS:
            path = os.path.join(managed_dir, dll)
            if not os.path.isfile(path):
                continue
            with open(path, "rb") as f:
                data = f.read()
            seen[dll] = data
            if b"+v" in data:
                continue                       # marker present → SKIP
            if apply:
                bak = path + ".bak"
                if not os.path.isfile(bak):
                    shutil.copy2(path, bak)
                with open(path, "wb") as f:
                    f.write(data + b"+v2")
        self.calls.append((apply, seen))
        return True


class ManagedDir:
    """A throwaway Managed directory with the five target DLLs plus one bystander."""

    def __init__(self):
        self.path = tempfile.mkdtemp(prefix="cs2-managed-")
        self.write("UnityEngine.dll", b"bystander")

    def write(self, name, data):
        with open(os.path.join(self.path, name), "wb") as f:
            f.write(data)

    def read(self, name):
        with open(os.path.join(self.path, name), "rb") as f:
            return f.read()

    def exists(self, name):
        return os.path.exists(os.path.join(self.path, name))

    def cleanup(self):
        shutil.rmtree(self.path, ignore_errors=True)


class RepatchTests(unittest.TestCase):
    def setUp(self):
        self.managed = ManagedDir()
        self.fake = FakePatcher()
        self._real_run_patcher = patch.run_patcher
        patch.run_patcher = self.fake
        self._quiet = contextlib.redirect_stdout(io.StringIO())   # patch.py narrates
        self._quiet.__enter__()

    def tearDown(self):
        self._quiet.__exit__(None, None, None)
        patch.run_patcher = self._real_run_patcher
        self.managed.cleanup()

    def install_old_patch(self):
        """An install patched by an older patcher: live = OLD_PATCH, .bak = ORIGINAL,
        manifest records the live sha (so patch.py knows the bytes are ours)."""
        manifest = {}
        for dll in patch.DLLS:
            self.managed.write(dll, OLD_PATCH)
            self.managed.write(dll + ".bak", ORIGINAL)
            manifest[dll] = sha(OLD_PATCH)
        patch.save_manifest(self.managed.path, manifest)

    def test_apply_rebuilds_an_already_patched_install_from_its_backups(self):
        self.install_old_patch()

        ok = patch.apply_fixes("dotnet", self.managed.path)

        self.assertTrue(ok)
        for dll in patch.DLLS:
            self.assertEqual(self.managed.read(dll), NEW_PATCH, dll)
            self.assertEqual(self.managed.read(dll + ".bak"), ORIGINAL, dll + ".bak")
        self.assertEqual(patch.load_manifest(self.managed.path),
                         {dll: sha(NEW_PATCH) for dll in patch.DLLS})

    def test_apply_leaves_a_dll_the_game_updated_alone_and_patches_it_in_place(self):
        self.install_old_patch()
        # Steam replaced Game.dll with a new original; its stale .bak must not come back.
        self.managed.write("Game.dll", b"orig-newer")

        patch.apply_fixes("dotnet", self.managed.path)

        self.assertEqual(self.managed.read("Game.dll"), b"orig-newer+v2")  # not downgraded
        self.assertEqual(self.managed.read("PDX.SDK.dll"), NEW_PATCH)       # others still rebuilt

    def test_apply_without_manifest_confirmation_patches_in_place(self):
        # Pre-manifest install: live bytes differ from .bak but nothing proves they are ours.
        for dll in patch.DLLS:
            self.managed.write(dll, OLD_PATCH)
            self.managed.write(dll + ".bak", ORIGINAL)
        patch.save_manifest(self.managed.path, {})

        patch.apply_fixes("dotnet", self.managed.path)

        for dll in patch.DLLS:
            self.assertEqual(self.managed.read(dll), OLD_PATCH, dll)   # SKIP, nothing restored

    def test_preview_shows_what_a_rebuild_would_apply_without_touching_the_install(self):
        self.install_old_patch()

        patch.preview_fixes("dotnet", self.managed.path)

        (apply, seen), = self.fake.calls
        self.assertFalse(apply)
        self.assertEqual(seen, {dll: ORIGINAL for dll in patch.DLLS})   # dry-run saw the originals
        for dll in patch.DLLS:
            self.assertEqual(self.managed.read(dll), OLD_PATCH, dll)      # live files untouched
            self.assertEqual(self.managed.read(dll + ".bak"), ORIGINAL)
        self.assertEqual(self.managed.read("UnityEngine.dll"), b"bystander")

    def test_preview_view_keeps_the_rest_of_managed_visible_to_the_patcher(self):
        # Assembly resolvers scan the directory: bystander DLLs must be there too.
        self.install_old_patch()
        listing = {}

        def spy(dotnet, managed_dir, apply):
            listing.update({n: os.path.realpath(os.path.join(managed_dir, n))
                            for n in os.listdir(managed_dir)})
            return True
        patch.run_patcher = spy

        patch.preview_fixes("dotnet", self.managed.path)

        self.assertEqual(listing["UnityEngine.dll"],
                         os.path.realpath(os.path.join(self.managed.path, "UnityEngine.dll")))
        self.assertEqual(listing["Game.dll"],
                         os.path.realpath(os.path.join(self.managed.path, "Game.dll.bak")))


if __name__ == "__main__":
    unittest.main()
