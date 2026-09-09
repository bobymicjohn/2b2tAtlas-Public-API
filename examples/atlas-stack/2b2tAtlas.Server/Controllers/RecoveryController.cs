using System.Text.Json;
using Atlas.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>Owner-only visibility into private backup health. Provides no restore or delete API.</summary>
[ApiController]
[Route("api/admin/recovery")]
[Authorize(Policy = Permissions.SettingsManage)]
public sealed class RecoveryController : ControllerBase
{
    /// <summary>Reports backup failures/staleness, available capacity and the most recent measured inventory.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        Response.Headers.CacheControl = "private, no-store";
        var backups = new List<object>();
        foreach (var tier in new[] { "Metadata", "Assets", "Preservation" })
        {
            var path = Path.Combine(@"C:\AtlasExample\Recovery", $"{tier}-status.json");
            try
            {
                if (!System.IO.File.Exists(path)) { backups.Add(new { tier, state = "not-started", needsAttention = true }); continue; }
                using var json = JsonDocument.Parse(System.IO.File.ReadAllText(path));
                var status = json.RootElement.Clone();
                var complete = status.TryGetProperty("completedUtc", out var value) && DateTimeOffset.TryParse(value.GetString(), out var timestamp)
                    ? timestamp : (DateTimeOffset?)null;
                var state = status.GetProperty("state").GetString();
                var maxAge = tier == "Metadata" ? TimeSpan.FromHours(3) : TimeSpan.FromDays(2);
                var log = status.TryGetProperty("log", out var logValue) ? logValue.GetString() : null;
                var lastActivity = log != null && System.IO.File.Exists(log)
                    ? new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(log)) : (DateTimeOffset?)null;
                backups.Add(new { tier, status, needsAttention = state == "failed" ||
                    (state == "running" && (!lastActivity.HasValue || DateTimeOffset.UtcNow - lastActivity.Value > TimeSpan.FromMinutes(30))) ||
                    (state != "running" && (!complete.HasValue || DateTimeOffset.UtcNow - complete.Value > maxAge)) });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { backups.Add(new { tier, state = "unavailable", needsAttention = true }); }
        }
        var capacity = new List<object>();
        foreach (var path in new[] { @"B:\", @"E:\", @"X:\" })
        {
            try
            {
                var drive = new DriveInfo(path);
                capacity.Add(new { drive = path, available = true, availableBytes = drive.AvailableFreeSpace, totalBytes = drive.TotalSize });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { capacity.Add(new { drive = path, available = false, needsAttention = true }); }
        }
        return Ok(new { checkedUtc = DateTimeOffset.UtcNow, owner = "atlas-owner", backups, capacity,
            editLimits = new { perMinute = 10, perHour = 60, perDay = 200, sharedPerHour = 120, sharedPerDay = 400 },
            protection = "Verified pre-edit snapshots; versioned encrypted backups; connected storage is not offline or immutable." });
    }
}
