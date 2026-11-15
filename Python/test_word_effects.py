"""Offline checks for the quiet-v3 regression; no provider calls or credentials."""
from array import array
import io
import json
import math
import sys
import unittest
from unittest.mock import patch

import live_backend as lb
import word_effects as effects


def tone(amplitude, seconds=1):
    samples = array("h", (round(amplitude * math.sin(2 * math.pi * 440 * i / 24000))
                          for i in range(int(seconds * 24000))))
    if sys.byteorder != "little": samples.byteswap()
    return samples.tobytes()


class WordEffectsTests(unittest.TestCase):
    def test_quiet_pig_is_made_audible_without_clipping(self):
        pcm, metrics = effects.level_pcm(tone(300), True)
        self.assertLessEqual(metrics["gainDb"], 30)
        self.assertGreater(metrics["outputRmsDbfs"], -25)
        self.assertLessEqual(metrics["outputPeakDbfs"], -3)
        self.assertEqual(len(pcm), 48000)

    def test_loud_effect_is_limited_and_silence_is_not_amplified(self):
        _, metrics = effects.level_pcm(tone(32760), True)
        self.assertLessEqual(metrics["outputPeakDbfs"], -3)
        self.assertLess(metrics["gainDb"], 0)
        with self.assertRaises(effects.TooQuietError):
            effects.level_pcm(bytes(48000), True)

    def test_intentionally_quiet_subject_never_gets_boosted(self):
        quiet = tone(40)
        result, metrics = effects.level_pcm(quiet, False)
        self.assertEqual(quiet, result)
        self.assertEqual(metrics["gainDb"], 0)
        _, louder = effects.level_pcm(tone(20000), False)
        self.assertLessEqual(louder["outputRmsDbfs"], -41.9)

    def test_common_sources_are_concrete_and_unknown_plans_are_cached(self):
        for word, expected in (("CAT", "meowing"), ("PIG", "oinks"), ("PIANO.", "piano notes")):
            prompt = effects.effect_prompt(effects.known_plan(word))
            self.assertIn(expected, prompt)
            self.assertNotIn("very quiet", prompt)
            self.assertNotIn("non-vocal hum", prompt)
        self.assertFalse(effects.known_plan("MOON")["audible"])
        calls = []
        provider = lb.ElevenLabsEffects("unused-test-key", plan_provider=lambda word: (
            calls.append(word) or {"audible": True, "description": "A rooster crowing"}))
        self.assertTrue(provider.plan("ROOSTER")["audible"])
        self.assertTrue(provider.plan("ROOSTER.")["audible"])
        self.assertEqual(calls, ["ROOSTER"])

    def test_provider_sends_word_description_and_levels_returned_wav(self):
        requests = []
        def upstream(request, timeout):
            requests.append(json.loads(request.data))
            return io.BytesIO(lb.pcm_wav(tone(300, 3)))
        provider = lb.ElevenLabsEffects("unused-test-key")
        with patch.object(lb, "urlopen", upstream):
            output = provider.generate("PIG", "A private spoken answer that must not be sent")
        self.assertEqual(requests[0]["model_id"], "eleven_text_to_sound_v3")
        self.assertIn("oinks", requests[0]["text"])
        self.assertNotIn("private", requests[0]["text"])
        self.assertNotIn("quiet non-vocal", requests[0]["text"])
        self.assertTrue(output.startswith(b"RIFF"))
        self.assertGreater(provider.last_audio_metrics["outputRmsDbfs"], -25)


if __name__ == "__main__":
    unittest.main()
