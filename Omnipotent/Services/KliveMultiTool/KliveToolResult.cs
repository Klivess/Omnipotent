using Newtonsoft.Json;

namespace Omnipotent.Services.KliveMultiTool
{
    public class KliveToolResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("output")]
        public string Output { get; set; } = string.Empty;

        [JsonProperty("errorMessage")]
        public string? ErrorMessage { get; set; }

        public static KliveToolResult Ok(string output) =>
            new() { Success = true, Output = output };

        public static KliveToolResult Fail(string error, string output = "") =>
            new() { Success = false, ErrorMessage = error, Output = output };
    }
}
