using Atlas.Auth;

namespace _2b2tAtlas.Ingestor.Tests;

public class RbacProfileTests
{
    [Fact]
    public void CanonicalRolesHaveExpectedDisplayNames()
    {
        Assert.Equal("Founder", RoleNames.DisplayName(RoleNames.SuperAdmin));
        Assert.Equal("Archivist", RoleNames.DisplayName(RoleNames.Admin));
        Assert.Equal("Highway Architect", RoleNames.DisplayName(RoleNames.HighwayArchitect));
        Assert.Equal("Member", RoleNames.DisplayName(RoleNames.User));
    }

    [Theory]
    [InlineData("Archivist", RoleNames.Admin)]
    [InlineData("highway architect", RoleNames.HighwayArchitect)]
    [InlineData("Member", RoleNames.User)]
    [InlineData("Cartographer", RoleNames.Cartographer)]
    public void FriendlyAndCanonicalNamesNormalize(string input, string expected)
    {
        Assert.True(RoleNames.TryNormalize(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LegacyTechnicianIsNotCanonical()
    {
        Assert.False(RoleNames.TryNormalize("Technician", out _));
    }

    [Fact]
    public void SpecialistProfilesAreNonCumulative()
    {
        var chronicler = RolePermissions.ForRole(RoleNames.Chronicler);
        Assert.Contains(Permissions.LocationsCreate, chronicler);
        Assert.DoesNotContain(Permissions.LocationsEdit, chronicler);
        Assert.DoesNotContain(Permissions.HighwaysCreate, chronicler);

        var architect = RolePermissions.ForRole(RoleNames.HighwayArchitect);
        Assert.Contains(Permissions.HighwaysEdit, architect);
        Assert.DoesNotContain(Permissions.LocationsCreate, architect);

        var cartographer = RolePermissions.ForRole(RoleNames.Cartographer);
        Assert.Contains(Permissions.AttachmentsManage, cartographer);
        Assert.Contains(Permissions.RendersManage, cartographer);
        Assert.DoesNotContain(Permissions.RenderSettingsManage, cartographer);
        Assert.DoesNotContain(Permissions.LocationsDelete, cartographer);
        Assert.DoesNotContain(Permissions.UsersManage, cartographer);

        var archivist = RolePermissions.ForRole(RoleNames.Admin);
        Assert.DoesNotContain(Permissions.RenderSettingsManage, archivist);
        Assert.DoesNotContain(Permissions.LocationsDelete, archivist);
        Assert.DoesNotContain(Permissions.HighwaysDelete, archivist);
        Assert.DoesNotContain(Permissions.UsersRolesAssign, archivist);
    }

    [Fact]
    public void ArchivistCannotAssignPeerOrFounder()
    {
        Assert.True(RoleNames.CanAssign(RoleNames.Admin, RoleNames.Cartographer, false));
        Assert.False(RoleNames.CanAssign(RoleNames.Admin, RoleNames.Admin, false));
        Assert.False(RoleNames.CanAssign(RoleNames.Admin, RoleNames.SuperAdmin, false));
        Assert.True(RoleNames.CanAssign(RoleNames.SuperAdmin, RoleNames.SuperAdmin, true));
    }
}
