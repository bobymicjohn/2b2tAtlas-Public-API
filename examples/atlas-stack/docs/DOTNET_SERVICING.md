# .NET servicing

This example targets .NET 10 for the browser client, API, ingestion worker and
tests. Use SDK 10.0.401 or a newer .NET 10 feature band. The current runtime,
ASP.NET Core and EF Core packages are 10.0.12. Microsoft.OpenApi 2.12.0 satisfies
the ASP.NET Core OpenAPI dependency.

Run `dotnet --version`, build the solution in Release, and run its tests before
publishing. A self-contained API or worker includes its runtime: republish it
when servicing .NET, even if the host runtime has already been updated.

Blazor includes a browser runtime, so the static frontend needs rebuilding and
redeploying too. Keep old fingerprinted assets through the transition for
existing browser tabs. Back up your database and retain the previous binaries
before a server cutover. Replace the ingestion worker between jobs.

The Java collector and BlueMap processes do not use .NET. Their versions and
upgrade schedules are independent.
