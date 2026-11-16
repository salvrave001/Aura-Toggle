using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AuraToggle;

/// <summary>What the Ryuo IV screen is set to. Stored beside the lighting files and sent as one session.</summary>
internal sealed class RyuoLayout
{
    public const string FileName = "ryuo.json";

    /// <summary>A clip that ships on the screen, used until the screen's own list has been read.</summary>
    public const string DefaultMedia = "RYUO_IV_HW_Info_02.mp4";

    /// <summary>Item strings the screen actually draws. Anything else is stored as an empty slot.</summary>
    public static readonly string[] KnownItems =
    {
        "CPU Temperature",
        "CPU Load",
        "CPU Speed Average",
        "Memory Utilization",
        "GPU Temperature",
        "GPU Load",
        "Date&Time",
    };

    public bool Drive { get; set; }

    /// <summary>0 to 100. The screen's own scale tops out at 102, which 100 is sent as.</summary>
    public int Brightness { get; set; } = 85;

    public string TitleColor { get; set; } = "#E5252B";

    public string ContentColor { get; set; } = "#FFFFFF";

    /// <summary><c>Single</c>, <c>Random</c> or <c>Cycle</c>. Anything else blacks the screen out.</summary>
    public string PlayMode { get; set; } = "Single";

    public string Media { get; set; } = DefaultMedia;

    public string[] Items { get; set; } =
    {
        "CPU Load",
        "CPU Speed Average",
        "Memory Utilization",
        "Date&Time",
        "CPU Temperature",
        "GPU Temperature",
    };

    public static RyuoLayout Load()
    {
        var layout = new RyuoLayout();

        using JsonDocument? document = AuraFiles.Read(FileName, JsonValueKind.Object);
        if (document == null)
        {
            return layout;
        }

        JsonElement root = document.RootElement;
        layout.Drive = root.TryGetProperty("drive", out JsonElement drive) &&
            drive.ValueKind == JsonValueKind.True;
        layout.Brightness = Math.Clamp(AuraFiles.JsonNumber(root, "brightness", layout.Brightness), 0, 100);
        layout.TitleColor = Colour(JsonText(root, "titleColor"), layout.TitleColor);
        layout.ContentColor = Colour(JsonText(root, "contentColor"), layout.ContentColor);
        layout.PlayMode = Mode(JsonText(root, "playMode"));
        layout.Media = Clip(JsonText(root, "media"), layout.Media);
        layout.Items = ReadItems(root);
        return layout;
    }

    public void Save()
    {
        string[] items = NormalItems();
        AuraFiles.Write(FileName, writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("drive", Drive);
            writer.WriteNumber("brightness", Brightness);
            writer.WriteString("titleColor", TitleColor);
            writer.WriteString("contentColor", ContentColor);
            writer.WriteString("playMode", PlayMode);
            writer.WriteString("media", Media);
            writer.WritePropertyName("items");
            writer.WriteStartArray();
            foreach (string item in items)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>The value the brightness command carries. 100 on the slider is the panel's real maximum.</summary>
    public int WireBrightness => Brightness >= 100 ? 102 : Brightness;

    public string[] NormalItems()
    {
        var items = new string[6];
        for (int i = 0; i < items.Length; i++)
        {
            string candidate = i < Items.Length ? Items[i] : "";
            items[i] = Array.IndexOf(KnownItems, candidate) >= 0 ? candidate : "";
        }

        return items;
    }

    public string ConfigJson(string? cpu, string? gpu)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("temperature", "Celsius");
            writer.WritePropertyName("waterBlockScreen");
            writer.WriteStartObject();
            writer.WriteBoolean("enable", true);
            writer.WriteBoolean("displayInSleep", true);
            writer.WriteNumber("brightness", WireBrightness);
            writer.WritePropertyName("id");
            writer.WriteStartObject();
            WriteScreen(writer, includeMedia: true);
            writer.WriteEndObject();
            writer.WritePropertyName("fanLCD");
            writer.WriteStartObject();
            writer.WriteString("speed", "");
            writer.WriteString("mode", "");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("spec");
            writer.WriteStartObject();
            writer.WriteString("cpu", ClipText(cpu, 64));
            writer.WriteString("gpu", ClipText(gpu, 64));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Colours and slots only. Sending this does not restart the clip.</summary>
    public string PresetJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteScreen(writer, includeMedia: false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string BrightnessJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("value", WireBrightness);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteScreen(Utf8JsonWriter writer, bool includeMedia)
    {
        writer.WriteString("id", "Customization");
        if (includeMedia)
        {
            writer.WriteString("screenMode", "Full Screen");
            writer.WriteString("playMode", PlayMode is "Single" or "Random" or "Cycle" ? PlayMode : "Single");
            writer.WritePropertyName("media");
            writer.WriteStartArray();
            writer.WriteStringValue(Media);
            writer.WriteEndArray();
        }

        writer.WritePropertyName("settings");
        writer.WriteStartObject();
        writer.WriteString("titleColor", TitleColor);
        writer.WriteString("contentColor", ContentColor);
        writer.WritePropertyName("filter");
        writer.WriteStartObject();
        writer.WriteNull("value");
        writer.WriteNumber("opacity", 100);
        writer.WriteEndObject();
        writer.WritePropertyName("badges");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WritePropertyName("sysinfoDisplay");
        writer.WriteStartArray();
        foreach (string item in NormalItems())
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
        if (includeMedia)
        {
            writer.WriteString("timeZone", Zone());
        }
    }

    private static string Zone() =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out string? iana) &&
        !string.IsNullOrEmpty(iana)
            ? iana
            : "UTC";

    private static string[] ReadItems(JsonElement root)
    {
        var items = new string[6];
        if (!root.TryGetProperty("items", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return new RyuoLayout().Items;
        }

        int index = 0;
        foreach (JsonElement entry in array.EnumerateArray())
        {
            if (index >= items.Length)
            {
                break;
            }

            string text = entry.ValueKind == JsonValueKind.String ? entry.GetString() ?? "" : "";
            items[index++] = Array.IndexOf(KnownItems, text) >= 0 ? text : "";
        }

        return items;
    }

    private static string Colour(string value, string fallback)
    {
        if (value.Length == 7 && value[0] == '#' &&
            int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return value.ToUpperInvariant();
        }

        return fallback;
    }

    private static string Mode(string value) =>
        value is "Single" or "Random" or "Cycle" ? value : "Single";

    private static string Clip(string value, string fallback)
    {
        if (value.Length == 0 || value.Length > 80)
        {
            return fallback;
        }

        foreach (char character in value)
        {
            if (character is '\\' or '/' or '"' or '\r' or '\n' || char.IsControl(character))
            {
                return fallback;
            }
        }

        return value;
    }

    private static string ClipText(string? value, int max)
    {
        // A name that has not been read yet is null. Trimming that used to throw on the first
        // configuration and drop the session before the screen ever left its dimmed clip.
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        value = value.Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static string JsonText(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
