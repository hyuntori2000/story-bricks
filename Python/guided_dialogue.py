"""Version-two dialogue contracts and deterministic spoken envelopes.

No inference, transport, or provider retries live in this module.
"""
from __future__ import annotations

import re


INTRO = "Who is your story about? Build the word, and capture."
STORY_ANNOUNCEMENT = "That’s awesome! Your story is ready. Here it comes!"
INPUT_TYPES = ("build_word", "speak_answer")
MANUAL_COMPLETION_MODE = "manual_saved_progress"
UUID_N = re.compile(r"^[0-9a-f]{32}$")
IDENTIFIER = re.compile(r"^[A-Za-z0-9_.:-]{1,96}$")


class GuidedError(Exception):
    def __init__(self, code, message, status=400):
        super().__init__(message)
        self.code, self.message, self.status = code, message, status


def text(value, label, maximum):
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise GuidedError("invalid_context", label + " is missing or too long.")
    return value.strip()


def identifier(value, label):
    if not isinstance(value, str) or not IDENTIFIER.fullmatch(value):
        raise GuidedError("invalid_context", label + " is not a valid identifier.")


def validate_context(context, operation):
    if not isinstance(context, dict) or type(context.get("schemaVersion")) is not int or context["schemaVersion"] != 2:
        raise GuidedError("invalid_context", "Guided dialogue requires context schemaVersion 2.")
    if operation not in ("dialogue", "story"):
        raise GuidedError("invalid_context", "Use /dialogue for version-two questions.")
    turn_id = context.get("turnId")
    if ((operation == "dialogue" or turn_id not in (None, "")) and
            (not isinstance(turn_id, str) or not UUID_N.fullmatch(turn_id))):
        raise GuidedError("invalid_turn_id", "turnId must identify the new assistant turn with 32 lowercase hex characters.")
    if type(context.get("finishRequested")) is not bool:
        raise GuidedError("invalid_context", "finishRequested must be a boolean.")
    mode = context.get("completionMode")
    if mode not in (None, "", MANUAL_COMPLETION_MODE) or (mode == MANUAL_COMPLETION_MODE and operation != "story"):
        raise GuidedError("invalid_completion_mode", "Manual completion is available only for story generation.")
    words, turns = context.get("words"), context.get("turns")
    if not isinstance(words, list) or not isinstance(turns, list):
        raise GuidedError("invalid_context", "Provide ordered words and dialogue exchanges.")
    event = context.get("dialogueEvent")
    if operation == "dialogue" and event not in ("start", "word", "answer"):
        raise GuidedError("invalid_dialogue_event", "dialogueEvent must be start, word or answer.")
    if operation == "dialogue" and event == "start":
        if words or turns or context.get("currentWord") not in (None, "") or context["finishRequested"]:
            raise GuidedError("invalid_context", "The opening prompt requires an empty new conversation.")
        return context
    if not words:
        raise GuidedError("invalid_context", "At least one accepted word is required.")
    groups = set()
    for index, word in enumerate(words):
        if not isinstance(word, dict) or type(word.get("index")) is not int or word["index"] != index:
            raise GuidedError("invalid_context", "Words must retain ordered zero-based indices.")
        value = text(word.get("word"), "word", 32)
        if not re.fullmatch(r"[A-Za-z.]+", value) or not re.search(r"[A-Za-z]", value):
            raise GuidedError("invalid_context", "A word must contain letters; a standalone period is a finish signal.")
        identifier(word.get("groupId"), "groupId")
        if word["groupId"] in groups:
            raise GuidedError("invalid_context", "Each word capture needs its own groupId.")
        groups.add(word["groupId"])
        ids = word.get("componentIds")
        if not isinstance(ids, list) or not 1 <= len(ids) <= 40:
            raise GuidedError("invalid_context", "Each word needs component identities.")
        for component in ids:
            identifier(component, "componentId")
        if len(ids) != len(set(ids)):
            raise GuidedError("invalid_context", "Component identities within a word must be distinct.")
        if type(word.get("finishMarker", False)) is not bool:
            raise GuidedError("invalid_context", "Word finishMarker must be a boolean.")
    if context.get("currentWord") != words[-1]["word"]:
        raise GuidedError("invalid_context", "currentWord must match the final accepted word.")
    turn_ids, previous_index = set(), -1
    builds, answers = [0] * len(words), [0] * len(words)
    for turn in turns:
        if not isinstance(turn, dict):
            raise GuidedError("invalid_context", "Dialogue exchanges must be objects.")
        turn_id, index, kind = turn.get("turnId"), turn.get("wordIndex"), turn.get("inputType")
        if not isinstance(turn_id, str) or not UUID_N.fullmatch(turn_id) or turn_id in turn_ids or turn_id == context.get("turnId"):
            raise GuidedError("invalid_turn_id", "Saved exchanges need distinct prior assistant turn identities.")
        turn_ids.add(turn_id)
        if type(index) is not int or not 0 <= index < len(words) or index < previous_index or kind not in INPUT_TYPES:
            raise GuidedError("answer_order", "Dialogue exchanges must follow their accepted word order and input type.")
        word = words[index]
        if any(turn.get(key) != word.get(key) for key in ("word", "groupId", "componentIds")):
            raise GuidedError("invalid_context", "A dialogue exchange lost its word or component identity link.")
        text(turn.get("question"), "question", 1000)
        answer = text(turn.get("answer"), "answer", 4000)
        if kind == "build_word":
            if builds[index] or answers[index] or answer != word["word"]:
                raise GuidedError("answer_order", "A build exchange must record the recognized word once, before its spoken answers.")
            builds[index] += 1
        else:
            if answers[index] >= 2:
                raise GuidedError("answer_order", "A word permits at most two spoken answers.")
            answers[index] += 1
        previous_index = index
    if operation == "dialogue":
        if event == "answer" and (not turns or turns[-1]["wordIndex"] != len(words) - 1 or turns[-1]["inputType"] != "speak_answer"):
            raise GuidedError("answer_order", "An answer event must follow the newest saved spoken-answer exchange.")
        if event == "word" and answers[-1]:
            raise GuidedError("answer_order", "A word event cannot replay a word whose spoken answer is already saved.")
    elif mode != MANUAL_COMPLETION_MODE:
        if not context["finishRequested"] and not words[-1].get("finishMarker", False):
            raise GuidedError("finish_required", "The story requires a recognized finish signal.")
    return context


