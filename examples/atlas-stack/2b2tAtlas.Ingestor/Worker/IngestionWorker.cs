using System.Net;
using System.Net.Http.Json;
using Atlas;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Pipeline;
using Atlas.Ingestor.Publishing;
using Atlas.Ingestor.Rendering;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Worker;

/// <summary>Claims ingestion jobs and runs the local inspect/prepare/render/adapt/publish/register pipeline.</summary>
public static class IngestionWorker
{
    private static readonly IReadOnlyDictionary<string, StageProgress> ProgressByStage =
        new Dictionary<string, StageProgress>(StringComparer.Ordinal)
        {
            ["intake"] = new(2, 8, 15),
            ["prepare"] = new(8, 25, 90),
            ["render"] = new(25, 78, 480),
            ["adapt"] = new(78, 92, 90),
            ["publish"] = new(92, 98, 30),
            ["registered"] = new(100, 100, 0),
        };

    // Certified per-location render dimensions: key -> (map dimension index, Atlas tile scheme, label).
    private static readonly IReadOnlyDictionary<string, (int Index, string Scheme, string Label)> RenderDimensions =
        new Dictionary<string, (int, string, string)>(StringComparer.Ordinal)
        {
            ["overworld"] = (0, "atlas-overworld-sparse-v1", "Overworld"),
            ["nether"] = (1, "atlas-nether-sparse-v1", "Nether"),
            ["end"] = (2, "atlas-end-sparse-v1", "End"),
        };

