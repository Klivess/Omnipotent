namespace Omnipotent.Services.KliveGames.Models
{
    public enum ConfigFieldType
    {
        Text = 0,
        Number = 1,
        Boolean = 2,
        Dropdown = 3,
    }

    /// <summary>
    /// Describes a single configurable field (e.g. a well-known server.properties key) so the website
    /// can render a typed editor. Unknown keys are still editable as freeform Text fields.
    /// </summary>
    public class ConfigSchemaField
    {
        /// <summary>Underlying properties key, e.g. "max-players".</summary>
        public string Key { get; set; } = "";

        /// <summary>Human label, e.g. "Max Players".</summary>
        public string Label { get; set; } = "";

        public ConfigFieldType Type { get; set; } = ConfigFieldType.Text;

        /// <summary>Short helper text shown under the field.</summary>
        public string? Description { get; set; }

        /// <summary>Options for <see cref="ConfigFieldType.Dropdown"/>.</summary>
        public List<string> Options { get; set; } = new();

        /// <summary>Category for grouping in the UI (e.g. "General", "World", "Players", "Network").</summary>
        public string Category { get; set; } = "General";

        /// <summary>Current value (filled in when the schema is returned for a specific instance).</summary>
        public string Value { get; set; } = "";
    }
}
