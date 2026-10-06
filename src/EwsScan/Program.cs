using System.Reflection;
using EwsScan;

const string Usage = """
    Usage: ews-scan <path> [options]

      <path>               A folder with compiled assemblies (searched with its subfolders) or one .dll or .exe.

    Options:
      --format <format>    text (default), markdown or json
      --output <file>      Write the report to a file
      --callers            Name the methods each call is made from
      --version            Print the version
      --help               Print this help
    """;

var version = typeof(Catalog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
string? path = null;
string? output = null;
var format = "text";
var callers = false;

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--help" or "-h" or "-?":
            Console.WriteLine(Usage);
            return 0;
        case "--version":
            Console.WriteLine(version);
            return 0;
        case "--callers":
            callers = true;
            break;
        case "--format" when index + 1 < args.Length && args[index + 1] is "text" or "markdown" or "json":
            format = args[++index];
            break;
        case "--output" when index + 1 < args.Length:
            output = args[++index];
            break;
        case var argument when path is null && !argument.StartsWith('-'):
            path = argument;
            break;
        default:
            Console.Error.WriteLine($"Unexpected argument: {args[index]}");
            Console.Error.WriteLine(Usage);
            return 2;
    }
}

if (path is null)
{
    Console.Error.WriteLine(Usage);
    return 2;
}
if (!File.Exists(path) && !Directory.Exists(path))
{
    Console.Error.WriteLine($"Not found: {path}");
    return 2;
}

var catalog = Catalog.Load();
var report = FolderScanner.Scan(path, catalog);
var width = output is null && !Console.IsOutputRedirected ? Math.Clamp(Console.WindowWidth - 1, 60, 120) : 100;
var text = ReportWriter.Write(report, catalog, format, callers, version, width);

if (output is null)
{
    Console.Write(text);
}
else
{
    File.WriteAllText(output, text);
    Console.WriteLine($"{output}: {report.Assemblies.Count} of {report.FilesScanned} scanned files call the EWS Managed API.");
}
return 0;
