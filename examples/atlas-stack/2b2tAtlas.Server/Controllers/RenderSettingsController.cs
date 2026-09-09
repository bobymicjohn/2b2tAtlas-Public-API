using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Atlas;
using Atlas.Auth;
using _2b2tAtlas.Server.Services;
using SkiaSharp;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>
/// Admin surface for tuning the unMINED render pipeline. It reads and writes the same <c>renderer.json</c> and
/// unMINED colour/biome/style config files the worker renders with, and runs a small sample preview so an
/// operator can iterate on the look without re-ingesting a world download. The unMINED executable path and its
/// SHA-256 pin are never modified, preserving the fail-closed render gate. All routes require
/// <c>render.settings.manage</c>.
/// </summary>
[ApiController]
[Route("api/render-settings")]
[Authorize(Policy = Permissions.RenderSettingsManage)]
public sealed class RenderSettingsController : ControllerBase
{
    private static readonly HashSet<string> ValidDimensions = new(StringComparer.OrdinalIgnoreCase) { "overworld", "nether", "end" };
    private static readonly HashSet<string> ValidShadows = new(StringComparer.OrdinalIgnoreCase) { "default", "false", "true", "2d", "3d", "3do" };
    private static readonly HashSet<string> ValidFormats = new(StringComparer.OrdinalIgnoreCase) { "png" };
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonWrite = new() { WriteIndented = true };

    private const string ColorsFile = "custom.colors.txt";
    private const string BiomeTintsFile = "custom.biometints.txt";
    private const string BlockStylesFile = "custom.blockstyles.txt";

    private readonly RenderSettingsOptions _options;
    private readonly WdlArchiveOptions _archiveOptions;
    private readonly _2b2tAtlas.Server.Models.AtlasContext _context;
    private readonly AuditService _audit;
    private readonly ILogger<RenderSettingsController> _logger;

    /// <summary>Initializes the render-settings controller.</summary>
    /// <param name="options">The bound filesystem locations for the renderer profile and sample world.</param>
    /// <param name="archiveOptions">The content-addressed WDL archive location.</param>
    /// <param name="context">The Atlas database containing completed ingestion jobs.</param>
    /// <param name="audit">The append-only audit recorder.</param>
    /// <param name="logger">The diagnostic logger.</param>
    public RenderSettingsController(
        IOptions<RenderSettingsOptions> options,
        IOptions<WdlArchiveOptions> archiveOptions,
        _2b2tAtlas.Server.Models.AtlasContext context,
        AuditService audit,
        ILogger<RenderSettingsController> logger)
    {
        _options = options.Value;
        _archiveOptions = archiveOptions.Value;
        _context = context;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>GET /api/render-settings — the current render flags and raw colour/biome/style config.</summary>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The current settings, or 500 when the renderer profile cannot be read.</returns>
    [HttpGet]
    public async Task<ActionResult<RenderSettingsDto>> Get(CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(_options.RendererJsonPath))
            return Problem($"Renderer profile not found at {_options.RendererJsonPath}.");

        JsonNode root;
        try
        {
            root = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(_options.RendererJsonPath, cancellationToken))!;
        }
        catch (JsonException exception)
        {
            return Problem($"Renderer profile is not valid JSON: {exception.Message}");
        }

        var configDir = ConfigDir(root);
        var dto = new RenderSettingsDto
        {
            RendererVersion = root["version"]?.GetValue<string>() ?? string.Empty,
            ExecutablePath = root["executablePath"]?.GetValue<string>() ?? string.Empty,
            NightColorGrade = ParseNightColorGrade(root),
            SampleWorlds = ListSampleWorlds(),
            ColorsText = await ReadConfigAsync(configDir, ColorsFile, cancellationToken),
            BiomeTintsText = await ReadConfigAsync(configDir, BiomeTintsFile, cancellationToken),
            BlockStylesText = await ReadConfigAsync(configDir, BlockStylesFile, cancellationToken),
        };

        if (root["dimensionArguments"] is JsonObject dims)
        {
            foreach (var (dim, args) in dims)
            {
                if (args is JsonArray array)
                    dto.Dimensions.Add(ParseArgs(dim, array));
            }
        }

