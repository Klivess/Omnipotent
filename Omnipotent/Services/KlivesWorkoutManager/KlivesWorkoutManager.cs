using DSharpPlus.Entities;
using HevySharp;
using Newtonsoft.Json;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;

namespace Omnipotent.Services.KlivesWorkoutManager
{
    public class NewPersonalRecordEventArgs : EventArgs
    {
        public required string ExerciseName { get; set; }
        public required double OldOneRepMaxKg { get; set; }
        public required double NewOneRepMaxKg { get; set; }
        public required double WeightKg { get; set; }
        public required int Reps { get; set; }
        public required string StrengthLevelRating { get; set; }
    }

    public class KlivesWorkoutManager : OmniService
    {

        HevyAPI hevAPI = new HevyAPI();
        public event Func<NewPersonalRecordEventArgs, Task>? OnNewPersonalRecord;

        public KlivesWorkoutManager()
        {
            name = "Klives Workout Manager";
            threadAnteriority = ThreadAnteriority.Low;
        }

        protected override async void ServiceMain()
        {


            string hevyApiKey = await GetDataHandler().ReadDataFromFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesWorkoutManagerHevyAPIKey));
            if (string.IsNullOrEmpty(hevyApiKey))
            {
                string response = (string)await ExecuteServiceMethod<Omnipotent.Services.Notifications.NotificationsService>("SendTextPromptToKlivesDiscord",
                    "Hevy API Key is not set for Klives Workout Manager.",
                    "Get the API key from Hevy developer settings and enter it below.",
                    TimeSpan.FromDays(7), "Enter your Hevy API key", "API key");
                hevyApiKey = response?.Trim();
                if (string.IsNullOrEmpty(hevyApiKey))
                {
                    await ServiceLogError("Hevy API Key was not provided. Exiting Klives Workout Manager service.");
                    await TerminateService();
                    return;
                }
                await GetDataHandler().WriteToFile(OmniPaths.GetPath(OmniPaths.GlobalPaths.KlivesWorkoutManagerHevyAPIKey), hevyApiKey);
                await ServiceLog($"Hevy API Key has been saved.");
            }

            await hevAPI.AuthoriseHevy(hevyApiKey);

            CreateRoutes();
        }

        private async Task<double> GetAllTimeBest1RM(string exerciseTemplateId, string excludeWorkoutId)
        {
            double best = 0;
            int page = 1;
            while (true)
            {
                var history = await hevAPI.GetExerciseHistory(exerciseTemplateId, page, 10);
                if (history?.History == null || history.History.Count == 0)
                    break;

                foreach (var entry in history.History)
                {
                    if (entry.WorkoutId == excludeWorkoutId)
                        continue;

                    foreach (var set in entry.Sets ?? [])
                    {
                        if (set.WeightKg.HasValue && set.WeightKg > 0 && set.Reps.HasValue && set.Reps > 0)
                        {
                            double oneRepMax = StrengthLevel.CalculateOneRepMax(set.WeightKg.Value, set.Reps.Value);
                            if (oneRepMax > best)
                                best = oneRepMax;
                        }
                    }
                }

                if (history.History.Count < 10)
                    break;
                page++;
            }
            return best;
        }

        private async Task CreateRoutes()
        {
            await CreateAPIRoute("workoutmanager/sendWorkout", async (req) =>
            {
                try
                {
                    dynamic json = JsonConvert.DeserializeObject(req.userMessageContent);
                    string workoutId = json.workoutId;
                    var workout = await hevAPI.GetWorkout(workoutId);

                    var embedBuilder = new DiscordEmbedBuilder
                    {
                        Title = $"🏋️ {workout.Title}",
                        Color = new DiscordColor(0x008ee6),
                        Timestamp = DateTimeOffset.UtcNow
                    };

                    if (!string.IsNullOrEmpty(workout.StartTime) && !string.IsNullOrEmpty(workout.EndTime)
                        && DateTime.TryParse(workout.StartTime, out var start) && DateTime.TryParse(workout.EndTime, out var end))
                    {
                        var duration = end - start;
                        embedBuilder.Description = $"Duration: **{(int)duration.TotalMinutes} min** • Exercises: **{workout.Exercises?.Count ?? 0}**";
                    }

                    foreach (var exercise in workout.Exercises ?? [])
                    {
                        var template = await hevAPI.GetExerciseTemplate(exercise.ExerciseTemplateId);
                        string exerciseName = template?.Title ?? "Unknown Exercise";

                        var validSets = (exercise.Sets ?? [])
                            .Where(s => s.WeightKg.HasValue && s.WeightKg > 0 && s.Reps.HasValue && s.Reps > 0)
                            .ToList();

                        if (validSets.Count == 0)
                        {
                            embedBuilder.AddField(exerciseName, "No weighted sets recorded", false);
                            continue;
                        }

                        var bestSet = validSets
                            .OrderByDescending(s => StrengthLevel.CalculateOneRepMax(s.WeightKg!.Value, s.Reps!.Value))
                            .First();

                        double bestOneRepMax = StrengthLevel.CalculateOneRepMax(bestSet.WeightKg!.Value, bestSet.Reps!.Value);

                        string setsOverview = string.Join(" | ", validSets.Select(s => $"{s.WeightKg}kg × {s.Reps}"));

                        string levelText = "";
                        try
                        {
                            var resolved = await StrengthLevel.ResolveHevyExercise(exerciseName);
                            if (resolved != null)
                            {
                                var slResult = await StrengthLevel.CalculateStrengthLevel(new StrengthLevel.StrengthLevelRequest
                                {
                                    Gender = "male",
                                    AgeYears = 19,
                                    BodyMassKg = 80,
                                    Exercise = resolved.NameUrl,
                                    LiftMassKg = bestOneRepMax,
                                    Repetitions = 1
                                });
                                levelText = $" • **{slResult.Level}** {"★".PadRight(slResult.Stars, '★')}";
                            }
                        }
                        catch { }

                        string prIndicator = "";
                        double previousBest = await GetAllTimeBest1RM(exercise.ExerciseTemplateId, workoutId);
                        if (bestOneRepMax > previousBest && previousBest > 0)
                        {
                            prIndicator = " 🎉 **NEW PR!**";

                            try
                            {
                                if (OnNewPersonalRecord != null)
                                {
                                    await OnNewPersonalRecord.Invoke(new NewPersonalRecordEventArgs
                                    {
                                        ExerciseName = exerciseName,
                                        OldOneRepMaxKg = previousBest,
                                        NewOneRepMaxKg = bestOneRepMax,
                                        WeightKg = bestSet.WeightKg!.Value,
                                        Reps = bestSet.Reps!.Value,
                                        StrengthLevelRating = levelText.Length > 0 ? levelText.Trim(' ', '•') : "Unknown"
                                    });
                                }
                            }
                            catch { }
                        }

                        string fieldValue = $"Best: **{bestSet.WeightKg}kg × {bestSet.Reps}** (1RM: {bestOneRepMax}kg){levelText}{prIndicator}\n{setsOverview}";
                        embedBuilder.AddField(exerciseName, fieldValue, false);
                    }

                    var message = new DiscordMessageBuilder().AddEmbed(embedBuilder);
                    await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", message);
                }
                catch (Exception e)
                {
                    await ServiceLogError(e, "Failed to process workout notification.");
                }
            }, HttpMethod.Post, Profiles.KMProfileManager.KMPermissions.Anybody);
        }
    }
}
