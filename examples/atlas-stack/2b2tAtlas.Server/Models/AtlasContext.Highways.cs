using Microsoft.EntityFrameworkCore;

namespace _2b2tAtlas.Server.Models;

/// <summary>
/// Registers the <see cref="Highway"/> entity on the context. Kept in a separate
/// partial so EF Core Power Tools re-scaffolds of <c>AtlasContext</c> don't clobber it.
/// The physical table is created by <c>SchemaUpgrader</c> (EnsureCreated won't add it
/// to an existing database).
/// </summary>
public partial class AtlasContext
{
    /// <summary>Gets or sets canonical highway geometry, attribution, visibility, and moderation state.</summary>
    public virtual DbSet<Highway> Highways { get; set; } = null!;

    /// <summary>Gets or sets immutable records of security, content, and moderation mutations.</summary>
    public virtual DbSet<AuditLog> AuditLogs { get; set; } = null!;

    /// <summary>Gets or sets 2b2t group records used for builder and infrastructure attribution.</summary>
    public virtual DbSet<Group> Groups { get; set; } = null!;

    /// <summary>Gets or sets explicit many-to-many group attribution for documented locations.</summary>
    public virtual DbSet<LocationGroup> LocationGroups { get; set; } = null!;

    /// <summary>Gets or sets many-to-many builder and maintenance attribution for highways.</summary>
    public virtual DbSet<HighwayGroup> HighwayGroups { get; set; } = null!;

    /// <summary>Gets or sets explicit role grants that replace a role's code-defined permission profile.</summary>
    public virtual DbSet<RolePermission> RolePermissions { get; set; } = null!;

    /// <summary>Gets or sets contributor proposals awaiting trusted moderator review.</summary>
    public virtual DbSet<Revision> Revisions { get; set; } = null!;

    /// <summary>Gets or sets dimension-level tile pyramids available to the map layer picker.</summary>
    public virtual DbSet<MapRender> MapRenders { get; set; } = null!;

    /// <summary>Gets or sets durable world-download ingestion queue and worker lease state.</summary>
    public virtual DbSet<IngestionJob> IngestionJobs { get; set; } = null!;

    /// <summary>
    /// Runs after the scaffolded model config. Fixes the scaffolded
    /// <c>Location.Rowid</c> mapping: it is a SQLite INTEGER PRIMARY KEY (rowid
    /// alias) that auto-assigns, but EF Power Tools scaffolded it as
    /// <c>ValueGeneratedNever</c>, so inserts sent Rowid = 0 and new locations
    /// collided on the primary key. Let the database generate it on insert.
    /// </summary>
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Location>().Property(l => l.Rowid).ValueGeneratedOnAdd();
        modelBuilder.Entity<Attachment>().Property(attachment => attachment.Id).ValueGeneratedOnAdd();
        modelBuilder.Entity<LocationGroup>(entity =>
        {
            entity.HasKey(link => new { link.LocationRowid, link.GroupId });
            entity.HasOne(link => link.Location)
                .WithMany(location => location.LocationGroups)
                .HasForeignKey(link => link.LocationRowid)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(link => link.Group)
                .WithMany(group => group.LocationGroups)
                .HasForeignKey(link => link.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<HighwayGroup>(entity =>
        {
            entity.HasKey(link => new { link.HighwayId, link.GroupId });
            entity.HasOne(link => link.Highway)
                .WithMany(highway => highway.HighwayGroups)
                .HasForeignKey(link => link.HighwayId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(link => link.Group)
                .WithMany(group => group.HighwayGroups)
                .HasForeignKey(link => link.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Warp>().HasIndex(warp => warp.ArchiveSha256).IsUnique()
            .HasFilter("\"ArchiveSha256\" IS NOT NULL AND \"ArchiveSha256\" <> ''");
    }
}