        return Ok(dto);
    }

    /// <summary>PUT /api/render-settings — persist the render flags and raw config files.</summary>
    /// <param name="dto">The settings to save.</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>No content on success, or 400 with validation errors.</returns>
    [HttpPut]
    public async Task<IActionResult> Save([FromBody] RenderSettingsDto dto, CancellationToken cancellationToken)
    {
        if (dto is null) return BadRequest("Request body is required.");

        var errors = Validate(dto);
        if (errors.Count > 0) return BadRequest(new { errors });

        if (!System.IO.File.Exists(_options.RendererJsonPath))
            return Problem($"Renderer profile not found at {_options.RendererJsonPath}.");

        JsonNode root;
        try
        {
            root = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(_options.RendererJsonPath, cancellationToken))!;
        }
        catch (JsonException exception)
        {
            return Problem($"Renderer profile is not valid JSON: {exception.Message}");
        }

        // Rebuild only the dimension argument arrays; executablePath, expectedSha256, version stay untouched.
        var dims = root["dimensionArguments"] as JsonObject ?? new JsonObject();
        foreach (var option in dto.Dimensions)
            dims[option.Dimension.ToLowerInvariant()] = BuildArgs(option);
        root["dimensionArguments"] = dims;
        root["nightColorGrade"] = new JsonObject
        {
            ["enabled"] = dto.NightColorGrade.Enabled,
            ["saturation"] = dto.NightColorGrade.Saturation,
            ["lightness"] = dto.NightColorGrade.Lightness,
        };

        BackupThenWrite(_options.RendererJsonPath, root.ToJsonString(JsonWrite));

        var configDir = ConfigDir(root);
        WriteConfig(configDir, ColorsFile, dto.ColorsText);
        WriteConfig(configDir, BiomeTintsFile, dto.BiomeTintsText);
        WriteConfig(configDir, BlockStylesFile, dto.BlockStylesText);

        await _audit.LogAsync("render.settings.update", "RenderSettings", 0, CurrentUserId(), CurrentUsername(),
            "Updated unMINED render settings and colour/biome/style overrides");
        return NoContent();
    }

    /// <summary>Gets the current bulk re-render queue state.</summary>
    /// <param name="cancellationToken">Token that cancels the query.</param>
    [HttpGet("rerender-status")]
    public async Task<ActionResult<BulkRerenderStatus>> GetRerenderStatus(CancellationToken cancellationToken)
    {
        var rows = await _context.IngestionJobs.AsNoTracking()
            .Where(job => job.RerenderRequested == 1)
            .GroupBy(job => job.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        int Count(params string[] statuses) => rows.Where(row => statuses.Contains(row.Status)).Sum(row => row.Count);
        return Ok(new BulkRerenderStatus
        {
            Queued = Count("queued"),
            Running = Count("claimed", "running", "completing"),
            Failed = Count("failed"),
        });
    }

    /// <summary>Queues every completed location render for replacement using its archived source WDL.</summary>
    /// <param name="cancellationToken">Token that cancels preflight and queueing.</param>
    /// <returns>Counts of queued jobs and distinct source archives.</returns>
    [HttpPost("rerender-all")]
    public async Task<ActionResult<BulkRerenderResult>> RerenderAll(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_archiveOptions.Root))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "The WDL archive is unavailable.");

        var active = await _context.IngestionJobs.AnyAsync(job =>
            job.RerenderRequested == 1 &&
            (job.Status == "queued" || job.Status == "claimed" || job.Status == "running" || job.Status == "completing"),
            cancellationToken);
        if (active) return Conflict("A bulk re-render is already queued or running.");

        var jobs = await _context.IngestionJobs
            .Where(job => job.RenderId != null && job.ArchiveSha256 != null &&
                (job.Status == "completed" || job.RerenderRequested == 1 && job.Status == "failed"))
            .OrderBy(job => job.Id)
            .ToListAsync(cancellationToken);
        if (jobs.Count == 0) return Conflict("No completed renders are eligible for rebuilding.");

        var missing = jobs.Select(job => job.ArchiveSha256!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(sha => !System.IO.File.Exists(WdlArchiveStore.ObjectPath(_archiveOptions.Root, sha)))
            .ToList();
        if (missing.Count > 0)
            return Conflict(new { message = "Bulk re-render was not queued because archived WDL sources are missing.", missing });

        var renderIds = jobs.Select(job => job.RenderId!.Value).ToList();
        var renders = await _context.Renders
            .Where(render => renderIds.Contains(render.Id))
            .ToDictionaryAsync(render => render.Id, cancellationToken);

        var now = DateTime.UtcNow.ToString("o");
        foreach (var job in jobs)
        {
            if (!renders.TryGetValue(job.RenderId!.Value, out var render))
                return Conflict($"Render {job.RenderId} for ingestion job {job.Id} is missing.");
            job.Dimension = render.Dimension switch
            {
                0 => "overworld",
                1 => "nether",
                2 => "end",
                _ => throw new InvalidOperationException($"Render {render.Id} has an invalid dimension."),
            };
            job.ExistingLocationId = render.LocationRowid;
            job.Status = "queued";
            job.Stage = "rerender-queued";
            job.Message = "Queued to rebuild the existing render with current unMINED settings.";
            job.ProgressPercent = 0;
            job.DayNight = 1;
            job.EtaSeconds = null;
            job.RerenderRequested = 1;
            job.AttemptCount = 0;
            job.ClaimTokenSha256 = null;
            job.ClaimedUtc = null;
            job.LeaseExpiresUtc = null;
            job.UpdatedUtc = now;
            job.CompletedUtc = null;
        }
        await _context.SaveChangesAsync(cancellationToken);
        var distinctArchives = jobs.Select(job => job.ArchiveSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        await _audit.LogAsync("render.rerender.all", "RenderSettings", 0, CurrentUserId(), CurrentUsername(),
            $"Queued {jobs.Count} renders from {distinctArchives} archived WDLs for rebuilding");
        return Ok(new BulkRerenderResult
        {
            QueuedJobs = jobs.Count,
            DistinctArchives = distinctArchives,
            Message = $"Queued {jobs.Count} renders. The worker will rebuild them sequentially.",
        });
    }

    /// <summary>POST /api/render-settings/preview — render a bounded sample area with the current settings.</summary>
    /// <param name="world">The staged sample world name to render.</param>
    /// <param name="dimension">Which dimension to preview (overworld/nether/end).</param>
    /// <param name="cancellationToken">Token that cancels the request.</param>
    /// <returns>The rendered PNG (base64) or a failure explanation.</returns>
    [HttpPost("preview")]
    public async Task<ActionResult<RenderPreviewResult>> Preview([FromQuery] string world, [FromQuery] string dimension, CancellationToken cancellationToken)
    {
        if (!ValidDimensions.Contains(dimension ?? string.Empty))
            return BadRequest("dimension must be overworld, nether, or end.");
        var worldPath = ResolveSampleWorld(world);
        if (worldPath is null)
            return Ok(new RenderPreviewResult { Ok = false, Error = "Unknown or unavailable sample world." });
        if (!System.IO.File.Exists(_options.RendererJsonPath))
            return Ok(new RenderPreviewResult { Ok = false, Error = "Renderer profile not found." });

        var root = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(_options.RendererJsonPath, cancellationToken))!;
        var exe = root["executablePath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(exe) || !System.IO.File.Exists(exe))
            return Ok(new RenderPreviewResult { Ok = false, Error = "unMINED executable not found." });

        var area = AutoDetectArea(worldPath, dimension!);
        if (area is null)
            return Ok(new RenderPreviewResult { Ok = false, Error = "The sample world has no region files to preview." });

        var outputRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"atlas-preview-{Guid.NewGuid():N}");
        var dayOutput = System.IO.Path.Combine(outputRoot, "day");
        var nightOutput = System.IO.Path.Combine(outputRoot, "night");
        System.IO.Directory.CreateDirectory(dayOutput);
        System.IO.Directory.CreateDirectory(nightOutput);
        var args = new List<string>
        {
            "web", "render",
            $"--world={worldPath}",
            $"--dimension={dimension!.ToLowerInvariant()}",
            $"--area={area.Expression}",
            $"--zoomout={_options.PreviewZoom}",
            "--zoomin=0",
            "--imageformat=png",
        };

        if (root["dimensionArguments"]?[dimension.ToLowerInvariant()] is JsonArray dimensionArgs)
        {
            var renderOptions = ParseArgs(dimension, dimensionArgs);
            if (renderOptions.TopY is int topY) args.Add($"--topY={topY}");
            if (renderOptions.BottomY is int bottomY) args.Add($"--bottomY={bottomY}");
            if (!string.IsNullOrWhiteSpace(renderOptions.Shadows) &&
                !string.Equals(renderOptions.Shadows, "default", StringComparison.OrdinalIgnoreCase))
                args.Add($"--shadows={renderOptions.Shadows}");
            if (!string.IsNullOrWhiteSpace(renderOptions.Background))
                args.Add($"--background={renderOptions.Background}");
        }
        var stopwatch = Stopwatch.StartNew();
        var dayArgs = args.Append($"--output={dayOutput}").ToList();
        var nightArgs = args.Append("--night=true").Append($"--output={nightOutput}").ToList();
        var (dayExit, dayDiagnostics) = await RunProcessAsync(exe!, dayArgs, _options.PreviewTimeoutSeconds, cancellationToken);
        var (nightExit, nightDiagnostics) = await RunProcessAsync(exe!, nightArgs, _options.PreviewTimeoutSeconds, cancellationToken);
        stopwatch.Stop();

        try
        {
            if (dayExit != 0 || nightExit != 0)
            {
                var diagnostics = $"Day ({dayExit}): {dayDiagnostics} Night ({nightExit}): {nightDiagnostics}";
                _logger.LogWarning("Dual render preview failed: {Error}", diagnostics);
                return Ok(new RenderPreviewResult { Ok = false, Error = Truncate(diagnostics, 1200) });
            }

            var composition = await RenderTileComposer.ComposeAsync(dayOutput, cancellationToken);
            var nightComposition = await RenderTileComposer.ComposeAsync(nightOutput, cancellationToken);
            if (nightComposition.MinX != composition.MinX || nightComposition.MinZ != composition.MinZ ||
                nightComposition.MaxXExclusive != composition.MaxXExclusive ||
                nightComposition.MaxZExclusive != composition.MaxZExclusive)
                throw new InvalidOperationException("Day and night previews do not share identical bounds.");
            var nightPng = ApplyNightColorGrade(
                composition.Png,
                nightComposition.Png,
                ParseNightColorGrade(root));
            return Ok(new RenderPreviewResult
            {
                Ok = true,
                ImageBase64 = Convert.ToBase64String(composition.Png),
                NightImageBase64 = Convert.ToBase64String(nightPng),
                WidthPx = composition.Width,
                HeightPx = composition.Height,
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                MinX = composition.MinX,
                MinZ = composition.MinZ,
                MaxXExclusive = composition.MaxXExclusive,
                MaxZExclusive = composition.MaxZExclusive,
            });
        }
        finally
        {
            try { System.IO.Directory.Delete(outputRoot, recursive: true); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

    // ---- helpers ----

    private List<string> ListSampleWorlds()
    {
        if (!System.IO.Directory.Exists(_options.SampleWorldsRoot)) return [];
        return System.IO.Directory.EnumerateDirectories(_options.SampleWorldsRoot)
            .Where(path => System.IO.File.Exists(System.IO.Path.Combine(path, "level.dat")))
            .Select(System.IO.Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    private string? ResolveSampleWorld(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var match = ListSampleWorlds().FirstOrDefault(candidate =>
            string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        var root = System.IO.Path.GetFullPath(_options.SampleWorldsRoot)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, match));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static PreviewArea? AutoDetectArea(string worldPath, string dimension)
    {
        var relativeRegionPath = dimension.ToLowerInvariant() switch
        {
            "nether" => System.IO.Path.Combine("DIM-1", "region"),
            "end" => System.IO.Path.Combine("DIM1", "region"),
            _ => "region",
        };
        var regionDir = System.IO.Path.Combine(worldPath, relativeRegionPath);
        if (!System.IO.Directory.Exists(regionDir)) return null;

        var pattern = new Regex(@"^r\.(-?\d+)\.(-?\d+)\.mca$", RegexOptions.CultureInvariant);
        var anchor = System.IO.Directory.EnumerateFiles(regionDir, "r.*.*.mca")
            .Select(path => new { Path = path, Match = pattern.Match(System.IO.Path.GetFileName(path)) })
            .Where(item => item.Match.Success)
            .Select(item => new
            {
                X = int.Parse(item.Match.Groups[1].Value),
                Z = int.Parse(item.Match.Groups[2].Value),
                Size = new System.IO.FileInfo(item.Path).Length,
            })
            .OrderByDescending(item => item.Size)
            .FirstOrDefault();

        if (anchor is null) return null;
        var regionX = anchor.X - 1;
        var regionZ = anchor.Z - 1;
        return new PreviewArea(
            $"r({regionX},{regionZ},3,3)",
            regionX * 512,
            regionZ * 512,
            (regionX + 3) * 512,
            (regionZ + 3) * 512);
    }

    private sealed record PreviewArea(
        string Expression,
        int MinX,
        int MinZ,
        int MaxXExclusive,
        int MaxZExclusive);

    private static string ConfigDir(JsonNode root)
    {
        var exe = root["executablePath"]?.GetValue<string>() ?? string.Empty;
        var toolDir = System.IO.Path.GetDirectoryName(exe) ?? string.Empty;
        return System.IO.Path.Combine(toolDir, "config");
    }

    private async Task<string> ReadConfigAsync(string configDir, string file, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(configDir, file);
        return System.IO.File.Exists(path) ? await System.IO.File.ReadAllTextAsync(path, cancellationToken) : string.Empty;
    }

    private void WriteConfig(string configDir, string file, string content)
    {
        var path = System.IO.Path.Combine(configDir, file);
        // Only write the whitelisted files inside the unMINED config directory.
        var fullDir = System.IO.Path.GetFullPath(configDir);
        var fullPath = System.IO.Path.GetFullPath(path);
        if (System.IO.Path.GetDirectoryName(fullPath) != fullDir)
            throw new InvalidOperationException("Refusing to write outside the unMINED config directory.");
        BackupThenWrite(path, content ?? string.Empty);
    }

    private static void BackupThenWrite(string path, string content)
    {
        if (System.IO.File.Exists(path))
            System.IO.File.Copy(path, path + ".bak", overwrite: true);
        System.IO.File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private List<string> Validate(RenderSettingsDto dto)
    {
        var errors = new List<string>();
        foreach (var option in dto.Dimensions)
        {
            if (!ValidDimensions.Contains(option.Dimension ?? string.Empty))
                errors.Add($"Unknown dimension '{option.Dimension}'.");
            if (!ValidShadows.Contains(option.Shadows ?? "default"))
                errors.Add($"Invalid shadows value '{option.Shadows}' for {option.Dimension}.");
            if (!ValidFormats.Contains(option.ImageFormat ?? "png"))
                errors.Add($"Invalid image format '{option.ImageFormat}' for {option.Dimension}.");
            if (!string.IsNullOrWhiteSpace(option.Background) && !HexColor.IsMatch(option.Background))
                errors.Add($"Background must be #rrggbb for {option.Dimension}.");
            if (option.TopY is int ty && ty is < -64 or > 384)
                errors.Add($"topY out of range for {option.Dimension}.");
            if (option.BottomY is int by && by is < -64 or > 384)
                errors.Add($"bottomY out of range for {option.Dimension}.");
            if (option.TopY is int topY && option.BottomY is int bottomY && bottomY > topY)
                errors.Add($"bottomY cannot be greater than topY for {option.Dimension}.");
        }
        if ((dto.ColorsText?.Length ?? 0) > _options.MaxConfigFileBytes) errors.Add("Colours file is too large.");
        if ((dto.BiomeTintsText?.Length ?? 0) > _options.MaxConfigFileBytes) errors.Add("Biome tints file is too large.");
        if ((dto.BlockStylesText?.Length ?? 0) > _options.MaxConfigFileBytes) errors.Add("Block styles file is too large.");
        if (!double.IsFinite(dto.NightColorGrade.Saturation) || dto.NightColorGrade.Saturation is < 0 or > 2)
            errors.Add("Night saturation must be between 0 and 2.");
        if (!double.IsFinite(dto.NightColorGrade.Lightness) || dto.NightColorGrade.Lightness is < 0.5 or > 2)
            errors.Add("Night lightness must be between 0.5 and 2.");
        return errors;
    }

    private static NightColorGradeOptions ParseNightColorGrade(JsonNode root) => new()
    {
        Enabled = root["nightColorGrade"]?["enabled"]?.GetValue<bool>() ?? true,
        Saturation = root["nightColorGrade"]?["saturation"]?.GetValue<double>() ?? 1.2,
        Lightness = root["nightColorGrade"]?["lightness"]?.GetValue<double>() ?? 1.15,
    };

    private static byte[] ApplyNightColorGrade(
        byte[] dayPng,
        byte[] nightPng,
        NightColorGradeOptions options)
    {
        if (!options.Enabled) return nightPng;

        using var day = SKBitmap.Decode(dayPng)
            ?? throw new InvalidOperationException("The daytime preview could not be decoded.");
        using var night = SKBitmap.Decode(nightPng)
            ?? throw new InvalidOperationException("The night preview could not be decoded.");
        if (day.Width != night.Width || day.Height != night.Height)
            throw new InvalidOperationException("Day and night preview dimensions differ.");

        var dayPixels = day.Pixels;
        var nightPixels = night.Pixels;
        var outputPixels = new SKColor[nightPixels.Length];
        for (var index = 0; index < nightPixels.Length; index++)
        {
            var d = dayPixels[index];
            var n = nightPixels[index];
            if (n.Alpha == 0)
            {
                outputPixels[index] = n;
                continue;
            }

            var graded = NightColorGrade.Apply(
                d.Red, d.Green, d.Blue, n.Red, n.Green, n.Blue,
                options.Saturation, options.Lightness);
            outputPixels[index] = new SKColor(graded.R, graded.G, graded.B, n.Alpha);
        }

        using var output = new SKBitmap(new SKImageInfo(
            night.Width, night.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        output.Pixels = outputPixels;
        using var image = SKImage.FromBitmap(output);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The graded night preview could not be encoded.");
        return encoded.ToArray();
    }

    private static DimensionRenderOptions ParseArgs(string dim, JsonArray args)
    {
        var list = args.Select(a => a?.GetValue<string>() ?? string.Empty).ToList();
        string? Value(string prefix) => list.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

        var option = new DimensionRenderOptions
        {
            Dimension = dim,
            Shadows = Value("--shadows=") ?? "default",
            Night = list.Any(a => a is "--night" or "--night=true"),
            Background = Value("--background="),
            ImageFormat = Value("--imageformat=") ?? "png",
        };
        if (int.TryParse(Value("--topY="), out var topY)) option.TopY = topY;
        if (int.TryParse(Value("--bottomY="), out var bottomY)) option.BottomY = bottomY;
        return option;
    }

    private static JsonArray BuildArgs(DimensionRenderOptions option)
    {
        var args = new List<string> { "web", "render", "--world={world}", $"--dimension={option.Dimension.ToLowerInvariant()}" };
        if (option.TopY is int topY) args.Add($"--topY={topY}");
        if (option.BottomY is int bottomY) args.Add($"--bottomY={bottomY}");
        if (!string.IsNullOrWhiteSpace(option.Shadows) && !string.Equals(option.Shadows, "default", StringComparison.OrdinalIgnoreCase))
            args.Add($"--shadows={option.Shadows}");
        if (!string.IsNullOrWhiteSpace(option.Background)) args.Add($"--background={option.Background}");
        args.Add("--imageformat=png");
        args.Add("--output={output}");
        return new JsonArray(args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
    }

    private static async Task<(int Exit, string Diagnostics)> RunProcessAsync(string exe, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var diagnostics = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) diagnostics.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) diagnostics.AppendLine(e.Data); };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            return (-1, "Preview render timed out.");
        }
        return (process.ExitCode, diagnostics.ToString());
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value ?? string.Empty : value[..max];

    private int? CurrentUserId() =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;

    private string? CurrentUsername() => User.FindFirstValue(ClaimTypes.Name);
}
