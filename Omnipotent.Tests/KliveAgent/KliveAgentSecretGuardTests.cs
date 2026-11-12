using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Service_Manager;
using Omnipotent.Services.KliveAgent;
using Omnipotent.Services.KliveAgent.Models;

namespace Omnipotent.Tests.KliveAgent;

/// <summary>
/// Where the masker is applied: the record of a run (what the host stores and serves) is masked while the
/// brain's own results stay real; conversations saved before the guard existed are masked in what the
/// routes return and their files are left alone; and the pass that does rewrite those files keeps every
/// original and is off unless its setting is switched on.
///
/// Every key-shaped value is invented and put together at run time (see KliveAgentSecretMaskerTests).
/// </summary>
public class KliveAgentSecretGuardTests
{
    private static string Invented(int length, int seed = 1, string alphabet = KliveAgentSecretMaskerTests.Base62) =>
        KliveAgentSecretMaskerTests.Invented(length, seed, alphabet);

    private static AgentScriptResult Script(string code, string output = "", string? error = null) => new()
    {
        Code = code, ExecutedCode = code, Output = output, Success = error == null, ErrorMessage = error!,
        FailureKind = error == null ? null : "runtime", ExecutionTimeMs = 12, CompileMs = 4, RunMs = 8,
    };

    private static string BearerCall(string token) =>
        "var http = new HttpClient();\nhttp.DefaultRequestHeaders.Add(\"Authorization\", \"Bearer " + token + "\");";

