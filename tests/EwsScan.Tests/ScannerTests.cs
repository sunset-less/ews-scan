using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace EwsScan.Tests;

public class ScannerTests
{
    private static readonly Catalog Catalog = Catalog.Load();

    // ExchangeService methods the catalog leaves out on purpose.
    private static readonly string[] Unmapped =
    [
        "GetAppMarketplaceUrl", "BindToGroupItems", "FindGroupConversation", "GetGroupConversationItems",
        "RegisterConsent", "SubscribeToGroupPushNotifications",
    ];

    private static string Fixture(string name)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tests = output.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(tests, "Fixtures", name, "bin", output.Parent.Name, output.Name, name + ".dll");
    }

    private static AssemblyReport Report(string path) => Assert.Single(FolderScanner.Scan(path, Catalog).Assemblies);

    [Fact]
    public void ClassicCodeIsGroupedByEwsOperation()
    {
        var report = Report(Fixture("ClassicApp"));

        Assert.Equal("Microsoft.Exchange.WebServices 15.0.0.0", report.Library);
        Assert.Equal(["mail", "calendar", "tasks"], report.Workloads);
        Assert.Equal(
            ["FindItem:1", "GetItem:3", "CreateItem:3", "DeleteItem:2", "GetFolder:2", "SyncFolderItems:1", "Subscribe (streaming):1", "GetStreamingEvents:1"],
            report.Operations.Select(operation => $"{operation.Operation}:{operation.CallSites}"));
        Assert.Equal(["GetAppMarketplaceUrl"], report.Unrecognized.Select(row => row.Member.Split('.')[1]));
    }

    [Fact]
    public void GraphAndCoverageComeFromTheCatalog()
    {
        var report = Report(Fixture("ClassicApp"));

        var streaming = report.Operations.Single(operation => operation.Operation == "Subscribe (streaming)");
        Assert.Equal("none", streaming.Graph);
        Assert.Equal("differences", streaming.Status);

        var create = report.Operations.Single(operation => operation.Operation == "CreateItem");
        Assert.Equal(["Appointment.Save", "Item.Save"], create.Members.Select(member => member.Member));
        Assert.Contains(create.Coverage, row => row.Id == "calendar-items");
        Assert.Contains(create.Coverage, row => row.Id == "tasks");
        Assert.Equal("differences", create.Status);
    }

    [Fact]
    public void LiteralEnumArgumentsAreFound()
    {
        var report = Report(Fixture("ClassicApp"));

        Assert.Equal(
            ["public-folders", "archive", "silent-meetings", "mime", "tasks", "password-sign-in", "windows-sign-in"],
            report.Signals.Select(signal => signal.Id));
        Assert.Equal(["ExchangeServiceBase.set_UseDefaultCredentials"], Evidence(report, "windows-sign-in"));
        Assert.Equal(["WellKnownFolderName.PublicFoldersRoot"], Evidence(report, "public-folders"));
        Assert.Equal(["WellKnownFolderName.ArchiveMsgFolderRoot"], Evidence(report, "archive"));
        Assert.Equal(["SendInvitationsMode.SendToNone", "SendCancellationsMode.SendToNone"], Evidence(report, "silent-meetings"));

        var cancellations = report.Signals.Single(signal => signal.Id == "silent-meetings").Evidence[1];
        Assert.Equal(2, cancellations.CallSites);
        Assert.Equal(["ClassicApp.Sync.QuietMeeting"], cancellations.Callers);
    }

    [Fact]
    public void CallersAreNamedAfterTheSourceMethod()
    {
        var report = Report(Fixture("ClassicApp"));

        var open = report.Operations.Single(operation => operation.Operation == "GetStreamingEvents").Members.Single();
        Assert.Equal(["ClassicApp.Sync.ListenAsync"], open.Callers);

        var bind = report.Operations.Single(operation => operation.Operation == "GetItem").Members.Single(member => member.Member == "Item.Bind");
        Assert.Equal(["ClassicApp.Sync.Tasks"], bind.Callers);
    }

    [Fact]
    public void EnumValuesFollowTheLibraryTheCodeWasBuiltAgainst()
    {
        var report = Report(Fixture("PortApp"));

        Assert.Equal("Microsoft.Exchange.WebServices.NETStandard 0.0.0.0", report.Library);
        Assert.Equal(["WellKnownFolderName.ArchiveRoot"], Evidence(report, "archive"));
        Assert.Equal(["PortApp.ArchiveCount.CountAsync"], report.Signals.Single().Evidence.Single().Callers);
        Assert.Equal(["FindItem", "GetFolder"], report.Operations.Select(operation => operation.Operation));
    }

    [Theory]
    [InlineData("ClassicApp", "WellKnownFolderName.ArchiveMsgFolderRoot")]
    [InlineData("PortApp", "WellKnownFolderName.ArchiveRoot")]
    public void EnumValuesAreKnownWithoutTheLibraryFile(string fixture, string expected)
    {
        var folder = Directory.CreateTempSubdirectory("ews-scan-").FullName;
        try
        {
            File.Copy(Fixture(fixture), Path.Combine(folder, fixture + ".dll"));
            Assert.Equal([expected], Evidence(Report(folder), "archive"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("Microsoft.Exchange.WebServices", "31bf3856ad364e35", "microsoft")]
    [InlineData("Microsoft.Exchange.WebServices", "1b8c25c6fc94dcf1", "sunsetless")]
    [InlineData("Microsoft.Exchange.WebServices", "", "other")]
    [InlineData("Microsoft.Exchange.WebServices.NETStandard", "1b8c25c6fc94dcf1", "other")]
    [InlineData("Microsoft.Exchange.WebServices.NETStandard", "", "other")]
    public void TheEnumNumberingFollowsWhoBuiltTheLibrary(string library, string token, string numbering) =>
        Assert.Equal(numbering, AssemblyScanner.Numbering(library, Convert.FromHexString(token)));

    [Fact]
    public void SunsetlessNumbersFoldersLikeMicrosoftAndKeepsEveryName()
    {
        var folders = Catalog.EnumFallback["WellKnownFolderName"];

        Assert.Equal(folders["microsoft"], folders["sunsetless"].Take(folders["microsoft"].Length));
        Assert.Equal(folders["other"].Order(), folders["sunsetless"].Order());
    }

    [Fact]
    public void TheLibraryItselfIsNotReported()
    {
        var folder = Path.GetDirectoryName(Fixture("PortApp"))!;
        var report = FolderScanner.Scan(folder, Catalog);

        Assert.True(report.FilesScanned > 1);
        Assert.Equal(["PortApp"], report.Assemblies.Select(assembly => assembly.Name));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("markdown")]
    [InlineData("json")]
    public void EveryFormatNamesTheOperations(string format)
    {
        var report = FolderScanner.Scan(Fixture("ClassicApp"), Catalog);
        var text = ReportWriter.Write(report, Catalog, format, callers: true, "1.0.0", 100);

        Assert.Contains("Subscribe (streaming)", text);
        Assert.Contains("ClassicApp.Sync.QuietMeeting", text);
        Assert.Contains("PublicFoldersRoot", text);
    }

    [Fact]
    public void ASingleFileIsNamedInTheReport()
    {
        var report = FolderScanner.Scan(Fixture("ClassicApp"), Catalog);

        Assert.Contains("== ClassicApp (ClassicApp.dll)", ReportWriter.Write(report, Catalog, "text", callers: false, "1.0.0", 100));
        Assert.Contains("`ClassicApp.dll`, built against", ReportWriter.Write(report, Catalog, "markdown", callers: false, "1.0.0", 100));
    }

    [Fact]
    public void TheCatalogIsConsistent()
    {
        foreach (var (member, value) in Catalog.Members)
        {
            var operation = value.Split('@')[0];
            Assert.True(Catalog.Operations.ContainsKey(operation), $"{member} names the unknown operation {operation}");
            if (value.Contains('@'))
            {
                Assert.Contains(value.Split('@')[1], Catalog.AllWorkloads);
            }
        }
        foreach (var (name, operation) in Catalog.Operations)
        {
            Assert.True(Catalog.Members.Values.Any(value => value.Split('@')[0] == name), $"no member sends {name}");
            if (operation.Coverage is { } coverage)
            {
                Assert.True(Catalog.Entries.ContainsKey(coverage), $"{name} names the unknown coverage entry {coverage}");
            }
            else
            {
                Assert.True(Catalog.Actions.ContainsKey(operation.Soap ?? name), $"{name} has no coverage entry");
            }
        }
        foreach (var signal in Catalog.Signals.Where(signal => signal.Coverage is not null))
        {
            Assert.True(Catalog.Entries.ContainsKey(signal.Coverage!), $"{signal.Id} names the unknown coverage entry {signal.Coverage}");
        }
        Assert.All(Catalog.Workloads.Values, workload => Assert.Contains(workload, Catalog.AllWorkloads));
    }

    [Theory]
    [InlineData("ClassicApp", "Microsoft.Exchange.WebServices.dll")]
    [InlineData("PortApp", "Microsoft.Exchange.WebServices.NETStandard.dll")]
    public void EveryExchangeServiceMethodIsInTheCatalog(string fixture, string library)
    {
        using var stream = File.OpenRead(Path.Combine(Path.GetDirectoryName(Fixture(fixture))!, library));
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var service = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(type =>
            reader.GetString(type.Name) == "ExchangeService" && reader.GetString(type.Namespace) == "Microsoft.Exchange.WebServices.Data");

        var missing = service.GetMethods()
            .Select(reader.GetMethodDefinition)
            .Where(method => (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public
                && (method.Attributes & MethodAttributes.SpecialName) == 0)
            .Select(method => reader.GetString(method.Name))
            .Distinct()
            .Where(name => !name.StartsWith("End") && !Unmapped.Contains(name) && Catalog.OperationOf("ExchangeService." + name) is null)
            .ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData(@"\\host\share\x")]
    [InlineData("//host/share/x")]
    [InlineData(@"..\x")]
    [InlineData("../x")]
    [InlineData(@"C:\x")]
    [InlineData("C:x")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public void OnlyAPlainLibraryNameIsLookedUpNextToTheAssembly(string libraryName) =>
        Assert.Null(AssemblyScanner.LibraryFile(Path.GetTempPath(), libraryName));

    [Fact]
    public void ThePlainLibraryNameIsLookedUpNextToTheAssembly()
    {
        var folder = Path.GetTempPath();

        Assert.Equal(
            Path.Combine(folder, "Microsoft.Exchange.WebServices.dll"),
            AssemblyScanner.LibraryFile(folder, "Microsoft.Exchange.WebServices"));
    }

    [Theory]
    [InlineData(new byte[] { 0x28, 0x01, 0x00, 0x00, 0x77 })]
    [InlineData(new byte[] { 0x29, 0x01, 0x00, 0x00, 0x0A })]
    public void AMethodWithBrokenILIsSkipped(byte[] il)
    {
        var folder = Directory.CreateTempSubdirectory("ews-scan-").FullName;
        try
        {
            var file = Path.Combine(folder, "ClassicApp.dll");
            var bytes = File.ReadAllBytes(Fixture("ClassicApp"));
            il.CopyTo(bytes, FirstMethodBody(bytes, il.Length));
            File.WriteAllBytes(file, bytes);

            Assert.Equal("ClassicApp", Report(file).Name);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ALoopOfNestedTypesDoesNotOverflowTheStack()
    {
        var metadata = new MetadataBuilder();
        var name = metadata.GetOrAddString("Loop");
        metadata.AddModule(0, name, metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        var type = metadata.AddTypeDefinition(default, default, name, default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        metadata.AddNestedType(type, type);
        var reference = metadata.AddTypeReference(MetadataTokens.TypeReferenceHandle(1), default, name);
        var image = new BlobBuilder();
        new MetadataRootBuilder(metadata).Serialize(image, 0, 0);
        using var provider = MetadataReaderProvider.FromMetadataImage(image.ToImmutableArray());
        var reader = provider.GetMetadataReader();

        Assert.Throws<BadImageFormatException>(() => TypeNames.Instance.GetTypeFromDefinition(reader, type, 0));
        Assert.Throws<BadImageFormatException>(() => TypeNames.Instance.GetTypeFromReference(reader, reference, 0));
    }

    [Fact]
    public void ALinkBackToAParentFolderIsNotFollowed()
    {
        var folder = Directory.CreateTempSubdirectory("ews-scan-").FullName;
        try
        {
            File.Copy(Fixture("ClassicApp"), Path.Combine(folder, "ClassicApp.dll"));
            var loop = Path.Combine(folder, "loop");
            if (OperatingSystem.IsWindows())
            {
                // A junction needs no extra privilege, unlike a symbolic link.
                using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{loop}\" \"{folder}\"") { UseShellExecute = false, RedirectStandardOutput = true })!;
                mklink.WaitForExit();
                Assert.Equal(0, mklink.ExitCode);
            }
            else
            {
                Directory.CreateSymbolicLink(loop, folder);
            }

            var report = FolderScanner.Scan(folder, Catalog);

            Assert.Equal(1, report.FilesScanned);
            Assert.Equal(["ClassicApp"], report.Assemblies.Select(assembly => assembly.Name));
        }
        finally
        {
            foreach (var link in Directory.EnumerateDirectories(folder))
            {
                Directory.Delete(link);
            }
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void NamesFromTheAssemblyCannotFormatTheReport()
    {
        const string hostile = "Evil\u001b]0;x\u0007\u009b<img src=x>[a](b)";
        MemberRow[] members = [new($"ExchangeService.{hostile}", 1, [hostile])];
        var report = new ScanReport("C:\\apps", 1,
        [
            new AssemblyReport(hostile, "C:\\apps\\Evil.dll", hostile, [], [], [], members),
        ]);

        var text = ReportWriter.Write(report, Catalog, "text", callers: true, "1.0.0", 100);
        var markdown = ReportWriter.Write(report, Catalog, "markdown", callers: true, "1.0.0", 100);

        Assert.DoesNotContain(text.Replace(Environment.NewLine, ""), char.IsControl);
        Assert.Contains("Evil]0;x<img src=x>[a](b)", text);
        Assert.DoesNotContain(markdown.Replace(Environment.NewLine, ""), char.IsControl);
        Assert.DoesNotMatch(@"(?<!\\)<img", markdown);
        Assert.DoesNotContain("[a](b)", markdown);
        Assert.Contains(@"## Evil\]0;x\<img src=x\>\[a\](b)", markdown);
        Assert.Contains(@"ExchangeService.Evil\]0;x\<img src=x\>\[a\](b) (1) in Evil\]0;x", markdown);
    }

    [Theory]
    [InlineData("C:\\apps", "`C:\\apps`")]
    [InlineData("a`b", "``a`b``")]
    [InlineData("`a", "`` `a ``")]
    public void PathsInMarkdownAreCodeSpans(string path, string expected) =>
        Assert.Equal(expected, ReportWriter.Code(path));

    /// <summary>The file offset of the IL of the first method with at least the given number of IL bytes.</summary>
    private static int FirstMethodBody(byte[] image, int length)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();
        foreach (var method in reader.MethodDefinitions.Select(reader.GetMethodDefinition).Where(method => method.RelativeVirtualAddress != 0))
        {
            if (pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!.Length < length)
            {
                continue;
            }
            var section = pe.PEHeaders.SectionHeaders.Single(header =>
                method.RelativeVirtualAddress >= header.VirtualAddress && method.RelativeVirtualAddress < header.VirtualAddress + header.VirtualSize);
            var header = method.RelativeVirtualAddress - section.VirtualAddress + section.PointerToRawData;
            // A tiny header is one byte; a fat header is twelve.
            return header + ((image[header] & 3) == 2 ? 1 : 12);
        }
        throw new InvalidOperationException("No method body is long enough.");
    }

    private static IEnumerable<string> Evidence(AssemblyReport report, string signal) =>
        report.Signals.Single(found => found.Id == signal).Evidence.Select(row => row.Member);
}
