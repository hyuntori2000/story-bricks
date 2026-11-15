"""Guided dialogue contract tests with fake providers and temporary storage only."""
import copy
from contextlib import redirect_stdout
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import guided_dialogue as guided
import live_backend as lb
from test_live_backend import FakeUpstream, wav_bytes


SENTENCES = [
    "Judy wondered how to visit the moon.",
    "She imagined its surface as soft and cold bread dough.",
    "A friendly wind carried a bright kite toward her window.",
    "Judy climbed aboard and held its ribbons tightly.",
    "The kite sailed gently until her feet touched the dough moon.",
    "Its quiet squeak made Judy laugh with delight.",
    "She returned home with a little moon song in her heart.",
]


def context(words=(), event="start", finish=False):
    records = [{"index": index, "word": word, "groupId": "group-" + str(index),
                "componentIds": ["component-" + str(index)], "finishMarker": word.endswith(".")}
               for index, word in enumerate(words)]
    return {"schemaVersion": 2, "dialogueEvent": event, "turnId": "f" * 32,
            "finishRequested": finish, "currentWord": records[-1]["word"] if records else None,
            "words": records, "turns": []}


def exchange(value, index, kind, answer=None, question="What will happen next?"):
    word = value["words"][index]
    value["turns"].append(dict(wordIndex=index, word=word["word"], groupId=word["groupId"],
                              componentIds=word["componentIds"], inputType=kind,
                              turnId=format(len(value["turns"]) + 1, "032x"), question=question,
                              answer=word["word"] if answer is None else answer))
    return value


def envelope(value, request="request-1", workspace="demo"):
    return json.dumps({"workspaceId": workspace, "experienceSessionId": "a" * 32,
                       "sessionId": "conversation", "requestId": request, "context": value}).encode()


class GuidedUpstream(FakeUpstream):
    def __init__(self):
        super().__init__()
        self.output = {"question": "What is Judy wondering about tonight?", "nextInput": "speak_answer"}

    def generate_structured(self, operation, instructions, value):
        self.calls.append((operation, copy.deepcopy(value)))
        return copy.deepcopy(self.output)


class GuidedBackendTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.output = io.StringIO()
        self.quiet = redirect_stdout(self.output)
        self.quiet.__enter__()
        self.addCleanup(self.quiet.__exit__, None, None, None)
        self.upstream = GuidedUpstream()
        self.prompts = {key: key for key in ("question", "story", "dialogue", "guided_story")}
        self.backend = self.make_backend(self.upstream)

    def make_backend(self, upstream):
        return lb.Backend(upstream, Path(self.temporary.name) / "audio", self.prompts)

    def dispatch(self, value, route="/dialogue", request="request-1"):
        return self.backend.dispatch(route, envelope(value, request), "application/json")

    def test_intro_is_speech_only_with_owner_turn_and_durable_replay(self):
        value = context()
        status, result = self.dispatch(value)
        self.assertEqual(status, 200)
        self.assertEqual(self.upstream.calls, [("speech", guided.INTRO)])
        self.assertEqual((result["text"], result["nextInput"], result["turnId"]),
                         (guided.INTRO, "build_word", value["turnId"]))
        self.assertEqual((result["workspaceId"], result["experienceSessionId"]), ("demo", "a" * 32))
        replacement = GuidedUpstream()
        restarted = self.make_backend(replacement)
        self.assertEqual(restarted.dispatch("/dialogue", envelope(value), "application/json"), (status, result))
        self.assertEqual(replacement.calls, [])
        self.assertEqual(restarted.audio.read(result["audioPath"]), wav_bytes())
        self.assertTrue(restarted.health()["guidedDialogueEnabled"])
        self.assertEqual(restarted.health()["storySentenceCount"], 7)

    def test_word_events_acknowledge_once_and_select_required_input(self):
        cases = [(("JUDY",), False, "speak_answer"), (("JUDY", "MOON"), False, "build_word"),
                 (("JUDY.",), False, "speak_answer"), (("JUDY",), True, "speak_answer")]
        for number, (words, marker, expected) in enumerate(cases):
            value = context(words, "word")
            if marker:
                value["words"][-1]["finishMarker"] = True
            closing = expected == "build_word"
            phrase = "Your adventure can now reach the moon." if closing else "What is this adventure like?"
            self.upstream.output = {"question": phrase, "nextInput": expected}
            status, result = self.dispatch(value, request="word-" + str(number))
            self.assertEqual(status, 200)
            name = words[-1].rstrip(".").capitalize()
            directive = "Keep building another word, or press Create Story to make your story." if closing else "Say the sentence."
            spelling = ", ".join(c for c in words[-1].upper() if "A" <= c <= "Z") + ". "
            self.assertEqual(result["text"], spelling + name + "! " + phrase + " " + directive)
            self.assertEqual(result["spokenText"], result["text"])
            self.assertEqual(result["question"], phrase)
            self.assertEqual(result["nextInput"], expected)

    def test_first_word_requires_conversation_before_a_related_build_invitation(self):
        value = exchange(context(("CAT",), "word"), 0, "build_word",
                         question="Who is your story about?")
        self.upstream.output = {"question": "Where will your cat go?", "nextInput": "build_word"}
        status, result = self.dispatch(value, request="early-build")
        self.assertEqual((status, result["code"]), (502, "invalid_dialogue_input"))
        self.assertFalse(any(call[0] == "speech" for call in self.upstream.calls))

        self.upstream.output = {"question": "What does your cat want to do today?", "nextInput": "speak_answer"}
        status, result = self.dispatch(value, request="cat-question")
        self.assertEqual(status, 200)
        self.assertEqual(result["spokenText"],
                         "C, A, T. Cat! What does your cat want to do today? Say the sentence.")
        self.assertNotIn("Build", result["spokenText"])

        value["dialogueEvent"] = "answer"
        exchange(value, 0, "speak_answer", "She wants to visit the moon.",
                 question=result["question"])
        self.upstream.output = {"question": "What could help your cat reach the moon?", "nextInput": "build_word"}
        status, result = self.dispatch(value, request="related-build")
        self.assertEqual(status, 200)
        self.assertEqual(result["spokenText"],
                         "What could help your cat reach the moon? Build a word for that idea.")
        self.assertEqual(self.upstream.calls[-2][1]["turns"][-1]["answer"],
                         "She wants to visit the moon.")

    def test_spelling_preserves_letters_and_excludes_finish_markers(self):
        for word, prefix in [("MOON.", "M, O, O, N. Moon! "), ("a", "A. A! "),
                             ("zook", "Z, O, O, K. Zook! "),
                             ("O" * 32, ", ".join(["O"] * 32) + ". " + ("O" * 32).capitalize() + "! ")]:
            value = context((word,), "word")
            guided.validate_context(value, "dialogue")
            reply = guided.dialogue_reply({"question": "Where will it go?", "nextInput": guided.allowed_next_inputs(value)[0]}, value)
            self.assertEqual(reply["spokenText"], prefix + "Where will it go? Say the sentence.")
            self.assertEqual(value["currentWord"], word)
        value = context(("MOON", "MOON"), "answer")
        exchange(value, 1, "speak_answer", "It feels soft.")
        reply = guided.dialogue_reply({"question": "What could help it travel?", "nextInput": "build_word"}, value)
        self.assertEqual(reply["spokenText"], "What could help it travel? Build a word for that idea.")

    def test_one_answer_always_invites_related_word_without_a_spoken_followup(self):
        value = context(("CAT",), "answer")
        exchange(value, 0, "speak_answer", "She wants to visit the moon.")
        self.assertEqual(guided.allowed_next_inputs(value), ["build_word"])
        self.upstream.output = {"question": "What does your cat sound like?", "nextInput": "speak_answer"}
        status, failure = self.dispatch(value, request="extra-question")
        self.assertEqual((status, failure["code"]), (502, "invalid_dialogue_input"))
        self.assertFalse(any(call[0] == "speech" for call in self.upstream.calls))
        self.upstream.output = {"question": "What could carry your cat there?", "nextInput": "build_word"}
        self.assertEqual(self.dispatch(value, request="next-word")[0], 200)

    def test_second_word_closes_without_auto_finish_and_third_reopens_question(self):
        value = context(("CAT", "ROCKET"), "word")
        exchange(value, 0, "speak_answer", "She wants to visit the moon.")
        self.assertEqual(guided.dialogue_phase(value), "word_closing")
        self.upstream.output = {"question": "Your cat is ready for a moon adventure.", "nextInput": "build_word"}
        status, result = self.dispatch(value)
        self.assertEqual(status, 200)
        self.assertEqual(result["spokenText"], "R, O, C, K, E, T. Rocket! Your cat is ready for a moon adventure. Keep building another word, or press Create Story to make your story.")
        self.assertFalse(value["finishRequested"])
        self.upstream.output = {"question": "Where does the rocket go?", "nextInput": "build_word"}
        self.assertEqual(self.dispatch(value, request="extra-second-question")[1]["code"], "invalid_dialogue_question")
        third = context(("CAT", "ROCKET", "RAIN"), "word")
        self.assertEqual(guided.allowed_next_inputs(third), ["speak_answer"])

    def test_period_requires_one_final_question_even_after_a_saved_answer(self):
        value = context(("CAT",), "finish", finish=True)
        exchange(value, 0, "speak_answer", "She wants to fly.")
        self.assertIs(guided.validate_context(value, "dialogue"), value)
        self.assertEqual(guided.dialogue_phase(value), "final_question")
        self.assertEqual(guided.allowed_next_inputs(value), ["speak_answer"])
        self.upstream.output = {"question": "What does your cat discover at the end?", "nextInput": "speak_answer"}
        status, result = self.dispatch(value)
        self.assertEqual(status, 200)
        self.assertEqual(result["spokenText"], "What does your cat discover at the end? Say the sentence.")
        self.assertNotIn("C, A, T", result["spokenText"])
        self.assertEqual(self.upstream.calls[0][1]["dialogueEvent"], "finish")
        value["finishRequested"] = False
        with self.assertRaises(guided.GuidedError): guided.validate_context(value, "dialogue")
        marked = context(("CAT", "ROCKET."), "word", finish=True)
        self.assertEqual(guided.dialogue_phase(marked), "final_question")
        self.assertEqual(guided.allowed_next_inputs(marked), ["speak_answer"])


    def test_saved_exchange_types_and_repeated_word_roles_are_preserved(self):
        value = context(("MOON", "MOON"), "answer")
        exchange(value, 0, "build_word", question="Who is your story about?")
        exchange(value, 1, "build_word", question="Where does Moon travel?")
        exchange(value, 1, "speak_answer", "I float on warm dough.")
        exchange(value, 1, "speak_answer", "It gently hums.")
        self.assertIs(lb.validate_context(value, "dialogue"), value)
        projected = lb.narrative_context(value)
        self.assertEqual([turn["inputType"] for turn in projected["turns"]],
                         ["build_word", "build_word", "speak_answer", "speak_answer"])
        self.assertEqual(projected["turns"][1]["question"], "Where does Moon travel?")
        self.assertEqual(projected["turns"][2]["answer"], "I float on warm dough.")
        self.assertEqual(projected["allowedNextInputs"], ["build_word"])
        self.assertNotIn("componentIds", projected["turns"][0])

    def test_queued_words_and_manual_finish_never_require_fabricated_turns(self):
        value = context(("JUDY", "MOON", "KITE"), "word", finish=True)
        exchange(value, 0, "build_word", question="Who is your story about?")
        exchange(value, 1, "speak_answer", "A soft moon.")
        self.assertIs(lb.validate_context(value, "dialogue"), value)
        self.assertIs(lb.validate_context(value, "story"), value)
        self.assertEqual(guided.allowed_next_inputs(value), ["speak_answer"])
        value.update(dialogueEvent="", turnId="", finishRequested=False, completionMode="manual_saved_progress")
        self.upstream.output = {"sentences": SENTENCES}
        status, result = self.dispatch(value, route="/story")
        self.assertEqual(status, 200)
        self.assertEqual(len(self.upstream.calls[0][1]["turns"]), 2)
        self.assertEqual(result["text"], " ".join(SENTENCES))
        self.assertEqual(result["spokenText"], guided.STORY_ANNOUNCEMENT + " " + result["text"])
        self.assertEqual(self.upstream.calls[-1], ("speech", result["spokenText"]))
        self.assertNotIn("Listens", result["spokenText"])
        self.assertEqual(result["spokenText"].count(guided.STORY_ANNOUNCEMENT), 1)

    def test_unanswered_first_word_can_finish_without_changing_physical_marker(self):
        value = context(("JUDY",), "", finish=True)
        value.pop("turnId")
        self.assertIs(lb.validate_context(value, "story"), value)
        self.assertEqual(value["turns"], [])
        self.assertFalse(value["words"][0]["finishMarker"])

    def test_bad_identity_or_context_fails_before_provider(self):
        base = exchange(context(("JUDY",), "word"), 0, "build_word")
        mutations = [lambda v: v.update(turnId="bad"), lambda v: v.update(dialogueEvent="other"),
                     lambda v: v.update(completionMode="manual_saved_progress"),
                     lambda v: v["turns"][0].update(groupId="another"),
                     lambda v: v["turns"][0].update(componentIds=["another"]),
                     lambda v: v["turns"][0].update(answer="MOON"),
                     lambda v: v["turns"].append(copy.deepcopy(v["turns"][0]))]
        for mutate in mutations:
            value = copy.deepcopy(base)
            mutate(value)
            with self.assertRaises(lb.BackendError):
                self.dispatch(value)
        self.assertEqual(self.upstream.calls, [])

    def test_invalid_generated_content_is_not_spoken_or_automatically_retried(self):
        for index, question in enumerate(("Judy! Where next?", "Where next? Build the word.", "Where next? Why?")):
            self.upstream.output = {"question": question, "nextInput": "build_word"}
            status, failure = self.dispatch(context(("JUDY",), "word"), request="bad-question-" + str(index))
            self.assertEqual((status, failure["code"]), (502, "invalid_dialogue_question"))
        for index, sentences in enumerate((SENTENCES[:6], SENTENCES + ["She smiled."],
                                           ["She smiled. She waved."] + SENTENCES[1:],
                                           ["Listens."] + SENTENCES[1:])):
            self.upstream.output = {"sentences": sentences}
            status, failure = self.dispatch(context(("JUDY",), "", finish=True),
                                            route="/story", request="bad-story-" + str(index))
            self.assertEqual((status, failure["code"]), (502, "invalid_story_sentences"))
        self.assertEqual(len(self.upstream.calls), 7)
        self.assertFalse(any(call[0] == "speech" for call in self.upstream.calls))

    def test_schema_requests_use_strict_responses_shape_and_handle_refusal(self):
        upstream = lb.OpenAIUpstream(lb.Settings("fake-test-key"))
        value = context(("JUDY",), "word")
        output = {"question": "Where will Judy go?", "nextInput": "speak_answer"}
        response = {"status": "completed", "output": [{"type": "message", "content": [
            {"type": "output_text", "text": json.dumps(output)}]}]}
        with patch.object(upstream, "_http", return_value=json.dumps(response).encode()) as http:
            self.assertEqual(upstream.generate_structured("dialogue", "instruction", value), output)
        payload = json.loads(http.call_args.args[1])
        self.assertEqual(http.call_count, 1)
        shape = payload["text"]["format"]
        self.assertEqual((shape["type"], shape["strict"]), ("json_schema", True))
        self.assertEqual(shape["schema"]["properties"]["nextInput"]["enum"], ["speak_answer"])
        self.assertFalse(payload["store"])
        story_array = guided.response_schema("story", value)["properties"]["sentences"]
        self.assertEqual((story_array["minItems"], story_array["maxItems"]), (7, 7))
        for response, expected in (({"status": "incomplete"}, "text_incomplete"),
                                   ({"status": "completed", "output": [{"type": "message", "content": [
                                       {"type": "refusal", "refusal": "No"}]}]}, "text_refusal")):
            with patch.object(upstream, "_http", return_value=json.dumps(response).encode()) as http:
                with self.assertRaises(lb.BackendError) as raised:
                    upstream.generate_structured("dialogue", "instruction", value)
                self.assertEqual(raised.exception.code, expected)
                self.assertEqual(http.call_count, 1)


if __name__ == "__main__":
    unittest.main()
