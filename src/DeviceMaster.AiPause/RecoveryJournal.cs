using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceMaster.AiPause;

public enum ProviderStage
{
    /// <summary>What was loaded is on disk; nothing has been unloaded yet (or unloading failed).</summary>
    Captured,

    /// <summary>The provider confirmed its models/process are gone.</summary>
    Unloaded,
}

public sealed class ProviderEntry
{
    public ProviderStage Stage { get; set; }

    /// <summary>Provider-specific restore record (opaque here).</summary>
    public JsonElement Record { get; set; }

    public string Summary { get; set; } = "";

    /// <summary>Approximate memory freed, when the provider can tell.</summary>
    public long? Bytes { get; set; }
}

/// <summary>
/// The durable record of a pause. Written BEFORE anything is unloaded and updated after every
/// provider step, so if DeviceMaster or the PC dies mid-pause the next start finds exactly what
/// to bring back. Deleted only once every provider has restored.
/// </summary>
public sealed class RecoveryJournal
{
    public DateTimeOffset PausedAt { get; set; }

    public string Reason { get; set; } = "";

    public List<string> Games { get; set; } = [];

    public Dictionary<string, ProviderEntry> Providers { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Null when there's no journal. Throws on an unreadable one — never silently drop recovery.</summary>
    public static RecoveryJournal? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<RecoveryJournal>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("recovery journal is empty");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