    /// <summary>Runs one claim attempt or continuously polls the ingestion queue.</summary>
    /// <param name="configPath">Path to validated worker configuration.</param>
    /// <param name="once"><see langword="true"/> to process at most one claim; otherwise poll continuously.</param>
    /// <param name="cancellationToken">Token that stops polling, HTTP operations, and the active local stage.</param>
    /// <returns>A task that completes after one-shot work or graceful continuous-worker shutdown.</returns>
    /// <exception cref="InputValidationException">Configuration, credentials, claimed metadata, state, or API responses are invalid.</exception>
    /// <exception cref="InputSecurityException">A claim lacks authority or conflicts with durable local job identity.</exception>
    /// <exception cref="OperationCanceledException">Worker cancellation was requested.</exception>
    /// <remarks>
    /// The API-issued claim token is required and accompanies every status update. Progress is reported every five
    /// seconds; if reporting fails, the active stage is canceled so work does not continue without remote authority.
    /// Job failures are reported as terminal state on a best-effort basis, while polling errors retry after the configured delay.
    /// </remarks>
    public static async Task RunAsync(string configPath, bool once, CancellationToken cancellationToken)
    {
        var options = await WorkerOptions.LoadAsync(configPath, cancellationToken);
        using var workerLock = AcquireWorkerLock(options.FullWorkRoot);
        var apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnvironment);
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 32)
            throw new InputValidationException($"Worker key environment variable {options.ApiKeyEnvironment} must contain at least 32 characters.");

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.ApiBase.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Add("X-Atlas-Worker-Key", apiKey);

        do
        {
            try
            {
                var job = await ClaimAsync(client, cancellationToken);
                if (job is null)
                {
                    if (once) return;
                    await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), cancellationToken);
                    continue;
                }

                await ProcessAsync(client, options, job, cancellationToken);
                if (once) return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Worker polling error: {exception.Message}");
                if (once) throw;
                await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), cancellationToken);
            }
        } while (!cancellationToken.IsCancellationRequested);
    }

    private static FileStream AcquireWorkerLock(string workRoot)
    {
        var path = Path.Combine(workRoot, ".worker.lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InputValidationException("Another Atlas ingestion worker already holds the work-root lock.", exception);
        }
    }

    private static async Task<IngestionJobDto?> ClaimAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync("api/ingestion-jobs/claim", null, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        await EnsureSuccessAsync(response, "claim an ingestion job", cancellationToken);
        return await response.Content.ReadFromJsonAsync<IngestionJobDto>(cancellationToken: cancellationToken)
            ?? throw new InputValidationException("The ingestion API returned an empty claimed job.");
    }

    private static async Task ProcessAsync(
        HttpClient client,
        WorkerOptions options,
        IngestionJobDto job,
        CancellationToken cancellationToken)
    {
        string? archiveSha256 = null;
        string? publicationBackup = null;
        string? publicationDestination = null;
        string? publicationStaging = null;
        var publicationHadExistingDestination = false;
        var publicationBackupReady = false;
        var stage = "intake";
        try
        {
            if (string.IsNullOrWhiteSpace(job.ClaimToken))
                throw new InputSecurityException("The claimed job did not include a claim token.");
            var requestErrors = IngestionJobValidator.ValidateRequest(job);
            if (requestErrors.Count > 0)
                throw new InputValidationException(string.Join(" ", requestErrors));

            var dimensionKey = string.IsNullOrWhiteSpace(job.Dimension) ? "overworld" : job.Dimension.Trim().ToLowerInvariant();
            if (!RenderDimensions.TryGetValue(dimensionKey, out var dim))
                throw new InputValidationException($"Dimension '{dimensionKey}' is not supported for rendering yet.");

            var archivedPath = !string.IsNullOrWhiteSpace(job.ArchiveSha256)
                ? WdlArchiveStore.ObjectPath(options.FullArchiveRoot, job.ArchiveSha256)
                : null;
            // Keep inspection/extraction on the local scratch tier. Only recover
            // from the serving archive when the original intake has disappeared.
            var archivePath = ResolveArchivePath(options.FullIntakeRoot, job.IntakeFileName, requireExists: false);
            if (!File.Exists(archivePath) && archivedPath is not null)
            {
                archivePath = Path.Combine(options.FullWorkRoot, ".sources", job.ArchiveSha256!.ToLowerInvariant() + ".zip");
                if (!File.Exists(archivePath))
                    await RunStageAsync(client, job, stage, "Recovering WDL to local scratch.", job.ArchiveSha256,
                        token => WdlArchiveStore.MaterializeAsync(options.FullArchiveRoot, job.ArchiveSha256, archivePath, token),
                        cancellationToken);
            }
            var limits = new IngestLimits();
            var report = await RunStageAsync(
                client, job, stage, "Inspecting local intake archive.", null,
                token => SecureZipArchive.InspectAsync(archivePath, limits, job.ArchiveSha256, cancellationToken: token),
                cancellationToken);
            archiveSha256 = report.Sha256;
            // Preserve to the serving landing zone under an active API lease.
            await RunStageAsync(
                client, job, stage, "Verifying the immutable WDL archive.", archiveSha256,
                token => WdlArchiveStore.ArchiveAsync(
                    archivePath, options.FullArchiveRoot, archiveSha256, token),
                cancellationToken);

            // Isolate each dimension's content-addressed job tree so overworld and nether renders
            // from the same intake ZIP (identical SHA) do not share one linear stage state machine.
            var dimWorkRoot = Path.Combine(options.FullWorkRoot, dimensionKey);
            Directory.CreateDirectory(dimWorkRoot);
            var manifestPath = await WriteManifestAsync(dimWorkRoot, job, dimensionKey, cancellationToken);
            var jobRoot = Path.Combine(dimWorkRoot, archiveSha256);
            if (job.RerenderRequested && Directory.Exists(jobRoot))
                Directory.Delete(jobRoot, recursive: true);
            var paths = JobStore.BuildPaths(jobRoot);
            var localState = File.Exists(paths.State)
                ? await JobStore.ReadJsonAsync<JobState>(paths.State, cancellationToken)
                : null;
            if (localState is not null && File.Exists(paths.RenderPlan))
            {
                var savedPlan = await JobStore.ReadJsonAsync<RenderPlan>(paths.RenderPlan, cancellationToken);
                if (!PreparedMetadataMatches(savedPlan, job))
                {
                    throw new InputSecurityException(
                        "The existing archive job belongs to different public render metadata.");
                }
            }
            stage = "prepare";
            if (localState is null || localState.Stage is "snapshotted" ||
                localState.Stage == "failed" && localState.FailedStage == "prepare")
            {
                var resume = localState is not null;
                await RunStageAsync(client, job, stage, "Preparing immutable world snapshot.", archiveSha256,
                    token => global::Cli.PrepareAsync(Arguments.Parse(resume
                        ? ["--archive", archivePath, "--manifest", manifestPath, "--work", dimWorkRoot, "--resume", "true"]
                        : ["--archive", archivePath, "--manifest", manifestPath, "--work", dimWorkRoot]), limits, token),
                    cancellationToken);
                localState = await JobStore.ReadJsonAsync<JobState>(paths.State, cancellationToken);
            }

            var preparedPlan = await JobStore.ReadJsonAsync<RenderPlan>(paths.RenderPlan, cancellationToken);
            var inspection = BuildInspection(preparedPlan, job.ArchiveEvidence);
            var skippedRegions = inspection.Dimensions.Sum(value => value.SkippedRegionCount);
            var skippedChunks = inspection.Dimensions.Sum(value => value.SkippedChunkCount);
            var corruptionNote = skippedRegions + skippedChunks > 0
                ? $" Skipped {skippedRegions} corrupt region(s) and {skippedChunks} chunk(s)."
                : string.Empty;
            var prepareStatus = await ReportAsync(
                client, job, "running", "prepare",
                $"Verified {inspection.VersionName ?? "unknown-version"} {inspection.StorageEra} world metadata and chunk inventory.{corruptionNote}",
                archiveSha256, null, cancellationToken,
                progressPercent: ProgressByStage["prepare"].End,
                etaSeconds: EstimateRemainingSeconds("render", TimeSpan.Zero),
                inspection: inspection);
            if (prepareStatus == "needs-match")
            {
                Console.WriteLine($"Worker job {job.Id} parked for manual location match; moving on.");
                return;
            }

            stage = "render";
            var variants = new[] { "day", "night" };
            if (localState.Stage == "prepared" || localState.Stage == "failed" && localState.FailedStage is "render" or "adapt" or "publish")
            {
                var rendererProfile = await RendererOptions.LoadAsync(
                    options.FullRendererProfile, limits.RenderTimeout, limits.MaxLogBytes, cancellationToken);
                var planHash = await JobStore.ComputeSha256Async(paths.RenderPlan, cancellationToken);
                foreach (var variant in variants)
                {
                    var variantPaths = VariantPaths(paths, variant);
                    ResetVariantArtifacts(variantPaths);
                    var variantProfile = WithVariant(rendererProfile, dimensionKey, variant, job.RenderTopY);
                    await RunStageAsync(client, job, stage, $"Rendering {dim.Label} {variant} tiles.", archiveSha256,
                        token => RendererRunner.RenderAsync(
                            variantProfile, preparedPlan, planHash, variantPaths.Render, token),
                        cancellationToken);
                }
                var grade = rendererProfile.NightColorGrade ?? new NightColorGradeOptions();
                if (grade.Enabled)
                {
                    await RunStageAsync(client, job, stage, $"Applying colour-preserving {dim.Label} night grade.", archiveSha256,
                        token => NightTileColorizer.ApplyAsync(
                            Path.Combine(VariantPaths(paths, "day").Render, dimensionKey, "tiles", "zoom.0"),
                            Path.Combine(VariantPaths(paths, "night").Render, dimensionKey, "tiles", "zoom.0"),
                            grade,
                            token),
                        cancellationToken);
                    var nightPaths = VariantPaths(paths, "night");
                    var nightProvenance = await JobStore.ReadJsonAsync<RenderProvenance>(
                        nightPaths.RenderProvenance(dimensionKey), cancellationToken);
                    await JobStore.WriteJsonAsync(
                        nightPaths.RenderProvenance(dimensionKey),
                        nightProvenance with { NightColorGrade = grade, UpdatedAtUtc = DateTimeOffset.UtcNow },
                        cancellationToken);
                }
                await JobStore.WriteJsonAsync(
                    paths.State, new JobState("rendered", DateTimeOffset.UtcNow, "day,night"), cancellationToken);
                localState = new JobState("rendered", DateTimeOffset.UtcNow, "day,night");
            }

            var dimensionDestination = Path.Combine(options.FullPublishRoot, job.Slug, dimensionKey);
            if (localState.Stage == "published")
            {
                var publishedGeneration = ResolvePublishedGeneration(
                    localState.Detail, dimensionDestination, receiptIsVariant: false);
                var publishedGenerationPath = Path.Combine(dimensionDestination, publishedGeneration);
                if (!Directory.Exists(publishedGenerationPath))
                {
                    // A failed registration rolls a newly published generation back. If that API job is
                    // subsequently retried, the durable local checkpoint can still say "published" even
                    // though the rollback correctly removed its generation. Rebuild from authenticated
                    // native renderer output when possible; otherwise fall back to a full render.
                    var canReadapt = variants.All(variant =>
                    {
                        var variantPaths = VariantPaths(paths, variant);
                        return File.Exists(variantPaths.RenderProvenance(dimensionKey)) &&
                               Directory.Exists(Path.Combine(
                                   variantPaths.Render, dimensionKey, "tiles", "zoom.0"));
                    });
                    localState = new JobState(
                        canReadapt ? "rendered" : "prepared",
                        DateTimeOffset.UtcNow,
                        "Recovering a publication generation removed by failed-registration rollback.");
                    await JobStore.WriteJsonAsync(paths.State, localState, cancellationToken);
                }
            }

            stage = "adapt";
            if (localState.Stage == "rendered")
            {
                foreach (var variant in variants)
                {
                    var variantPaths = VariantPaths(paths, variant);
                    await RunStageAsync(client, job, stage, $"Adapting {variant} tiles to the Atlas pyramid.", archiveSha256,
                        token => AtlasTileAdapter.AdaptJobAsync(
                            variantPaths, archiveSha256, dimensionKey, dim.Scheme, limits, token),
                        cancellationToken);
                    if (variant == "day")
                    {
                        var dayReceipt = await JobStore.ReadJsonAsync<AdaptationReceipt>(
                            variantPaths.AdaptationReceipt(dimensionKey), cancellationToken);
                        EnsureDualRenderFreeSpace(
                            options.FullPublishRoot,
                            Path.Combine(options.FullPublishRoot, job.Slug, dimensionKey),
                            dayReceipt.Report.TotalBytes,
                            limits.MinimumFreeBytes);
                    }
                }
                await JobStore.WriteJsonAsync(
                    paths.State, new JobState("adapted", DateTimeOffset.UtcNow, "day,night"), cancellationToken);
                localState = new JobState("adapted", DateTimeOffset.UtcNow, "day,night");
            }

            var generation = localState.Stage == "published"
                ? ResolvePublishedGeneration(localState.Detail, dimensionDestination, receiptIsVariant: false)
                : $"g-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
            var destination = Path.Combine(dimensionDestination, generation);
            stage = "publish";
            if (localState.Stage == "adapted")
            {
                publicationDestination = destination;
                publicationBackup = destination + ".rerender-prev-" + job.Id;
                publicationStaging = destination + ".rerender-next-" + job.Id;
                if (Directory.Exists(publicationBackup)) Directory.Delete(publicationBackup, recursive: true);
                if (Directory.Exists(publicationStaging)) Directory.Delete(publicationStaging, recursive: true);
                try
                {
                    foreach (var variant in variants)
                    {
                        var variantPaths = VariantPaths(paths, variant);
                        var variantStaging = Path.Combine(publicationStaging, variant);
                        await RunStageAsync(client, job, stage, $"Publishing verified {variant} tiles.", archiveSha256,
                            token => PublicationService.PublishAsync(
                                variantPaths, archiveSha256, dimensionKey,
                                AtlasTileAdapter.OutputRelativePath, variantStaging, limits, token),
                            cancellationToken);
                    }

                    foreach (var variant in variants)
                    {
                        await SynchronizeDirectoryAsync(
                            Path.Combine(publicationStaging, variant),
                            Path.Combine(destination, variant),
                            cancellationToken);
                        var variantPaths = VariantPaths(paths, variant);
                        var stagedReceipt = await JobStore.ReadJsonAsync<PublicationReceipt>(
                            variantPaths.PublicationReceipt(dimensionKey), cancellationToken);
                        var variantDestination = Path.Combine(destination, variant);
                        var publishedReport = TilePublisher.Verify(
                            variantDestination, limits, stagedReceipt.Report.CoordinateScheme);
                        if (!TilePublisher.ReportsMatch(stagedReceipt.Report, publishedReport))
                            throw new InputSecurityException($"Synchronized {variant} tile inventory does not match staging.");
                        await JobStore.WriteJsonAsync(
                            variantPaths.PublicationReceipt(dimensionKey),
                            stagedReceipt with { Destination = variantDestination, Report = publishedReport },
                            cancellationToken);
                    }
                    Directory.Delete(publicationStaging, recursive: true);
                    publicationStaging = null;
                    await JobStore.WriteJsonAsync(
                        paths.State, new JobState("published", DateTimeOffset.UtcNow, destination), cancellationToken);
                }
                catch
                {
                    await RestorePublishedTilesAsync(
                        publicationDestination, publicationBackup, publicationStaging,
                        publicationHadExistingDestination, publicationBackupReady, cancellationToken);
                    throw;
                }
                localState = await JobStore.ReadJsonAsync<JobState>(paths.State, cancellationToken);
            }

            if (localState.Stage is not ("published" or "registered-unpublished"))
                throw new InputValidationException($"Local job cannot safely continue from stage '{localState.Stage}'.");

            // A resumed job can spend minutes re-hashing a large already-published pyramid. Keep the
            // API lease alive during that verification just as we do during render/adapt/publish work.
            var receipts = await RunStageAsync(
                client, job, stage, "Verifying published day/night tiles.", archiveSha256,
                async token =>
                {
                    var verified = new List<PublicationReceipt>();
                    foreach (var variant in variants)
                        verified.Add(await PublicationService.ReadAndVerifyAsync(
                            VariantPaths(paths, variant), archiveSha256, dimensionKey, limits, token));
                    return verified;
                },
                cancellationToken);
            var receipt = receipts[0];
            if (localState.Stage == "registered-unpublished")
            {
                generation = ResolvePublishedGeneration(receipt.Destination, dimensionDestination, receiptIsVariant: true);
                destination = Path.Combine(dimensionDestination, generation);
            }
            if (receipts.Any(value => value.Report.MaxZoom != receipt.Report.MaxZoom ||
                                      value.Report.CoordinateScheme != receipt.Report.CoordinateScheme))
                throw new InputSecurityException("Day and night tile pyramids do not share one layout contract.");
            var plan = await JobStore.ReadJsonAsync<RenderPlan>(paths.RenderPlan, cancellationToken);
            var bounds = plan.Dimensions.Single(value => value.Key == dimensionKey).Bounds
                ?? throw new InputSecurityException($"Prepared {dim.Label} plan has no authoritative chunk bounds.");
            var scheme = AtlasTileScheme.Parse(dim.Scheme);
            var render = new LocationRenderCompletion
            {
                TilesPath = $"{options.PublicTileRoot.TrimEnd('/')}/{job.Slug}/{dimensionKey}/{generation}/{{dn}}/{{z}}/{{y}}/{{x}}.png",
                HasDayNight = true,
                Dimension = dim.Index,
                MinX = bounds.MinBlockX,
                MinZ = bounds.MinBlockZ,
                MaxXExclusive = bounds.MaxBlockXExclusive,
                MaxZExclusive = bounds.MaxBlockZExclusive,
                MaxNativeZoom = receipt.Report.MaxZoom - scheme.UrlZoomOffset,
                CoordinateScheme = receipt.Report.CoordinateScheme,
            };
            await ReportAsync(client, job, "completed", "registered", "Registered and linked the location base render.",
                archiveSha256, render, cancellationToken);
            try { DeleteSupersededRenderEntries(dimensionDestination, generation); }
            catch (IOException exception) { Console.Error.WriteLine($"Could not remove superseded render entries: {exception.Message}"); }
            if (publicationBackup is not null && Directory.Exists(publicationBackup))
            {
                try { Directory.Delete(publicationBackup, recursive: true); }
                catch (IOException exception) { Console.Error.WriteLine($"Could not remove render rollback: {exception.Message}"); }
            }
            publicationBackup = null;
            publicationBackupReady = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RestorePublishedTilesAsync(
                publicationDestination, publicationBackup, publicationStaging,
                publicationHadExistingDestination, publicationBackupReady, CancellationToken.None);
            Console.Error.WriteLine($"Worker job {job.Id} failed in {stage}: {exception}");
            await SafeReportAsync(client, job, "failed", stage,
                $"{stage} failed; inspect the local worker log ({exception.GetType().Name}).",
                archiveSha256, null);
        }
    }

    private static async Task RestorePublishedTilesAsync(
        string? destination,
        string? backup,
        string? staging,
        bool hadExistingDestination,
        bool backupReady,
        CancellationToken cancellationToken)
    {
        if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        if (destination is null || backup is null) return;
        if (hadExistingDestination && backupReady && Directory.Exists(backup))
        {
            await SynchronizeDirectoryAsync(backup, destination, cancellationToken);
            Directory.Delete(backup, recursive: true);
            return;
        }
        if (!hadExistingDestination && Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
    }

    private static async Task SynchronizeDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(source);
        var destinationRoot = Path.GetFullPath(destination);
        Directory.CreateDirectory(destinationRoot);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, sourceFile);
            expected.Add(relative);
            var destinationFile = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            await CopyFileWithRetryAsync(sourceFile, destinationFile, cancellationToken);
        }

        foreach (var destinationFile in Directory.EnumerateFiles(destinationRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(destinationRoot, destinationFile);
            if (!expected.Contains(relative)) File.Delete(destinationFile);
        }
        foreach (var directory in Directory.EnumerateDirectories(destinationRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    private static async Task CopyFileWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        IOException? lastError = null;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException exception) when (attempt < 10)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
            }
        }
        throw lastError ?? new IOException($"Could not copy tile '{source}' to '{destination}'.");
    }

    private static void DeleteSupersededRenderEntries(string dimensionRoot, string currentGeneration)
    {
        if (!Directory.Exists(dimensionRoot)) return;
        foreach (var file in Directory.EnumerateFiles(dimensionRoot)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(dimensionRoot))
        {
            var name = Path.GetFileName(directory);
            if (!string.Equals(name, currentGeneration, StringComparison.Ordinal))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static JobPaths VariantPaths(JobPaths paths, string variant)
    {
        if (variant is not ("day" or "night"))
            throw new InputValidationException("Render variant must be day or night.");
        var artifactRoot = Path.Combine(paths.Root, "variants", variant);
        Directory.CreateDirectory(artifactRoot);
        return new JobPaths(
            artifactRoot,
            paths.Snapshot,
            paths.Extracted,
            Path.Combine(paths.Render, variant),
            paths.State,
            paths.ArchiveReport,
            paths.RenderPlan);
    }

    private static void ResetVariantArtifacts(JobPaths paths)
    {
        if (Directory.Exists(paths.Render)) Directory.Delete(paths.Render, recursive: true);
        foreach (var file in Directory.EnumerateFiles(paths.Root)) File.Delete(file);
    }

    private static RendererOptions WithVariant(
        RendererOptions options,
        string dimension,
        string variant,
        int? renderTopY)
    {
        var commands = options.DimensionArguments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .Where(argument => !argument.StartsWith("--night", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            StringComparer.Ordinal);
        if (variant == "night") commands[dimension].Add("--night=true");
        if (renderTopY is int topY)
        {
            commands[dimension].RemoveAll(argument =>
                argument.StartsWith("--topY=", StringComparison.OrdinalIgnoreCase));
            commands[dimension].Add($"--topY={topY}");
        }
        return options with
        {
            DimensionArguments = commands.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal),
            Resume = false,
        };
    }

    private static void EnsureDualRenderFreeSpace(
        string publishRoot,
        string existingDestination,
        long oneVariantBytes,
        long reserveBytes)
    {
        var existingBytes = Directory.Exists(existingDestination)
            ? Directory.EnumerateFiles(existingDestination, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length)
            : 0L;
        // Remaining work must fit: night variant, day+night staging, one rollback, and reserve.
        var required = checked((oneVariantBytes * 3) + existingBytes + reserveBytes);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(publishRoot))!);
        if (drive.AvailableFreeSpace < required)
            throw new InputValidationException(
                $"Insufficient free space for dual render replacement. Need {required:N0}, have {drive.AvailableFreeSpace:N0} bytes.");
    }

    private static string ResolveArchivePath(string intakeRoot, string fileName, bool requireExists = true)
    {
        var fullPath = Path.GetFullPath(Path.Combine(intakeRoot, fileName));
        var relative = Path.GetRelativePath(intakeRoot, fullPath);
        if (relative != fileName || requireExists && !File.Exists(fullPath))
            throw new InputValidationException("The requested intake file does not exist.");
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InputSecurityException("Intake files cannot be links or reparse points.");
        return fullPath;
    }

    private static async Task<string> WriteManifestAsync(
        string workRoot,
        IngestionJobDto job,
        string dimensionKey,
        CancellationToken cancellationToken)
    {
        var requestRoot = Path.Combine(workRoot, ".requests");
        Directory.CreateDirectory(requestRoot);
        var path = Path.Combine(requestRoot, job.Id + ".json");
        await JobStore.WriteJsonAsync(path, new
        {
            schemaVersion = 1,
            slug = job.Slug,
            name = job.Name,
            worldDownloadDate = job.WorldDownloadDate,
            source = job.Source,
            dimensions = new[] { dimensionKey },
            worldRoot = job.WorldRoot,
            dayNight = true,
            scale = job.Scale,
            publish = false,
        }, cancellationToken);
        return path;
    }

    private static async Task<string?> ReportAsync(
        HttpClient client,
        IngestionJobDto job,
        string status,
        string stage,
        string message,
        string? archiveSha256,
        LocationRenderCompletion? render,
        CancellationToken cancellationToken,
        int? progressPercent = null,
        int? etaSeconds = null,
        IngestionWorldInspection? inspection = null)
    {
        var update = new IngestionJobUpdate
        {
            ClaimToken = job.ClaimToken ?? string.Empty,
            Status = status,
            Stage = stage,
            Message = message,
            ArchiveSha256 = archiveSha256,
            LocationRender = render,
            ProgressPercent = status == "completed" ? 100 : progressPercent ?? ProgressByStage.GetValueOrDefault(stage)?.Start ?? 0,
            EtaSeconds = status is "completed" or "failed" ? null : etaSeconds ?? EstimateRemainingSeconds(stage, TimeSpan.Zero),
            Inspection = inspection,
        };
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var response = await client.PutAsJsonAsync(
                    $"api/ingestion-jobs/{job.Id}/status", update, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var dto = await response.Content.ReadFromJsonAsync<IngestionJobDto>(cancellationToken: cancellationToken);
                    return dto?.Status;
                }
                if (attempt < 3 && ((int)response.StatusCode >= 500 ||
                    response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
                    continue;
                }
                await EnsureSuccessAsync(response, "update ingestion status", cancellationToken);
            }
            catch (Exception exception) when (attempt < 3 &&
                exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
            }
        }
        throw new InputValidationException("Could not update ingestion status after retries.");
    }

    private static async Task SafeReportAsync(
        HttpClient client,
        IngestionJobDto job,
        string status,
        string stage,
        string message,
        string? archiveSha256,
        LocationRenderCompletion? render)
    {
        try
        {
            await ReportAsync(client, job, status, stage, message, archiveSha256, render, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not report terminal state for job {job.Id}: {exception.Message}");
        }
    }

    private static async Task RunStageAsync(
        HttpClient client,
        IngestionJobDto job,
        string stage,
        string message,
        string? archiveSha256,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        await RunStageAsync<object?>(client, job, stage, message, archiveSha256, async token =>
        {
            await action(token);
            return null;
        }, cancellationToken);

    private static async Task<T> RunStageAsync<T>(
        HttpClient client,
        IngestionJobDto job,
        string stage,
        string message,
        string? archiveSha256,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        await ReportProgressAsync(client, job, stage, message, archiveSha256, TimeSpan.Zero, cancellationToken);
        using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stageTask = action(stageCancellation.Token);
        while (!stageTask.IsCompleted)
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            if (await Task.WhenAny(stageTask, delay) == stageTask) break;
            try
            {
                await ReportProgressAsync(client, job, stage, message, archiveSha256,
                    DateTimeOffset.UtcNow - started, cancellationToken);
            }
            catch
            {
                stageCancellation.Cancel();
                try { await stageTask; } catch { }
                throw;
            }
        }
        return await stageTask;
    }

    private static async Task ReportProgressAsync(
        HttpClient client,
        IngestionJobDto job,
        string stage,
        string message,
        string? archiveSha256,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var progress = ProgressByStage.GetValueOrDefault(stage) ?? new StageProgress(0, 1, 60);
        var fraction = progress.EstimatedSeconds == 0
            ? 1d
            : Math.Min(0.95, elapsed.TotalSeconds / progress.EstimatedSeconds);
        var percent = progress.Start + (int)Math.Floor((progress.End - progress.Start) * fraction);
        await ReportAsync(client, job, "running", stage, message, archiveSha256, null,
            cancellationToken, percent, EstimateRemainingSeconds(stage, elapsed));
    }

    private static int? EstimateRemainingSeconds(string stage, TimeSpan elapsed)
    {
        if (!ProgressByStage.TryGetValue(stage, out var current)) return null;
        var remaining = Math.Max(0, current.EstimatedSeconds - (int)elapsed.TotalSeconds);
        var afterCurrent = false;
        foreach (var item in ProgressByStage)
        {
            if (afterCurrent) remaining += item.Value.EstimatedSeconds;
            if (item.Key == stage) afterCurrent = true;
        }
        return remaining;
    }

    /// <summary>
    /// Resolves the immutable generation already bound to a durable publication receipt. A resumed job must
    /// register this generation rather than inventing a new URL and deleting the verified published tiles.
    /// </summary>
    internal static string ResolvePublishedGeneration(
        string? publishedDestination,
        string dimensionDestination,
        bool receiptIsVariant)
    {
        if (string.IsNullOrWhiteSpace(publishedDestination))
            throw new InputSecurityException("Published job state does not identify its immutable tile destination.");
        var destination = Path.GetFullPath(publishedDestination);
        var generationPath = receiptIsVariant
            ? Directory.GetParent(destination)?.FullName
            : destination;
        if (generationPath is null || !string.Equals(
                Path.GetDirectoryName(generationPath), Path.GetFullPath(dimensionDestination),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InputSecurityException("Published receipt destination escaped the render's dimension root.");
        var generation = Path.GetFileName(generationPath);
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                generation, "^g-[0-9]{14}-[a-f0-9]{8}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InputSecurityException("Published receipt has an invalid generation identifier.");
        return generation;
    }

    private static bool PreparedMetadataMatches(RenderPlan savedPlan, IngestionJobDto job)
    {
        if (savedPlan.Slug != job.Slug || savedPlan.Name != job.Name ||
            savedPlan.Source != job.Source || savedPlan.Scale != job.Scale)
            return false;

        if (savedPlan.WorldDownloadDate.ToString("yyyy-MM-dd") == job.WorldDownloadDate)
            return true;

        // The API may replace an uploader's provisional date with this same snapshot's
        // validated LastPlayed date during prepare. Permit exactly that authenticated drift
        // so a manually matched job can resume; arbitrary metadata changes remain rejected.
        if (job.UseArchiveLastPlayed == false ||
            savedPlan.World.LastPlayedUnixMilliseconds is not long milliseconds)
            return false;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                .UtcDateTime.ToString("yyyy-MM-dd") == job.WorldDownloadDate;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static IngestionWorldInspection BuildInspection(RenderPlan plan, ArchiveWdlEvidence? intakeEvidence)
    {
        DateTime? lastPlayedUtc = null;
        if (plan.World.LastPlayedUnixMilliseconds is long milliseconds)
        {
            try
            {
                var candidate = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                if (candidate >= new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero) &&
                    candidate <= DateTimeOffset.UtcNow.AddDays(1))
                    lastPlayedUtc = candidate.UtcDateTime;
            }
            catch (ArgumentOutOfRangeException) { }
        }
        return new IngestionWorldInspection
        {
            LevelName = plan.World.LevelName,
            DataVersion = plan.World.DataVersion,
            VersionName = plan.World.VersionName,
            LastPlayedUtc = lastPlayedUtc,
            StorageEra = plan.World.StorageEra,
            ProvenanceStatus = "unverified",
            ProvenanceMessage =
                "Structurally valid Minecraft Java world; 2b2t origin requires reviewed source and overlap evidence.",
            ArchiveEvidence = ArchiveWdlEvidence.Merge(intakeEvidence, plan.World.ArchiveEvidence),
            Dimensions = plan.World.Dimensions.Select(dimension =>
            {
                var bounds = dimension.Bounds
                    ?? throw new InputSecurityException($"Prepared {dimension.Key} dimension has no authoritative bounds.");
                return new IngestionDimensionInspection
                {
                    Key = dimension.Key,
                    StorageEra = dimension.StorageEra,
                    StorageFileCount = dimension.RegionFileCount,
                    ChunkCount = bounds.ChunkCount,
                    NativeTileCount = bounds.NativeTileCount,
                    MinX = bounds.MinBlockX,
                    MinZ = bounds.MinBlockZ,
                    MaxXExclusive = bounds.MaxBlockXExclusive,
                    MaxZExclusive = bounds.MaxBlockZExclusive,
                    SkippedRegionCount = bounds.SkippedRegions,
                    SkippedChunkCount = bounds.SkippedChunks,
                };
            }).ToList(),
        };
    }

    private sealed record StageProgress(int Start, int End, int EstimatedSeconds);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 500) body = body[..500];
        throw new InputValidationException($"Could not {operation}: HTTP {(int)response.StatusCode} {body}");
    }
}
