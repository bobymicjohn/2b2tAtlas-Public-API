namespace _2b2tAtlas.Server.Models;

/// <summary>
/// A single role→permission grant (GAMEPLAN §15). When any rows exist for a role
/// they REPLACE that role's code-defined default bundle; when none exist the
/// code default (<c>Atlas.Auth.RolePermissions.ForRole</c>) applies. SuperAdmin is
/// never stored here — it always has every permission.
/// Physical table created by <c>SchemaUpgrader</c>.
/// </summary>
public class RolePermission
{
    /// <summary>Gets or sets the database-generated grant identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the editable canonical role whose default bundle is being replaced.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Gets or sets one validated permission identifier granted by the role override.</summary>
    public string Permission { get; set; } = string.Empty;
}
