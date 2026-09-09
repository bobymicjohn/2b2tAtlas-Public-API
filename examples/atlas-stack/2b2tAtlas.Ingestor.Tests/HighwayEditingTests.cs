using System.Security.Claims;
using System.Text.Json;
using Atlas;
using Atlas.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using _2b2tAtlas.Server.Controllers;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using Highway = Atlas.Highway;

namespace Atlas.Ingestor.Tests;

public sealed class HighwayEditingTests
{
    [Fact]
    public async Task Several_small_shrinks_cannot_bypass_the_daily_geometry_guard()
    {
        await using var f = await Fixture.Create();
        var first = await f.Read(); first.Points[1].Z = 8000;
        Assert.IsType<OkObjectResult>((await f.Editor.UpdateHighway(1, first)).Result);
        var second = await f.Read(); second.Points[1].Z = 6400;
        Assert.Equal(422, Assert.IsType<ObjectResult>((await f.Editor.UpdateHighway(1, second)).Result).StatusCode);
        Assert.Equal(8000, (await f.Read()).Points[1].Z);
        var audit = await f.Db.AuditLogs.SingleAsync(a => a.Action == "highway.blocked", TestContext.Current.CancellationToken);
        Assert.Equal(6400, JsonSerializer.Deserialize<HighwayChange>(audit.DetailsJson!)!.Proposed!.Points[1].Z);
    }

    [Fact]
    public async Task Architect_edits_attributes_and_adds_credits_but_stale_and_destructive_edits_cannot_overwrite()
    {
        await using var f = await Fixture.Create();
        var current = await f.Read();
        var stale = await f.Read();
        current.Width = 7;
        current.BuilderGroups.Add(new() { GroupId = 2, Role = "Maintainer", Evidence = "HWU maintenance report" });
        var saved = Assert.IsType<Highway>(Assert.IsType<OkObjectResult>((await f.Editor.UpdateHighway(1, current)).Result).Value);
        Assert.Equal(7, saved.Width);
        Assert.Equal(2, saved.BuilderGroups.Count);
        Assert.Equal(6, saved.Height);
        Assert.Equal("https://example.com/tour", saved.VideoUrl);
        Assert.True(saved.Lit);
        Assert.NotEqual(current.EditVersion, saved.EditVersion);
        stale.Width = 12;
        Assert.IsType<ConflictObjectResult>((await f.Editor.UpdateHighway(1, stale)).Result);
        var missingVersion = await f.Read(); missingVersion.EditVersion = null;
        Assert.Equal(428, Assert.IsType<ObjectResult>((await f.Editor.UpdateHighway(1, missingVersion)).Result).StatusCode);

        foreach (var mutation in new Action<Highway>[] {
            h => h.Visibility = HighwayVisibility.Hidden,
            h => h.Points = [new(0,0), new(0,100)],
            h => h.Dimension = Dimension.End,
            h => h.BuilderGroups.RemoveAt(0),
            h => h.BuilderGroupId = 2,
            h => h.Points = [new(1000000,0), new(1000000,10000)] })
        {
            var proposal = await f.Read(); mutation(proposal);
            Assert.Equal(422, Assert.IsType<ObjectResult>((await f.Editor.UpdateHighway(1, proposal)).Result).StatusCode);
        }
        var unchanged = await f.Read();
        Assert.Equal(saved.EditVersion, unchanged.EditVersion);
        Assert.Equal(6, await f.Db.AuditLogs.CountAsync(a => a.Action == "highway.blocked", TestContext.Current.CancellationToken));
        Assert.IsType<ForbidResult>(await f.Editor.DeleteHighway(1));
        Assert.IsType<ForbidResult>((await f.Editor.Restore(1, new() { ExpectedVersion = saved.EditVersion! })).Result);
        Assert.IsType<BadRequestObjectResult>(await f.Editor.Reject(1));
    }

