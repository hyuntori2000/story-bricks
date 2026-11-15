using System;
using System.Collections.Generic;

namespace BrailleSoundDemo.PhysicalStory
{
    /// <summary>Supported physical text and session-local repetition; unfamiliar names are valid story material.</summary>
    public static class PhysicalWordInputPolicy
    {
        public static bool IsPunctuation(char symbol) => symbol == '.';

        public static bool IsAcceptedSymbol(string value, string kind, bool accepted)
        {
            if (!accepted || value == null || value.Length != 1) return false;
            char symbol = value[0];
            if (symbol >= 'A' && symbol <= 'Z')
                return string.IsNullOrEmpty(kind) || kind == "letter";
            // Only a positively identified period is supported; unread '?' cells stay rejected.
            return kind == "punctuation" && IsPunctuation(symbol);
        }

        public static bool IsRecognizedWord(string word)
        {
            if (string.IsNullOrEmpty(word) || word.Length > 32) return false;
            bool hasLetter = false;
            foreach (char symbol in word)
            {
                if (symbol >= 'A' && symbol <= 'Z') hasLetter = true;
                else if (!IsPunctuation(symbol)) return false;
            }
            return hasLetter;
        }

        public static bool IsRepeatedWord(string candidate, IReadOnlyList<WordRecord> acceptedWords)
        {
            if (string.IsNullOrWhiteSpace(candidate) || acceptedWords == null) return false;
            string normalized = candidate.Trim();
            foreach (var accepted in acceptedWords)
                if (accepted != null && string.Equals(normalized, accepted.word?.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
