using System.Text.Json;
using System.Text.Json.Serialization;
using AllMyTasks.Core.Calendar;
using AllMyTasks.Core.Model;
using AllMyTasks.Core.Tasks;

namespace AllMyTasks.Storage.Sqlite;

/// <summary>
/// JSON used for the <c>json</c> column that holds each item as the source of truth. The query columns next to it
/// are derived from the same object. Explicit converters keep Core free of serialisation attributes.
/// </summary>
internal static class ModelJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Stored item was null.");

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new ItemKeyConverter());
        options.Converters.Add(new EventTimeConverter());
        options.Converters.Add(new TaskDueConverter());
        options.Converters.Add(new RecurrenceConverter());
        return options;
    }

    private sealed class ItemKeyConverter : JsonConverter<ItemKey>
    {
        public override ItemKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            return new ItemKey(
                root.GetProperty("source").GetString()!,
                root.GetProperty("collection").GetString()!,
                root.GetProperty("item").GetString()!,
                root.TryGetProperty("rid", out var rid) && rid.ValueKind == JsonValueKind.String ? rid.GetDateTimeOffset() : null);
        }

        public override void Write(Utf8JsonWriter writer, ItemKey value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("source", value.SourceId);
            writer.WriteString("collection", value.CollectionId);
            writer.WriteString("item", value.ItemId);
            if (value.RecurrenceId is { } rid)
            {
                writer.WriteString("rid", rid);
            }

            writer.WriteEndObject();
        }
    }

    private sealed class EventTimeConverter : JsonConverter<EventTime>
    {
        public override EventTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            return root.GetProperty("kind").GetString() switch
            {
                "timed" => new TimedEvent(
                    root.GetProperty("start").GetDateTimeOffset(),
                    root.GetProperty("end").GetDateTimeOffset(),
                    root.TryGetProperty("tz", out var tz) && tz.ValueKind == JsonValueKind.String ? tz.GetString() : null,
                    root.TryGetProperty("floating", out var f) && f.GetBoolean()),
                "allday" => new AllDayEvent(
                    DateOnly.Parse(root.GetProperty("start").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                    DateOnly.Parse(root.GetProperty("end").GetString()!, System.Globalization.CultureInfo.InvariantCulture)),
                var other => throw new JsonException($"Unknown event time kind '{other}'."),
            };
        }

        public override void Write(Utf8JsonWriter writer, EventTime value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case TimedEvent t:
                    writer.WriteString("kind", "timed");
                    writer.WriteString("start", t.Start);
                    writer.WriteString("end", t.End);
                    if (t.TimeZoneId is not null)
                    {
                        writer.WriteString("tz", t.TimeZoneId);
                    }

                    writer.WriteBoolean("floating", t.IsFloating);
                    break;
                case AllDayEvent a:
                    writer.WriteString("kind", "allday");
                    writer.WriteString("start", a.Start.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteString("end", a.EndExclusive.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new JsonException("Unknown event time type.");
            }

            writer.WriteEndObject();
        }
    }

    private sealed class TaskDueConverter : JsonConverter<TaskDue>
    {
        public override TaskDue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            return root.GetProperty("kind").GetString() switch
            {
                "date" => new DueDate(DateOnly.Parse(root.GetProperty("date").GetString()!, System.Globalization.CultureInfo.InvariantCulture)),
                "datetime" => new DueDateTime(
                    root.GetProperty("at").GetDateTimeOffset(),
                    root.TryGetProperty("tz", out var tz) && tz.ValueKind == JsonValueKind.String ? tz.GetString() : null),
                var other => throw new JsonException($"Unknown due kind '{other}'."),
            };
        }

        public override void Write(Utf8JsonWriter writer, TaskDue value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case DueDate d:
                    writer.WriteString("kind", "date");
                    writer.WriteString("date", d.Date.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case DueDateTime dt:
                    writer.WriteString("kind", "datetime");
                    writer.WriteString("at", dt.At);
                    if (dt.TimeZoneId is not null)
                    {
                        writer.WriteString("tz", dt.TimeZoneId);
                    }

                    break;
                default:
                    throw new JsonException("Unknown due type.");
            }

            writer.WriteEndObject();
        }
    }

    private sealed class RecurrenceConverter : JsonConverter<Recurrence>
    {
        public override Recurrence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            return new Recurrence(
                root.GetProperty("rule").GetString() ?? string.Empty,
                [.. root.GetProperty("extra").EnumerateArray().Select(e => e.GetDateTimeOffset())],
                [.. root.GetProperty("excluded").EnumerateArray().Select(e => e.GetDateTimeOffset())]);
        }

        public override void Write(Utf8JsonWriter writer, Recurrence value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("rule", value.Rule);
            writer.WriteStartArray("extra");
            foreach (var d in value.ExtraDates)
            {
                writer.WriteStringValue(d);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("excluded");
            foreach (var d in value.ExcludedDates)
            {
                writer.WriteStringValue(d);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }
}
