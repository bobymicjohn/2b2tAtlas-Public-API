namespace Atlas.Auth;

/// <summary>
/// Role → permission matrix for the admin editor (GAMEPLAN §15). Grants reflect the
/// effective permissions (DB overrides when present, else the code-defined default).
/// </summary>
public class RoleMatrix
{
    /// <summary>Editable roles (SuperAdmin is excluded — it always has everything).</summary>
    public List<string> Roles { get; set; } = new();

    /// <summary>All permission keys in the system.</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>role → the permission keys it currently grants.</summary>
    public Dictionary<string, List<string>> Grants { get; set; } = new();

    /// <summary>Roles whose grants come from DB overrides (vs the code default).</summary>
    public List<string> Customized { get; set; } = new();
}
