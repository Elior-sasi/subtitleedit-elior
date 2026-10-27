import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import auto_upstream as updater

class AutoUpstreamTests(unittest.TestCase):
    def test_newest_published_beta_or_rc_includes_older_pages(self):
        releases = [dict(tag_name='v5.4.0', published_at='2026-01-01', draft=False), dict(tag_name='v5.5.0-rc2', published_at='2026-10-27', draft=False), dict(tag_name='v5.6.0-beta1', published_at='2026-10-28', draft=True), dict(tag_name='untrusted-tag', published_at='2026-10-29', draft=False)]
        self.assertEqual(updater.select_release(releases)['tag_name'], 'v5.5.0-rc2')

    def test_prepare_changes_only_installer_and_notes(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)/'tools/subtitleedit-rtl-fixed'
            (base/'installer').mkdir(parents=True)
            (base/'release-notes.json').write_text(json.dumps(dict(upstream='v5.4.0-beta8', changes=['feature'])))
            for name in ['SubtitleEditElior.iss', 'test-installer.ps1']:
                (base/'installer'/name).write_text('5.4.0-beta8 5.4.0.8')
            feature = base/'font.cs'
            feature.write_text('FontSize = 16;')
            updater.prepare(temp, 'v5.5.0-rc2')
            self.assertEqual(feature.read_text(), 'FontSize = 16;')
            self.assertEqual((base/'installer/SubtitleEditElior.iss').read_text(encoding='utf-8-sig'), '5.5.0-rc2 5.5.0.2')
            self.assertEqual(json.loads((base/'release-notes.json').read_text())['upstream'], 'v5.5.0-rc2')
            with self.assertRaises(ValueError): updater.prepare(temp, 'v5.0; injected')

    def test_resolver_skips_only_same_custom_commit_and_official_release(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)/'outputs'
            env = dict(GITHUB_OUTPUT=str(output), GITHUB_REPOSITORY='Elior-sasi/subtitleedit-elior', GITHUB_SHA='a'*40)
            replies = [[dict(tag_name='v5.4.0-beta8', published_at='2026-10-27', draft=False)], dict(sha='b'*40), [dict(tag_name='custom-build-123-1', draft=False, target_commitish='a'*40, body='Custom Windows build based on v5.4.0-beta8.')]]
            with patch.dict(os.environ, env), patch.object(updater, 'api', side_effect=replies): updater.resolve()
            self.assertIn('build=false', output.read_text())
            self.assertIn('upstream_sha='+'b'*40, output.read_text())

if __name__ == '__main__': unittest.main()
