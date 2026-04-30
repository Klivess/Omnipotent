using Newtonsoft.Json;

namespace Omnipotent.Services.KliveMultiTool
{
    public enum KliveToolJobStatus
    {
        Running,
        Completed,
        Failed
    }

    public class KliveToolJob
    {
        [JsonProperty("jobId")]
        public string JobId { get; set; } = Guid.NewGuid().ToString("N")[..12];

        [JsonProperty("toolName")]
        public string ToolName { get; set; } = string.Empty;

        [JsonProperty("functionName")]
        public string FunctionName { get; set; } = string.Empty;

        [JsonProperty("status")]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public KliveToolJobStatus Status { get; set; } = KliveToolJobStatus.Running;

        [JsonProperty("startTime")]
        public DateTime StartTime { get; set; } = DateTime.UtcNow;

        [JsonProperty("endTime")]
        public DateTime? EndTime { get; set; }

        [JsonProperty("result")]
        public KliveToolResult? Result { get; set; }
    }
}
