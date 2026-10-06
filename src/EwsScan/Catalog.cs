using System.Text.Json;

namespace EwsScan;

internal sealed record Operation(string? Soap, string? Coverage, string? Graph, string? Note);

internal sealed record Signal(
    string Id,
    string Title,
    string[]? Enums,
    string[]? Values,
    string[]? Types,
    string[]? Members,
    string? Coverage,
    string? Graph,
    string? Note);

internal sealed record CoverageEntry(string Title, string Status);

/// <summary>What the scanner knows about the EWS Managed API: which member sends which EWS operation and what that operation is in Microsoft Graph.</summary>
internal sealed class Catalog
{
    public static readonly string[] AllWorkloads = ["mail", "calendar", "contacts", "tasks"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] Severity = ["verified", "differences", "unavailable"];

    public required Dictionary<string, Operation> Operations { get; init; }
    public required Dictionary<string, string> Members { get; init; }
    public required Dictionary<string, string> Workloads { get; init; }
    public required Signal[] Signals { get; init; }
    public required Dictionary<string, Dictionary<string, string[]>> EnumFallback { get; init; }
    public required Dictionary<string, string> Statuses { get; init; }
    public required Dictionary<string, CoverageEntry> Entries { get; init; }
    public required Dictionary<string, Dictionary<string, string>> Actions { get; init; }

    public IEnumerable<string> TrackedEnums => Signals.SelectMany(signal => signal.Enums ?? []).Distinct();

    public static Catalog Load()
    {
        var catalog = Read<CatalogFile>("catalog.json");
        var coverage = Read<CoverageFile>("coverage.json");
        return new Catalog
        {
            Operations = catalog.Operations,
            Members = catalog.Members,
            Workloads = catalog.Workloads,
            Signals = catalog.Signals,
            EnumFallback = catalog.EnumFallback,
            Statuses = coverage.Statuses,
            Entries = coverage.Entries,
            Actions = coverage.Actions,
        };
    }

    /// <summary>The operation and, where the member fixes it, the kind of item: "Appointment.Save" gives CreateItem and calendar.</summary>
    public (string Operation, string? Workload)? OperationOf(string member)
    {
        if (!Members.TryGetValue(member, out var value))
        {
            // BeginSyncFolderItems is the APM form of SyncFolderItems.
            var dot = member.IndexOf('.');
            if (dot < 0 || !member.AsSpan(dot + 1).StartsWith("Begin") || !Members.TryGetValue(member[..(dot + 1)] + member[(dot + 6)..], out value))
            {
                return null;
            }
        }
        var at = value.IndexOf('@');
        return at < 0 ? (value, null) : (value[..at], value[(at + 1)..]);
    }

    /// <summary>The coverage entries that answer for an operation, given the kinds of items the code works with.</summary>
    public (string? Workload, string Id)[] CoverageIds(string operation, IReadOnlyCollection<string> workloads)
    {
        var definition = Operations.GetValueOrDefault(operation);
        if (definition?.Coverage is { } fixedEntry)
        {
            return [(null, fixedEntry)];
        }
        if (!Actions.TryGetValue(definition?.Soap ?? operation, out var areas))
        {
            return [];
        }
        if (areas.TryGetValue("any", out var any))
        {
            return [(null, any)];
        }
        var kinds = workloads.Count > 0 ? workloads : AllWorkloads;
        return kinds.Where(areas.ContainsKey).Select(kind => ((string?)kind, areas[kind])).ToArray();
    }

    public string? WorstStatus(IEnumerable<string> coverageIds) =>
        coverageIds.Select(id => Entries[id].Status).OrderByDescending(status => Array.IndexOf(Severity, status)).FirstOrDefault();

    public string GraphOf(string operation, string? status) =>
        Operations.GetValueOrDefault(operation)?.Graph ?? (status == "unavailable" ? "none" : "not in Microsoft's mapping table");

    private static T Read<T>(string resource)
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing resource {resource}.");
        return JsonSerializer.Deserialize<T>(stream, Json)
            ?? throw new InvalidOperationException($"Resource {resource} is empty.");
    }

    private sealed record CatalogFile(
        Dictionary<string, Operation> Operations,
        Dictionary<string, string> Members,
        Dictionary<string, string> Workloads,
        Signal[] Signals,
        Dictionary<string, Dictionary<string, string[]>> EnumFallback);

    private sealed record CoverageFile(
        Dictionary<string, string> Statuses,
        Dictionary<string, CoverageEntry> Entries,
        Dictionary<string, Dictionary<string, string>> Actions);
}
