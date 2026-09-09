namespace Atlas.Auth;

/// <summary>
/// Canonical role names for the Atlas RBAC system (see GAMEPLAN §15).
/// Roles are ordered bundles of <see cref="Permissions"/>; SuperAdmin implicitly
/// has every permission (enforced by an authorization bypass on the server).
/// </summary>
public static class RoleNames
{
    /// <summary>The founder role with an authorization bypass for every permission.</summary>
    public const string SuperAdmin = "SuperAdmin";

    /// <summary>The archivist role for broad content and user administration.</summary>
    public const string Admin = "Admin";

    /// <summary>The specialist role for maintaining location and render content.</summary>
    public const string Cartographer = "Cartographer";

    /// <summary>The specialist role for maintaining highway content.</summary>
    public const string HighwayArchitect = "HighwayArchitect";

    /// <summary>The specialist role for contributing historical location records.</summary>
    public const string Chronicler = "Chronicler";

    /// <summary>The baseline member role without default mutation permissions.</summary>
    public const string User = "User";

    /// <summary>All assignable roles, most-privileged first.</summary>
    public static readonly string[] All =
        { SuperAdmin, Admin, Cartographer, HighwayArchitect, Chronicler, User };

    /// <summary>Friendly role names shown in the administration UI.</summary>
    public static string DisplayName(string? role) => role switch
    {
        SuperAdmin => "Founder",
        Admin => "Archivist",
        Cartographer => "Cartographer",
        HighwayArchitect => "Highway Architect",
        Chronicler => "Chronicler",
        User => "Member",
        _ => role ?? "Unknown",
    };

    /// <summary>Resolves a canonical role ID or one of its friendly labels.</summary>
    public static bool TryNormalize(string? role, out string canonicalRole)
    {
        canonicalRole = string.Empty;
        if (string.IsNullOrWhiteSpace(role)) return false;

        var value = role.Trim();
        canonicalRole = All.FirstOrDefault(r => string.Equals(r, value, StringComparison.OrdinalIgnoreCase))
            ?? value.ToLowerInvariant() switch
            {
                "founder" => SuperAdmin,
                "archivist" => Admin,
                "highway architect" => HighwayArchitect,
                "member" => User,
                _ => string.Empty,
            };
        return canonicalRole.Length > 0;
    }

    /// <summary>
    /// A numeric privilege rank used for "at least" comparisons and to prevent
    /// privilege escalation (a user may never grant a role higher than their own).
    /// </summary>
    public static int Rank(string? role) => role switch
    {
        SuperAdmin => 100,
        Admin => 80,
        Cartographer => 60,
        HighwayArchitect => 40,
        Chronicler => 40,
        User => 20,
        _ => 0,
    };

    /// <summary>Whether a caller may assign the target role without escalation.</summary>
    public static bool CanAssign(string? callerRole, string? targetRole, bool callerIsSuperAdmin)
    {
        if (!TryNormalize(targetRole, out var target)) return false;
        if (callerIsSuperAdmin || string.Equals(callerRole, SuperAdmin, StringComparison.Ordinal)) return true;
        if (!TryNormalize(callerRole, out var caller)) return false;
        return target != SuperAdmin && Rank(target) < Rank(caller);
    }
}

/// <summary>
/// Granular permission strings (GAMEPLAN §15). Roles map to bundles of these via
/// <see cref="RolePermissions"/>; server endpoints are gated with a policy per
/// permission and the client uses them for conditional UI.
/// </summary>
public static class Permissions
{
    /// <summary>Permission to create, update, and deactivate user accounts.</summary>
    public const string UsersManage = "users.manage";

    /// <summary>Permission to assign canonical roles within the caller's privilege ceiling.</summary>
    public const string UsersRolesAssign = "users.roles.assign";

    /// <summary>Permission to customize the permissions granted by role profiles.</summary>
    public const string RolesManage = "roles.manage";

