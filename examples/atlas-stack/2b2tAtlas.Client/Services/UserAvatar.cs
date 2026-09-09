using Atlas.Auth;

namespace _2b2tAtlas.Client.Services;

/// <summary>Resolves profile skins separately from Atlas login names.</summary>
public static class UserAvatar
{
    /// <summary>Prefer the recorded Minecraft name; keep login-name fallback for older profiles.</summary>
    public static string Url(User? user)
    {
        var name = string.IsNullOrWhiteSpace(user?.MinecraftUsername) ? user?.Username : user.MinecraftUsername;
        return string.IsNullOrWhiteSpace(name) ? "./Images/logo.png"
            : $"https://mc-heads.net/avatar/{Uri.EscapeDataString(name.Trim())}/80";
    }
}
