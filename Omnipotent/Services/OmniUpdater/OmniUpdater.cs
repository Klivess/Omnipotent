using DSharpPlus.Entities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Data_Handling;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveBot_Discord;
using System.Net.Http.Headers;

namespace Omnipotent.Services.OmniUpdater
{
    /// <summary>
    /// Watches the master branch of the Omnipotent GitHub repository and
    /// triggers <see cref="ExistentialBotUtilities.UpdateBot"/> when a new
    /// commit is detected. Sends a Discord notification before updating.
    /// </summary>
    public class OmniUpdater : OmniService
    {
        private const string GitHubOwner = "Klivess";
        private const string GitHubRepo = "Omnipotent";
        private const string Branch = "master";
        private const string PollIntervalSettingName = "OmniUpdaterPollIntervalSeconds";
        private const int DefaultPollIntervalSeconds = 15;
        private const int MinimumPollIntervalSeconds = 5;
        private const int MaximumPollIntervalSeconds = 3600;

        private string stateFilePath = string.Empty;
        private string lastSeenSha = string.Empty;
        private string? lastEtag;

        private static readonly HttpClient httpClient = CreateHttpClient();

        public OmniUpdater()
        {
            name = "OmniUpdater";
            threadAnteriority = ThreadAnteriority.Low;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Omnipotent-OmniUpdater/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        protected override async void ServiceMain()
        {
            try
            {
                Directory.CreateDirectory(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniUpdaterDirectory));
                stateFilePath = Path.Combine(OmniPaths.GetPath(OmniPaths.GlobalPaths.OmniUpdaterDirectory), "lastSeenCommit.json");

                await LoadState();

                // If we have no recorded SHA, seed it with the current upstream SHA so we don't
                // trigger an immediate update on first run.
                if (string.IsNullOrEmpty(lastSeenSha))
                {
                    var initial = await FetchLatestCommitSha();
                    if (initial != null)
                    {
                        lastSeenSha = initial.Value.Sha;
                        await SaveState();
                        await ServiceLog($"Seeded last-seen master SHA: {lastSeenSha[..Math.Min(7, lastSeenSha.Length)]}");
                    }
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await CheckForUpdate();
                    }
                    catch (Exception ex)
                    {
                        await ServiceLogError(ex, "OmniUpdater poll iteration failed");
                    }

                    TimeSpan pollInterval = await GetPollInterval();
                    try { await Task.Delay(pollInterval, cancellationToken.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "OmniUpdater service crashed");
            }
        }

        private async Task<TimeSpan> GetPollInterval()
        {
            try
            {
                int configuredSeconds = await GetIntOmniSetting(PollIntervalSettingName, DefaultPollIntervalSeconds);
                int clampedSeconds = Math.Clamp(configuredSeconds, MinimumPollIntervalSeconds, MaximumPollIntervalSeconds);
                if (configuredSeconds != clampedSeconds)
                {
                    await ServiceLogError($"{PollIntervalSettingName}={configuredSeconds} is outside the allowed range ({MinimumPollIntervalSeconds}-{MaximumPollIntervalSeconds}); using {clampedSeconds} seconds.", false);
                }

                return TimeSpan.FromSeconds(clampedSeconds);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"Failed to load {PollIntervalSettingName}; using {DefaultPollIntervalSeconds} seconds.", false);
                return TimeSpan.FromSeconds(DefaultPollIntervalSeconds);
            }
        }

        private async Task CheckForUpdate()
        {
            var latest = await FetchLatestCommitSha();
            if (latest == null) return; // 304 Not Modified or transient failure - nothing to do.

            if (string.IsNullOrEmpty(latest.Value.Sha) || latest.Value.Sha == lastSeenSha)
            {
                return;
            }

            string newSha = latest.Value.Sha;
            string shortSha = newSha[..Math.Min(7, newSha.Length)];
            string commitMessage = latest.Value.Message ?? "(no message)";
            string commitAuthor = latest.Value.Author ?? "unknown";
            string commitUrl = latest.Value.HtmlUrl ?? $"https://github.com/{GitHubOwner}/{GitHubRepo}/commit/{newSha}";

            await ServiceLog($"New master commit detected: {shortSha} by {commitAuthor}");

            // Persist the new SHA before triggering an update so a restart loop can't re-trigger.
            lastSeenSha = newSha;
            await SaveState();

            await NotifyKlives(shortSha, commitMessage, commitAuthor, commitUrl);

            if (OmniPaths.CheckIfOnServer())
            {
                await ServiceLog("Triggering UpdateBot to pull and restart with the new commit.");
                ExistentialBotUtilities.UpdateBot();
            }
            else
            {
                await ServiceLog("Not running on server (env 'server' != 'server'), skipping auto-update.");
            }
        }

        private async Task NotifyKlives(string shortSha, string commitMessage, string commitAuthor, string commitUrl)
        {
            try
            {
                string firstLine = (commitMessage ?? string.Empty).Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
                if (firstLine.Length > 256) firstLine = firstLine[..256] + "...";

                string body = $"**Commit:** [`{shortSha}`]({commitUrl})\n**Author:** {commitAuthor}\n**Message:** {firstLine}\n\n" +
                              (OmniPaths.CheckIfOnServer()
                                  ? "Pulling and restarting now..."
                                  : "Detected on a non-server build, will not auto-update.");

                var embed = KliveBotDiscord.MakeSimpleEmbed(
                    "OmniUpdater: New master commit detected",
                    body,
                    DiscordColor.Green);

                await ExecuteServiceMethod<KliveBotDiscord>("SendMessageToKlives", embed);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to notify Klives about pending update");
            }
        }

        private async Task<(string Sha, string? Message, string? Author, string? HtmlUrl)?> FetchLatestCommitSha()
        {
            string url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/commits/{Branch}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(lastEtag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", lastEtag);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                await ServiceLogError($"GitHub commits API returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
                return null;
            }

            if (response.Headers.ETag != null)
            {
                lastEtag = response.Headers.ETag.Tag;
            }

            string json = await response.Content.ReadAsStringAsync();
            JObject parsed = JObject.Parse(json);
            string? sha = (string?)parsed["sha"];
            string? message = (string?)parsed["commit"]?["message"];
            string? author = (string?)parsed["commit"]?["author"]?["name"];
            string? htmlUrl = (string?)parsed["html_url"];

            if (string.IsNullOrEmpty(sha)) return null;
            return (sha, message, author, htmlUrl);
        }

        private async Task LoadState()
        {
            try
            {
                if (!File.Exists(stateFilePath)) return;
                string raw = await GetDataHandler().ReadDataFromFile(stateFilePath);
                if (string.IsNullOrWhiteSpace(raw)) return;
                var state = JsonConvert.DeserializeObject<UpdaterState>(raw);
                if (state != null)
                {
                    lastSeenSha = state.LastSeenSha ?? string.Empty;
                    lastEtag = state.LastEtag;
                }
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to load OmniUpdater state, starting fresh");
                lastSeenSha = string.Empty;
                lastEtag = null;
            }
        }

        private async Task SaveState()
        {
            try
            {
                var state = new UpdaterState { LastSeenSha = lastSeenSha, LastEtag = lastEtag };
                string json = JsonConvert.SerializeObject(state, Formatting.Indented);
                await GetDataHandler().WriteToFile(stateFilePath, json);
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "Failed to persist OmniUpdater state");
            }
        }

        private class UpdaterState
        {
            public string? LastSeenSha { get; set; }
            public string? LastEtag { get; set; }
        }
    }
}
