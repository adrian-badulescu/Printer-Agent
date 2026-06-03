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
    public void DefaultLines_contains_urs_header()
    {
        var lines = ReceiptHeaderAsciiReader.DefaultLines();

        Assert.Equal(5, lines.Count);
        Assert.All(lines, line => Assert.True(line.Length <= ReceiptHeaderAsciiReader.MaxLineWidth));
    }
}
