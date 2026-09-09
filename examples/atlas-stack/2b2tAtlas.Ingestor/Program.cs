using System.Text.Json;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Minecraft;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Pipeline;
using Atlas.Ingestor.Publishing;
using Atlas.Ingestor.Rendering;
using Atlas.Ingestor.Security;
using Atlas.Ingestor.Worker;

return await Cli.RunAsync(args);

/// <summary>Routes command-line operations through the guarded ingestion pipeline stages.</summary>
internal static class Cli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Runs a single CLI command and translates expected failures and cancellation into process exit codes.</summary>
    /// <param name="args">Command name followed by strict <c>--name value</c> pairs.</param>
    /// <returns>0 on success, 2 when no command is supplied, 130 on cancellation, or 1 on failure.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return args.Length == 0 ? 2 : 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            var command = args[0].ToLowerInvariant();
            var options = Arguments.Parse(args[1..]);
            var limits = new IngestLimits();
            switch (command)
            {
                case "inspect":
                    await InspectAsync(options, limits, cancellation.Token);
                    break;
                case "prepare":
                    await PrepareAsync(options, limits, cancellation.Token);
                    break;
                case "render":
                    await RenderAsync(options, limits, cancellation.Token);
                    break;
                case "adapt":
                    await AdaptAsync(options, limits, cancellation.Token);
                    break;
                case "verify":
                    Verify(options, limits);
                    break;
                case "publish":
                    await PublishAsync(options, limits, cancellation.Token);
                    break;
                case "register":
                    await RegisterAsync(options, limits, cancellation.Token);
                    break;
                case "worker":
                    options.RequireOnly("config", "once");
                    await IngestionWorker.RunAsync(
                        options.Require("config"),
                        options.OptionalBool("once", false),
                        cancellation.Token);
                    break;
                default:
                    throw new InputValidationException($"Unknown command: {command}");
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (IngestException exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unexpected error: {exception.Message}");
            return 1;
        }
    }

    private static async Task InspectAsync(Arguments options, IngestLimits limits, CancellationToken cancellationToken)
    {
        options.RequireOnly("archive");
        var report = await SecureZipArchive.InspectAsync(options.Require("archive"), limits, cancellationToken: cancellationToken);
        WriteJson(report);
    }

    /// <summary>Snapshots, extracts, inspects, and plans a job, recording a durable failed-prepare state on error.</summary>
    /// <param name="options">Strict prepare options.</param>
    /// <param name="limits">Ingestion safety ceilings.</param>
    /// <param name="cancellationToken">Token that cancels the stage.</param>
    /// <returns>A task that completes after the job reaches <c>prepared</c>.</returns>
    internal static async Task PrepareAsync(Arguments options, IngestLimits limits, CancellationToken cancellationToken)
    {
        options.RequireOnly("archive", "manifest", "work", "resume");
        var manifest = await IngestManifest.LoadAsync(options.Require("manifest"), cancellationToken);
        var store = new JobStore(options.Require("work"), limits);
        var archive = options.Require("archive");
        var (paths, report) = options.OptionalBool("resume", false)
            ? await store.ResumePrepareAsync(archive, cancellationToken)
            : await store.CreateAsync(archive, cancellationToken);
        try
        {
            await SecureZipArchive.ExtractAsync(paths.Snapshot, paths.Extracted, report, limits, cancellationToken);
            var roots = WorldInspector.FindWorldRoots(paths.Extracted, limits);
            var worlds = new List<WorldInfo>(roots.Count);
            foreach (var root in roots)
            {
                WorldLayoutNormalizer.NormalizeCanonicalNamespacedDimensions(root, manifest.Dimensions);
                var world = await WorldInspector.InspectAsync(root, limits, cancellationToken);
                WorldLayoutNormalizer.EnsureRendererLevelDat(root, world.LevelName ?? manifest.Name);
                worlds.Add(world);
            }
            var plan = RenderPlanBuilder.Build(manifest, worlds, paths.Extracted);
            await JobStore.WriteJsonAsync(paths.RenderPlan, plan, cancellationToken);
            await JobStore.WriteJsonAsync(paths.State, new JobState("prepared", DateTimeOffset.UtcNow, null), cancellationToken);
            WriteJson(new { jobId = report.Sha256, paths.Root, report, plan });
        }
        catch (Exception exception)
        {
            await JobStore.WriteJsonAsync(
                paths.State,
                new JobState("failed", DateTimeOffset.UtcNow, exception.Message, "prepare"),
                CancellationToken.None);
            throw;
        }
    }

    /// <summary>Runs the pinned renderer and records a durable failed-render state on error.</summary>
    /// <param name="options">Strict render options.</param>
    /// <param name="limits">Renderer timeout and log ceilings.</param>
    /// <param name="cancellationToken">Token that cancels the stage and active process tree.</param>
    /// <returns>A task that completes after the job reaches <c>rendered</c>.</returns>
    internal static async Task RenderAsync(Arguments options, IngestLimits limits, CancellationToken cancellationToken)
    {
        options.RequireOnly("job", "work", "profile", "resume");
        var paths = new JobStore(options.Require("work"), limits).Open(options.Require("job"));
        var plan = await ReadJsonAsync<RenderPlan>(paths.RenderPlan, cancellationToken);
        var loadedOptions = await RendererOptions.LoadAsync(
            options.Require("profile"),
            limits.RenderTimeout,
            limits.MaxLogBytes,
            cancellationToken);
        var rendererOptions = loadedOptions with { Resume = options.OptionalBool("resume", false) };
        JobStatePolicy.RequireRender(
            await JobStore.ReadJsonAsync<JobState>(paths.State, cancellationToken),
            rendererOptions.Resume);
        try
        {
            await RendererRunner.RenderAsync(
                rendererOptions,
                plan,
                await JobStore.ComputeSha256Async(paths.RenderPlan, cancellationToken),
                paths.Render,
                cancellationToken);
            await JobStore.WriteJsonAsync(paths.State, new JobState("rendered", DateTimeOffset.UtcNow, null), cancellationToken);
            Console.WriteLine(paths.Render);
        }
        catch (Exception exception)
        {
            await JobStore.WriteJsonAsync(
                paths.State,
                new JobState("failed", DateTimeOffset.UtcNow, exception.Message, "render"),
                CancellationToken.None);
            throw;
        }
    }

    private static void Verify(Arguments options, IngestLimits limits)
    {
        options.RequireOnly("tiles", "coordinate-scheme");
        WriteJson(TilePublisher.Verify(
            options.Require("tiles"),
            limits,
            options.Optional("coordinate-scheme") ?? TilePublisher.StandardXyzScheme));
    }

            /// <summary>Adapts authenticated renderer output and records a durable failed-adapt state on error.</summary>
            /// <param name="options">Strict adaptation options.</param>
            /// <param name="limits">Renderer-output and tile-output ceilings.</param>
            /// <param name="cancellationToken">Token that cancels the stage.</param>
            /// <returns>A task that completes after the job reaches <c>adapted</c>.</returns>
    internal static async Task AdaptAsync(Arguments options, IngestLimits limits, CancellationToken cancellationToken)
    {
        options.RequireOnly("job", "work", "dimension", "scheme");
        var jobId = options.Require("job").ToLowerInvariant();
        var paths = new JobStore(options.Require("work"), limits).Open(jobId);
        JobStatePolicy.RequireAdapt(await JobStore.ReadJsonAsync<JobState>(paths.State, cancellationToken));
        var dimension = options.Require("dimension").ToLowerInvariant();
        try
        {
            var receipt = await AtlasTileAdapter.AdaptJobAsync(
                paths,
                jobId,
                dimension,
                options.Require("scheme").ToLowerInvariant(),
                limits,
                cancellationToken);
            await JobStore.WriteJsonAsync(
                paths.State,
                new JobState("adapted", DateTimeOffset.UtcNow, $"{dimension}:{receipt.Scheme}"),
                cancellationToken);
            WriteJson(receipt);
        }
        catch (Exception exception)
        {
            await JobStore.WriteJsonAsync(
                paths.State,
                new JobState("failed", DateTimeOffset.UtcNow, exception.Message, "adapt"),
                CancellationToken.None);
            throw;
        }
    }

    /// <summary>Promotes receipt-bound adapted output and records the immutable destination in job state.</summary>
    /// <param name="options">Strict publication options.</param>
    /// <param name="limits">Tile verification ceilings.</param>
    /// <param name="cancellationToken">Token that cancels verification and persistence.</param>
    /// <returns>A task that completes after the job reaches <c>published</c>.</returns>
    internal static async Task PublishAsync(Arguments options, IngestLimits limits, CancellationToken cancellationToken)
    {
        options.RequireOnly("job", "work", "dimension", "tile-root", "destination");
        var jobId = options.Require("job").ToLowerInvariant();
        var paths = new JobStore(options.Require("work"), limits).Open(jobId);
        JobStatePolicy.RequirePublish(
            await JobStore.ReadJsonAsync<JobState>(paths.State, cancellationToken));
        var receipt = await PublicationService.PublishAsync(
            paths,
            jobId,
            options.Require("dimension").ToLowerInvariant(),
            options.Require("tile-root"),
            options.Require("destination"),
            limits,
            cancellationToken);
        await JobStore.WriteJsonAsync(
            paths.State,
            new JobState("published", DateTimeOffset.UtcNow, receipt.Destination),
            cancellationToken);
        WriteJson(receipt);
    }

    private static async Task RegisterAsync(Arguments options, IngestLimits limits, CancellationToken cancellationToken)
    {
        options.RequireOnly("job", "work", "api", "token-env", "dimension", "url-template");
        var jobId = options.Require("job").ToLowerInvariant();
        var paths = new JobStore(options.Require("work"), limits).Open(jobId);
        var plan = await ReadJsonAsync<RenderPlan>(paths.RenderPlan, cancellationToken);
        var dimensionKey = options.Require("dimension").ToLowerInvariant();
        var dimension = plan.Dimensions.SingleOrDefault(value => value.Key == dimensionKey)
            ?? throw new InputValidationException($"Dimension is not in the render plan: {dimensionKey}");
        var receipt = await PublicationService.ReadAndVerifyAsync(
            paths, jobId, dimensionKey, limits, cancellationToken);
        var adaptation = await JobStore.ReadJsonAsync<AdaptationReceipt>(
            paths.AdaptationReceipt(dimensionKey), cancellationToken);
        var scheme = AtlasTileScheme.Parse(adaptation.Scheme);

        await MapRenderRegistrar.RegisterAsync(
            new Uri(options.Require("api"), UriKind.Absolute),
            options.Require("token-env"),
            plan,
            dimension,
            options.Require("url-template"),
            receipt.Report.MaxZoom - scheme.UrlZoomOffset,
            cancellationToken);
        await JobStore.WriteJsonAsync(
            paths.State,
            new JobState("registered-unpublished", DateTimeOffset.UtcNow, $"{plan.Slug}-{dimension.Key}"),
            cancellationToken);
        Console.WriteLine($"Registered {plan.Slug}-{dimension.Key} as unpublished.");
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T>(input, JsonOptions, cancellationToken)
            ?? throw new InputValidationException($"JSON file is empty or invalid: {path}");
    }

    private static void WriteJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

        private static void PrintHelp() => Console.WriteLine("""
                2b2t Atlas WDL Ingestor

                inspect --archive <world.zip>
                    Read-only ZIP preflight. Does not extract or execute code.

                prepare --archive <world.zip> --manifest <ingest.json> --work <private-dir> [--resume true]
                    Snapshot, revalidate, safely extract, discover worlds, and write a render plan.

                render --job <sha256> --work <private-dir> --profile <renderer.json> [--resume true]
                    Execute a versioned, hash-pinned uNmINeD profile for each selected dimension.

                adapt --job <sha256> --work <private-dir> --dimension <key> --scheme <name>
                    Convert signed native uNmINeD tiles into a receipt-bound Atlas XYZ pyramid.

                verify --tiles <rendered-dir> [--coordinate-scheme xyz-v1|atlas-sparse-v1]
                    Validate tile coordinates and enforce output limits.

                publish --job <sha256> --work <private-dir> --dimension <key> \
                    --tile-root <relative-dir> --destination <immutable-dir>
                    Verify an adapted tile-only subtree, atomically publish it, and write a job receipt.

                register --job <sha256> --work <private-dir> --api <https-url> \
                    --token-env <variable> --dimension <key> --url-template <template>
                    Reverify the publication receipt and register an unpublished MapRenderDto.

                worker --config <worker.json> [--once true]
                    Poll the metadata queue and run the offline Overworld ingestion pipeline locally.
                """);
}

