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
  <a href="https://github.com/sunset-less/ews-scan/blob/main/LICENSE"><img alt="MIT" src="https://img.shields.io/badge/license-MIT-lightgrey"></a>
</p>

`ews-scan` finds the EWS Managed API calls in compiled .NET code and tells you what each EWS operation turns into in Microsoft Graph.

Microsoft is [retiring Exchange Web Services in Exchange Online](https://learn.microsoft.com/en-us/exchange/clients-and-mobile-in-exchange-online/deprecation-of-ews-exchange-online). Before you plan the move for an application built on `Microsoft.Exchange.WebServices`, you need to know how much of that API it uses. ews-scan reads it from the binaries, so it works even when whoever wrote the application is long gone.

## Install and run

```
dotnet tool install --global Sunsetless.EwsScan
ews-scan C:\path\to\the\application
```

Point it at a folder of compiled assemblies (a `bin` folder, or wherever the application is installed) or at a single `.dll` or `.exe`. Subfolders are included. You need the .NET 8 runtime or later.

## The report

For each assembly that calls the EWS Managed API, the calls are grouped by EWS operation. The operation names are the SOAP actions you see in the EWS usage report in the Microsoft 365 admin center, so the two are easy to compare.

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

Some things affect how big a migration is without showing up in the operation names. Those go under "Also in this code": public folders, archive mailboxes, `SendToNone` on appointments, extended properties, MIME content, impersonation, contact groups, tasks and search folders. The same part says how the code signs in, either with OAuth for Exchange Online or with a password or the Windows account for Exchange Server.

## Options

| Option | What it does |
|---|---|
| `--format text` | Plain text for the terminal (default). |
| `--format markdown` | Tables to paste into a ticket or a pull request. |
| `--format json` | The same data as JSON, for scripts. |
| `--output <file>` | Writes the report to a file. |
| `--callers` | Shows which methods each call is made from. |

## How it works

ews-scan reads the metadata and IL of each assembly with `System.Reflection.Metadata`. It never loads or runs the code and makes no network requests. Because it works on IL, the source language doesn't matter: C#, VB.NET and F# are all fine.

It knows three libraries: Microsoft's `Microsoft.Exchange.WebServices`, the .NET Standard port `Microsoft.Exchange.WebServices.NETStandard`, and Sunsetless EWS, which shows up on the Library line as "(Sunsetless EWS)". The assemblies can target anything from .NET Framework 2.0 to 4.8, .NET Standard or .NET.

## What it misses

- Calls through reflection or `dynamic`, and PowerShell scripts that load the library. Neither leaves a call in a compiled assembly.
- Enum values that come from a field, a config file or a branch. `WellKnownFolderName.PublicFoldersRoot` is only found when it's written as a literal in the calling method.
- Folders and items opened by a stored ID. A stored public folder ID looks like any other ID.
- EWS requests sent as raw SOAP, without the Managed API.
- How often a call runs. The report counts call sites in the code, while the EWS usage report in the admin center counts requests.

`Load()` on a folder is counted under `GetItem`, because items and folders share that method. If a folder tree has several copies of the same assembly, you only see the newest. `obj` and `ref` folders are skipped.

## Where the Graph column comes from

The Graph calls and notes come from [Microsoft's EWS to Graph mapping table](https://learn.microsoft.com/en-us/graph/migrate-exchange-web-services-api-mapping) and from our own tests on Exchange Online, written up in [EWS operations in Microsoft Graph](https://github.com/sunset-less/sunsetless-ews-samples/blob/main/docs/ews-to-graph.md). If the Graph line says "not in Microsoft's mapping table", Microsoft doesn't list the operation and we haven't tested a replacement either. Graph keeps changing, so check the calls your code relies on.

## The Sunsetless line

We also make [Sunsetless EWS](https://sunsetless.com), a commercial library: you keep your EWS Managed API code and it sends the calls to Microsoft Graph. The "Sunsetless" line in each entry says whether the library handles that call, and in the Markdown report it links to the [compatibility list](https://sunsetless.com/compatibility). If you're rewriting for Graph by hand, skip that line. Nothing else in the report depends on it.

## Build from source

```
dotnet test
dotnet run --project src/EwsScan -- C:\path\to\the\application
```

Which API members map to which EWS operations is defined in [`src/EwsScan/catalog.json`](https://github.com/sunset-less/ews-scan/blob/main/src/EwsScan/catalog.json). If the report puts a call under the wrong operation or misses one, open an issue or send a pull request for that file.

## License

[MIT](https://github.com/sunset-less/ews-scan/blob/main/LICENSE). Sunsetless is independent and not affiliated with, endorsed by or sponsored by Microsoft.