    /// <summary>Permission to create location records.</summary>
    public const string LocationsCreate = "locations.create";

    /// <summary>Permission to edit location records.</summary>
    public const string LocationsEdit = "locations.edit";

    /// <summary>Permission to delete location records.</summary>
    public const string LocationsDelete = "locations.delete";

    /// <summary>Permission to replace a location's validated external attachment links.</summary>
    public const string AttachmentsManage = "attachments.manage";

    /// <summary>Permission to submit highway records.</summary>
    public const string HighwaysCreate = "highways.create";

    /// <summary>Permission to edit highway records.</summary>
    public const string HighwaysEdit = "highways.edit";

    /// <summary>Permission to delete highway records.</summary>
    public const string HighwaysDelete = "highways.delete";

    /// <summary>Permission to approve or reject pending content submissions.</summary>
    public const string SubmissionsModerate = "submissions.moderate";

    /// <summary>Permission to register and manage map renders.</summary>
    public const string RendersManage = "renders.manage";

    /// <summary>Permission to tune unMINED settings and request bulk re-renders.</summary>
    public const string RenderSettingsManage = "render.settings.manage";

    /// <summary>Permission to create, update, and delete attribution groups.</summary>
    public const string GroupsManage = "groups.manage";

    /// <summary>Permission to modify application settings.</summary>
    public const string SettingsManage = "settings.manage";

    /// <summary>Permission to read the administrative audit log.</summary>
    public const string AuditView = "audit.view";

    /// <summary>Every known permission (used to register one authorization policy each).</summary>
    public static readonly string[] All =
    {
        UsersManage, UsersRolesAssign, RolesManage,
        LocationsCreate, LocationsEdit, LocationsDelete, AttachmentsManage,
        HighwaysCreate, HighwaysEdit, HighwaysDelete,
        SubmissionsModerate, RendersManage, RenderSettingsManage, GroupsManage, SettingsManage, AuditView,
    };
}

/// <summary>
/// Resolves the default permission bundle for a role. Specialist roles are
/// explicit profiles rather than a cumulative privilege ladder.
/// </summary>
public static class RolePermissions
{
    private static readonly string[] UserPerms = System.Array.Empty<string>();

    private static readonly string[] ChroniclerPerms =
    {
        Permissions.LocationsCreate,
    };

    private static readonly string[] HighwayArchitectPerms =
    {
        Permissions.HighwaysCreate, Permissions.HighwaysEdit,
    };

    private static readonly string[] CartographerPerms =
    {
        Permissions.LocationsCreate, Permissions.LocationsEdit, Permissions.AttachmentsManage,
        Permissions.HighwaysCreate, Permissions.HighwaysEdit,
        Permissions.SubmissionsModerate, Permissions.RendersManage, Permissions.GroupsManage,
    };

    private static readonly string[] AdminPerms =
    {
        Permissions.UsersManage,
        Permissions.LocationsCreate, Permissions.LocationsEdit, Permissions.AttachmentsManage,
        Permissions.HighwaysCreate, Permissions.HighwaysEdit,
        Permissions.SubmissionsModerate, Permissions.RendersManage,
        Permissions.GroupsManage, Permissions.AuditView,
    };

    /// <summary>
    /// The permissions granted to a user by their role. A SuperAdmin (or the
    /// SuperAdmin role) receives every permission.
    /// </summary>
    public static IReadOnlyCollection<string> ForRole(string? role, bool isSuperAdmin = false)
    {
        if (isSuperAdmin || role == RoleNames.SuperAdmin)
            return Permissions.All;

        return role switch
        {
            RoleNames.Admin => AdminPerms,
            RoleNames.Cartographer => CartographerPerms,
            RoleNames.HighwayArchitect => HighwayArchitectPerms,
            RoleNames.Chronicler => ChroniclerPerms,
            RoleNames.User => UserPerms,
            _ => UserPerms,
        };
    }
}
