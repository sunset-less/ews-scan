using System.Text;
using System.Text.Json;

namespace EwsScan;

internal static class ReportWriter
{
    public const string MappingUrl = "https://github.com/sunset-less/sunsetless-ews-samples/blob/main/docs/ews-to-graph.md";
    public const string CompatibilityUrl = "https://sunsetless.com/compatibility";

    private const int LabelWidth = 12;
    private static readonly string[] Severity = ["verified", "differences", "unavailable"];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Write(ScanReport report, Catalog catalog, string format, bool callers, string version, int width) => format switch
    {
        "json" => JsonSerializer.Serialize(report, Json) + Environment.NewLine,
        "markdown" => Markdown(report, catalog, callers),
        _ => Text(report, catalog, callers, version, width),
    };

    private static string Text(ScanReport report, Catalog catalog, bool callers, string version, int width)
    {
        var text = new StringBuilder();
        text.AppendLine($"ews-scan {version}");
        text.AppendLine($"Scanned {Count(report.FilesScanned, "file")} in {report.Root}. {Summary(report)}");
        if (report.Assemblies.Count == 0)
        {
            text.AppendLine();
            Wrap(text, "", "ews-scan reads compiled assemblies (.dll and .exe). Build the project first, or point it at the folder the application is installed in.", width);
            return text.ToString();
        }

        foreach (var assembly in report.Assemblies)
        {
            text.AppendLine();
            text.AppendLine($"== {assembly.Name} ({Location(report, assembly)})");
            Line(text, "Library", assembly.Library, width);
            if (assembly.Workloads.Count > 0)
            {
                Line(text, "Works with", string.Join(", ", assembly.Workloads), width);
            }

            foreach (var operation in assembly.Operations)
            {
                text.AppendLine();
                text.AppendLine($"{operation.Operation}, {Count(operation.CallSites, "call site")}");
                Line(text, "Calls", Members(operation.Members, callers), width);
                Line(text, "Graph", operation.Graph, width);
                Line(text, "Watch for", operation.Note, width);
                Line(text, "Sunsetless", Status(operation.Coverage, catalog, link: false), width);
            }

            if (assembly.Signals.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Also in this code");
            }
            foreach (var signal in assembly.Signals)
            {
                text.AppendLine();
                text.AppendLine(signal.Title);
                Line(text, "Found", Members(signal.Evidence, callers), width);
                Line(text, "Graph", signal.Graph, width);
                Line(text, "Watch for", signal.Note, width);
                Line(text, "Sunsetless", signal.Coverage.Count > 0 ? Status(signal.Coverage, catalog, link: false) : null, width);
            }

            if (assembly.Unrecognized.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("ExchangeService calls this version does not know");
                Line(text, "Calls", Members(assembly.Unrecognized, callers), width);
            }

            text.AppendLine();
            Line(text, "Summary", Totals(assembly, catalog), width);
        }

        text.AppendLine();
        Wrap(text, "", $"Graph calls and notes: Microsoft's mapping table and tests on Exchange Online, {MappingUrl}", width);
        Wrap(text, "", $"Sunsetless lines: whether Sunsetless EWS, a commercial library from the authors of this tool, runs the call on Graph with the EWS code unchanged, {CompatibilityUrl}", width);
        return text.ToString();
    }

    private static string Markdown(ScanReport report, Catalog catalog, bool callers)
    {
        var text = new StringBuilder();
        text.AppendLine("# EWS Managed API usage");
        text.AppendLine();
        text.AppendLine($"Scanned {Count(report.FilesScanned, "file")} in `{report.Root}`. {Summary(report)}");

        foreach (var assembly in report.Assemblies)
        {
            text.AppendLine();
            text.AppendLine($"## {assembly.Name}");
            text.AppendLine();
            text.AppendLine($"`{Location(report, assembly)}`, built against {assembly.Library}."
                + (assembly.Workloads.Count > 0 ? $" Works with {string.Join(", ", assembly.Workloads)}." : ""));
            text.AppendLine();
            text.AppendLine("| EWS operation | Call sites | Calls | Microsoft Graph | Watch out for | Sunsetless EWS |");
            text.AppendLine("|---|---|---|---|---|---|");
            foreach (var operation in assembly.Operations)
            {
                text.AppendLine(Cells(operation.Operation, operation.CallSites.ToString(), Members(operation.Members, callers), operation.Graph, operation.Note, Status(operation.Coverage, catalog, link: true)));
            }

            if (assembly.Signals.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("| Also in this code | Found | Microsoft Graph | Watch out for | Sunsetless EWS |");
                text.AppendLine("|---|---|---|---|---|");
                foreach (var signal in assembly.Signals)
                {
                    text.AppendLine(Cells(signal.Title, Members(signal.Evidence, callers), signal.Graph, signal.Note, signal.Coverage.Count > 0 ? Status(signal.Coverage, catalog, link: true) : ""));
                }
            }

            if (assembly.Unrecognized.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"ExchangeService calls this version does not know: {Members(assembly.Unrecognized, callers)}.");
            }

            text.AppendLine();
            text.AppendLine(Totals(assembly, catalog));
        }

