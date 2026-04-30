namespace Omnipotent.Services.KliveMultiTool
{
    public class KliveToolResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }

        public static KliveToolResult Ok(string output) =>
            new() { Success = true, Output = output };

        public static KliveToolResult Fail(string error, string output = "") =>
            new() { Success = false, ErrorMessage = error, Output = output };
    }
}
