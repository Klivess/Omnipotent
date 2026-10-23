using System.Text.RegularExpressions;
using Omnipotent.Services.OmniBlox.Core;

namespace Omnipotent.Services.OmniBlox.Marketing
{
    public sealed class ComplianceReport
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        /// <summary>Honest answers for Roblox's content-maturity questionnaire, prepared for the owner.</summary>
        public List<string> QuestionnaireNotes { get; } = new();
        public bool Ok => Errors.Count == 0;

        public override string ToString() =>
            (Ok ? "PASS" : "FAIL") +
            (Errors.Count > 0 ? "\nErrors:\n- " + string.Join("\n- ", Errors) : "") +
            (Warnings.Count > 0 ? "\nWarnings:\n- " + string.Join("\n- ", Warnings) : "") +
            (QuestionnaireNotes.Count > 0 ? "\nMaturity questionnaire notes:\n- " + string.Join("\n- ", QuestionnaireNotes) : "");
    }

    /// <summary>
    /// The hard gate before anything goes public: Roblox's rules as checks a machine can run. No other
    /// franchises' names, no scam bait ("free robux"), no off-platform links, no profanity, honest lengths,
    /// sane prices, and no paid random items (they need published odds and age gating). Everything here is
    /// deterministic; the launch agent adds its own judgement on top.
    /// </summary>
    public static class ComplianceChecker
    {
        public const int TitleMax = 50;
        public const int DescriptionMax = 1000;

        private static readonly string[] Franchises =
        {
            "pokemon", "pokémon", "pikachu", "minecraft", "fortnite", "mario", "nintendo", "zelda", "disney", "pixar", "marvel",
            "spiderman", "spider-man", "avengers", "batman", "superman", "star wars", "harry potter", "hogwarts", "sonic the hedgehog",
            "spongebob", "naruto", "one piece", "dragon ball", "among us", "five nights at freddy", "fnaf", "call of duty", "grand theft auto",
            "gta ", "peppa pig", "paw patrol", "hello kitty", "lego", "barbie", "transformers", "stranger things", "squid game", "bluey",
            "roblox corporation", "apex legends", "valorant", "league of legends", "genshin", "skibidi toilet",
        };

        private static readonly string[] ScamBait =
        {
            "free robux", "robux generator", "get robux", "earn robux", "robux giveaway", "giveaway robux", "free admin", "free items",
            "hack", "exploit", "dupe", "script executor", "free headless", "free korblox", "double your robux",
        };

        private static readonly string[] Profanity = { "fuck", "shit", "bitch", "cunt", "nigg", "fagg", "retard", "whore", "slut", "dick" };

        private static readonly string[] RandomItemWords = { "random", "mystery", "loot box", "lootbox", "gacha", "spin the wheel", "lucky crate", "crate", "egg hatch", "roll" };
        /// <summary>Words that mean chance, whatever the product grants.</summary>
        private static readonly string[] ChanceWords = { "random", "mystery", "loot box", "lootbox", "gacha", "spin the wheel", "lucky", "chance", "odds", "egg hatch" };
        /// <summary>Grants that give exactly what they say.</summary>
        private static readonly string[] FixedGrants = { "currency:", "multiplier:", "speed:", "flag:", "item:", "skipstage" };

        private static readonly Regex Url = new(@"(https?://|www\.|\.com\b|\.gg\b|\.net\b|discord\.gg|bit\.ly)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.Compiled);
        private static readonly Regex Phone = new(@"\+?\d[\d\s().-]{8,}\d", RegexOptions.Compiled);

        public static ComplianceReport Check(StorePage page, IEnumerable<ProductRecord> products, GameBrief? brief)
        {
            var report = new ComplianceReport();
            string title = page.Title?.Trim() ?? "";
            string description = page.Description?.Trim() ?? "";

            if (title.Length < 3) report.Errors.Add("the title is missing or too short");
            if (title.Length > TitleMax) report.Errors.Add($"the title is {title.Length} characters; Roblox allows {TitleMax}");
            if (description.Length < 40) report.Errors.Add("the description is too short to tell players what the game is");
            if (description.Length > DescriptionMax) report.Errors.Add($"the description is {description.Length} characters; Roblox allows {DescriptionMax}");
            int letters = title.Count(char.IsLetter);
            if (letters > 6 && title.Count(char.IsUpper) > letters * 0.7) report.Warnings.Add("the title is mostly capitals, which reads as spam");
            if (Regex.Matches(title + description, @"[!?]{3,}").Count > 0) report.Warnings.Add("repeated !!! or ??? reads as spam");

            void Scan(string where, string text)
            {
                string lower = " " + text.ToLowerInvariant() + " ";
                foreach (var f in Franchises.Where(f => HasWord(lower, f.Trim())))
                    report.Errors.Add($"{where} mentions \"{f.Trim()}\" — another company's franchise (copyright/trademark)");
                foreach (var s in ScamBait.Where(s => Regex.IsMatch(lower, $@"\b{Regex.Escape(s)}\b")))
                    report.Errors.Add($"{where} contains \"{s}\", which Roblox treats as scam bait");
                foreach (var p in Profanity.Where(p => Regex.IsMatch(lower, $@"\b{Regex.Escape(p)}")))
                    report.Errors.Add($"{where} contains profanity");
                if (Url.IsMatch(text)) report.Errors.Add($"{where} contains a link; off-platform links are not allowed (use the social-link fields)");
                if (Email.IsMatch(text)) report.Errors.Add($"{where} contains an email address");
                if (Phone.IsMatch(text)) report.Errors.Add($"{where} contains what looks like a phone number");
            }
            Scan("the title", title);
            Scan("the description", description);
            foreach (var k in page.Keywords) Scan("a keyword", k);

            foreach (var p in products)
            {
                Scan($"product \"{p.Name}\"", p.Name + " " + p.Description);
                if (p.PriceRobux < 1 || p.PriceRobux > 10_000) report.Errors.Add($"product \"{p.Name}\" costs {p.PriceRobux} Robux; keep prices between 1 and 10,000");
                if (p.Kind == ProductKind.GamePass && p.PriceRobux > 2_500) report.Warnings.Add($"game pass \"{p.Name}\" at {p.PriceRobux} Robux is far above typical prices for young players");
                string blob = (p.Name + " " + p.Description + " " + p.Grant).ToLowerInvariant();
                // A container word ("crate", "chest") on a fixed grant (500 coins, a pass) is a bundle, not a lottery;
                // words of chance are random whatever the grant.
                bool fixedGrant = FixedGrants.Any(g => (p.Grant ?? "").Trim().StartsWith(g, StringComparison.OrdinalIgnoreCase));
                if (ChanceWords.Any(w => HasWord(blob, w)) || !fixedGrant && RandomItemWords.Any(w => HasWord(blob, w)))
                    report.Errors.Add($"product \"{p.Name}\" looks like a paid random item; those need published odds and age gating — sell a specific item instead");
                if (string.IsNullOrWhiteSpace(p.Description)) report.Warnings.Add($"product \"{p.Name}\" has no description of what the player gets");
            }
            if (!products.Any()) report.Warnings.Add("the game sells nothing; it cannot earn beyond Premium Payouts");

            // Maturity questionnaire: prepared honestly from the brief.
            string theme = ((brief?.Pitch ?? "") + " " + (brief?.ArtStyle ?? "") + " " + string.Join(" ", brief?.MustHave ?? new List<string>()) + " " + description).ToLowerInvariant();
            bool violence = new[] { "sword", "gun", "fight", "battle", "shoot", "combat", "weapon", "zombie", "kill" }.Any(theme.Contains);
            bool fear = new[] { "horror", "scary", "creepy", "ghost", "haunted", "monster" }.Any(theme.Contains);
            report.QuestionnaireNotes.Add(violence ? "Violence: mild, unrealistic (cartoon combat, no blood)." : "Violence: none.");
            report.QuestionnaireNotes.Add("Blood and gore: none.");
            report.QuestionnaireNotes.Add(fear ? "Fear: mild (spooky themes, no jump-scares with gore)." : "Fear: none.");
            report.QuestionnaireNotes.Add("Crude humour, romance, alcohol, gambling, strong language: none.");
            report.QuestionnaireNotes.Add("Social features: standard Roblox chat only; no free-text player content from the game.");
            report.QuestionnaireNotes.Add("Monetization: " + (products.Any() ? string.Join(", ", products.Select(p => $"{p.Name} ({p.Kind}, {p.PriceRobux} R$)")) : "none") + "; no paid random items.");
            return report;
        }

        /// <summary>Whole-word (or whole-phrase) match, so "mario" never fires on "marionette".</summary>
        private static bool HasWord(string text, string word) =>
            Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase);
    }
}
