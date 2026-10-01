import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


REPOSITORY = Path(__file__).resolve().parents[2]
SCRIPT = REPOSITORY / "deploy" / "deploy.sh"
COMMIT = "a" * 40


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "server"
        self.release = Path(self.temp.name) / "release"
        self.bin = Path(self.temp.name) / "bin"
        self.root.mkdir()
        self.release.mkdir()
        self.bin.mkdir()
        (self.release / "deploy").mkdir()
        shutil.copy(SCRIPT, self.release / "deploy" / "deploy.sh")
        (self.release / "compose.yaml").write_text("services: {}\n")
        (self.root / ".env").write_text("ENVIRONMENT=Production\n")
        (self.root / "appsettings.Production.json").write_text(
            json.dumps({"Jwt": {"Key": "test-jwt-key"}, "AdminAuth": {"Key": "test-admin-key"}})
        )
        self.uploads = self.root / "data" / "uploads"
        self.sourced = self.root / "data" / "sourced"
        self.uploads.mkdir(parents=True)
        self.sourced.mkdir(parents=True)
        (self.uploads / "keep.jpg").write_text("uploaded")
        (self.sourced / "keep.jpg").write_text("sourced")
        self.config = {
            "services": {
                "app": {
                    "environment": {"ASPNETCORE_ENVIRONMENT": "Production", "Smtp__Host": "smtp.example.com"},
                    "volumes": [
                        {"target": "/app/appsettings.Production.json", "source": str(self.root / "appsettings.Production.json")},
                        {"target": "/app/wwwroot/media/uploads", "source": str(self.uploads)},
                        {"target": "/app/wwwroot/media/sourced", "source": str(self.sourced)},
                    ],
                },
                "mongo": {"volumes": [{"source": "mongo-data"}]},
                "redis": {"volumes": [{"source": "redis-data"}]},
            },
            "volumes": {
                "mongo-data": {"name": "quesshi-existing-mongo"},
                "redis-data": {"name": "quesshi-existing-redis"},
            },
        }
        self.config_file = Path(self.temp.name) / "compose-config.json"
        self.write_config()
        self.log = Path(self.temp.name) / "docker.log"
        docker = self.bin / "docker"
        docker.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "printf '%s TAG=%s\\n' \"$*\" \"${TAG:-}\" >> \"$FAKE_DOCKER_LOG\"\n"
            "case \"$*\" in\n"
            "  *'config --format json'*) cat \"$FAKE_COMPOSE_CONFIG\" ;;\n"
            "  'volume inspect '*) exit 0 ;;\n"
            "  'inspect --format {{.Image}} quesshi-app') echo sha256:previous ;;\n"
            "  'inspect --format {{.State.Health.Status}} quesshi-app') if [[ \"${TAG:-}\" == rollback-* ]]; then echo healthy; else echo \"${FAKE_HEALTH_STATUS:-healthy}\"; fi ;;\n"
            "  *' build app') [[ \"${FAKE_BUILD_FAILURE:-0}\" != 1 ]] ;;\n"
            "  *' up -d --no-deps app'*) [[ \"${TAG:-}\" == rollback-* || \"${FAKE_SWITCH_FAILURE:-0}\" != 1 ]] ;;\n"
            "  *) exit 0 ;;\n"
            "esac\n"
        )
        docker.chmod(0o755)

    def write_config(self):
        self.config_file.write_text(json.dumps(self.config))

    def run_deploy(self, run_number="10", run_attempt="1", **extra_env):
        env = os.environ.copy()
        env.update(
            {
                "PATH": f"{self.bin}:{env['PATH']}",
                "QUESSHI_DEPLOY_ROOT": str(self.root),
                "QUESSHI_HEALTH_TIMEOUT_SECONDS": "0",
                "QUESSHI_HEALTH_POLL_SECONDS": "1",
                "FAKE_DOCKER_LOG": str(self.log),
                "FAKE_COMPOSE_CONFIG": str(self.config_file),
                **extra_env,
            }
        )
        return subprocess.run(
            ["bash", str(self.release / "deploy" / "deploy.sh"), COMMIT, run_number, run_attempt],
            text=True,
            capture_output=True,
            env=env,
            check=False,
        )

    def calls(self):
        return self.log.read_text().splitlines() if self.log.exists() else []

    def test_success_builds_and_switches_only_app_after_persistent_preflight(self):
        result = self.run_deploy()

        self.assertEqual(0, result.returncode, result.stderr)
        state = json.loads((self.root / ".deployment-state").read_text())
        self.assertEqual(COMMIT, state["commit"])
        self.assertIsNone(state["previous_commit"])
        calls = self.calls()
        self.assertTrue(any("volume inspect quesshi-existing-mongo" in call for call in calls))
        self.assertTrue(any("volume inspect quesshi-existing-redis" in call for call in calls))
        self.assertTrue(any(f"TAG={COMMIT}" in call and "build app" in call for call in calls))
        self.assertTrue(any(f"TAG={COMMIT}" in call and "up -d --no-deps app" in call for call in calls))
        self.assertFalse(any(" up -d " in call and "mongo" in call for call in calls))
        self.assertFalse(any(" down " in call or "volume rm" in call for call in calls))
        self.assertEqual("uploaded", (self.uploads / "keep.jpg").read_text())
        self.assertEqual("sourced", (self.sourced / "keep.jpg").read_text())
        self.assertTrue((self.root / ".env").is_file())
        self.assertTrue((self.root / "appsettings.Production.json").is_file())

    def test_missing_persistent_path_fails_before_build(self):
        shutil.rmtree(self.uploads)

        result = self.run_deploy()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("uploaded media directory is missing", result.stderr)
        self.assertFalse(any("build app" in call for call in self.calls()))

    def test_missing_required_auth_setting_fails_before_build(self):
        (self.root / "appsettings.Production.json").write_text(json.dumps({"Jwt": {}, "AdminAuth": {"Key": "key"}}))

        result = self.run_deploy()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("Jwt.Key and AdminAuth.Key", result.stderr)
        self.assertFalse(any("build app" in call for call in self.calls()))

    def test_failed_build_does_not_switch_running_app(self):
        result = self.run_deploy(FAKE_BUILD_FAILURE="1")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("image build failed", result.stderr)
        self.assertFalse(any("up -d --no-deps app" in call for call in self.calls()))

    def test_failed_switch_restores_previous_image(self):
        result = self.run_deploy(FAKE_SWITCH_FAILURE="1")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("Application switch failed", result.stderr)
        switches = [call for call in self.calls() if "up -d --no-deps app" in call]
        self.assertEqual(2, len(switches))
        self.assertIn(f"TAG=rollback-{COMMIT}", switches[-1])

    def test_failed_health_check_restores_previous_image_and_keeps_state(self):
        previous_state = {"commit": "b" * 40, "run_number": 9, "run_attempt": 1}
        (self.root / ".deployment-state").write_text(json.dumps(previous_state))

        result = self.run_deploy(FAKE_HEALTH_STATUS="unhealthy")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("did not pass its /health", result.stderr)
        state = json.loads((self.root / ".deployment-state").read_text())
        self.assertEqual(previous_state["commit"], state["commit"])
        switches = [call for call in self.calls() if "up -d --no-deps app" in call]
        self.assertEqual(2, len(switches))
        self.assertIn(f"TAG=rollback-{COMMIT}", switches[-1])

    def test_older_delayed_run_cannot_replace_newer_release(self):
        newer_state = {"commit": "b" * 40, "run_number": 11, "run_attempt": 1}
        (self.root / ".deployment-state").write_text(json.dumps(newer_state))

        result = self.run_deploy(run_number="10")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("stale run", result.stderr)
        self.assertFalse(any("build app" in call for call in self.calls()))


if __name__ == "__main__":
    unittest.main()
