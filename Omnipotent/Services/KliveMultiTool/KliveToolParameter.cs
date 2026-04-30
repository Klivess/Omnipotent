namespace Omnipotent.Services.KliveMultiTool
{
    public enum KliveToolParameterType
    {
        Infer = -1,
        String,
        Int,
        Bool,
        Float,
        Json,
        MultiLineText,
        Dropdown,
        MultiSelect,
        FilePath,
        DateTimeInput,
        Color,
        Slider,
        Password
    }

    public class KliveToolParameter
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public KliveToolParameterType Type { get; set; } = KliveToolParameterType.String;
        public bool Required { get; set; } = true;
        public string? DefaultValue { get; set; }
        public List<string>? Options { get; set; }   // Dropdown / MultiSelect
        public double? Min { get; set; }             // Slider / Int range
        public double? Max { get; set; }
        public double? Step { get; set; }
    }
}
