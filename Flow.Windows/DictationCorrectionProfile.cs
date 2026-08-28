namespace Flow.Windows;

public sealed record DictationCorrectionOptions(
    bool RemoveFillers = true,
    bool RemoveRepetitions = true,
    bool ResolveSelfCorrections = true,
    bool FormatText = true,
    string Tone = "auto");

public sealed record DictationStyleSettings(
    string Work = "professional",
    string Email = "formal",
    string Code = "technical",
    string Personal = "casual")
{
    public string ForCategory(string category) => category switch
    {
        DictationStyleCatalog.WorkCategory => Work,
        DictationStyleCatalog.EmailCategory => Email,
        DictationStyleCatalog.CodeCategory => Code,
        DictationStyleCatalog.PersonalCategory => Personal,
        _ => DictationStyleCatalog.Auto
    };

    public DictationStyleSettings WithCategory(string category, string? style) => category switch
    {
        DictationStyleCatalog.WorkCategory => this with { Work = DictationStyleCatalog.Normalize(style) },
        DictationStyleCatalog.EmailCategory => this with { Email = DictationStyleCatalog.Normalize(style) },
        DictationStyleCatalog.CodeCategory => this with { Code = DictationStyleCatalog.Normalize(style) },
        DictationStyleCatalog.PersonalCategory => this with { Personal = DictationStyleCatalog.Normalize(style) },
        _ => this
    };
}

public static class DictationStyleCatalog
{
    public const string Auto = "auto";
    public const string Neutral = "neutral";
    public const string Professional = "professional";
    public const string Formal = "formal";
    public const string Concise = "concise";
    public const string Technical = "technical";
    public const string Casual = "casual";

    public const string WorkCategory = "work";
    public const string EmailCategory = "email";
    public const string CodeCategory = "code";
    public const string PersonalCategory = "personal";
    public const string GenericCategory = "generic";

    public static string Normalize(string? style) => style?.Trim().ToLowerInvariant() switch
    {
        Professional => Professional,
        Formal => Formal,
        Concise => Concise,
        Technical => Technical,
        Casual => Casual,
        Neutral => Neutral,
        _ => Auto
    };

    public static string DisplayName(string? style) => Normalize(style) switch
    {
        Professional => "Profesional",
        Formal => "Formal",
        Concise => "Conciso",
        Technical => "Técnico",
        Casual => "Cercano",
        Neutral => "Neutro",
        _ => "Automático"
    };

    public static string Instruction(string? style) => Normalize(style) switch
    {
        Professional => "Reescribe con un tono profesional, claro y directo, sin sonar rígido",
        Formal => "Reescribe con un tono formal y cuidado, con frases completas y cortesía solo cuando esté presente en el dictado",
        Concise => "Reescribe con frases breves y accionables; elimina redundancias sin perder ningún dato, condición o petición",
        Technical => "Reescribe con precisión técnica; conserva identificadores, comandos, rutas, cifras, URLs y sintaxis exacta",
        Casual => "Reescribe con un tono cercano y natural, conservando las expresiones coloquiales que formen parte de la intención",
        Neutral => "Reescribe con un tono natural y claro, sin formalizar ni adornar el mensaje",
        _ => "Reescribe con el tono natural que corresponda al destino sin imponer una personalidad artificial"
    };

    public static string DefaultForCategory(string category) => category switch
    {
        WorkCategory => Professional,
        EmailCategory => Formal,
        CodeCategory => Technical,
        PersonalCategory => Casual,
        _ => Neutral
    };

    public static string CategoryLabel(string category) => category switch
    {
        WorkCategory => "mensajería de trabajo",
        EmailCategory => "correo electrónico",
        CodeCategory => "código o prompt técnico",
        PersonalCategory => "mensajería personal",
        _ => "destino no identificado"
    };
}

public sealed record DictationCorrectionContext(
    string? TargetAppName,
    IReadOnlyList<DictionaryEntryItem> PersonalDictionary,
    DictationCorrectionOptions Options,
    DictationStyleSettings? Styles = null)
{
    public string StyleInstruction
    {
        get
        {
            var category = GetApplicationCategory(TargetAppName);
            var configuredStyle = Styles?.ForCategory(category) ?? Options.Tone;
            var style = DictationStyleCatalog.Normalize(configuredStyle);
            if (style == DictationStyleCatalog.Auto)
                style = DictationStyleCatalog.DefaultForCategory(category);

            return $"Destino: {DictationStyleCatalog.CategoryLabel(category)}. " +
                   $"Estilo elegido: {DictationStyleCatalog.DisplayName(style)}. " +
                   DictationStyleCatalog.Instruction(style) + ".";
        }
    }

    public static string GetApplicationCategory(string? appName)
    {
        var app = (appName ?? string.Empty).ToLowerInvariant();
        if (app.Contains("code") || app.Contains("cursor") || app.Contains("devenv") || app.Contains("windsurf"))
            return DictationStyleCatalog.CodeCategory;
        if (app.Contains("outlook") || app.Contains("thunderbird") || app.Contains("mail") || app.Contains("gmail"))
            return DictationStyleCatalog.EmailCategory;
        if (app.Contains("slack") || app.Contains("teams") || app.Contains("discord"))
            return DictationStyleCatalog.WorkCategory;
        if (app.Contains("whatsapp") || app.Contains("telegram"))
            return DictationStyleCatalog.PersonalCategory;
        return DictationStyleCatalog.GenericCategory;
    }
}
