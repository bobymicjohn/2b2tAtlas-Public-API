using Atlas.Auth;
using Atlas.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _2b2tAtlas.Server.Services;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>Exposes sanitized example host Archive collector progress to authorized Atlas administrators.</summary>
[ApiController]
[Route("api/admin/collector")]
[Authorize(Policy = Permissions.RendersManage)]
public sealed class ArchiveCollectorAdminController : ControllerBase
{
    private readonly ArchiveCollectorStatusService _status;

    /// <summary>Initializes the permission-gated collector status endpoint.</summary>
    public ArchiveCollectorAdminController(ArchiveCollectorStatusService status) => _status = status;

    /// <summary>Returns the current five-worker capture and rolling-production-handoff status.</summary>
    [HttpGet]
    [ProducesResponseType<ArchiveCollectorStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ArchiveCollectorStatusDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _status.GetAsync(cancellationToken));
}
