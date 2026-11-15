import json
from pathlib import Path
import tempfile
import unittest
from story_library import StoryLibrary, LibraryError


class StoryLibraryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.library = StoryLibrary(self.root)

    def movie(self, owner="a" * 32, job="b" * 32, status="ready", date="2026-11-15T12:00:00+00:00"):
        path = self.root / "create-world" / "sessions" / owner / "videos" / job
        path.mkdir(parents=True)
        (path / "story.mp4").write_bytes((24).to_bytes(4, "big") + b"ftypisom" + bytes(12))
        record = dict(workspaceId="demo", experienceSessionId=owner, sessionId="c" * 32,
                      jobId=job, status=status, createdAtUtc=date, videoPath="/story-videos/" + job + ".mp4",
                      storyArchive="d" * 64 + ".json", shots=[{"duration": 8}, {"duration": 6}])
        (path / "job.json").write_text(json.dumps(record))
        requests = path.parents[1] / "requests"
        requests.mkdir(exist_ok=True)
        (requests / record["storyArchive"]).write_text(json.dumps(dict(
            workspaceId="demo", experienceSessionId=owner, sessionId="c" * 32,
            operation="story", status="completed", httpStatus=200,
            input={"context": {"words": [{"word": "CAT"}, {"word": "MOON."}]}})))
        return path

    def test_ready_library_survives_restart_and_preserves_original_movie(self):
        path = self.movie()
        self.movie(owner="e" * 32, job="f" * 32, date="2026-11-16T12:00:00+00:00")
        result = self.library.listing()
        self.assertEqual([x["id"] for x in result["stories"]], ["f" * 32, "b" * 32])
        self.assertEqual(result["stories"][1]["title"], "CAT + MOON")
        self.assertEqual(result["stories"][1]["durationSeconds"], 14)
        self.assertTrue(self.library.index.is_file())
        self.assertEqual(StoryLibrary(self.root).listing(), result)
        self.assertEqual(self.library.video_file("/story-videos/" + "b" * 32 + ".mp4"), (path / "story.mp4").resolve())

    def test_pending_missing_and_corrupt_movies_are_not_playable(self):
        path = self.movie(status="running")
        self.assertEqual(self.library.listing()["stories"], [])
        record = json.loads((path / "job.json").read_text())
        record["status"] = "ready"
        (path / "job.json").write_text(json.dumps(record))
        (path / "story.mp4").write_bytes(b"not a movie")
        self.assertEqual(self.library.listing()["stories"], [])
        with self.assertRaises(LibraryError): self.library.video_file("/story-videos/" + "b" * 32 + ".mp4")

    def test_symlink_and_identity_mismatch_are_excluded(self):
        path = self.movie()
        outside = self.root / "outside.mp4"
        (path / "story.mp4").rename(outside)
        (path / "story.mp4").symlink_to(outside)
        self.assertEqual(self.library.listing()["stories"], [])
        (path / "story.mp4").unlink()
        outside.rename((path / "story.mp4").resolve())
        record = json.loads((path / "job.json").read_text()); record["experienceSessionId"] = "e" * 32
        (path / "job.json").write_text(json.dumps(record))
        self.assertEqual(self.library.listing()["stories"], [])
        with self.assertRaises(LibraryError): self.library.video_file("/story-videos/../secret.mp4")


if __name__ == "__main__": unittest.main()
