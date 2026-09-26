# Runtime and storage

The [example launcher](../../scripts/start-example.ps1) runs the API and browser
client together at `http://127.0.0.1:5297`. Its database and secrets live under
`.local`, outside the published files. For a separate frontend and API, follow
the [installation guide](../DEPLOY_FROM_SCRATCH.md).

## Processes and connections

| Component | Connection or storage |
| --- | --- |
| Browser client | Static Blazor files; `ApiBaseUrl` selects the API |
| API | ASP.NET Core; bind locally behind your HTTPS proxy |
| Database | `Database__Path`, or `.local/data/atlas.db` under the working directory |
| Ingestion worker | Calls the API with a worker key; reads private intake |
| Renderer | Reads a disposable world and writes to private work storage |
| Tile server | Serves completed tile trees with read-only filesystem access |
| BlueMap | Builds optional 3D generations from preserved sources |
| Archive collector | Uses its own Minecraft account, game directory and checkpoints |

`atlas.example`, `api.atlas.example` and `tiles.atlas.example` are placeholders.
Replace them with your own HTTPS origins. A browser on another machine cannot
reach your API through `127.0.0.1`; that address always points to the browser's
own machine.

## Settings to check

Set `ASPNETCORE_URLS` for the API listener. `HostStaticClient=true` serves the
client from the API; use `false` when a separate web server hosts it. Put the
matching public API URL in the client's `ApiBaseUrl`, and test requests from the frontend's origin. This snapshot's `PublicAPI` CORS policy allows all origins without cookies. Writes still
require bearer authentication and permissions.

Keep `JwtSettings__SecretKey` in private configuration. The API receives only
the hash of the worker key through `IngestionWorker__ApiKeySha256`. The worker
receives the raw key separately. Neither belongs in browser settings.

Set `IngestionWorker__IntakeRoot` to storage both processes can reach. Configure
the worker's paths and renderer profile in its JSON configuration. Allowed public
tile roots come from `MapRenders__AllowedUrlPrefixes__0` and subsequent entries.

BlueMap uses `BlueMap__OutputRoot`, `BlueMap__RequestPath` and
`BlueMap__PublicOrigin`. Its catalog also checks the minimum profile, manifests
and output files. Configure its status path and activity logs if you want Admin
to show progress. See [BlueMap configuration](../BLUEMAP_PIPELINE.md).

## Keep these directories separate

Use separate locations for application binaries, database state, intake, preserved
sources, working copies and published tiles. Replacing the application must not
replace its database. Back up SQLite through its online backup API rather than
copying an open WAL database.

The worker's work and publication roots must share a volume for atomic moves,
but neither may overlap intake or the other root. The public web server should
read only completed output. Store original WDLs by SHA-256 and keep verified
backups outside scratch storage.

Each collector needs its own account state, game directory, logs and checkpoints.
Use the handoff scripts to move reviewed captures into intake. Do not copy tokens
between workers or place a partial survey in the ready queue. The
[collector guide](../ARCHIVE_SYNC_AUTOMATION.md) covers recovery and scheduling.
