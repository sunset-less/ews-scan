namespace EwsScan;

internal sealed record MemberRow(string Member, int CallSites, IReadOnlyCollection<string> Callers);

/// <summary>One entry of the compatibility list. Workload is set when the answer depends on the kind of item.</summary>
internal sealed record CoverageRow(string Id, string Title, string Status, string? Workload);

internal sealed record OperationRow(
    string Operation,
    int CallSites,
    string Graph,
    string? Note,
    string? Status,
    IReadOnlyList<CoverageRow> Coverage,
    IReadOnlyList<MemberRow> Members);

internal sealed record SignalRow(
    string Id,
    string Title,
    string? Graph,
    string? Note,
    string? Status,
    IReadOnlyList<CoverageRow> Coverage,
    IReadOnlyList<MemberRow> Evidence);

internal sealed record AssemblyReport(
    string Name,
    string Path,
    string Library,
    IReadOnlyList<string> Workloads,
    IReadOnlyList<OperationRow> Operations,
    IReadOnlyList<SignalRow> Signals,
    IReadOnlyList<MemberRow> Unrecognized);

internal sealed record ScanReport(string Root, int FilesScanned, IReadOnlyList<AssemblyReport> Assemblies);

internal static class ReportBuilder
{
    private static readonly string[] Accessors = ["get_", "set_", "add_", "remove_", "End", "."];

    public static AssemblyReport Build(AssemblyUsage usage, Catalog catalog)
    {
        var workloads = Catalog.AllWorkloads
            .Where(workload => usage.Types.Any(type => catalog.Workloads.GetValueOrDefault(type) == workload))
            .ToArray();

        var order = catalog.Operations.Keys.ToList();
        var operations = usage.Members
            .Select(member => (Row: Row(member), Target: catalog.OperationOf(member.Key)))
            .Where(use => use.Target is not null)
            .GroupBy(use => use.Target!.Value.Operation)
            .OrderBy(group => order.IndexOf(group.Key))
            .Select(group =>
            {
                var coverage = group
                    .SelectMany(use => catalog.CoverageIds(group.Key, use.Target!.Value.Workload is { } fixedKind ? [fixedKind] : workloads))
                    .Distinct()
                    .ToArray();
                var status = catalog.WorstStatus(coverage.Select(area => area.Id));
                return new OperationRow(
                    group.Key,
                    group.Sum(use => use.Row.CallSites),
                    catalog.GraphOf(group.Key, status),
                    catalog.Operations.GetValueOrDefault(group.Key)?.Note,
                    status,
                    CoverageRows(coverage, catalog),
                    group.Select(use => use.Row).OrderByDescending(row => row.CallSites).ToArray());
            })
            .ToArray();

        var signals = catalog.Signals
            .Select(signal => (Signal: signal, Evidence: Evidence(signal, usage)))
            .Where(found => found.Evidence.Count > 0)
            .Select(found =>
            {
                (string?, string)[] coverage = found.Signal.Coverage is { } id ? [(null, id)] : [];
                return new SignalRow(
                    found.Signal.Id,
                    found.Signal.Title,
                    found.Signal.Graph,
                    found.Signal.Note,
                    found.Signal.Coverage is null ? null : catalog.Entries[found.Signal.Coverage].Status,
                    CoverageRows(coverage, catalog),
                    found.Evidence);
            })
            .ToArray();

        var unrecognized = usage.Members
            .Where(member => member.Key.StartsWith("ExchangeService.", StringComparison.Ordinal)
                && catalog.OperationOf(member.Key) is null
                && !Accessors.Any(prefix => member.Key.AsSpan("ExchangeService.".Length).StartsWith(prefix)))
            .Select(Row)
            .ToArray();

        return new AssemblyReport(usage.Name, usage.Path, usage.Library, workloads, operations, signals, unrecognized);
    }

    private static List<MemberRow> Evidence(Signal signal, AssemblyUsage usage)
    {
        var evidence = new List<MemberRow>();
        foreach (var name in signal.Enums ?? [])
        {
            foreach (var value in signal.Values ?? [])
            {
                if (usage.Constants.TryGetValue($"{name}.{value}", out var use))
                {
                    evidence.Add(new MemberRow($"{name}.{value}", use.CallSites, use.Callers));
                }
            }
        }
        foreach (var type in signal.Types ?? [])
        {
            if (!usage.Types.Contains(type))
            {
                continue;
            }
            var members = usage.Members.Where(member => member.Key.StartsWith(type + ".", StringComparison.Ordinal)).ToArray();
            evidence.Add(new MemberRow(type, members.Sum(member => member.Value.CallSites), members.SelectMany(member => member.Value.Callers).Distinct().Order().ToArray()));
        }
        foreach (var member in signal.Members ?? [])
        {
            if (usage.Members.TryGetValue(member, out var use))
            {
                evidence.Add(new MemberRow(member, use.CallSites, use.Callers));
            }
        }
        return evidence;
    }

    private static MemberRow Row(KeyValuePair<string, MemberUse> member) => new(member.Key, member.Value.CallSites, member.Value.Callers);

    private static CoverageRow[] CoverageRows(IEnumerable<(string? Workload, string Id)> areas, Catalog catalog) =>
        areas.Select(area => new CoverageRow(area.Id, catalog.Entries[area.Id].Title, catalog.Entries[area.Id].Status, area.Workload)).ToArray();
}

internal static class FolderScanner
{
    private static readonly string[] SkippedFolders = ["obj", "ref", "refint"];

    public static ScanReport Scan(string path, Catalog catalog)
    {
        var root = Path.GetFullPath(path);
        var files = File.Exists(root)
            ? [root]
            : Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".dll" or ".exe")
                .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).SkipLast(1).Any(folder => SkippedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
                .ToArray();

        var scanner = new AssemblyScanner(catalog);
        // A build leaves copies of an assembly in several folders; the newest copy is reported.
        var assemblies = files
            .Select(scanner.Scan)
            .OfType<AssemblyUsage>()
            .GroupBy(usage => usage.Name, StringComparer.OrdinalIgnoreCase)
            .Select(copies => copies.MaxBy(usage => File.GetLastWriteTimeUtc(usage.Path))!)
            .OrderBy(usage => usage.Name, StringComparer.OrdinalIgnoreCase)
            .Select(usage => ReportBuilder.Build(usage, catalog))
            .ToArray();
        return new ScanReport(root, files.Length, assemblies);
    }
}
