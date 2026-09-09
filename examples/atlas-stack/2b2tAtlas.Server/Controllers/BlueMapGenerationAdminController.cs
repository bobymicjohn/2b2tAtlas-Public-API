using Atlas.Auth;
using Atlas.Ingestion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using _2b2tAtlas.Server.Services;

namespace _2b2tAtlas.Server.Controllers;

/// <summary>Exposes sanitized BlueMap derivative progress to authorized Atlas administrators.</summary>
[ApiController]
[Route("api/admin/bluemap")]
[Authorize(Policy = Permissions.RendersManage)]
public sealed class BlueMapGenerationAdminController : ControllerBase
{
    private readonly BlueMapGenerationStatusService _status;

    /// <summary>Initializes the permission-gated BlueMap status endpoint.</summary>
    public BlueMapGenerationAdminController(BlueMapGenerationStatusService status) => _status = status;

    /// <summary>Returns current batch, quality-gated catalog, dimension, and storage status.</summary>
    [HttpGet]
    [ProducesResponseType<BlueMapGenerationStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BlueMapGenerationStatusDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _status.GetAsync(cancellationToken));
}
