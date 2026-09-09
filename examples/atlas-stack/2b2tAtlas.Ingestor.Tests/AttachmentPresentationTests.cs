using _2b2tAtlas.Client.Services;

namespace Atlas.Ingestor.Tests;

public sealed class AttachmentPresentationTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=B-JUzsfrjXo&t=1140s", "B-JUzsfrjXo", "19:00")]
    [InlineData("https://youtu.be/B-JUzsfrjXo?t=1h2m3s", "B-JUzsfrjXo", "1:02:03")]
    [InlineData("https://www.youtube.com/shorts/IhiilHguhQU", "IhiilHguhQU", null)]
    [InlineData("https://www.youtube-nocookie.com/embed/B-JUzsfrjXo?start=12", "B-JUzsfrjXo", "0:12")]
    [InlineData("https://youtube.com.example.org/watch?v=B-JUzsfrjXo", null, null)]
    [InlineData("https://example.org/image.jpg?v=B-JUzsfrjXo", null, null)]
    [InlineData("https://youtube.com/watch?v=invalid&t=123", null, null)]
    [InlineData("https://youtube.com/watch?v=B-JUzsfrjXo&t=999999999999999999", "B-JUzsfrjXo", null)]
    public void Video_identity_and_chapter_labels_respect_host_and_format(string url, string? id, string? start)
    {
        Assert.Equal(id, YoutubeCreators.VideoId(url));
        Assert.Equal(start, YoutubeCreators.StartTime(url));
    }

    [Fact]
    public void Bundled_identity_uses_video_owner_before_free_text_credit()
    {
        var profile = YoutubeCreators.Find("B-JUzsfrjXo", "FitMC");
        Assert.NotNull(profile);
        Assert.Equal("ohnepixel", profile.Name);
        Assert.StartsWith("Images/creators/", profile.AvatarPath);
        Assert.StartsWith("https://www.youtube.com/channel/UC", profile.ChannelUrl);
        Assert.Null(YoutubeCreators.Find("not-a-video", "Some unknown person"));
    }
}
