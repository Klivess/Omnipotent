using Newtonsoft.Json;

namespace Omnipotent.Data_Handling
{
    /// <summary>
    /// Writes a <see cref="DayOfWeek"/> as its number (Sunday = 0); reads a number, a numeric string or a day name.
    /// Put it on a property to keep it numeric even where a global StringEnumConverter is in force: websites
    /// index day names by number, and "Friday" silently matches nothing there.
    /// </summary>
    public sealed class DayOfWeekNumberConverter : JsonConverter<DayOfWeek>
    {
        public override void WriteJson(JsonWriter writer, DayOfWeek value, JsonSerializer serializer) => writer.WriteValue((int)value);

        public override DayOfWeek ReadJson(JsonReader reader, Type objectType, DayOfWeek existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            int? number = reader.TokenType switch
            {
                JsonToken.Integer => Convert.ToInt32(reader.Value),
                JsonToken.String when int.TryParse((string?)reader.Value, out int n) => n,
                JsonToken.String when Enum.TryParse((string?)reader.Value, true, out DayOfWeek named) => (int)named,
                _ => null,
            };
            return number is >= 0 and <= 6 ? (DayOfWeek)number.Value : throw new JsonSerializationException($"'{reader.Value}' is not a day of the week.");
        }
    }
}
