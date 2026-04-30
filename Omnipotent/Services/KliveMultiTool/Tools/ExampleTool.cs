using static Omnipotent.Profiles.KMProfileManager;

namespace Omnipotent.Services.KliveMultiTool.Tools
{
    /// <summary>
    /// Example KliveTool demonstrating: multiple functions, typed parameters,
    /// [KliveObservable] state, persistent storage, and all helper methods.
    /// </summary>
    public class ExampleTool : KliveTool
    {
        public ExampleTool()
        {
            Name = "ExampleTool";
            Description = "Demo tool showcasing all KliveMultiTool features.";
        }

        public override KMPermissions RequiredPermission => KMPermissions.Admin;

        // ── Observable state — streamed to the UI on demand ──

        [KliveObservable("Total Echoes")]
        public int TotalEchoes { get; private set; }

        [KliveObservable("Message Log")]
        public List<string> MessageLog { get; private set; } = new();

        // ── Functions ──

        [KliveFunction("Echo", "Echoes a message back the specified number of times.")]
        public async Task<KliveToolResult> Echo(
            [KliveParam(Description = "The message to echo", Required = true)] string message,
            [KliveParam(Description = "How many times to repeat", Type = KliveToolParameterType.Slider, Min = 1, Max = 10, Step = 1, Required = false, DefaultValue = "1")] int repeatCount = 1)
        {
            TotalEchoes++;
            var output = string.Join("\n", Enumerable.Range(1, repeatCount).Select(i => $"Echo {i}: {message}"));
            MessageLog.Add($"[{DateTime.UtcNow:HH:mm:ss}] Echo x{repeatCount}: {message}");
            await Log($"Echo called — message='{message}', repeatCount={repeatCount}");
            return KliveToolResult.Ok(output);
        }

        [KliveFunction("Save Note", "Saves a note to persistent storage under a given key.")]
        public async Task<KliveToolResult> SaveNote(
            [KliveParam(Description = "Key/title for the note", Required = true)] string key,
            [KliveParam(Description = "Note content", Type = KliveToolParameterType.MultiLineText, Required = true)] string content)
        {
            await SaveToolData(key, content);
            await Log($"Saved note '{key}' ({content.Length} chars).");
            return KliveToolResult.Ok($"Note '{key}' saved successfully.");
        }

        [KliveFunction("Load Note", "Loads a previously saved note by key.")]
        public async Task<KliveToolResult> LoadNote(
            [KliveParam(Description = "Key/title of the note to load", Required = true)] string key)
        {
            if (!await ToolDataExists(key))
                return KliveToolResult.Fail($"No note found for key '{key}'.");

            var content = await LoadToolData<string>(key);
            return KliveToolResult.Ok(content ?? string.Empty);
        }
    }
}

