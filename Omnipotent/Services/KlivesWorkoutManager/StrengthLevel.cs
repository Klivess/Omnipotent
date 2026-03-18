using System.Net.Http;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Omnipotent.Services.KlivesWorkoutManager
{
    public class StrengthLevel
    {
        private static readonly HttpClient httpClient = new HttpClient();

        public static double CalculateOneRepMax(double weightKg, int reps)
        {
            if (reps <= 0 || weightKg <= 0) return 0;
            if (reps == 1) return weightKg;
            //Brzycki formula
            return (weightKg / (1.0278 - (0.0278 * reps)));

        }

        public class StrengthLevelRequest
        {
            public string Gender { get; set; }
            public int AgeYears { get; set; }
            public double BodyMassKg { get; set; }
            public string Exercise { get; set; }
            public double LiftMassKg { get; set; }
            public int Repetitions { get; set; }
            public int Timezone { get; set; } = 0;
        }

        public class StrengthStandards
        {
            public double Bodyweight { get; set; }
            public double Beginner { get; set; }
            public double Novice { get; set; }
            public double Intermediate { get; set; }
            public double Advanced { get; set; }
            public double Elite { get; set; }
        }

        public class StrengthLevelResponse
        {
            public string Exercise { get; set; } = "";
            public string Level { get; set; } = "";
            public int Stars { get; set; }
            public string StrongerThanPercentage { get; set; } = "";
            public string StrongerThanDescription { get; set; } = "";
            public string BodyweightRatio { get; set; } = "";
            public StrengthStandards Standards { get; set; } = new();
        }

        public static async Task<StrengthLevelResponse> CalculateStrengthLevel(StrengthLevelRequest request)
        {
            var formData = new Dictionary<string, string>
            {
                { "gender", request.Gender },
                { "ageyears", request.AgeYears.ToString() },
                { "bodymass", request.BodyMassKg.ToString() },
                { "bodymassunit", "kg" },
                { "exercise", request.Exercise },
                { "liftmass", request.LiftMassKg.ToString() },
                { "liftmassunit", "kg"},
                { "repetitions", request.Repetitions.ToString() },
                { "timezone", request.Timezone.ToString() },
                { "source", "homepage" },
                { "modalsearch", "" },
                { "modalbodypart", "" },
                { "modalcategory", "" }
            };

            var content = new FormUrlEncodedContent(formData);

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, "https://strengthlevel.com/")
            {
                Content = content
            };
            requestMessage.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            requestMessage.Headers.Add("Accept-Language", "en-GB,en;q=0.9");
            requestMessage.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");

            var response = await httpClient.SendAsync(requestMessage);
            response.EnsureSuccessStatusCode();
            string html = await response.Content.ReadAsStringAsync();

            return ParseResponse(html, request.Exercise);
        }

        public class ExerciseSearchResult
        {
            [JsonProperty("id")]
            public string Id { get; set; } = "";
            [JsonProperty("name")]
            public string Name { get; set; } = "";
            [JsonProperty("name_url")]
            public string NameUrl { get; set; } = "";
            [JsonProperty("bodypart")]
            public string Bodypart { get; set; } = "";
            [JsonProperty("count")]
            public long Count { get; set; }
            [JsonProperty("category")]
            public string Category { get; set; } = "";
            [JsonProperty("aliases")]
            public List<string> Aliases { get; set; } = [];
            [JsonProperty("icon_url")]
            public string IconUrl { get; set; } = "";
        }

        public static async Task<List<ExerciseSearchResult>> SearchExercises(string query, int limit = 32)
        {
            string url = $"https://strengthlevel.com/api/exercises?limit={limit}&exercise.fields=category,name_url,bodypart,count,aliases,icon_url&query={Uri.EscapeDataString(query)}&standard=true";

            var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
            requestMessage.Headers.Add("Accept", "application/json, text/plain, */*");
            requestMessage.Headers.Add("Accept-Language", "en-GB,en;q=0.9");
            requestMessage.Headers.Add("Referer", "https://strengthlevel.com/");
            requestMessage.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");

            var response = await httpClient.SendAsync(requestMessage);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();

            var parsed = JsonConvert.DeserializeAnonymousType(json, new { data = new List<ExerciseSearchResult>() });
            return parsed?.data ?? [];
        }

        public class ResolvedExercise
        {
            public string NameUrl { get; set; } = "";
            public string Category { get; set; } = "";
            public string Name { get; set; } = "";
        }

        public static async Task<ResolvedExercise?> ResolveHevyExercise(string hevyExerciseName)
        {
            // Parse equipment hint from parentheses, e.g. "Bench Press (Dumbbell)" → base="Bench Press", equipment="Dumbbell"
            string baseName = hevyExerciseName;
            string? equipmentHint = null;

            var parenMatch = Regex.Match(hevyExerciseName, @"^(.+?)\s*\(([^)]+)\)\s*$");
            if (parenMatch.Success)
            {
                baseName = parenMatch.Groups[1].Value.Trim();
                equipmentHint = parenMatch.Groups[2].Value.Trim();
            }

            var results = await SearchExercises(baseName);
            if (results.Count == 0)
                return null;

            // Try to match by category if equipment hint is present
            if (equipmentHint != null)
            {
                var categoryMatch = results.FirstOrDefault(r =>
                    r.Category.Equals(equipmentHint, StringComparison.OrdinalIgnoreCase));
                if (categoryMatch != null)
                    return new ResolvedExercise { NameUrl = categoryMatch.NameUrl, Category = categoryMatch.Category, Name = categoryMatch.Name };

                // Also check if the equipment hint appears in the exercise name or aliases
                var aliasMatch = results.FirstOrDefault(r =>
                    r.Name.Contains(equipmentHint, StringComparison.OrdinalIgnoreCase) ||
                    r.Aliases.Any(a => a.Contains(equipmentHint, StringComparison.OrdinalIgnoreCase)));
                if (aliasMatch != null)
                    return new ResolvedExercise { NameUrl = aliasMatch.NameUrl, Category = aliasMatch.Category, Name = aliasMatch.Name };
            }

            // Fall back to first result (highest relevance)
            var best = results[0];
            return new ResolvedExercise { NameUrl = best.NameUrl, Category = best.Category, Name = best.Name };
        }

        private static StrengthLevelResponse ParseResponse(string html, string exercise)
        {
            var result = new StrengthLevelResponse { Exercise = exercise };

            // Parse level (e.g., "Your Strength Level for Bench Press is Novice")
            var levelMatch = Regex.Match(html,
                @"Your Strength Level for .+? is\s+(\w+)\s*</h2>",
                RegexOptions.Singleline);
            if (levelMatch.Success)
                result.Level = levelMatch.Groups[1].Value.Trim();

            // Parse stars count
            var starsMatch = Regex.Match(html,
                @"<div class=""subtitle is-1"">\s*<span class=""stars""><span class=""star__full"">(★+)</span>",
                RegexOptions.Singleline);
            if (starsMatch.Success)
                result.Stars = starsMatch.Groups[1].Value.Length;

            // Parse "You're stronger than" percentage and description
            var strongerMatch = Regex.Match(html,
                @"You're stronger than</h3>.*?<strong>(\d+%?)</strong>\s*(.*?)\s*</p>",
                RegexOptions.Singleline);
            if (strongerMatch.Success)
            {
                result.StrongerThanPercentage = strongerMatch.Groups[1].Value.Trim().TrimEnd('%') + "%";
                result.StrongerThanDescription = Regex.Replace(strongerMatch.Groups[2].Value.Trim(), "<.*?>", "").Trim();
            }

            // Parse "Your lift is" bodyweight ratio
            var liftMatch = Regex.Match(html,
                @"Your lift is</h3>.*?<strong>(\d+\.?\d*)</strong>\s*times your bodyweight",
                RegexOptions.Singleline);
            if (liftMatch.Success)
                result.BodyweightRatio = liftMatch.Groups[1].Value.Trim();

            // Parse strength standards table
            var tableMatch = Regex.Match(html,
                @"Strength Level boundaries.*?<tbody>\s*<tr>(.*?)</tr>\s*</tbody>",
                RegexOptions.Singleline);
            if (tableMatch.Success)
            {
                var cells = Regex.Matches(tableMatch.Groups[1].Value, @"<td[^>]*>([\d.]+)</td>");
                if (cells.Count >= 6)
                {
                    result.Standards = new StrengthStandards
                    {
                        Bodyweight = double.Parse(cells[0].Groups[1].Value),
                        Beginner = double.Parse(cells[1].Groups[1].Value),
                        Novice = double.Parse(cells[2].Groups[1].Value),
                        Intermediate = double.Parse(cells[3].Groups[1].Value),
                        Advanced = double.Parse(cells[4].Groups[1].Value),
                        Elite = double.Parse(cells[5].Groups[1].Value)
                    };
                }
            }

            return result;
        }
    }
}
