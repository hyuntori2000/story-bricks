"""Word-only effect descriptions and bounded PCM gain; no transport or credentials."""
from array import array
import math
import re
import sys

PLAN_SCHEMA = {"type": "object", "additionalProperties": False,
               "properties": {"audible": {"type": "boolean"}, "description": {"type": "string"}},
               "required": ["audible", "description"]}
PLAN_INSTRUCTIONS = (
    "Describe a short recognizable sound for the supplied English word, not a story. "
    "Set audible=true only for a subject with a characteristic sound: animal calls, instruments, "
    "vehicles, weather or sounding objects. Describe the actual audible action in one short phrase. "
    "For people, quiet creatures, rocks, planets, abstract concepts and objects without their own "
    "characteristic sound, set audible=false and description empty. Do not invent speech, breathing, "
    "footsteps, impacts or handling actions to make a silent subject audible. "
    "Examples: CAT -> a domestic cat meowing; PIG -> a pig grunting and oinking; "
    "PIANO -> a few clear acoustic piano notes; BOAT -> a small motorboat engine idling on water; "
    "MAN, ROCK, MOON -> audible=false. Return only the required JSON."
)

# Frequent demo subjects avoid an extra text request. Other words use the same
# semantic rule through the existing backend text model, cached per process.
SOUNDS = {
    "CAT": "A domestic cat meowing naturally",
    "DOG": "A dog barking naturally",
    "PIG": "A pig making distinct natural grunts and oinks",
    "COW": "A cow mooing",
    "SHEEP": "A sheep bleating",
    "GOAT": "A goat bleating",
    "HORSE": "A horse neighing",
    "DUCK": "A duck quacking",
    "BIRD": "A small bird chirping",
    "OWL": "An owl hooting",
    "FROG": "A frog croaking",
    "LION": "A lion roaring",
    "WOLF": "A wolf howling",
    "FOX": "A fox making a short natural yip",
    "BEE": "A bee buzzing as it flies nearby",
    "PIANO": "A few clearly separated acoustic piano notes with natural decay",
    "GUITAR": "A few clearly plucked acoustic guitar strings",
    "DRUM": "A few distinct acoustic drum beats",
    "BELL": "A small metal bell ringing with natural decay",
    "VIOLIN": "A short clear bowed violin phrase",
    "FLUTE": "A short clear flute phrase",
    "TRUMPET": "A short clear trumpet phrase",
    "BOAT": "A small motorboat engine idling on water",
    "CAR": "A car engine starting then idling",
    "TRAIN": "A train rolling on rails with rhythmic wheel clacks",
    "TRUCK": "A truck engine idling",
    "PLANE": "An airplane engine passing overhead",
    "ROCKET": "A rocket engine firing with a controlled low rumble",
    "WIND": "Wind blowing through leaves with a recognizable gentle whoosh",
    "RAIN": "Distinct rain drops pattering on a window",
    "THUNDER": "A natural distant thunder rumble",
    "WATER": "Water flowing in a small stream",
    "RIVER": "Water flowing in a small river",
    "OCEAN": "An ocean wave washing onto a beach",
    "SEA": "An ocean wave washing onto a beach",
    "FIRE": "A small wood fire crackling",
    "CLOCK": "An analog clock ticking clearly",
    "SIREN": "A recognizable distant emergency siren",
}
QUIET_WORDS = set("MAN WOMAN BOY GIRL CHILD PERSON PEOPLE JUDY ROCK STONE MOON SUN STAR "
                  "SPACE SKY CLOUD FLOWER TREE LEAF GRASS SAND BOOK TABLE CHAIR HOUSE "
                  "HOME BALL APPLE BANANA LOVE DREAM HAPPY SAD SNAIL BUTTERFLY FISH".split())
QUIET_PROMPT = "A brief very quiet non-vocal low hum fading gently in and out. No voice, music or other sounds."


class TooQuietError(ValueError):
    pass


def subject(word):
    return re.sub(r"[^A-Z]", "", word.upper())


def known_plan(word):
    key = subject(word)
    singular = key[:-1] if key.endswith("S") else key
    if key in SOUNDS or singular in SOUNDS:
        return {"audible": True, "description": SOUNDS.get(key, SOUNDS.get(singular)), "source": "catalog"}
    if key in QUIET_WORDS or singular in QUIET_WORDS:
        return {"audible": False, "description": "", "source": "quiet_subject"}
    return None


def checked_plan(value):
    if not isinstance(value, dict) or set(value) != {"audible", "description"} or type(value["audible"]) is not bool:
        raise ValueError("Invalid sound description")
    description = value["description"]
    if not isinstance(description, str) or len(description) > 240 or (value["audible"] and not description.strip()):
        raise ValueError("Invalid sound description")
    return dict(audible=value["audible"], description=description.strip(), source="semantic")


def effect_prompt(plan):
    if not plan["audible"]:
        return QUIET_PROMPT
    return (plan["description"].rstrip(". ") + ". Isolated close recording, clear and present at normal "
            "listening volume, quiet background. No human speech, narration or background soundtrack.")


def level_pcm(pcm, audible):
    """Raise audible effects at most 30 dB; never boost silence or quiet subjects."""
    samples = array("h", pcm)
    if sys.byteorder != "little": samples.byteswap()
    if not samples: raise ValueError("Empty sound effect")
    peak = max(abs(x) for x in samples) / 32768.0
    rms = math.sqrt(sum(float(x) * x for x in samples) / len(samples)) / 32768.0
    db = lambda value: round(20 * math.log10(value), 2) if value > 0 else -120.0
    gain = 1.0
    if audible:
        if rms < 10 ** (-70 / 20) and peak < 10 ** (-50 / 20):
            raise TooQuietError("Sound effect is near silent")
        gain = min(10 ** (30 / 20), 10 ** (-22 / 20) / max(rms, 1e-9),
                   10 ** (-3 / 20) / max(peak, 1e-9))
    else:
        gain = min(1.0, 10 ** (-42 / 20) / max(rms, 1e-9), 10 ** (-30 / 20) / max(peak, 1e-9))
    adjusted = array("h", (max(-32768, min(32767, round(x * gain))) for x in samples))
    output_peak = max(abs(x) for x in adjusted) / 32768.0
    output_rms = math.sqrt(sum(float(x) * x for x in adjusted) / len(adjusted)) / 32768.0
    if sys.byteorder != "little": adjusted.byteswap()
    return adjusted.tobytes(), {"inputRmsDbfs": db(rms), "outputRmsDbfs": db(output_rms),
                               "outputPeakDbfs": db(output_peak), "gainDb": db(gain), "audible": audible}