    /// <summary>How the file store writes a record (DataUtil.SerialiseObjectToFile).</summary>
    private static string Stored(object record) =>
        JsonConvert.SerializeObject(record, new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });

    // ── the record of a run ──

    [Fact]
    public void WhatTheHostRecords_IsMasked_WhileTheBrainKeepsItsRealResult()
    {
        var guard = new KliveAgentSecretGuard();
        string token = Invented(40, 1), printed = Invented(32, 2);
        var real = Script(BearerCall(token), output: "{\"api_key\":\"" + printed + "\",\"plan\":\"pro\"}");
        var received = new List<AgentProgressUpdate>();
        var progress = guard.Wrap(received.Add)!;

        progress(new AgentProgressUpdate
        {
            Scripts = new List<AgentScriptResult> { real },
            NewActivity = new AgentActivityEvent { Iteration = 2, Kind = "think", Text = "Calling it with Bearer " + token + " now" },
            StatusNote = "waiting for \"access_token=" + printed + "\"… (4s elapsed)",
            Text = "On it.", Phase = "running", Iteration = 2, PromptTokens = 900,
        });
        progress(new AgentProgressUpdate { Scripts = new List<AgentScriptResult> { real }, Phase = "observing" });

        var recorded = received[0].Scripts[0];
        Assert.NotSame(real, recorded);
        Assert.DoesNotContain(token, recorded.Code);
        Assert.DoesNotContain(token, recorded.ExecutedCode);
        Assert.DoesNotContain(printed, recorded.Output);
        Assert.Contains("\"Bearer [masked:bearer .." + token[^4..] + "]\"", recorded.Code);
        Assert.Equal("{\"api_key\":\"[masked:api-key .." + printed[^4..] + "]\",\"plan\":\"pro\"}", recorded.Output);
        Assert.DoesNotContain(token, received[0].NewActivity.Text);
        Assert.DoesNotContain(printed, received[0].StatusNote);
        // Nothing else about the update is touched.
        Assert.Equal("On it.", received[0].Text);
        Assert.Equal("running", received[0].Phase);
        Assert.Equal(2, received[0].Iteration);
        Assert.Equal(900, received[0].PromptTokens);
        Assert.Equal("think", received[0].NewActivity.Kind);
        Assert.Equal(2, received[0].NewActivity.Iteration);

        // The brain's own result is what the model is shown: it stays real.
        Assert.Contains(token, real.Code);
        Assert.Contains(printed, real.Output);

        // Masked once, then the same copy every time (a run reports its scripts with every update).
        Assert.Same(recorded, received[1].Scripts[0]);
        Assert.Same(recorded, guard.Mask(real));
        Assert.Same(recorded, guard.Mask(recorded));

        // The brain's final answer, from which the conversation is written.
        var response = guard.MaskResponse(new AgentChatResponse { Response = "Done.", Success = true, ScriptsExecuted = new List<AgentScriptResult> { real } })!;
        Assert.Same(recorded, response.ScriptsExecuted[0]);

        // What lands on disk: the run record and the conversation.
        var run = new AgentPendingChatResponse
        {
            ConversationId = "c1", UserMessage = "check my plan", Response = received[0].Text, StatusNote = received[0].StatusNote,
            ScriptsExecuted = new List<AgentScriptResult>(received[1].Scripts), FinalResponse = response,
            Activity = new List<AgentActivityEvent> { received[0].NewActivity },
        };
        var conversation = new AgentConversation { ConversationId = "c1" };
        conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.User, Content = "check my plan" });
        conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.Agent, Content = response.Response, ScriptResults = new List<AgentScriptResult>(response.ScriptsExecuted) });
        foreach (string file in new[] { Stored(run), Stored(conversation) })
        {
            Assert.DoesNotContain(token, file);
            Assert.DoesNotContain(printed, file);
            Assert.Contains("[masked:bearer", file);
        }
        Assert.True(guard.Totals.Total >= 4, guard.Totals.ToString());
    }

    [Fact]
    public void AResultWithNothingToHide_IsRecordedAsItself()
    {
        var guard = new KliveAgentSecretGuard();
        var plain = Script("var services = ListServices();\nOutput(services.Count);", output: "41");
        Assert.Same(plain, guard.Mask(plain));
        var list = guard.Mask(new List<AgentScriptResult> { plain });
        Assert.Same(plain, Assert.Single(list));
        Assert.Null(guard.Wrap(null));
        Assert.Null(guard.Mask((AgentScriptResult?)null));
        Assert.Null(guard.MaskResponse(null));
        Assert.Equal(0, guard.Totals.Total);
    }

    [Fact]
    public void TheMaskedCopy_CarriesEveryOtherField_IncludingOnesAddedLater()
    {
        // Filled by reflection, so a property added to the model tomorrow is covered without editing this test.
        string token = Invented(40, 3);
        var result = Filled<AgentScriptResult>();
        result.Code = BearerCall(token);
        result.ExecutedCode = "// repaired\n" + BearerCall(token);
        result.Output = "token=" + token;
        result.ErrorMessage = "Runtime error: 401 for https://api.example.test/v1?access_token=" + token;
        result.Repairs = new List<string> { "line 2: `await` was added", "line 3: `Bearer " + token + "` was moved to the top" };

        var copy = new KliveAgentSecretGuard().Mask(result)!;

        Assert.NotSame(result, copy);
        foreach (var property in typeof(AgentScriptResult).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? before = property.GetValue(result), after = property.GetValue(copy);
            if (property.Name is nameof(AgentScriptResult.Code) or nameof(AgentScriptResult.ExecutedCode)
                or nameof(AgentScriptResult.Output) or nameof(AgentScriptResult.ErrorMessage))
            {
                Assert.DoesNotContain(token, (string)after!);
                Assert.Contains("[masked:", (string)after!);
            }
            else if (property.Name == nameof(AgentScriptResult.Repairs))
            {
                var notes = (List<string>)after!;
                Assert.Equal("line 2: `await` was added", notes[0]);
                Assert.DoesNotContain(token, notes[1]);
                Assert.Equal(2, ((List<string>)before!).Count);
                Assert.Contains(token, ((List<string>)before!)[1]);   // the original list is not edited
            }
            else Assert.Equal(before, after);
        }

        foreach (var type in new[] { typeof(AgentActivityEvent), typeof(AgentChatResponse) })
        {
            object source = typeof(KliveAgentSecretGuardTests).GetMethod(nameof(Filled), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type).Invoke(null, null)!;
            object twin = typeof(KliveAgentSecretGuard).GetMethod(nameof(KliveAgentSecretGuard.ShallowCopy), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type).Invoke(null, new[] { source })!;
            Assert.NotSame(source, twin);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
                Assert.Equal(property.GetValue(source), property.GetValue(twin));
        }
    }

    /// <summary>An object with every writable property set to something other than its default.</summary>
    private static T Filled<T>() where T : new()
    {
        var value = new T();
        int n = 1;
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            n++;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? filled =
                type == typeof(string) ? property.Name + "-" + n
                : type == typeof(bool) ? true
                : type == typeof(int) ? n
                : type == typeof(long) ? (long)n * 1000
                : type == typeof(DateTime) ? new DateTime(2026, 11, 12, 0, 0, n, DateTimeKind.Utc)
                : type == typeof(List<string>) ? new List<string> { "note " + n }
                : type == typeof(List<AgentScriptResult>) ? new List<AgentScriptResult> { new() { Code = "x" + n } }
                : throw new NotSupportedException($"Teach Filled<T> about {type.Name} ({typeof(T).Name}.{property.Name}).");
            property.SetValue(value, filled);
        }
        return value;
    }

    [Fact]
    public void ScriptFailureStatistics_GetTheMaskedErrorText()
    {
        // What the brain hands to the statistics (a line of each failure's error is kept and served).
        var guard = new KliveAgentSecretGuard();
        string key = Invented(32, 4);
        var failed = Script("await Http(url);", error: "Runtime error: HttpRequestException: 401 (Unauthorized) GET https://api.example.test/v1/me?api_key=" + key);
        var stats = new KliveAgentStats(Path.Combine(Path.GetTempPath(), "kliveagent-stats-" + Guid.NewGuid().ToString("N") + ".json"));

        stats.RecordScriptOutcome(guard.Mask(failed)!, 1);

        string served = JsonConvert.SerializeObject(stats.BuildScriptHealth(7));
        Assert.DoesNotContain(key, served);
        Assert.Contains("api_key=[masked:api-key", served);
    }

    // ── values known to be secret ──

    [Fact]
    public void AKnownValue_IsMaskedInTheRecord_HoweverOrdinaryItLooks()
    {
        string password = "harbour-lantern-" + Invented(6, 5, "abcdefghijklmnopqrstuvwxyz");
        var guard = KliveAgentSecretGuard.WithKnown(new KnownSecret(password, "account-secret"));
        Assert.Equal(1, guard.KnownCount);

        var recorded = guard.Mask(Script("Output(GetObjectMember(account, \"Note\"));", output: "note: " + password + " (set in March)"))!;

        Assert.Equal("note: [masked:account-secret .." + password[^4..] + "] (set in March)", recorded.Output);
        Assert.Equal("typed [masked:account-secret .." + password[^4..] + "]", guard.MaskText("typed " + password));
    }

    [Fact]
    public void AValueThatBecomesKnownLater_IsMaskedInWhatWasAlreadyRecorded()
    {
        var known = new List<KnownSecret>();
        int stamp = 0;
        var guard = new KliveAgentSecretGuard(new KliveAgentSecretGuard.KnownSource(() => "v" + stamp, () => known.ToList()));
        string password = "harbour-lantern-" + Invented(6, 6, "abcdefghijklmnopqrstuvwxyz");
        var result = Script("Output(note);", output: "note: " + password);
        var line = new AgentActivityEvent { Kind = "think", Text = "the note says " + password };

        // Not known yet, and nothing about it looks like a secret.
        Assert.Same(result, guard.Mask(result));
        Assert.Same(line, guard.Mask(line));

        // A script stores it in the account registry: the registry's file changes, and the guard reads it again.
        known.Add(new KnownSecret(password, "account-secret"));
        stamp++;
        guard.Warm();

        var recorded = guard.Mask(result)!;
        Assert.NotSame(result, recorded);
        Assert.DoesNotContain(password, recorded.Output);
        Assert.DoesNotContain(password, guard.Mask(line)!.Text);
        Assert.Same(recorded, guard.Mask(recorded));

        // A source that fails keeps what it last gave.
        var flaky = new KliveAgentSecretGuard(new KliveAgentSecretGuard.KnownSource(
            () => "v" + stamp, () => stamp < 5 ? known.ToList() : throw new IOException("registry file is locked")));
        Assert.Equal(1, flaky.KnownCount);
        stamp = 5;
        flaky.Warm();
        Assert.Equal(1, flaky.KnownCount);
    }

    // ── what the routes return ──

    [Fact]
    public void ARunSavedBeforeTheGuard_IsMaskedInWhatIsServed_AndIsItselfLeftAlone()
    {
        var guard = new KliveAgentSecretGuard();
        string token = Invented(40, 7);
        var old = Script(BearerCall(token), output: "access_token=" + token);
        // As loaded from a run file written before the guard existed.
        var live = new AgentPendingChatResponse
        {
            ConversationId = "c2", UserMessage = "refresh the feed", StatusNote = "sent Authorization: Bearer " + token,
            ScriptsExecuted = new List<AgentScriptResult> { old },
            Activity = new List<AgentActivityEvent> { new() { Kind = "think", Text = "using Bearer " + token }, new() { Kind = "script", Text = "ran script — OK (132ms)" } },
            FinalResponse = new AgentChatResponse { Response = "Refreshed.", Success = true, ScriptsExecuted = new List<AgentScriptResult> { old } },
        };
        string before = Stored(live);

        // What a route is handed: a snapshot with its own lists and the run's own final response.
        AgentPendingChatResponse Snapshot() => new()
        {
            RequestId = live.RequestId, ConversationId = live.ConversationId, UserMessage = live.UserMessage, StatusNote = live.StatusNote,
            ScriptsExecuted = new List<AgentScriptResult>(live.ScriptsExecuted), Activity = new List<AgentActivityEvent>(live.Activity),
            FinalResponse = live.FinalResponse, CreatedAt = live.CreatedAt, UpdatedAt = live.UpdatedAt,
        };

        var served = guard.MaskRunView(Snapshot())!;

        string body = JsonConvert.SerializeObject(served);
        Assert.DoesNotContain(token, body);
        Assert.Contains("[masked:bearer .." + token[^4..] + "]", body);
        Assert.Equal("ran script — OK (132ms)", served.Activity[1].Text);
        Assert.Same(live.Activity[1], served.Activity[1]);
        Assert.Equal("Refreshed.", served.FinalResponse.Response);
        Assert.NotSame(live.FinalResponse, served.FinalResponse);

        // The run in memory, and so its file, is exactly as it was: only the pass behind the setting rewrites files.
        Assert.Equal(before, Stored(live));
        Assert.Same(old, live.ScriptsExecuted[0]);
        Assert.Same(old, live.FinalResponse.ScriptsExecuted[0]);

        // Asked for again (the page polls): the same masked copies, nothing masked twice.
        int masked = guard.Totals.Total;
        var again = guard.MaskRunView(Snapshot())!;
        Assert.Same(served.ScriptsExecuted[0], again.ScriptsExecuted[0]);
        Assert.Same(served.Activity[0], again.Activity[0]);
        Assert.Equal(masked + 1, guard.Totals.Total);   // the status note is a plain string, masked on each read
        Assert.Null(guard.MaskRunView(null));
    }

    [Fact]
    public void AConversationSavedBeforeTheGuard_IsMaskedInWhatIsServed()
    {
        var guard = new KliveAgentSecretGuard();
        string token = Invented(40, 8), legacy = Invented(36, 9);
        var script = Script(BearerCall(token));
        var single = Script("var apiKey = \"" + legacy + "\";");
        // A view's messages are copies with their own lists (KliveAgent.CloneMessage).
        var view = new AgentConversationView
        {
            ConversationId = "c3",
            Messages = new List<AgentMessage>
            {
                new() { Role = AgentMessageRole.User, Content = "use the key sk-is-what-I-call-it" },
                new() { Role = AgentMessageRole.Agent, Content = "Done.", ScriptResults = new List<AgentScriptResult> { script }, ScriptResult = single },
            },
            RecentRuns = new List<AgentPendingChatResponse> { new() { ConversationId = "c3", ScriptsExecuted = new List<AgentScriptResult> { script } } },
        };

        var served = guard.MaskConversationView(view)!;

        string body = JsonConvert.SerializeObject(served);
        Assert.DoesNotContain(token, body);
        Assert.DoesNotContain(legacy, body);
        Assert.Same(served.Messages[1].ScriptResults[0], served.RecentRuns[0].ScriptsExecuted[0]);
        // What Klives typed and what the agent replied are not rewritten.
        Assert.Equal("use the key sk-is-what-I-call-it", served.Messages[0].Content);
        Assert.Equal("Done.", served.Messages[1].Content);
        Assert.Contains(token, script.Code);
        Assert.Null(guard.MaskConversationView(null));
    }

    [Fact]
    public void ReplayedHistory_ExplainsThePlaceholder_OnlyWhenThereIsOne()
    {
        var format = typeof(KliveAgentBrain).GetMethod("FormatHistoryScripts", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Replay(AgentScriptResult script) => (string)format.Invoke(null, new object[]
        {
            new AgentMessage { Role = AgentMessageRole.Agent, Content = "Done.", ScriptResults = new List<AgentScriptResult> { script } }, 2000,
        })!;

        var guard = new KliveAgentSecretGuard();
        string withSecret = Replay(guard.Mask(Script(BearerCall(Invented(40, 10)), output: "200 OK"))!);
        Assert.Contains("[masked:bearer", withSecret);
        Assert.Contains(KliveAgentSecretGuard.PlaceholderNote, withSecret);

        string plain = Replay(Script("Output(ListServices().Count);", output: "41"));
        Assert.DoesNotContain("masked", plain);
    }

    // ── the pass over files already on disk ──

    [Fact]
    public async Task ThePass_IsAskedForByOneSetting_ThatIsCreatedOff()
    {
        Assert.Equal("KliveAgent_MaskStoredConversationsOnce", KliveAgentSecretGuard.ScrubSettingName);
        // OmniSettings encrypts and hides any setting named like a credential ("…Secret…", "…Token");
        // this one is an on/off switch Klives has to be able to see and flip.
        Assert.False(OmniGlobalSettingsManager.IsCredentialName(KliveAgentSecretGuard.ScrubSettingName));

        var asked = new List<(string Name, bool Default)>();
        bool enabled = await KliveAgentSecretGuard.ReadStoredPassSwitch((name, fallback) =>
        {
            asked.Add((name, fallback));
            return Task.FromResult(fallback);   // a setting that has never been saved reads as its default
        });

        Assert.False(enabled);
        Assert.Equal(("KliveAgent_MaskStoredConversationsOnce", false), Assert.Single(asked));
    }

    [Fact]
    public void ThePass_RewritesOnlyFilesThatHoldASecret_AndKeepsEveryOriginal()
    {
        string root = Path.Combine(Path.GetTempPath(), "kliveagent-mask-pass-" + Guid.NewGuid().ToString("N"));
        string conversations = Path.Combine(root, "Conversations"), runs = Path.Combine(root, "ChatRuns"), backups = Path.Combine(root, "backup");
        Directory.CreateDirectory(conversations);
        Directory.CreateDirectory(runs);
        try
        {
            string token = Invented(40, 20), password = "harbour-lantern-" + Invented(6, 21, "abcdefghijklmnopqrstuvwxyz"), typed = Invented(32, 22);
            var known = KnownSecretSet.From(new[] { new KnownSecret(password, "account-secret") });

            // A conversation with a secret in a script, in its output, in the old single-script slot; and, in
            // what Klives typed and in the reply, text that is deliberately not rewritten.
            var leaking = new AgentConversation { ConversationId = "leaking", LastUpdated = new DateTime(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc) };
            leaking.Messages.Add(new AgentMessage { Role = AgentMessageRole.User, Content = "my key is api_key=" + typed, Timestamp = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc) });
            leaking.Messages.Add(new AgentMessage
            {
                Role = AgentMessageRole.Agent, Content = "Stored it. The password is " + password + ".",
                ScriptResults = new List<AgentScriptResult>
                {
                    Script(BearerCall(token), output: "logged in as klives with " + password),
                    Script("Output(1 + 1);", output: "2"),
                },
                ScriptResult = Script("var apiKey = \"" + typed + "\";", error: "Runtime error: 403 for https://api.example.test/?api_key=" + typed),
            });
            var clean = new AgentConversation { ConversationId = "clean" };
            clean.Messages.Add(new AgentMessage { Role = AgentMessageRole.Agent, Content = "41 services.", ScriptResults = new List<AgentScriptResult> { Script("Output(ListServices().Count);", output: "41") } });
            var run = new AgentPendingChatResponse
            {
                RequestId = "run1", ConversationId = "leaking", Status = AgentTaskStatus.Completed, UserMessage = "my key is api_key=" + typed,
                StatusNote = "sent Bearer " + token,
                ScriptsExecuted = new List<AgentScriptResult> { Script(BearerCall(token)) },
                Activity = new List<AgentActivityEvent> { new() { Kind = "think", Text = "typing " + password + " into the form" } },
                FinalResponse = new AgentChatResponse { Response = "Stored it.", ScriptsExecuted = new List<AgentScriptResult> { Script(BearerCall(token)) } },
            };

            string leakingPath = Path.Combine(conversations, "leaking.json"), cleanPath = Path.Combine(conversations, "clean.json");
            string runPath = Path.Combine(runs, "run1.json"), brokenPath = Path.Combine(conversations, "broken.json");
            File.WriteAllText(leakingPath, Stored(leaking));
            File.WriteAllText(cleanPath, Stored(clean));
            File.WriteAllText(runPath, Stored(run));
            File.WriteAllText(brokenPath, "{\"conversationId\":\"broken\",\"messages\":[{\"scriptResults\":[{\"code\":\"Bearer " + token);
            byte[] leakingBefore = File.ReadAllBytes(leakingPath), cleanBefore = File.ReadAllBytes(cleanPath), runBefore = File.ReadAllBytes(runPath), brokenBefore = File.ReadAllBytes(brokenPath);
            var cleanWritten = File.GetLastWriteTimeUtc(cleanPath);
            var folders = new[] { (conversations, "Conversations"), (runs, "ChatRuns"), (Path.Combine(root, "missing"), "Missing") };

            var report = KliveAgentSecretGuard.MaskStoredFiles(folders, backups, known);

            Assert.Equal(4, report.FilesSeen);
            Assert.Equal(2, report.FilesChanged);
            Assert.Equal(1, report.FilesUnreadable);
            Assert.Equal(0, report.FilesFailed);
            Assert.Equal(backups, report.BackupDirectory);
            // conversation: bearer ×2 (code, executed code), the password, the key ×3 (code, executed code, error);
            // run: bearer ×4 (two scripts, code and executed code each), the status note, the activity line.
            Assert.Equal(12, report.Masked.Total);
            Assert.Equal(7, report.Masked.ByKind["bearer"]);
            Assert.Equal(2, report.Masked.ByKind["account-secret"]);
            Assert.Equal(3, report.Masked.ByKind["api-key"]);

            // The report is what gets logged: counts and a folder, never a value.
            string logged = report.ToString();
            Assert.DoesNotContain(token, logged);
            Assert.DoesNotContain(password, logged);
            Assert.DoesNotContain(typed, logged);
            Assert.Contains("2 of 4 file(s) rewritten", logged);
            Assert.Contains("12 (account-secret 2, api-key 3, bearer 7) value(s) masked", logged);

            // Every changed file has its original, byte for byte; the others were not touched or copied.
            Assert.Equal(leakingBefore, File.ReadAllBytes(Path.Combine(backups, "Conversations", "leaking.json")));
            Assert.Equal(runBefore, File.ReadAllBytes(Path.Combine(backups, "ChatRuns", "run1.json")));
            Assert.Equal(2, Directory.GetFiles(backups, "*", SearchOption.AllDirectories).Length);
            Assert.Equal(cleanBefore, File.ReadAllBytes(cleanPath));
            Assert.Equal(cleanWritten, File.GetLastWriteTimeUtc(cleanPath));
            Assert.Equal(brokenBefore, File.ReadAllBytes(brokenPath));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));

            // The rewritten conversation: scripts masked, everything else as it was, and still loadable.
            string rewritten = File.ReadAllText(leakingPath);
            Assert.DoesNotContain(token, rewritten);
            var loaded = JsonConvert.DeserializeObject<AgentConversation>(rewritten)!;
            Assert.Equal("leaking", loaded.ConversationId);
            Assert.Equal(leaking.LastUpdated, loaded.LastUpdated);
            Assert.Equal(leaking.Messages[0].Timestamp, loaded.Messages[0].Timestamp);
            Assert.Equal(leaking.Messages[0].MessageId, loaded.Messages[0].MessageId);
            Assert.Equal("my key is api_key=" + typed, loaded.Messages[0].Content);                    // what Klives typed
            Assert.Equal("Stored it. The password is " + password + ".", loaded.Messages[1].Content);  // what the agent replied
            Assert.Equal("logged in as klives with [masked:account-secret .." + password[^4..] + "]", loaded.Messages[1].ScriptResults[0].Output);
            Assert.Contains("\"Bearer [masked:bearer .." + token[^4..] + "]\"", loaded.Messages[1].ScriptResults[0].Code);
            Assert.Equal("Output(1 + 1);", loaded.Messages[1].ScriptResults[1].Code);
            Assert.Equal("2", loaded.Messages[1].ScriptResults[1].Output);
            Assert.Equal(12, loaded.Messages[1].ScriptResults[0].ExecutionTimeMs);
            Assert.Equal("var apiKey = \"[masked:api-key .." + typed[^4..] + "]\";", loaded.Messages[1].ScriptResult.Code);
            Assert.DoesNotContain(typed, loaded.Messages[1].ScriptResult.ErrorMessage);
            // Apart from the masked strings the document is the one that was there.
            var expected = JToken.Parse(Stored(leaking));
            foreach (var value in expected.SelectTokens("$..scriptResults[*]").Concat(expected.SelectTokens("$..scriptResult")).OfType<JObject>())
                foreach (string field in new[] { "code", "executedCode", "output", "errorMessage" }) value.Remove(field);
            var actual = JToken.Parse(rewritten);
            foreach (var value in actual.SelectTokens("$..scriptResults[*]").Concat(actual.SelectTokens("$..scriptResult")).OfType<JObject>())
                foreach (string field in new[] { "code", "executedCode", "output", "errorMessage" }) value.Remove(field);
            Assert.Equal(expected.ToString(Formatting.None), actual.ToString(Formatting.None));

            // The rewritten run record.
            var loadedRun = JsonConvert.DeserializeObject<AgentPendingChatResponse>(File.ReadAllText(runPath))!;
            Assert.DoesNotContain(token, JsonConvert.SerializeObject(loadedRun.ScriptsExecuted));
            Assert.DoesNotContain(token, JsonConvert.SerializeObject(loadedRun.FinalResponse.ScriptsExecuted));
            Assert.Equal("sent Bearer [masked:bearer .." + token[^4..] + "]", loadedRun.StatusNote);
            Assert.Equal("typing [masked:account-secret .." + password[^4..] + "] into the form", loadedRun.Activity[0].Text);
            Assert.Equal("my key is api_key=" + typed, loadedRun.UserMessage);
            Assert.Equal(AgentTaskStatus.Completed, loadedRun.Status);

            // Run again: nothing left to do, nothing rewritten, no second set of backups.
            byte[] leakingAfter = File.ReadAllBytes(leakingPath);
            var second = KliveAgentSecretGuard.MaskStoredFiles(folders, Path.Combine(root, "backup2"), known);
            Assert.Equal(0, second.FilesChanged);
            Assert.Equal(0, second.Masked.Total);
            Assert.Null(second.BackupDirectory);
            Assert.False(Directory.Exists(Path.Combine(root, "backup2")));
            Assert.Equal(leakingAfter, File.ReadAllBytes(leakingPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ThePass_LeavesAFileAlone_WhenItsOriginalCannotBeKept()
    {
        string root = Path.Combine(Path.GetTempPath(), "kliveagent-mask-pass-" + Guid.NewGuid().ToString("N"));
        string conversations = Path.Combine(root, "Conversations"), backups = Path.Combine(root, "backup");
        Directory.CreateDirectory(conversations);
        try
        {
            var conversation = new AgentConversation { ConversationId = "c9" };
            conversation.Messages.Add(new AgentMessage { Role = AgentMessageRole.Agent, Content = "ok", ScriptResults = new List<AgentScriptResult> { Script(BearerCall(Invented(40, 30))) } });
            string path = Path.Combine(conversations, "c9.json");
            File.WriteAllText(path, Stored(conversation));
            byte[] before = File.ReadAllBytes(path);
            // Something is already where the backup would go: the original cannot be kept, so nothing is rewritten.
            Directory.CreateDirectory(Path.Combine(backups, "Conversations"));
            File.WriteAllText(Path.Combine(backups, "Conversations", "c9.json"), "an earlier backup");

            var report = KliveAgentSecretGuard.MaskStoredFiles(new[] { (conversations, "Conversations") }, backups, null);

            Assert.Equal(1, report.FilesFailed);
            Assert.Equal(0, report.FilesChanged);
            Assert.Equal(0, report.Masked.Total);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal("an earlier backup", File.ReadAllText(Path.Combine(backups, "Conversations", "c9.json")));
            Assert.Empty(Directory.GetFiles(conversations, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void StoredJson_KeepsItsDatesNumbersLayoutAndUnknownFields()
    {
        string token = Invented(40, 40);
        // Fields this version of the model does not have, a date with seven decimals, a large number.
        string compact = "{\"conversationId\":\"c7\",\"futureField\":{\"activity\":\"not a list\",\"n\":12345678901234567890},"
            + "\"lastUpdated\":\"2026-11-01T09:30:00.1234567Z\",\"ratio\":0.5,\"messages\":[{\"role\":\"Agent\",\"content\":\"ok\","
            + "\"scriptResults\":[{\"code\":\"Authorization: Bearer " + token + "\",\"output\":null,\"success\":true,\"executionTimeMs\":3,\"madeUp\":[1,2]}]}]}";
        var tally = new SecretMaskTally();

        string masked = KliveAgentSecretGuard.MaskStoredJson(compact, null, tally)!;

        Assert.Equal(compact.Replace(token, "[masked:bearer .." + token[^4..] + "]"), masked);
        Assert.Equal(1, tally.Total);
        Assert.Null(KliveAgentSecretGuard.MaskStoredJson(masked, null, new SecretMaskTally()));

        // An indented file stays indented.
        string indented = JToken.Parse(compact, new JsonLoadSettings()).ToString(Formatting.Indented);
        string maskedIndented = KliveAgentSecretGuard.MaskStoredJson(indented, null, new SecretMaskTally())!;
        Assert.Contains("\n  \"conversationId\": \"c7\"", maskedIndented.Replace("\r", ""));
        Assert.DoesNotContain(token, maskedIndented);
    }

    // ── where the known values come from ──

    [Fact]
    public async Task SensitiveSettings_AreHandedOverForMasking_AndOnlyOnceTheyHaveLoaded()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "kliveagent-mask-settings-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "settings");
        Directory.CreateDirectory(directory);
        using var protector = new OmniSettingsProtector(RandomNumberGenerator.GetBytes(32));
        try
        {
            var manager = new OmniGlobalSettingsManager(directory, protector);
            // "Not loaded yet" must not read as "there are none".
            Assert.Throws<InvalidOperationException>(() => manager.SensitiveValuesForRedaction());
            await manager.InitializeAsync();
            Assert.Empty(manager.SensitiveValuesForRedaction());

            string key = Invented(40, 50), cookieA = Invented(48, 51), cookieB = Invented(48, 52);
            Assert.True(await manager.SetOmniSetting("Example_ApiKey", key, "svc", "Example service", OmniSettingType.String));
            Assert.True(await manager.SetOmniSetting("Example_Greeting", "hello there, " + Invented(20, 53), "svc", "Example service", OmniSettingType.String));
            Assert.True(await manager.SetOmniSetting("Example_Cookies", JsonConvert.SerializeObject(new[] { cookieA, cookieB }), "svc", "Example service", OmniSettingType.StringList));
            Assert.True(await manager.SetOmniSetting("Example_UseToken", "True", "svc", "Example service", OmniSettingType.Bool));
            Assert.True(await manager.SetOmniSetting("Example_Password", "", "svc", "Example service", OmniSettingType.String));

            var values = manager.SensitiveValuesForRedaction();

            // The key (a credential by name), both cookies of the list; not the plain setting, the flag or the empty one.
            Assert.Equal(new[] { cookieA, cookieB, key }.OrderBy(v => v, StringComparer.Ordinal), values.OrderBy(v => v, StringComparer.Ordinal));

            // And that is enough for the guard to mask one of them wherever it turns up.
            var guard = new KliveAgentSecretGuard(new KliveAgentSecretGuard.KnownSource(
                () => "loaded", () => manager.SensitiveValuesForRedaction().Select(v => new KnownSecret(v, "setting")).ToList()));
            Assert.Equal(3, guard.KnownCount);
            Assert.Equal("cookie jar: [masked:setting .." + cookieB[^4..] + "]", guard.MaskText("cookie jar: " + cookieB));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
