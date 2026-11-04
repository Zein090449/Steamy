import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
import zipfile
from create_delta_update import MANIFEST_NAME, build, previous_release


def zip_bytes(files):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, 'w') as archive:
        for name, data in files.items():
            archive.writestr(name, data)
    return buffer.getvalue()


class DeltaPackageTests(unittest.TestCase):
    def test_package_contains_only_changed_files_with_verified_hashes(self):
        with tempfile.TemporaryDirectory() as folder:
            new = Path(folder) / 'Steamy-v0.8.2.2.zip'
            new.write_bytes(zip_bytes({'Steamy.exe': b'new exe', 'same.dll': b'same', 'added.txt': b'added'}))
            old = zip_bytes({'Steamy.exe': b'old exe', 'same.dll': b'same', 'removed.txt': b'gone'})
            output = Path(folder) / 'Steamy-v0.8.2.2-delta.zip'
            manifest = build(new, old, 'v0.8.2.1', 'v0.8.2.2', output)
            self.assertEqual(manifest['baseTag'], 'v0.8.2.1')
            self.assertEqual(manifest['tag'], 'v0.8.2.2')
            self.assertEqual([item['path'] for item in manifest['files']], ['Steamy.exe', 'added.txt'])
            self.assertEqual(manifest['deleted'], ['removed.txt'])
            with zipfile.ZipFile(output) as package:
                names = set(package.namelist())
                embedded = json.loads(package.read(MANIFEST_NAME))
            self.assertEqual(names, {MANIFEST_NAME, 'Steamy.exe', 'added.txt'})
            self.assertEqual(embedded['files'][0]['sha256'], hashlib.sha256(b'new exe').hexdigest())

    def test_previous_release_uses_the_newest_older_published_release(self):
        def api(method, path, data=None):
            self.assertEqual((method, path), ('GET', '/releases?per_page=30'))
            return [
                {'tag_name': 'v0.8.3', 'draft': False, 'prerelease': False, 'published_at': '2026-11-20T00:00:00Z',
                 'assets': [{'name': 'Steamy-v0.8.3.zip', 'id': 3}]},
                {'tag_name': 'v0.8.2.1', 'draft': False, 'prerelease': False, 'published_at': '2026-11-10T00:00:00Z',
                 'assets': [{'name': 'Steamy-v0.8.2.1.zip', 'id': 21}]},
                {'tag_name': 'v0.8.2', 'draft': False, 'prerelease': False, 'published_at': '2026-11-01T00:00:00Z',
                 'assets': [{'name': 'Steamy-v0.8.2.zip', 'id': 2}]},
                {'tag_name': 'v0.8.1.3', 'draft': True, 'prerelease': False, 'published_at': '2026-10-30T00:00:00Z',
                 'assets': [{'name': 'Steamy-v0.8.1.3.zip', 'id': 13}]},
            ]

        release, asset = previous_release(api, 'v0.8.2.2')
        self.assertEqual(release['tag_name'], 'v0.8.2.1')
        self.assertEqual(asset['id'], 21)

    def test_no_older_release_yields_no_package(self):
        def api(method, path, data=None):
            return [{'tag_name': 'v0.8.2.2', 'draft': False, 'prerelease': False, 'published_at': '2026-11-01T00:00:00Z',
                     'assets': [{'name': 'Steamy-v0.8.2.2.zip', 'id': 22}]}]

        self.assertIsNone(previous_release(api, 'v0.8.2.2'))


if __name__ == '__main__':
    unittest.main()
