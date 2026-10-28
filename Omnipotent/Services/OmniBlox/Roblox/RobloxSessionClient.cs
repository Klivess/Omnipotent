using System.Globalization;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Omnipotent.Services.OmniBlox.Roblox
{
    /// <summary>
    /// The owner's Roblox session is missing, or Roblox no longer accepts it (it signs a session out when the
    /// password changes, after long disuse, or when it is used from another region than the one it was made in).
    /// </summary>
    public sealed class RobloxSessionException : Exception
    {
        public RobloxSessionException(bool missing, string message) : base(message) => Missing = missing;

        /// <summary>True when no session is configured at all; false when Roblox refused the one configured.</summary>
        public bool Missing { get; }
    }

    /// <summary>
    /// The Creator Dashboard actions Roblox offers no API key for — creating an experience, its icon, store thumbnails
    /// and the Maturity &amp; Compliance questionnaire — done the way the dashboard does them: with the owner's signed-in
    /// session (the .ROBLOSECURITY cookie, OmniSetting OmniBlox_RobloxSessionCookie) and Roblox's CSRF handshake.
    /// Roblox rotates the cookie from time to time; the new one is passed to <c>onRotated</c> to be saved, since the
    /// old one stops working soon after.
    /// </summary>
    public sealed class RobloxSessionClient
    {
        private const string Questionnaires = "https://apis.roblox.com/experience-questionnaire";
        private readonly RobloxHttp http;
        private readonly Func<string?> configured;
        private readonly Action<string>? onRotated;
        private readonly object sync = new();
        private string? seenConfigured, rotated, csrf;

        public RobloxSessionClient(RobloxHttp http, Func<string?> cookie, Action<string>? onRotated = null)
        {
            this.http = http;
            configured = cookie;
            this.onRotated = onRotated;
        }

        public bool HasSession => Current() != null;

        /// <summary>The cookie value from a bare value, a ".ROBLOSECURITY=…" pair or a whole Cookie header.</summary>
        internal static string? Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = text.Trim().Trim('"', '\'');
            int at = s.IndexOf(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                s = s[(at + ".ROBLOSECURITY=".Length)..];
                int end = s.IndexOf(';');
                if (end >= 0) s = s[..end];
            }
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        private string? Current()
        {
            lock (sync)
            {
                string? now = Normalize(configured());
                // The owner pasted a new one: it replaces whatever Roblox rotated in meanwhile.
                if (now != seenConfigured) { seenConfigured = now; rotated = null; csrf = null; }
                return rotated ?? now;
            }
        }

        private async Task<RobloxResponse> SendAsync(string bucket, HttpMethod method, string url, Func<HttpContent?>? content, CancellationToken ct, int maxAttempts = 4)
        {
            for (int attempt = 0; ; attempt++)
            {
                string cookie = Current() ?? throw new RobloxSessionException(true,
                    "No Roblox account is connected (OmniBlox dashboard → Roblox account → Connect), so OmniBlox cannot do what the Creator Dashboard does for you.");
                string? token;
                lock (sync) token = csrf;
                var response = await http.SendAsync(bucket, () =>
                {
                    var req = new HttpRequestMessage(method, url);
                    req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
                    if (token != null && method != HttpMethod.Get) req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
                    var c = content?.Invoke();
                    if (c != null) req.Content = c;
                    return req;
                }, ct, maxAttempts);
                NoteRotation(cookie, response);
                // Roblox's CSRF handshake: the first write is refused with the token to use; send it again with it.
                if (response.Status == HttpStatusCode.Forbidden && attempt == 0 && response.Headers.TryGetValues("x-csrf-token", out var tokens) && tokens.FirstOrDefault() is { Length: > 0 } fresh)
                {
                    lock (sync) csrf = fresh;
                    continue;
                }
                if (response.Status == HttpStatusCode.Unauthorized)
                    throw new RobloxSessionException(false, $"Roblox signed the session out ({new Uri(url).Host}{new Uri(url).AbsolutePath} → 401). Connect the account again from the OmniBlox dashboard (Roblox account → Connect).");
                if (!response.Ok) throw new RobloxApiException(new Uri(url).Host + new Uri(url).AbsolutePath, response.Status, response.Body);
                return response;
            }
        }

        /// <summary>Keeps the newest cookie Roblox handed back (and has it saved), so the session survives rotation.</summary>
        private void NoteRotation(string sent, RobloxResponse response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return;
            foreach (var header in cookies)
            {
                if (!header.StartsWith(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase)) continue;
                string? value = Normalize(header);
                if (value == null || value == sent) continue;
                lock (sync) rotated = value;
                try { onRotated?.Invoke(value); } catch { /* saving is best effort; the rotated one is used meanwhile */ }
            }
        }

        // ─────────────── Who is signed in ───────────────

        public async Task<(long UserId, string Name)> GetSignedInUserAsync(CancellationToken ct)
        {
            var r = await SendAsync("session", HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", null, ct);
            var j = JObject.Parse(r.Body);
            return ((long?)j["id"] ?? 0, (string?)j["name"] ?? "");
        }

        /// <summary>Who a candidate cookie signs in as, before it is saved (nothing else is done with it). Throws <see cref="RobloxSessionException"/> when Roblox refuses it.</summary>
        public async Task<(long UserId, string Name)> ProbeAsync(string cookie, CancellationToken ct)
        {
            string value = Normalize(cookie) ?? throw new RobloxSessionException(true, "No cookie was given.");
            var r = await http.SendAsync("session", () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated");
                req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + value);
                return req;
            }, ct);
            if (r.Status == HttpStatusCode.Unauthorized)
                throw new RobloxSessionException(false, "Roblox does not accept this session: it was signed out, has expired, or was not copied whole.");
            if (!r.Ok) throw new RobloxApiException("users.roblox.com/v1/users/authenticated", r.Status, r.Body);
            var j = JObject.Parse(r.Body);
            return ((long?)j["id"] ?? 0, (string?)j["name"] ?? "");
        }

        // ─────────────── Experiences ───────────────

        /// <summary>The template the Creator Dashboard's Create Experience starts from (Baseplate); the studio publishes its own place over it.</summary>
        public const long BaseplateTemplate = 95206881;

        /// <summary>
        /// Creates a new, unpublished experience, owned by the signed-in account or by <paramref name="groupId"/>: the
        /// Creator Dashboard's Create Experience (no API key can do it). Sent once: Roblox may have made the experience
        /// even when its answer is lost, and a retry would make a second one.
        /// </summary>
        public async Task<(long UniverseId, long RootPlaceId)> CreateExperienceAsync(long? groupId, CancellationToken ct)
        {
            string url = "https://apis.roblox.com/universes/v1/universes/create" + (groupId is > 0 ? "?groupId=" + groupId.Value.ToString(CultureInfo.InvariantCulture) : "");
            var body = new JObject { ["templatePlaceId"] = BaseplateTemplate, ["isPublish"] = false };
            var r = await SendAsync("session-create", HttpMethod.Post, url,
                () => new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"), ct, maxAttempts: 1);
            var j = JObject.Parse(r.Body);
            long universe = (long?)j["universeId"] ?? 0, place = (long?)j["rootPlaceId"] ?? 0;
            if (universe <= 0 || place <= 0) throw new RobloxApiException("universes/create", r.Status, r.Body, "no universe or root place id in the answer");
            return (universe, place);
        }

        /// <summary>
        /// Archives an experience, as the Creator Dashboard's Archive does (Roblox's nearest thing to deleting one: it
        /// leaves the owner's creations and cannot be played, and can be restored from the dashboard's Archived list).
        /// No API key can do it.
        /// </summary>
        public async Task ArchiveExperienceAsync(long universeId, CancellationToken ct)
        {
            string url = "https://develop.roblox.com/v2/universes/" + universeId.ToString(CultureInfo.InvariantCulture) + "/configuration";
            await SendAsync("session", HttpMethod.Patch, url, () => new StringContent(new JObject { ["isArchived"] = true }.ToString(Formatting.None), Encoding.UTF8, "application/json"), ct);
        }

        // ─────────────── Store page images ───────────────

        /// <summary>Replaces the experience's icon (the Creator Dashboard's Icon upload).</summary>
        public async Task UploadGameIconAsync(long universeId, byte[] png, CancellationToken ct) =>
            await SendAsync("session-upload", HttpMethod.Post, $"https://publish.roblox.com/v1/games/{universeId}/icon", () => Image(png, "icon.png"), ct);

        /// <summary>Adds one store-page thumbnail; returns its image id.</summary>
        public async Task<long> UploadGameThumbnailAsync(long universeId, byte[] png, CancellationToken ct)
        {
            var r = await SendAsync("session-upload", HttpMethod.Post, $"https://publish.roblox.com/v1/games/{universeId}/thumbnail/image", () => Image(png, "thumbnail.png"), ct);
            return (long?)JObject.Parse(r.Body)["targetId"] ?? 0;
        }

        private static HttpContent Image(byte[] png, string fileName)
        {
            var form = RobloxForm.Create();
            RobloxForm.AddFile(form, "request.files", png, "image/png", fileName);
            return form;
        }

        // ─────────────── Maturity & Compliance questionnaire ───────────────

        public async Task<string> GetLatestQuestionnaireIdAsync(long universeId, CancellationToken ct)
        {
            var r = await SendAsync("session", HttpMethod.Get, $"{Questionnaires}/v1/questionnaires/{universeId}/latest", null, ct);
            return (string?)JObject.Parse(r.Body)["questionnaireId"] ?? throw new RobloxApiException("experience-questionnaire", r.Status, r.Body, "no questionnaire id");
        }

        public async Task<Questionnaire> GetQuestionnaireAsync(string questionnaireId, CancellationToken ct)
        {
            var r = await SendAsync("session", HttpMethod.Get, $"{Questionnaires}/v1/questionnaires/{Uri.EscapeDataString(questionnaireId)}", null, ct);
            return ParseQuestionnaire(JObject.Parse(r.Body)["questionnaire"] as JObject ?? throw new RobloxApiException("experience-questionnaire", r.Status, r.Body, "no questionnaire"));
        }

        /// <summary>The second questionnaire the dashboard asks after the first; null when Roblox has none for this experience.</summary>
        public async Task<Questionnaire?> GetAdditionalQuestionnaireAsync(long universeId, CancellationToken ct)
        {
            try
            {
                var r = await SendAsync("session", HttpMethod.Get, $"{Questionnaires}/v2/questionnaires/additional/universes/{universeId}", null, ct);
                return JObject.Parse(r.Body)["questionnaire"] is JObject q && q.HasValues ? ParseQuestionnaire(q) : null;
            }
            catch (RobloxApiException ex) when (ex.IsNotFound)
            {
                return null;
            }
        }

        public async Task<QuestionnaireCheck> ValidateAsync(string questionnaireId, IReadOnlyList<QuestionnaireAnswer> answers, CancellationToken ct)
        {
            var r = await SendAsync("session", HttpMethod.Post, $"{Questionnaires}/v1/responses/validate",
                () => Json(new JObject { ["questionnaireId"] = questionnaireId, ["response"] = Response(answers) }), ct);
            return ParseCheck(JObject.Parse(r.Body));
        }

        /// <summary>The rating these answers would produce, without saving them.</summary>
        public async Task<QuestionnairePreview> PreviewAsync(long universeId, string questionnaireId, IReadOnlyList<QuestionnaireAnswer> answers, CancellationToken ct)
        {
            var r = await SendAsync("session", HttpMethod.Post, $"{Questionnaires}/v1/responses/preview",
                () => Json(new JObject { ["universeId"] = universeId, ["questionnaireId"] = questionnaireId, ["response"] = Response(answers) }), ct);
            return ParsePreview(JObject.Parse(r.Body));
        }

        /// <summary>Submits one questionnaire's answers (the dashboard's per-questionnaire Submit; nothing is published yet).</summary>
        public async Task<QuestionnaireCheck> SubmitAsync(long universeId, string questionnaireId, IReadOnlyList<QuestionnaireAnswer> answers, CancellationToken ct)
        {
            var r = await SendAsync("session", HttpMethod.Post, $"{Questionnaires}/v2/responses/{universeId}/submissions",
                () => Json(new JObject { ["questionnaireId"] = questionnaireId, ["response"] = Response(answers) }), ct);
            return ParseCheck(JObject.Parse(r.Body));
        }

        /// <summary>The older single-questionnaire flow, where submitting is what rates the experience.</summary>
        public async Task<string?> SubmitSingleAsync(long universeId, string questionnaireId, IReadOnlyList<QuestionnaireAnswer> answers, CancellationToken ct)
        {
            var r = await SendAsync("session", HttpMethod.Post, $"{Questionnaires}/v1/responses/{universeId}/submissions",
                () => Json(new JObject { ["questionnaireId"] = questionnaireId, ["response"] = Response(answers) }), ct);
            return (string?)JObject.Parse(r.Body)["submissionId"];
        }

        /// <summary>The rating the submitted questionnaires produce together.</summary>
        public async Task<QuestionnairePreview> PreviewSubmittedAsync(long universeId, IReadOnlyList<string> questionnaireIds, CancellationToken ct)
        {
            var r = await SendAsync("session", HttpMethod.Post, $"{Questionnaires}/v2/responses/preview",
                () => Json(new JObject { ["universeId"] = universeId, ["questionnaireIds"] = new JArray(questionnaireIds) }), ct);
            return ParsePreview(JObject.Parse(r.Body));
        }

        /// <summary>Publishes the submitted answers: the experience gets the rating they produce.</summary>
        public async Task PublishAsync(long universeId, IReadOnlyList<string> questionnaireIds, CancellationToken ct) =>
            await SendAsync("session", HttpMethod.Post, $"{Questionnaires}/v2/responses/publish",
                () => Json(new JObject { ["universeId"] = universeId, ["questionnaireIds"] = new JArray(questionnaireIds) }), ct);

        private static StringContent Json(JObject body) => new(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

        private static JObject Response(IReadOnlyList<QuestionnaireAnswer> answers) =>
            new() { ["answers"] = new JArray(answers.Select(a => new JObject { ["questionId"] = a.QuestionId, ["value"] = a.Value })) };

        internal static Questionnaire ParseQuestionnaire(JObject j) => new()
        {
            Id = (string?)j["id"] ?? "",
            Name = (string?)j["name"] ?? "",
            Sections = Sections(j["sections"]),
        };

        private static List<QuestionnaireSection> Sections(JToken? list) =>
            (list as JArray ?? new JArray()).OfType<JObject>().Select(s => new QuestionnaireSection
            {
                Id = (string?)s["id"] ?? "",
                Name = (string?)s["name"] ?? "",
                Description = Plain((string?)s["description"]),
                Questions = Questions(s["questions"]),
            }).ToList();

        private static List<QuestionnaireQuestion> Questions(JToken? list) =>
            (list as JArray ?? new JArray()).OfType<JObject>().Select(q => new QuestionnaireQuestion
            {
                Id = (string?)q["id"] ?? "",
                Text = Plain((string?)q["text"]),
                Type = (string?)q["type"] ?? "",
                Help = Help(q["helpInfo"]),
                MaxInputLength = (int?)q["maxInputLength"],
                Options = (q["options"] as JArray ?? new JArray()).OfType<JObject>().Select(o => new QuestionnaireOption
                {
                    Id = (string?)o["id"] ?? "",
                    Text = Plain((string?)o["text"]),
                    Help = Help(o["helpInfo"]),
                    ChildQuestions = Questions(o["childQuestions"]),
                    ChildSections = Sections(o["childSections"]),
                }).ToList(),
            }).ToList();

        /// <summary>A question's help panel as one short paragraph: its title, text and examples.</summary>
        private static string Help(JToken? help)
        {
            if (help is not JObject h) return "";
            var parts = new List<string> { Plain((string?)h["title"]), Plain((string?)h["text"]) };
            foreach (var e in h["examples"] as JArray ?? new JArray()) parts.Add(Plain((string?)e["title"]) + " " + Plain((string?)e["text"]));
            string text = string.Join(" ", parts.Where(p => p.Trim().Length > 0)).Trim();
            return text.Length <= 600 ? text : text[..600] + "…";
        }

        /// <summary>Text without the dashboard's markup (HTML tags, {placeholders}, markdown emphasis).</summary>
        private static string Plain(string? s) =>
            string.IsNullOrEmpty(s) ? "" : System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>|\\*\\*|__", " "), "\\s+", " ").Trim();

        private static QuestionnaireCheck ParseCheck(JObject j) => new()
        {
            Valid = (bool?)j["isValid"] ?? true,
            SubmissionId = (string?)j["submissionId"],
            Failures = (j["failures"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(f => ((string?)f["questionId"] ?? "", string.Join("; ", (f["reasons"] as JArray ?? new JArray()).Select(r => r.Type == JTokenType.String ? (string)r! : r.ToString(Formatting.None)))))
                .ToList(),
        };

        internal static QuestionnairePreview ParsePreview(JObject j)
        {
            var age = j["ageRecommendationDetails"]?["summary"]?["ageRecommendation"];
            static string Text(JToken t) => t.Type == JTokenType.String ? (string)t! :
                (string?)t["displayName"] ?? (string?)t["name"] ?? (string?)t["countryName"] ?? (string?)t["contentDescriptorType"] ?? t.ToString(Formatting.None);
            return new QuestionnairePreview
            {
                AgeRating = (string?)age?["displayName"],
                MinimumAge = age?["minimumAge"] is JToken min && min.Type is JTokenType.Integer or JTokenType.Float ? (int)min : int.TryParse((string?)age?["minimumAge"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : null,
                RestrictedCountries = (j["restrictedCountries"] as JArray ?? new JArray()).Select(Text).ToList(),
                Descriptors = (j["contentDescriptorUsages"] as JArray ?? new JArray()).Select(Text).ToList(),
                Error = NullIfBlank((string?)j["userErrorMessage"]),
                Warning = NullIfBlank((string?)j["userWarningMessage"]),
                SubmitDisabled = (bool?)j["disableSubmitButton"] ?? false,
            };
        }

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
