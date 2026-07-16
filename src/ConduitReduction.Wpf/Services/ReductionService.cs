using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ConduitReduction.Wpf.Services;

public record ReductionResult(
    string Original,
    string Compressed,
    int OriginalTokens,
    int CompressedTokens,
    double ReductionPct,
    string[] PassLog
);

public class ReductionService
{
    // Rough token estimator: ~4 chars per token
    private static int EstimateTokens(string text) => Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));

    public ReductionResult Reduce(string input)
    {
        var log = new List<string>();
        var original = input;
        var text = input;

        // ── Pass 1: Whitespace normalization ─────────────────────────────────
        var p1 = text;
        text = Regex.Replace(text, @"\r\n|\r", "\n");           // normalize line endings
        text = Regex.Replace(text, @"[^\S\n]+", " ");           // collapse horizontal whitespace
        text = Regex.Replace(text, @"\n{3,}", "\n\n");          // max 2 consecutive newlines
        text = Regex.Replace(text, @"[ \t]+\n", "\n");          // trailing whitespace
        text = text.Trim();
        var p1Saved = p1.Length - text.Length;
        log.Add($"[pass-1] whitespace  {p1.Length:N0} → {text.Length:N0} chars  ({p1Saved:+0;-0;0})");

        // ── Pass 2: KV substring deduplication ───────────────────────────────
        var p2 = text;
        text = DeduplicateSubstrings(text);
        var p2Saved = p2.Length - text.Length;
        log.Add($"[pass-2] dedup       {p2.Length:N0} → {text.Length:N0} chars  ({p2Saved:+0;-0;0})");

        // ── Pass 3: Delta timestamp encoding ─────────────────────────────────
        var p3 = text;
        text = CompressTimestamps(text);
        var p3Saved = p3.Length - text.Length;
        log.Add($"[pass-3] timestamps  {p3.Length:N0} → {text.Length:N0} chars  ({p3Saved:+0;-0;0})");

        // ── Pass 4: Dictionary substitution ─────────────────────────────────────
        var p4 = text;
        text = DictionarySubstitute(text, out var keyTable);
        var p4Saved = p4.Length - text.Length;
        log.Add($"[pass-4] dict-sub    {p4.Length:N0} → {text.Length:N0} chars  ({p4Saved:+0;-0;0})");

        var origTok  = EstimateTokens(original);
        var compTok  = EstimateTokens(text);
        var pct      = origTok > 0 ? (1.0 - (double)compTok / origTok) * 100.0 : 0.0;

        log.Add($"[result] {origTok:N0} tok → {compTok:N0} tok  ({pct:F1}% reduction)");

        return new ReductionResult(original, text, origTok, compTok, pct, log.ToArray());
    }

    // ── Pass 2 impl: sentence-level deduplication ────────────────────────────
    private static readonly Regex SentenceRx = new(
        @"(?<=[.!?])\s+(?=[A-Z])", RegexOptions.Compiled);

    // Lines made up entirely of structural punctuation ({, }, ;, etc.)
    // should never be deduplicated — they're syntactic markers, not content.
    private static bool IsStructuralOnly(string trimmed)
    {
        if (trimmed.Length == 0) return true;
        foreach (var c in trimmed)
            if (!"{}()[];,:".Contains(c)) return false;
        return true;
    }

    private static string DeduplicateSubstrings(string text)
    {
        var paragraphs = text.Split('\n');
        var seenSentences = new HashSet<string>(StringComparer.Ordinal);
        var resultParas = new List<string>();
        string? prevTrimmed = null;

        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();

            if (trimmed.Length < 20)
            {
                // Short-line branch:
                // Drop exact consecutive duplicates (catches typos like the
                // double `var text = input;`) but keep structural tokens
                // and allow non-adjacent repeats (e.g. `x = 0;` reused across
                // unrelated scopes).
                if (!IsStructuralOnly(trimmed)
                    && prevTrimmed != null
                    && prevTrimmed == trimmed)
                {
                    // skip this consecutive duplicate, don't update prev
                    continue;
                }
                resultParas.Add(para);
                prevTrimmed = trimmed;
                continue;
            }

            // Split paragraph into sentences, dedup each against global seen set
            var sentences = SentenceRx.Split(trimmed);
            var kept = new List<string>();
            foreach (var s in sentences)
            {
                var st = s.Trim();
                if (st.Length < 15 || seenSentences.Add(st))
                    kept.Add(s.Trim());
            }

            if (kept.Count > 0)
            {
                var joined = string.Join(" ", kept);
                resultParas.Add(joined);
                prevTrimmed = joined.Trim();
            }
        }

        return string.Join("\n", resultParas).TrimEnd();
    }

    // ── Pass 4 impl: dictionary substitution ────────────────────────────────
    private static readonly char[] Symbols = 
        "!@#$%^&*~`|<>?αβγδεζηθικλμνξπρστυφχψω".ToCharArray();

    private static string DictionarySubstitute(string text, out string keyTable)
    {
        keyTable = string.Empty;

        // Count word frequencies (words 7+ chars)
        var wordRx = new Regex(@"([A-Za-z]{7,})", RegexOptions.Compiled);
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match m in wordRx.Matches(text))
        {
            var w = m.Value;
            freq[w] = freq.GetValueOrDefault(w) + 1;
        }

        // Only substitute if net saving is positive
        // Key entry cost: "X=Word" = word.len + 3
        // Saving per occurrence: word.len - 1 (symbol is 1 char)
        var substitutions = new Dictionary<string, string>();
        var symbolIdx = 0;

        foreach (var kvp in freq.OrderByDescending(k => k.Value * (k.Key.Length - 1)))
        {
            if (symbolIdx >= Symbols.Length) break;
            var word = kvp.Key;
            var occurrences = kvp.Value;
            var savingPerOccurrence = word.Length - 1; // symbol is 1 char
            var keyCost = word.Length + 3;             // "X=Word"
            var netSaving = (savingPerOccurrence * occurrences) - keyCost;

            if (netSaving > 0)
            {
                var sym = Symbols[symbolIdx++].ToString();
                substitutions[word] = sym;
            }
        }

        if (substitutions.Count == 0)
            return text;

        // Build key table
        var kb = new StringBuilder();
        foreach (var s in substitutions)
            kb.Append($"{s.Value}={s.Key} ");
        keyTable = kb.ToString().TrimEnd();

        // Replace words (longest first to avoid partial matches).
        // Word boundaries (\b) prevent partial matches inside longer words.
        var result = text;
        foreach (var s in substitutions.OrderByDescending(k => k.Key.Length))
            result = Regex.Replace(result, $@"\b{Regex.Escape(s.Key)}\b", s.Value);

        // Prepend key
        return $"[{keyTable}]{result}";
    }

    // ── Pass 3 impl: encode repeated timestamp patterns ───────────────────────
    private static readonly Regex TimestampRx = new(
        @"\b(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)\b",
        RegexOptions.Compiled);

    private static string CompressTimestamps(string text)
    {
        var matches = TimestampRx.Matches(text);
        if (matches.Count < 2) return text;

        // Replace subsequent identical timestamps with a reference marker
        var seen = new Dictionary<string, int>();
        return TimestampRx.Replace(text, m =>
        {
            var ts = m.Value;
            if (!seen.ContainsKey(ts))
            {
                seen[ts] = seen.Count;
                return ts;
            }
            // Already seen — shorten to relative marker
            return $"[t={seen[ts]}]";
        });
    }
}