        text.AppendLine();
        text.AppendLine($"Graph calls and notes come from Microsoft's mapping table and from [tests on Exchange Online]({MappingUrl}). The last column says whether [Sunsetless EWS]({CompatibilityUrl}), a commercial library from the authors of this tool, runs the call on Graph with the EWS code unchanged.");
        return text.ToString();
    }

    private static string Location(ScanReport report, AssemblyReport assembly)
    {
        var relative = Path.GetRelativePath(report.Root, assembly.Path);
        // A single scanned file is its own root.
        return relative == "." ? Path.GetFileName(assembly.Path) : relative;
    }

    private static string Summary(ScanReport report) => report.Assemblies.Count switch
    {
        0 => "None of them calls the EWS Managed API.",
        1 => "1 assembly calls the EWS Managed API.",
        var count => $"{count} assemblies call the EWS Managed API.",
    };

    private static string Totals(AssemblyReport assembly, Catalog catalog)
    {
        var without = assembly.Operations.Count(operation => operation.Graph.StartsWith("none", StringComparison.Ordinal));
        var runs = assembly.Operations.Count(operation => operation.Status is "verified" or "differences");
        var impossible = assembly.Operations.Where(operation => operation.Status == "unavailable").Select(operation => operation.Operation)
            .Concat(assembly.Signals.Where(signal => signal.Status == "unavailable").Select(signal => signal.Title))
            .ToArray();
        return $"{Count(assembly.Operations.Count, "EWS operation")}, {without} without a Graph call. Sunsetless EWS runs {runs} of {assembly.Operations.Count}."
            + (impossible.Length > 0 ? $" {catalog.Statuses["unavailable"]}: {string.Join(", ", impossible)}." : "");
    }

    private static string Members(IEnumerable<MemberRow> members, bool callers) => string.Join(", ", members.Select(member =>
        (member.CallSites > 0 ? $"{member.Member} ({member.CallSites})" : member.Member)
        + (callers && member.Callers.Count > 0 ? $" in {string.Join(", ", member.Callers)}" : "")));

    /// <summary>"Verified", or "Verified (mail, calendar); Verified with differences (tasks)" when the kinds of items differ.</summary>
    private static string Status(IReadOnlyList<CoverageRow> coverage, Catalog catalog, bool link)
    {
        if (coverage.Count == 0)
        {
            return "not listed";
        }
        var groups = coverage
            .GroupBy(row => row.Status)
            .OrderBy(group => Array.IndexOf(Severity, group.Key))
            .Select(group =>
            {
                var label = link ? $"[{catalog.Statuses[group.Key]}]({CompatibilityUrl}#{group.First().Id})" : catalog.Statuses[group.Key];
                var kinds = group.Select(row => row.Workload).OfType<string>().Distinct().OrderBy(kind => Array.IndexOf(Catalog.AllWorkloads, kind)).ToArray();
                return (Label: label, Kinds: kinds);
            })
            .ToArray();
        return groups.Length == 1
            ? groups[0].Label
            : string.Join("; ", groups.Select(group => group.Kinds.Length > 0 ? $"{group.Label} ({string.Join(", ", group.Kinds)})" : group.Label));
    }

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string Cells(params string?[] cells) => $"| {string.Join(" | ", cells.Select(cell => (cell ?? "").Replace("|", "\\|")))} |";

    private static void Line(StringBuilder text, string label, string? value, int width)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Wrap(text, $"  {label.PadRight(LabelWidth)}", value, width);
        }
    }

    private static void Wrap(StringBuilder text, string prefix, string value, int width)
    {
        var line = new StringBuilder(prefix);
        foreach (var word in value.Split(' '))
        {
            if (line.Length > prefix.Length && line.Length + 1 + word.Length > width)
            {
                text.AppendLine(line.ToString());
                line.Clear().Append(' ', prefix.Length);
            }
            if (line.Length > prefix.Length)
            {
                line.Append(' ');
            }
            line.Append(word);
        }
        text.AppendLine(line.ToString());
    }
}
