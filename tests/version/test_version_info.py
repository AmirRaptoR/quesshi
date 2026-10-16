from pathlib import Path
import subprocess
import unittest


REPOSITORY = Path(__file__).resolve().parents[2]
SCRIPT = REPOSITORY / "scripts" / "version-info.sh"
TAG_COMMIT = "8fb1d5cccfe03c03cb3ee7ebe6a985a93e2f29c6"


def version_info(revision):
    result = subprocess.run(
        ["bash", str(SCRIPT), revision],
        cwd=REPOSITORY,
        text=True,
        capture_output=True,
        check=True,
    )
    return dict(line.split("=", 1) for line in result.stdout.splitlines())


class VersionInfoTests(unittest.TestCase):
    def test_release_tag_reports_its_semantic_version(self):
        self.assertEqual("0.1.0", version_info("v0.1.0")["QUESSHI_VERSION"])

    def test_release_tag_remains_anchored_to_the_expected_commit(self):
        self.assertEqual(TAG_COMMIT, version_info("v0.1.0")["QUESSHI_COMMIT"])

    def test_untagged_commit_reports_distance_and_short_sha(self):
        expected = subprocess.run(
            ["git", "describe", "--tags", "--match", "v[0-9]*.[0-9]*.[0-9]*", "--long", "HEAD"],
            cwd=REPOSITORY,
            text=True,
            capture_output=True,
            check=True,
        ).stdout.strip()
        tag, count, short_sha = expected.rsplit("-", 2)

        self.assertEqual(f"{tag[1:]}+{count}.{short_sha[1:]}", version_info("HEAD")["QUESSHI_VERSION"])


if __name__ == "__main__":
    unittest.main()
