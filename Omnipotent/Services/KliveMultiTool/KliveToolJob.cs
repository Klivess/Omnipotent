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
        public string JobId { get; set; } = Guid.NewGuid().ToString("N")[..12];
        public string ToolName { get; set; } = string.Empty;
        public string FunctionName { get; set; } = string.Empty;
        public KliveToolJobStatus Status { get; set; } = KliveToolJobStatus.Running;
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
        public KliveToolResult? Result { get; set; }
    }
}
