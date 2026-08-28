package com.pablo.flow

data class DictationCorrectionOptions(
    val removeFillers: Boolean = true,
    val removeRepetitions: Boolean = true,
    val resolveSelfCorrections: Boolean = true,
    val formatText: Boolean = true
)

data class DictationCorrectionContext(
    val targetAppName: String? = null,
    val personalDictionary: List<Pair<String, String?>> = emptyList(),
    val options: DictationCorrectionOptions = DictationCorrectionOptions(),
    val styleInstruction: String? = null
)

object DictationTextProcessor {
    private val ellipsis = Regex("(?:\\.{3,}|…+)")
    private val hesitation = Regex("(?<!\\p{L})(?:eh+|em+|mm+|mmm+|hmm+|eee+)(?!\\p{L})", RegexOption.IGNORE_CASE)
    private val repeatedPunctuation = Regex("([,;:])\\s*\\1+")
    private val spaceBeforePunctuation = Regex("\\s+([,.;:!?])")
    private val repeatedWord = Regex("(?i)(?<!\\p{L})([\\p{L}\\p{N}]+)([ \\t]+)\\1(?!\\p{L})")
    private val codeFence = Regex("```(?:text|plaintext)?", RegexOption.IGNORE_CASE)
    private val modelWrapper = Regex("(?i)^\\s*(?:texto final|respuesta)\\s*:\\s*")
    private val assistantReplyPrefix = Regex(
        "(?i)^\\s*(?:aquí tienes(?: la corrección| el texto)?|te dejo(?: la corrección| el texto)?|he corregido|(?:texto|resultado) (?:corregido|editado|final)\\s*(?:es|:)|la (?:transcripción|corrección)(?: final| corregida)?\\s*(?:es|:)|este es el texto(?: final| corregido)?\\s*(?:es|:)|(?:por supuesto|claro)[,!:.]\\s*(?:aquí|te|la (?:transcripción|corrección)|el texto)|lo siento[,!:]|como (?:ia|modelo)|espero que)\\b"
    )
    private val reasoningOrStructuredReply = Regex("(?i)^\\s*(?:analysis|reasoning|thoughts?)\\s*:|^\\s*<(?:analysis|reasoning|final|answer)\\b|^\\s*[{[]")
    private val manyLines = Regex("\\n{3,}")

    fun prepareForCorrection(text: String, options: DictationCorrectionOptions = DictationCorrectionOptions()): String {
        if (text.isBlank()) return ""
        var prepared = text.replace("\r\n", "\n").replace('\r', '\n')
        if (options.removeFillers) {
            prepared = ellipsis.replace(prepared, " ")
            prepared = hesitation.replace(prepared, " ")
        }
        if (options.removeRepetitions) prepared = removeObviousStutters(prepared)
        return normalize(prepared)
    }

    fun cleanFinal(text: String, options: DictationCorrectionOptions = DictationCorrectionOptions()): String {
        if (text.isBlank()) return ""
        var cleaned = text.replace("\r\n", "\n").replace('\r', '\n')
        cleaned = modelWrapper.replace(cleaned, " ")
        cleaned = codeFence.replace(cleaned, " ")
        if (options.removeFillers) {
            cleaned = ellipsis.replace(cleaned, " ")
            cleaned = hesitation.replace(cleaned, " ")
        }
        if (options.removeRepetitions) cleaned = removeObviousStutters(cleaned)
        cleaned = repeatedPunctuation.replace(cleaned, "$1")
        cleaned = spaceBeforePunctuation.replace(cleaned, "$1")
        return normalize(cleaned).trim(' ', '\t', ',', ';', ':')
    }

    fun tryAcceptModelCorrection(
        original: String,
        candidate: String,
        options: DictationCorrectionOptions = DictationCorrectionOptions()
    ): String? {
        if (candidate.isBlank()) return null
        val trimmed = candidate.trim()
        if (reasoningOrStructuredReply.containsMatchIn(trimmed)) return null
        val cleaned = cleanFinal(trimmed, options)
        if (cleaned.isBlank()) return null
        if (assistantReplyPrefix.containsMatchIn(cleaned) && !assistantReplyPrefix.containsMatchIn(original.trim())) return null
        return cleaned.takeIf { it.length <= maxOf(1_200, original.length * 4) }
    }

    fun expandSnippets(text: String, snippets: List<SnippetRecord>): String {
        var expanded = text
        snippets.asSequence()
            .filter { it.trigger.isNotBlank() && it.expansion.isNotBlank() }
            .sortedByDescending { it.trigger.length }
            .take(100)
            .forEach { snippet ->
                val pattern = Regex("(?i)(?<!\\p{L})${Regex.escape(snippet.trigger.trim())}(?!\\p{L})")
                expanded = pattern.replace(expanded, snippet.expansion)
            }
        return expanded
    }

    private fun removeObviousStutters(text: String): String {
        var current = text
        repeat(3) {
            val next = repeatedWord.replace(current, "$1")
            if (next == current) return current
            current = next
        }
        return current
    }

    private fun normalize(text: String): String = manyLines.replace(
        text.lines().joinToString("\n") { it.replace(Regex("[ \\t]{2,}"), " ").trim() },
        "\n\n"
    ).trim()
}
