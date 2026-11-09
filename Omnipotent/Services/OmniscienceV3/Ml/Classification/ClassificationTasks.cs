namespace Omnipotent.Services.OmniscienceV3.Ml.Classification
{
    /// <summary>One label of a task, with the definition shown to the LLM teacher (so labels mean the same thing everywhere).</summary>
    public sealed record TaskLabel(string Id, string Code, string Definition);

    /// <summary>
    /// A per-message classification task of the A2 ensemble (plan A1/A2/R2). Labels are a closed set; the
    /// <see cref="DefaultLabel"/> is what the LLM teacher may omit (compact codes "listing only non-default labels", R1);
    /// a null default means the label is always reported (language).
    /// </summary>
    public sealed record TaskDefinition(
        string Id,
        string Code,
        string Title,
        string Question,
        IReadOnlyList<TaskLabel> Labels,
        string? DefaultLabel,
        string? DimensionId,
        bool ContextMatters,
        bool StyleDependent,
        int Version)
    {
        public IReadOnlyList<string> LabelIds => Labels.Select(l => l.Id).ToList();
        public int IndexOf(string label) => Labels.ToList().FindIndex(l => string.Equals(l.Id, label, StringComparison.OrdinalIgnoreCase));
        public string? FromCode(string code) =>
            Labels.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase) || string.Equals(l.Id, code, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>
    /// The task catalogue. Every judgement of meaning is one of these, decided by models (heads on the shared embedding,
    /// context heads, the LLM teacher/adjudicator, fused and calibrated), never by word lists (plan A1). Bumping a task's
    /// <see cref="TaskDefinition.Version"/> (a label set change) invalidates its heads and teacher labels.
    /// </summary>
    public static class ClassificationTasks
    {
        private static TaskLabel L(string id, string code, string definition) => new(id, code, definition);

        public static readonly TaskDefinition Sentiment = new("sentiment", "sen", "Sentiment",
            "Overall feeling the author expresses in this message.",
            new[] { L("negative", "neg", "unhappy, critical, annoyed or negative"), L("neutral", "neu", "neither positive nor negative, or purely factual"), L("positive", "pos", "happy, approving, warm or positive") },
            "neutral", "content.sentiment", ContextMatters: true, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition Emotion = new("emotion", "emo", "Emotion",
            "The dominant emotion the author expresses.",
            new[]
            {
                L("anger", "ang", "anger, irritation, frustration"), L("disgust", "dis", "disgust, revulsion, contempt"), L("fear", "fea", "fear, worry, anxiety"),
                L("joy", "joy", "joy, amusement, excitement, relief"), L("sadness", "sad", "sadness, disappointment, loneliness"), L("surprise", "sur", "surprise, astonishment"),
                L("affection", "aff", "affection, love, gratitude, care"), L("neutral", "neu", "no clear emotion"),
            },
            "neutral", "content.emotion", ContextMatters: true, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition Toxicity = new("toxicity", "tox", "Toxicity",
            "Is the message hostile? Judge friendly banter between friends as banter, not as toxicity.",
            new[]
            {
                L("none", "no", "not hostile (including friendly teasing and swearing for emphasis between friends)"),
                L("mild", "mil", "rude or crude, swearing not aimed at anyone"),
                L("toxic", "tox", "insulting, demeaning or harassing someone"),
                L("severe", "sev", "threats, hate against a group, or encouraging harm"),
            },
            "none", "content.toxicity", ContextMatters: true, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition DialogueAct = new("dialogue_act", "da", "Dialogue act",
            "What the message does in the conversation.",
            new[]
            {
                L("statement", "st", "states something"), L("question", "q", "asks for information"), L("request", "req", "asks someone to do something"),
                L("answer", "ans", "answers a question"), L("agreement", "agr", "agrees or accepts"), L("disagreement", "dis", "disagrees or refuses"),
                L("greeting", "gr", "hello, goodbye, good night"), L("thanks", "th", "thanks someone"), L("apology", "ap", "apologises"),
                L("reaction", "rea", "a short reaction (lol, nice, an emoji) to something said"), L("other", "oth", "none of these"),
            },
            "statement", "content.dialogue_act", ContextMatters: true, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition Humour = new("humour", "hum", "Humour",
            "Is the author joking?",
            new[] { L("serious", "ser", "meant seriously"), L("playful", "pla", "light-hearted, teasing"), L("joke", "jk", "a joke or pun"), L("sarcasm", "sar", "sarcastic or ironic: means the opposite of what it says") },
            "serious", "content.humour", ContextMatters: true, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition Politeness = new("politeness", "pol", "Politeness",
            "How polite the message is towards the people addressed.",
            new[] { L("impolite", "imp", "curt, dismissive or rude"), L("neutral", "neu", "neither"), L("polite", "pol", "courteous, considerate") },
            "neutral", "content.politeness", ContextMatters: true, StyleDependent: true, Version: 1);

        public static readonly TaskDefinition Formality = new("formality", "for", "Formality",
            "Register of the language.",
            new[] { L("informal", "inf", "casual chat register"), L("neutral", "neu", "neither casual nor formal"), L("formal", "fml", "formal register") },
            "informal", "content.formality", ContextMatters: false, StyleDependent: true, Version: 1);

        public static readonly TaskDefinition SelfDisclosure = new("self_disclosure", "sd", "Self-disclosure",
            "Does the author share something about themselves (feelings, plans, experiences, preferences)? Rate the act of sharing, never the category of what is shared.",
            new[] { L("none", "no", "nothing about themselves"), L("low", "lo", "a small personal detail"), L("high", "hi", "a personal or intimate disclosure") },
            "none", "content.self_disclosure", ContextMatters: false, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition InfoDensity = new("info_density", "inf", "Information density",
            "How much information the message carries.",
            new[] { L("low", "lo", "filler, a reaction, small talk"), L("medium", "med", "some content"), L("high", "hi", "dense, specific information") },
            "medium", "content.info_density", ContextMatters: false, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition Conflict = new("conflict", "con", "Conflict",
            "Is the message part of a disagreement or dispute between people in the conversation?",
            new[] { L("none", "no", "no conflict"), L("tension", "ten", "friction, irritation or a mild dispute"), L("conflict", "con", "an open argument or fight") },
            "none", "content.conflict", ContextMatters: true, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition Support = new("support", "sup", "Support",
            "Is the author seeking or offering emotional or practical support?",
            new[] { L("none", "no", "neither"), L("seeking", "see", "asks for help, comfort or advice"), L("offering", "off", "offers help, comfort or advice") },
            "none", "content.support", ContextMatters: true, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition Modality = new("modality", "mod", "Modality",
            "How the content is meant (used to keep jokes, quotes and fiction out of facts).",
            new[]
            {
                L("literal", "lit", "meant literally"), L("joke", "jk", "a joke"), L("sarcasm", "sar", "sarcasm or irony"), L("hypothetical", "hyp", "a hypothetical or wish"),
                L("question", "q", "a question, not a claim"), L("quoted", "quo", "quoting or relaying someone else"), L("roleplay", "rp", "roleplay or fiction"),
                L("lyrics_copypasta", "lc", "song lyrics, a meme template or copypasta"),
            },
            "literal", "content.modality", ContextMatters: true, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition Language = new("language", "lang", "Language",
            "The main language of the message (ISO 639-1). Use 'und' when there is no language (emoji only, a link, a number).",
            new[]
            {
                "en", "de", "fr", "es", "it", "pt", "nl", "pl", "ru", "uk", "tr", "ar", "he", "fa", "hi", "bn", "ur", "zh", "ja", "ko",
                "vi", "th", "id", "ms", "tl", "sv", "no", "da", "fi", "el", "cs", "ro", "hu", "sw", "other", "und",
            }.Select(c => L(c, c, c == "other" ? "a language not listed" : c == "und" ? "no language" : "ISO 639-1 " + c)).ToArray(),
            null, "content.language", ContextMatters: false, StyleDependent: false, Version: 1);

        public static readonly TaskDefinition AboutKlives = new("about_klives", "kl", "About Klives",
            "Is the message addressed to Klives (the owner of this archive), or about him, named or not?",
            new[]
            {
                L("none", "no", "neither addressed to nor about Klives"),
                L("addressed", "to", "spoken to Klives"),
                L("mentioned", "men", "talks about Klives by name, nickname or @mention"),
                L("indirect", "ind", "talks about Klives without naming him (\"the guy who made that bot\")"),
            },
            "none", "content.about_klives", ContextMatters: true, StyleDependent: false, Version: 1);

        /// <summary>
        /// Wellbeing (N14) only: explicit distress or crisis language, used to suggest a check-in with crisis-resource text.
        /// It is a per-message reading, never a stored trait of the person, and it is not exposed as an Explore dimension.
        /// </summary>
        public static readonly TaskDefinition Distress = new("distress", "ds", "Distress",
            "Does the author express distress? 'crisis' only for explicit statements of being in danger or wanting to harm themselves.",
            new[] { L("none", "no", "no distress"), L("distress", "dst", "clearly struggling, overwhelmed or hopeless"), L("crisis", "cri", "explicit danger to themselves") },
            "none", null, ContextMatters: true, StyleDependent: false, Version: 1);

        public static IReadOnlyList<TaskDefinition> All { get; } = new[]
        {
            Sentiment, Emotion, Toxicity, DialogueAct, Humour, Politeness, Formality, SelfDisclosure, InfoDensity, Conflict, Support, Modality,
            Language, AboutKlives, Distress,
        };

        public static TaskDefinition Get(string id) =>
            All.FirstOrDefault(t => t.Id == id) ?? throw new KeyNotFoundException($"No classification task '{id}'.");

        public static bool TryGet(string id, out TaskDefinition task)
        {
            task = All.FirstOrDefault(t => t.Id == id)!;
            return task != null;
        }

        public static TaskDefinition? FromCode(string code) => All.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));
    }
}