/// <summary>Parses strict, duplicate-free command-line option pairs.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string> values;

    private Arguments(Dictionary<string, string> values) => this.values = values;

    /// <summary>Parses <c>--name value</c> pairs.</summary>
    /// <param name="args">Raw option tokens excluding the command name.</param>
    /// <returns>The parsed option set.</returns>
    /// <exception cref="InputValidationException">A pair is incomplete, malformed, empty, or duplicated.</exception>
    public static Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new InputValidationException($"Expected --name value, got: {args[index]}");
            var key = args[index][2..];
            if (key.Length == 0 || !values.TryAdd(key, args[index + 1]))
                throw new InputValidationException($"Duplicate or empty option: {args[index]}");
        }
        return new Arguments(values);
    }

    /// <summary>Gets a required nonblank option.</summary>
    /// <param name="key">Option name without leading dashes.</param>
    /// <returns>The supplied value.</returns>
    /// <exception cref="InputValidationException">The option is absent or blank.</exception>
    public string Require(string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InputValidationException($"Missing required option: --{key}");

            /// <summary>Gets an optional value.</summary>
            /// <param name="key">Option name without leading dashes.</param>
            /// <returns>The supplied value, or <see langword="null"/>.</returns>
    public string? Optional(string key) => values.GetValueOrDefault(key);

            /// <summary>Gets an optional Boolean value.</summary>
            /// <param name="key">Option name without leading dashes.</param>
            /// <param name="defaultValue">Value returned when the option is absent.</param>
            /// <returns>The parsed value or <paramref name="defaultValue"/>.</returns>
            /// <exception cref="InputValidationException">The supplied value is not Boolean.</exception>
    public bool OptionalBool(string key, bool defaultValue)
    {
        var value = Optional(key);
        if (value is null)
            return defaultValue;
        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InputValidationException($"Option --{key} must be true or false.");
    }

            /// <summary>Rejects options outside an operation's allowlist.</summary>
            /// <param name="allowed">Allowed option names without leading dashes.</param>
            /// <exception cref="InputValidationException">An unknown option is present.</exception>
    public void RequireOnly(params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.FirstOrDefault(key => !allowedSet.Contains(key));
        if (unknown is not null)
            throw new InputValidationException($"Unknown option: --{unknown}");
    }
}
