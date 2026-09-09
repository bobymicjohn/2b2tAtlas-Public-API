namespace Atlas;

/// <summary>A complete, attributable highway change, including group credits.</summary>
public sealed class HighwayChange
{
    /// <summary>Audit record identifier.</summary>
    public int Id { get; set; }
    /// <summary>Affected highway identifier.</summary>
    public int HighwayId { get; set; }
    /// <summary>Authenticated actor.</summary>
    public string? Username { get; set; }
    /// <summary>UTC change time.</summary>
    public DateTime CreatedUtc { get; set; }
    /// <summary>Action recorded by the server.</summary>
    public string Action { get; set; } = "";
    /// <summary>Human-readable summary.</summary>
    public string? Summary { get; set; }
    /// <summary>State before this change; null for creation.</summary>
    public Highway? Before { get; set; }
    /// <summary>State after this change; null for deletion.</summary>
    public Highway? After { get; set; }
    /// <summary>Rejected input for owner review; never treated as an applied version.</summary>
    public Highway? Proposed { get; set; }
    /// <summary>Fields changed by the operation.</summary>
    public List<string> Fields { get; set; } = [];
    /// <summary>Changes needing owner attention.</summary>
    public List<string> Warnings { get; set; } = [];
}

/// <summary>Owner restore request tied to the current state shown in the preview.</summary>
public sealed class HighwayRestoreRequest
{
    /// <summary>Current highway version, or "deleted" when absent.</summary>
    public string ExpectedVersion { get; set; } = "";
}