    [Fact]
    public async Task Owner_can_restore_complete_state_and_deleted_routes_with_conflict_protection()
    {
        await using var f = await Fixture.Create();
        var original = await f.Read();
        var edit = await f.Read();
        edit.Dimension = Dimension.End; edit.Width = 15; edit.DisplayWeight = 8;
        edit.BuilderGroups = [new() { GroupId = 2, Role = "Maintainer", Evidence = "new credits" }]; edit.BuilderGroupId = 2;
        Assert.IsType<OkObjectResult>((await f.Owner.UpdateHighway(1, edit)).Result);
        var change = await f.Db.AuditLogs.SingleAsync(a => a.Action == "highway.update", TestContext.Current.CancellationToken);
        Assert.Contains("ATTENTION", change.Summary!);
        Assert.IsType<ConflictObjectResult>((await f.Owner.Restore(change.Id, new() { ExpectedVersion = original.EditVersion! })).Result);
        var latest = await f.Read();
        var restore = await f.Owner.Restore(change.Id, new() { ExpectedVersion = latest.EditVersion! });
        var restored = Assert.IsType<Highway>(Assert.IsType<OkObjectResult>(restore.Result).Value);
        Assert.Equal(original.Dimension, restored.Dimension);
        Assert.Equal(original.Width, restored.Width);
        Assert.Equal(original.DisplayWeight, restored.DisplayWeight);
        Assert.Equal(original.BuilderGroups.Single().Evidence, restored.BuilderGroups.Single().Evidence);
        Assert.Equal(original.BuilderGroupId, restored.BuilderGroupId);
        Assert.Equal(9, (await f.Db.Highways.SingleAsync(h => h.Id == 2, TestContext.Current.CancellationToken)).Width);
        Assert.IsType<NoContentResult>(await f.Owner.DeleteHighway(1));
        var deleted = await f.Db.AuditLogs.SingleAsync(a => a.Action == "highway.delete", TestContext.Current.CancellationToken);
        var recreated = Assert.IsType<Highway>(Assert.IsType<OkObjectResult>((await f.Owner.Restore(deleted.Id, new() { ExpectedVersion = "deleted" })).Result).Value);
        Assert.Equal(1, recreated.Id);
        Assert.Single(recreated.BuilderGroups);
        Assert.Equal(original.DateAddedUtc, recreated.DateAddedUtc);
        Assert.Equal(2, await f.Db.Highways.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Audit_failure_rolls_back_geometry_and_group_writes()
    {
        await using var f = await Fixture.Create();
        var original = await f.Read();
        var edit = await f.Read(); edit.Width = 8;
        edit.BuilderGroups.Add(new() { GroupId = 2, Role = "Maintainer" });
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_audit BEFORE INSERT ON AuditLogs BEGIN SELECT RAISE(ABORT, 'audit fixture unavailable'); END;", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Editor.UpdateHighway(1, edit));
        f.Db.ChangeTracker.Clear();
        Assert.Equal(original.EditVersion, (await f.Read()).EditVersion);
        Assert.Single(await f.Db.HighwayGroups.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Approved_revisions_use_identical_validation_concurrency_and_attribution_rules()
    {
        await using var f = await Fixture.Create();
        var proposal = await f.Read(); proposal.Width = 8; proposal.BuilderGroups.Add(new() { GroupId = 2, Role = "Maintainer" });
        f.Db.Revisions.Add(new Revision { EntityType = "Highway", EntityId = 1, ProposedJson = JsonSerializer.Serialize(proposal), Status = "Pending" });
        await f.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var revisions = new RevisionsController(f.Db, new AuditService(f.Db), NullLogger<RevisionsController>.Instance) { ControllerContext = f.Editor.ControllerContext };
        Assert.IsType<OkObjectResult>(await revisions.Approve(1));
        Assert.Equal(2, (await f.Read()).BuilderGroups.Count);
        f.Db.Revisions.Add(new Revision { EntityType = "Highway", EntityId = 1, ProposedJson = JsonSerializer.Serialize(proposal), Status = "Pending" });
        await f.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.IsType<ConflictObjectResult>(await revisions.Approve(2));
        Assert.Equal("Pending", (await f.Db.Revisions.FindAsync(new object[] { 2 }, TestContext.Current.CancellationToken))!.Status);
        var dangerous = await f.Read(); dangerous.Visibility = HighwayVisibility.Hidden;
        f.Db.Revisions.Add(new Revision { EntityType = "Highway", EntityId = 1, ProposedJson = JsonSerializer.Serialize(dangerous), Status = "Pending" });
        await f.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(422, Assert.IsType<ObjectResult>(await revisions.Approve(3)).StatusCode);
        Assert.Equal(HighwayVisibility.Public, (await f.Read()).Visibility);
    }

    [Fact]
    public async Task Invalid_geometry_and_unknown_attribution_leave_no_partial_record()
    {
        await using var f = await Fixture.Create();
        foreach (var mutation in new Action<Highway>[] { h => h.Points = [], h => h.Points = [new(0,0),new(0,0)],
            h => h.Width = 0, h => h.DisplayWeight = 10000, h => h.Dimension = (Dimension)88,
            h => h.Points[0].X = int.MinValue, h => h.WikiUrl = "javascript:alert(1)", h => h.BuilderGroups = null! })
        {
            var dto = await f.Read(); mutation(dto);
            Assert.IsType<BadRequestObjectResult>((await f.Editor.CreateHighway(dto)).Result);
        }
        var unknown = await f.Read(); unknown.BuilderGroupId = 9999;
        Assert.IsType<BadRequestObjectResult>((await f.Editor.CreateHighway(unknown)).Result);
        Assert.Equal(2, await f.Db.Highways.CountAsync(TestContext.Current.CancellationToken));
        var ring = new Highway { Name = "Ring", Category = HighwayCategory.Ring, RingRadius = 10000 };
        Assert.IsType<CreatedAtActionResult>((await f.Editor.CreateHighway(ring)).Result);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public AtlasContext Db { get; }
        public HighwaysController Editor { get; }
        public HighwaysController Owner { get; }
        private Fixture(SqliteConnection connection, AtlasContext db)
        {
            this.connection = connection; Db = db;
            HighwaysController Controller(bool owner) => new(db, new AuditService(db), NullLogger<HighwaysController>.Instance)
            {
                ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, owner ? "1" : "2"), new Claim(ClaimTypes.Name, owner ? "atlas-owner" : "hwu-fixture"),
                    new Claim("perm", Permissions.HighwaysEdit), new Claim("perm", Permissions.HighwaysCreate),
                    new Claim("atlas_owner", owner ? "true" : "false")], "fixture")) } }
            };
            Editor = Controller(false); Owner = Controller(true);
        }
        public static async Task<Fixture> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(TestContext.Current.CancellationToken);
            var db = new AtlasContext(new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Groups.AddRange(new _2b2tAtlas.Server.Models.Group { Id = 1, Name = "Original builders" }, new _2b2tAtlas.Server.Models.Group { Id = 2, Name = "HWU" });
            db.Highways.AddRange(new _2b2tAtlas.Server.Models.Highway { Id = 1, Name = "South highway", Slug = "south", PointsJson = "[[0,0],[0,10000]]", Height = 6, Lit = 1,
                VideoUrl = "https://example.com/tour", Width = 4, DisplayWeight = 3, BuilderGroupId = 1,
                HighwayGroups = [new HighwayGroup { GroupId = 1, Role = "Primary builder", Evidence = "Original construction report" }] },
                new _2b2tAtlas.Server.Models.Highway { Id = 2, Name = "Unrelated highway", PointsJson = "[[0,0],[1000,0]]", Width = 9 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return new(connection, db);
        }
        public async Task<Highway> Read() => Assert.IsType<Highway>(Assert.IsType<OkObjectResult>((await Editor.GetHighway(1)).Result).Value);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
