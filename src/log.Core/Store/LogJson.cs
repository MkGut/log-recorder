using System.Text.Json;
using System.Text.Json.Serialization;
using Log.Core.Shell;

namespace Log.Core.Store;

// Source-generated JSON so serialization works under Native AOT. One context per
// historical option set keeps the on-disk format byte-for-byte compatible.

/// <summary>Web defaults, compact, numeric enums (settings, reminder state, journal lines).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WakeReminderStateStore.PersistedEnvelope))]
[JsonSerializable(typeof(LastCaptureSetup))]
[JsonSerializable(typeof(ShellSettings))]
[JsonSerializable(typeof(SessionJournalRecord))]
internal sealed partial class PlainJson : JsonSerializerContext;

/// <summary>Web defaults, indented, string enums (session and group manifests).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SessionManifest))]
[JsonSerializable(typeof(GroupManifest))]
internal sealed partial class ManifestJson : JsonSerializerContext;

/// <summary>Web defaults, compact, string enums (export job parameters).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SessionExportService.PersistedExportParameters))]
internal sealed partial class CompactJson : JsonSerializerContext;

/// <summary>Web defaults, indented, numeric enums (catalog records and shared files).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(SessionRecord))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<GroupRow>))]
internal sealed partial class CatalogJson : JsonSerializerContext;
