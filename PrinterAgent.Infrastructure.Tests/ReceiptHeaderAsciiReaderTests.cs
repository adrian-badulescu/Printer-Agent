using PrinterAgent.Infrastructure.Printing;

namespace PrinterAgent.Infrastructure.Tests;

public class ReceiptHeaderAsciiReaderTests
{
    [Fact]
    public void ParseContent_skips_comments_and_blank_lines()
    {
        const string content = """
            # comment
            LINE1
            # another

            LINE2
            """;

        var lines = ReceiptHeaderAsciiReader.ParseContent(content);

        Assert.Equal(2, lines.Count);
        Assert.Equal("LINE1", lines[0]);
        Assert.Equal("LINE2", lines[1]);
    }

    [Fact]
    public void DefaultLines_contains_brand_header_and_url()
    {
        var lines = ReceiptHeaderAsciiReader.DefaultLines();

        Assert.Equal(2, lines.Count);
        Assert.Equal(ReceiptHeaderAsciiReader.DefaultHeaderText, lines[0]);
        Assert.Equal(ReceiptHeaderAsciiReader.DefaultUrlText, lines[1]);
        Assert.All(lines, line => Assert.True(line.Length <= ReceiptHeaderAsciiReader.MaxLineWidth));
    }

    [Fact]
    public void ParseContent_keeps_short_url_on_one_line()
    {
        var lines = ReceiptHeaderAsciiReader.ParseContent(ReceiptHeaderAsciiReader.DefaultUrlText);

        Assert.Single(lines);
        Assert.Equal(ReceiptHeaderAsciiReader.DefaultUrlText, lines[0]);
    }
}
