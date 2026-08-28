using System.Text.RegularExpressions;

namespace Flow.Windows;

/// <summary>
/// Deterministic cleanup around the contextual model. Whisper remains the
/// source transcript; this only removes ASR hesitation artifacts from the
/// text that is pasted and stored as the final version.
/// </summary>
public static partial class DictationTextProcessor
{
    private static readonly Regex Ellipsis = new(@"(?:\.{3,}|…+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Hesitation = new(
        @"(?<!\p{L})(?:eh+|em+|mm+|mmm+|hmm+|eee+)(?!\p{L})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RepeatedPunctuation = new(@"([,;:])\s*\1+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SpaceBeforePunctuation = new(@"\s+([,.;:!?])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ManySpaces = new(@"[ \t]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ManyLineBreaks = new(@"\n{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AdjacentRepeatedWord = new(
        @"(?<!\p{L})(?<word>[\p{L}\p{N}]+)(?<separator>[ \t]+)(?i:\k<word>)(?!\p{L})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ModelWrapper = new(
        @"^\s*(?:```(?:text|plaintext)?\s*)?(?:texto final|respuesta)\s*:\s*|\s*```\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AssistantReplyPrefix = new(
        @"^\s*(?:aquí tienes(?: la corrección| el texto)?|te dejo(?: la corrección| el texto)?|he corregido|(?:texto|resultado) (?:corregido|editado|final)\s*(?:es|:)|la (?:transcripción|corrección)(?: final| corregida)?\s*(?:es|:)|este es el texto(?: final| corregido)?\s*(?:es|:)|(?:por supuesto|claro)[,!:.]\s*(?:aquí|te|la (?:transcripción|corrección)|el texto)|lo siento[,!:]|como (?:ia|modelo)|espero que)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AssistantReplyMarker = new(
        @"(?im)^\s*(?:aquí tienes(?: la corrección| el texto)?|te dejo(?: la corrección| el texto)?|he corregido|(?:texto|resultado) (?:corregido|editado|final)\s*(?:es|:)|la (?:transcripción|corrección)(?: final| corregida)?\s*(?:es|:)|este es el texto(?: final| corregido)?\s*(?:es|:)|nota\s*:|explicación\s*:|si quieres que|espero que)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ReasoningOrStructuredReply = new(
        @"^\s*(?:analysis|reasoning|thoughts?)\s*:|^\s*<(?:analysis|reasoning|final|answer)\b|^\s*[{[]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CodeFence = new(
        @"```(?:text|plaintext)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Makes the ASR output unambiguous before sending it to the contextual
    /// model. Spoken hesitation sounds and pause ellipses are not content.
    /// </summary>
    public static string PrepareForCorrection(string text, DictationCorrectionOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        options ??= new DictationCorrectionOptions();
        var prepared = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (options.RemoveFillers)
        {
            prepared = Ellipsis.Replace(prepared, " ");
            prepared = Hesitation.Replace(prepared, " ");
        }
        if (options.RemoveRepetitions)
            prepared = RemoveObviousStutters(prepared);
        return Normalize(prepared);
    }

    /// <summary>
    /// Applies a final local guarantee even when the correction model is
    /// unavailable or returns a pause marker despite the instruction.
    /// </summary>
    public static string CleanFinal(string text, DictationCorrectionOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        options ??= new DictationCorrectionOptions();
        var cleaned = ModelWrapper.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), " ");
        cleaned = CodeFence.Replace(cleaned, " ");
        if (options.RemoveFillers)
        {
            cleaned = Ellipsis.Replace(cleaned, " ");
            cleaned = Hesitation.Replace(cleaned, " ");
        }
        if (options.RemoveRepetitions)
            cleaned = RemoveObviousStutters(cleaned);
        cleaned = RepeatedPunctuation.Replace(cleaned, "$1");
        cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1");
        return Normalize(cleaned).Trim(' ', '\t', ',', ';', ':');
    }

    /// <summary>
    /// Accepts a model response only when it looks like a copy-edit of the
    /// source. A chatty GPT-OSS answer must never be pasted as dictated text.
    /// </summary>
    public static string? TryAcceptModelCorrection(
        string original,
        string candidate,
        DictationCorrectionOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var trimmed = candidate.Trim();
        if (ReasoningOrStructuredReply.IsMatch(trimmed)) return null;

        var cleaned = CleanFinal(trimmed, options);
        if (string.IsNullOrWhiteSpace(cleaned)) return null;

        // Do not reject a sentence that the user actually dictated with a
        // natural opening such as "Aquí tienes..." or "Claro...".
        if (AssistantReplyPrefix.IsMatch(cleaned) && !AssistantReplyPrefix.IsMatch(original.Trim()))
            return null;
        if (AssistantReplyMarker.IsMatch(cleaned) && !AssistantReplyMarker.IsMatch(original.Trim()))
            return null;

        // A correction should remain close to the source length. This catches
        // explanatory/chatty completions without limiting normal corrections.
        var maximumLength = Math.Max(1_200, original.Length * 4);
        return cleaned.Length > maximumLength ? null : cleaned;
    }

    public static string ExpandSnippets(string text, IReadOnlyList<SnippetItem> snippets)
    {
        if (string.IsNullOrWhiteSpace(text) || snippets.Count == 0) return text;
        var expanded = text;
        foreach (var snippet in snippets
                     .Where(item => !string.IsNullOrWhiteSpace(item.Trigger) && !string.IsNullOrWhiteSpace(item.Expansion))
                     .OrderByDescending(item => item.Trigger.Length))
        {
            var trigger = snippet.Trigger.Trim();
            var pattern = $@"(?<!\p{{L}}){Regex.Escape(trigger)}(?!\p{{L}})";
            expanded = Regex.Replace(
                expanded,
                pattern,
                _ => snippet.Expansion.Trim(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return Normalize(expanded);
    }

    private static string RemoveObviousStutters(string text)
    {
        var current = text;
        for (var pass = 0; pass < 3; pass++)
        {
            var next = AdjacentRepeatedWord.Replace(current, "${word}");
            if (string.Equals(next, current, StringComparison.Ordinal)) break;
            current = next;
        }
        return current;
    }

    private static string Normalize(string text)
    {
        var normalized = ManySpaces.Replace(text, " ");
        normalized = string.Join('\n', normalized.Split('\n').Select(line => line.Trim()));
        normalized = ManyLineBreaks.Replace(normalized, "\n\n");
        return normalized.Trim();
    }
}
