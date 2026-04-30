namespace Omnipotent.Services.KliveMultiTool
{
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class KliveParamAttribute : Attribute
    {
        public string? DisplayName { get; set; }
        public string Description { get; set; } = string.Empty;
        public KliveToolParameterType Type { get; set; } = KliveToolParameterType.Infer;
        public bool Required { get; set; } = true;
        public string? DefaultValue { get; set; }
        public string[]? Options { get; set; }
        public double Min { get; set; } = double.MinValue;
        public double Max { get; set; } = double.MaxValue;
        public double Step { get; set; } = 1.0;
    }
}
