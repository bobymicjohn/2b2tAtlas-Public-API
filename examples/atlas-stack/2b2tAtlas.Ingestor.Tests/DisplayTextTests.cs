using Atlas;

namespace Atlas.Ingestor.Tests;

public sealed class DisplayTextTests
{
    [Theory]
    [InlineData("First paragraph.rnrnSecond paragraph.", "First paragraph.\n\nSecond paragraph.")]
    [InlineData("First paragraph.rnrnrnSecond paragraph.", "First paragraph.\n\nSecond paragraph.")]
    [InlineData("First paragraph.\\r\\n\\r\\nSecond paragraph.", "First paragraph.\n\nSecond paragraph.")]
    [InlineData("First paragraph.\r\n\r\nSecond paragraph.", "First paragraph.\n\nSecond paragraph.")]
    public void NormalizeDescription_repairs_legacy_line_breaks(string input, string expected)
    {
        Assert.Equal(expected, DisplayText.NormalizeDescription(input));
    }

    [Fact]
    public void CompactDescription_produces_metadata_safe_single_line_text()
    {
        Assert.Equal("First paragraph. Second paragraph.",
            DisplayText.CompactDescription("First paragraph.rnrnSecond paragraph."));
    }
}