def spoken_answer_count(context):
    index = len(context["words"]) - 1
    return sum(turn.get("inputType") == "speak_answer" and turn.get("wordIndex") == index for turn in context["turns"])


def allowed_next_inputs(context):
    event = context["dialogueEvent"]
    if event == "start":
        return ["build_word"]
    if event == "word":
        first_plain = (len(context["words"]) == 1 and not context["words"][-1]["word"].endswith(".")
                       and not context["words"][-1].get("finishMarker", False))
        return ["build_word"] if first_plain else ["speak_answer"]
    return ["build_word"] if spoken_answer_count(context) >= 2 else ["speak_answer", "build_word"]


def response_schema(operation, context):
    if operation == "dialogue":
        return {"type": "object", "additionalProperties": False,
                "properties": {"question": {"type": "string"}, "nextInput": {"type": "string", "enum": allowed_next_inputs(context)}},
                "required": ["question", "nextInput"]}
    return {"type": "object", "additionalProperties": False,
            "properties": {"sentences": {"type": "array", "items": {"type": "string"}, "minItems": 7, "maxItems": 7}},
            "required": ["sentences"]}


def dialogue_reply(value, context):
    if not isinstance(value, dict) or set(value) != {"question", "nextInput"}:
        raise GuidedError("invalid_dialogue_response", "Dialogue generation returned an invalid response object.", 502)
    question, next_input = value["question"], value["nextInput"]
    if (not isinstance(question, str) or not question.strip() or len(question) > 450 or
            len(question.split()) > 35 or question.count("?") != 1 or not question.strip().endswith("?") or
            "." in question or "!" in question or
            "\n" in question or "\r" in question or
            re.search(r"\b(?:build the word|say the sentence)\b", question, re.IGNORECASE)):
        raise GuidedError("invalid_dialogue_question", "Dialogue generation must return one short question without input directions.", 502)
    if next_input not in allowed_next_inputs(context):
        raise GuidedError("invalid_dialogue_input", "Dialogue generation selected an unavailable input action.", 502)
    question = question.strip()
    prefix = ""
    if context["dialogueEvent"] == "word":
        prefix = context["currentWord"].rstrip(".").capitalize() + "! "
    directive = "Build the word." if next_input == "build_word" else "Say the sentence."
    spoken = prefix + question + " " + directive
    return {"question": question, "nextInput": next_input, "text": spoken, "spokenText": spoken, "turnId": context["turnId"]}


def story_reply(value, context):
    if not isinstance(value, dict) or set(value) != {"sentences"} or not isinstance(value["sentences"], list) or len(value["sentences"]) != 7:
        raise GuidedError("invalid_story_sentences", "The story must contain exactly seven sentences.", 502)
    sentences = []
    for sentence in value["sentences"]:
        # Deliberately simple spoken-English contract: no abbreviations,
        # ellipses or extra sentence punctuation hidden inside an array item.
        if (not isinstance(sentence, str) or not sentence.strip() or len(sentence) > 600 or
                not re.fullmatch(r'[^.!?\r\n]+[.!?]["\u201d\u2019\')]*', sentence.strip()) or
                sentence.strip().rstrip('.!?"\u201d\u2019\')').casefold() == "listens"):
            raise GuidedError("invalid_story_sentences", "Each story item must be one complete sentence without stage directions or internal sentence punctuation.", 502)
        sentences.append(sentence.strip())
    story = " ".join(sentences)
    if len(story) > 4000:
        raise GuidedError("invalid_story_sentences", "The seven-sentence story is too long.", 502)
    return {"text": story, "spokenText": STORY_ANNOUNCEMENT + " " + story,
            "turnId": context.get("turnId") or "", "sentences": sentences}
