using Atlas.Validation;

namespace _2b2tAtlas.Ingestor.Tests;

public class AttachmentLinkValidatorTests
{
    [Theory]
    [InlineData("https://2b2t.miraheze.org/wiki/Spawn")]
    [InlineData("https://www.youtube.com/watch?v=abc#details")]
    [InlineData("https://example.com/image.webp?size=large")]
    public void AcceptsHttpsLinks(string input)
    {
        Assert.True(AttachmentLinkValidator.TryNormalizeHttps(input, out var normalized, out var error));
        Assert.StartsWith("https://", normalized, StringComparison.Ordinal);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("ftp://example.com/archive.zip")]
    [InlineData("http://example.com/insecure")]
    [InlineData("https://user:password@example.com/private")]
    [InlineData("not a url")]
    public void RejectsUnsafeOrMalformedLinks(string input)
    {
        Assert.False(AttachmentLinkValidator.TryNormalizeHttps(input, out var normalized, out var error));
        Assert.Empty(normalized);
        Assert.NotEmpty(error);
    }
}