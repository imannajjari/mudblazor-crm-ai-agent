using System.IO.Compression;
using System.Text;
using MudBlazorCrmApp.Services.Agent;
using Xunit;

namespace MudBlazorCrmApp.Tests;

public class AgentAttachmentHardeningTests
{
    [Fact]
    public void PngExtension_WithJpegBytes_IsRejected()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 };
        var res = AgentAttachmentParser.ParseAndSniff(".png", jpeg);
        Assert.Equal("Image signature does not match file extension.", res.Error);
    }

    [Fact]
    public void JpegExtension_WithPngBytes_IsRejected()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var res = AgentAttachmentParser.ParseAndSniff(".jpg", png);
        Assert.Equal("Image signature does not match file extension.", res.Error);
    }

    [Fact]
    public void WebpExtension_WithoutRiffSignature_IsRejected()
    {
        var junk = new byte[40];
        var res = AgentAttachmentParser.ParseAndSniff(".webp", junk);
        Assert.Equal("Image signature does not match file extension.", res.Error);
    }

    [Fact]
    public void PngWithValidIhdr_ParsesWithDimensionsAndMediaType()
    {
        var png = new byte[45];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        new byte[] { 0, 0, 0, 13 }.CopyTo(png, 8);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        WriteBE(png, 16, 20);
        WriteBE(png, 20, 10);

        var res = AgentAttachmentParser.ParseAndSniff(".png", png);
        Assert.Null(res.Error);
        Assert.Equal("image/png", res.MediaType);
        Assert.Contains("(20x10,", res.Summary);
        Assert.NotNull(res.ImageBytes);
    }

    [Fact]
    public void JpegWithCraftedSof0_DimensionsAreEnforced()
    {
        var jpg = new byte[40];
        jpg[0] = 0xFF; jpg[1] = 0xD8;
        jpg[2] = 0xFF; jpg[3] = 0xC0;             // SOF0
        jpg[4] = 0x00; jpg[5] = 0x11;             // segment length
        jpg[6] = 0x08;                            // bits per sample
        jpg[7] = 0x00; jpg[8] = 30;              // height (16-bit BE)
        jpg[9] = 0x00; jpg[10] = 20;             // width (16-bit BE)

        var res = AgentAttachmentParser.ParseAndSniff(".jpg", jpg);
        Assert.Null(res.Error);
        Assert.Contains("(20x30,", res.Summary);
    }

    [Fact]
    public void WebpVp8x_HugeCanvas_IsRejectedBeforeVision()
    {
        var webp = WebpHeader("VP8X", 20);
        webp[24] = 0xA0; webp[25] = 0x8C; webp[26] = 0x00; // width-1 = 36000
        webp[27] = 0xA0; webp[28] = 0x8C; webp[29] = 0x00; // height-1 = 36000
        var res = AgentAttachmentParser.ParseAndSniff(".webp", webp);
        Assert.NotNull(res.Error);
        Assert.Contains("maximum resolution", res.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void WebpVp8Lossy_DimensionsReadAfterSyncCode()
    {
        var webp = WebpHeader("VP8 ", 20);
        webp[23] = 0x9D; webp[24] = 0x01; webp[25] = 0x2A; // sync code
        webp[26] = 40 - 1; webp[27] = 0;                   // width-1
        webp[28] = 30 - 1; webp[29] = 0;                   // height-1
        var res = AgentAttachmentParser.ParseAndSniff(".webp", webp);
        Assert.Null(res.Error);
        Assert.Contains("(40x30,", res.Summary);

        var noSync = WebpHeader("VP8 ", 20);
        Assert.Equal("Could not read image dimensions.", AgentAttachmentParser.ParseAndSniff(".webp", noSync).Error);
    }

    [Fact]
    public void WebpVp8Lossless_RequiresSignatureThenReadsDimensions()
    {
        var webp = WebpHeader("VP8L", 40);
        webp[20] = 0x2F;
        var bits = 19 | (9 << 14); // width-1=19, height-1=9
        webp[21] = (byte)bits;
        webp[22] = (byte)(bits >> 8);
        webp[23] = (byte)(bits >> 16);
        webp[24] = (byte)(bits >> 24);

        var res = AgentAttachmentParser.ParseAndSniff(".webp", webp);
        Assert.Null(res.Error);
        Assert.Contains("(20x10,", res.Summary);

        var noSignature = WebpHeader("VP8L", 40); // b[20] stays 0
        Assert.Equal("Could not read image dimensions.", AgentAttachmentParser.ParseAndSniff(".webp", noSignature).Error);
    }

    [Fact]
    public void XlsxWithoutZipMagic_IsRejected()
    {
        var res = AgentAttachmentParser.ParseAndSniff(".xlsx", Encoding.UTF8.GetBytes("MZ fake excel"));
        Assert.Equal("Invalid XLSX archive.", res.Error);
    }

    [Fact]
    public void XlsxMissingRequiredOpenXmlEntries_IsRejected()
    {
        var zip = BuildZip(("junk.txt", "hi"u8.ToArray()));
        var res = AgentAttachmentParser.ParseAndSniff(".xlsx", zip);
        Assert.Equal("Workbook is missing required OpenXML entries.", res.Error);
    }

    [Fact]
    public void XlsxMacroWorkbook_IsRejected()
    {
        var zip = BuildZip(
            ("[Content_Types].xml", "<a/>"u8.ToArray()),
            ("xl/workbook.xml", "<a/>"u8.ToArray()),
            ("xl/vbaProject.bin", new byte[] { 1, 2, 3 }));
        var res = AgentAttachmentParser.ParseAndSniff(".xlsx", zip);
        Assert.Equal("Macro-enabled workbooks are not allowed.", res.Error);
    }

    [Fact]
    public void XlsxEntryCountBomb_IsRejected()
    {
        var entries = new List<(string, byte[])>
        {
            ("[Content_Types].xml", "<a/>"u8.ToArray()),
            ("xl/workbook.xml", "<a/>"u8.ToArray())
        };
        for (var i = 0; i < 2001; i++)
            entries.Add(($"xl/b{i}.xml", Array.Empty<byte>()));
        var zip = BuildZip(entries.ToArray());

        var res = AgentAttachmentParser.ParseAndSniff(".xlsx", zip);
        Assert.Equal("Workbook exceeds maximum entry limit.", res.Error);
    }

    [Fact]
    public void XlsxRatioBomb_IsRejected()
    {
        var zip = BuildZip(
            ("[Content_Types].xml", "<a/>"u8.ToArray()),
            ("xl/workbook.xml", "<a/>"u8.ToArray()),
            ("xl/worksheets/sheet1.xml", new byte[300_000])); // zeros deflate ~1000:1
        var res = AgentAttachmentParser.ParseAndSniff(".xlsx", zip);
        Assert.Equal("Workbook compression ratio exceeds safety limit.", res.Error);
    }

    [Fact]
    public void TextExtension_WithNullBytes_IsRejected()
    {
        var bytes = new byte[] { (byte)'a', (byte)'b', 0, (byte)'c' };
        var res = AgentAttachmentParser.ParseAndSniff(".txt", bytes);
        Assert.Equal("The file does not look like plain text.", res.Error);
    }

    private static void WriteBE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static byte[] WebpHeader(string chunk, int minLen)
    {
        var b = new byte[Math.Max(minLen, 40)];
        "RIFF"u8.CopyTo(b.AsSpan(0));
        "WEBP"u8.CopyTo(b.AsSpan(8));
        Encoding.ASCII.GetBytes(chunk).CopyTo(b.AsSpan(12));
        return b;
    }

    private static byte[] BuildZip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return ms.ToArray();
    }
}
