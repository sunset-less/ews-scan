<p align="center">
  <a href="https://sunsetless.com">
    <img alt="Sunsetless" src="https://raw.githubusercontent.com/sunset-less/ews-scan/main/.github/assets/logo.svg" width="314">
  </a>
  <br>
  <a href="https://sunsetless.com">sunsetless.com</a>
</p>

<h1 align="center">ews-scan</h1>

<p align="center">
  <a href="https://www.nuget.org/packages/Sunsetless.EwsScan"><img alt="NuGet" src="https://img.shields.io/nuget/v/Sunsetless.EwsScan?logo=nuget&label=NuGet&color=635BFF"></a>
  <a href="https://www.nuget.org/packages/Sunsetless.EwsScan"><img alt=".NET 8 or later" src="https://img.shields.io/badge/.NET-8%2B-512BD4?logo=dotnet&logoColor=white"></a>
  <a href="https://sunsetless.com/compatibility"><img alt="EWS Managed API to Microsoft Graph" src="https://img.shields.io/badge/EWS%20Managed%20API-to%20Microsoft%20Graph-0078D4"></a>
  <a href="https://github.com/sunset-less/ews-scan/issues"><img alt="Open issues" src="https://img.shields.io/github/issues/sunset-less/ews-scan?logo=github&label=issues"></a>
  <a href="https://github.com/sunset-less/ews-scan/blob/main/LICENSE"><img alt="MIT" src="https://img.shields.io/badge/license-MIT-lightgrey"></a>
</p>

`ews-scan` lists the EWS Managed API calls in compiled .NET code and shows what each EWS operation becomes in Microsoft Graph.

Microsoft is [retiring Exchange Web Services in Exchange Online](https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/deprecation-of-ews-exchange-online). If you maintain an application built on `Microsoft.Exchange.WebServices`, you first need to know how much of the API it uses. This tool reads that from the binaries, so it also works when the application's author has left.

## Install and run

```
dotnet tool install --global Sunsetless.EwsScan
ews-scan C:\path\to\the\application
```

The path is a folder with compiled assemblies, such as a `bin` folder or the folder the application is installed in, or a single `.dll` or `.exe`. Subfolders are searched. The tool needs the .NET 8 runtime or later.

## What you get

For every assembly that calls the EWS Managed API, the report groups the calls by EWS operation. These are the same names as the SOAP actions in the EWS usage report of the Microsoft 365 admin center.

```
== ClassicApp (net8.0\ClassicApp.dll)
  Library     Microsoft.Exchange.WebServices 15.0.0.0
  Works with  mail, calendar, tasks

FindItem, 1 call site
  Calls       ExchangeService.FindItems (1)
  Graph       GET /users/{id}/mailFolders/{id}/messages with $filter, $orderby, $top, $skip or
              $search; /calendarView for a CalendarView; /contacts; /todo/lists/{id}/tasks
  Watch for   With both $filter and $orderby, the sort properties must also be in the filter, in the
              same order and first, or Graph answers InefficientFilter. [...]
  Sunsetless  Verified (mail, calendar); Verified with differences (tasks)

Subscribe (streaming), 1 call site
  Calls       ExchangeService.SubscribeToStreamingNotifications (1)
  Graph       none
  Watch for   No long-running connection. Use a webhook subscription or poll delta queries.
  Sunsetless  Verified with differences

Also in this code

Public folders
  Found       WellKnownFolderName.PublicFoldersRoot (1)
  Graph       none
  Watch for   Graph has no API for public folders or their items.
  Sunsetless  Not possible in Graph
```

The second part, "Also in this code", lists things that change the size of a migration and that the operation names do not show: public folders, archive mailboxes, `SendToNone` on appointments, extended properties, MIME content, impersonation, contact groups, tasks, search folders, and how the code signs in: OAuth for Exchange Online, or a password or the Windows account for Exchange Server.

## Options

| Option | What it does |
|---|---|
| `--format text` | Plain text for the terminal. This is the default. |
| `--format markdown` | Tables you can paste into a ticket or a pull request. |
| `--format json` | The same data for scripts. |
| `--output <file>` | Writes the report to a file. |
| `--callers` | Names the methods each call is made from. |

## How it works

The tool reads the metadata and the IL of each assembly with `System.Reflection.Metadata`. It does not load or run the code, and it makes no network requests. Because it reads IL, the source language does not matter: C#, VB.NET and F# all work.

It recognizes code built against Microsoft's `Microsoft.Exchange.WebServices` package, against the .NET Standard port, `Microsoft.Exchange.WebServices.NETStandard`, and against Sunsetless EWS, which the Library line marks with "(Sunsetless EWS)". The assemblies can target any version of .NET Framework from 2.0 to 4.8, .NET Standard, or .NET.

## What it cannot see

- Calls made through reflection or `dynamic`, and PowerShell scripts that load the library. Neither leaves a call in a compiled assembly.
- Enum values that reach the call from a field, a configuration file or a branch. `WellKnownFolderName.PublicFoldersRoot` is found when it is written as a literal in the calling method.
- Folders and items the code opens by a stored ID. A stored ID of a public folder looks like any other ID.
- EWS requests written as raw SOAP, without the Managed API.
- How often a call runs. The report counts call sites in the code. The EWS usage report in the Microsoft 365 admin center counts requests.

The report counts `Load()` on a folder under `GetItem`, because items and folders share that method. When a folder tree holds several copies of an assembly, the report shows the newest one. The tool skips `obj` and `ref` folders.

## Where the Graph column comes from

The Graph calls and the notes come from [Microsoft's EWS to Graph mapping table](https://learn.microsoft.com/en-us/graph/migrate-exchange-web-services-api-mapping) and from tests on Exchange Online, written up in [EWS operations in Microsoft Graph](https://github.com/sunset-less/sunsetless-ews-samples/blob/main/docs/ews-to-graph.md). When the Graph line reads "not in Microsoft's mapping table", the operation is missing from that table and we have not tested a replacement. Microsoft Graph changes, so check what your code depends on.

## The Sunsetless line

This tool is written by the people behind [Sunsetless EWS](https://sunsetless.com), a commercial library that keeps EWS Managed API code and sends its calls to Microsoft Graph. The "Sunsetless" line of each entry says whether that library runs the call, with a link to the [compatibility list](https://sunsetless.com/compatibility) in the Markdown report. If you are rewriting for Graph by hand, ignore that line; the rest of the report does not depend on it.

## Build from source

```
dotnet test
dotnet run --project src/EwsScan -- C:\path\to\the\application
```

The mapping from API members to EWS operations is in [`src/EwsScan/catalog.json`](https://github.com/sunset-less/ews-scan/blob/main/src/EwsScan/catalog.json). If the report puts a call under the wrong operation or misses one, open an issue or send a pull request for that file.

## License

[MIT](https://github.com/sunset-less/ews-scan/blob/main/LICENSE). Sunsetless is independent and not affiliated with, endorsed by or sponsored by Microsoft.
