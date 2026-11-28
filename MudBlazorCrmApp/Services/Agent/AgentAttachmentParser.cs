using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MudBlazorCrmApp.Services.Agent;

/// <summary>
/// Validates file magic bytes, image pixel limits, CSV RFC 4180 syntax, and XLSX Zip-Bomb/DTD/table-budget safety.
/// Text without a BOM is decoded as strict UTF-8 first, then Windows-1256 with an explicit warning.
/// </summary>
public static class AgentAttachmentParser
{
    private const int MaxRowsPerSheet = 5000;
    private const int MaxColumnsPerRow = 200;
    private const int MaxCellChars = 1000;
    private const int MaxExtractedChars = 200_000;
    private const int MaxZipEntries = 2000;
    private const long MaxInflatedBytes = 30L * 1024 * 1024;
    private const int MaxCompressionRatio = 100;
    private const long RatioCheckFromBytes = 64 * 1024;
    private const long MaxImagePixels = 25_000_000;

    private static readonly XmlReaderSettings SafeXmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static AgentAttachmentParser() =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public sealed record ParseOutcome(
        string Kind,
        List<AttachmentSheet> Sheets,
        string? RawText,
        byte[]? ImageBytes,
        string? MediaType,
        string Summary,
        string? Error);

    public static ParseOutcome ParseAndSniff(string ext, byte[] bytes)
    {
        var lowExt = ext.ToLowerInvariant();
        if (lowExt is ".png" or ".jpg" or ".jpeg" or ".webp")
            return ParseImage(lowExt, bytes);

        if (lowExt == ".xlsx")
            return ParseXlsxSafe(bytes);

        if (HasDisallowedControlBytes(bytes))
            return new("Text", [], null, null, null, "", "The file does not look like plain text.");

        var (text, encodingWarning) = DecodeText(bytes);
        if (text.Length > MaxExtractedChars) text = text[..MaxExtractedChars];

        if (lowExt is ".csv" or ".tsv")
            return ParseCsv(lowExt, text, encodingWarning);

        var lines = text.Length == 0 ? 0 : text.Split('\n').Length;
        var summary = $"Text: {lines} lines, {text.Length} chars";
        if (encodingWarning != null) summary += $"; encoding warning: {encodingWarning}";
        return new("Text", [], text, null, null, summary, null);
    }

    private static ParseOutcome ParseImage(string ext, byte[] bytes)
    {
        if (!MatchesImageMagic(ext, bytes))
            return new("Image", [], null, null, null, "", "Image signature does not match file extension.");

        var mediaType = ext switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };

        if (!TryReadImageDimensions(ext, bytes, out var width, out var height) || width <= 0 || height <= 0)
            return new("Image", [], null, null, null, "", "Could not read image dimensions.");

        if ((long)width * height > MaxImagePixels)
            return new("Image", [], null, null, null, "", $"Image exceeds maximum resolution ({MaxImagePixels:N0} pixels).");

        return new("Image", [], null, bytes, mediaType, $"Image ({width}x{height}, {bytes.Length} bytes)", null);
    }

    private static ParseOutcome ParseCsv(string ext, string text, string? encodingWarning)
    {
        var sep = ext == ".tsv" ? '\t' : DetectDelimiter(text);
        var records = ReadCsvRecords(text, sep);
        if (records == null)
            return new("Csv", [], null, null, null, "", "The CSV file has an unclosed quote.");

        var budget = new AgentTableBudget();
        var data = new List<string[]>();
        List<string>? headers = null;
        foreach (var rec in records)
        {
            if (rec.All(string.IsNullOrWhiteSpace)) continue;
            var arr = rec.Take(MaxColumnsPerRow).ToArray();
            if (headers == null) { headers = [.. arr]; continue; }
            if (data.Count >= MaxRowsPerSheet) break;
            if (budget.TryChargeAllocation(arr.Length) is { } allocErr)
                return new("Csv", [], null, null, null, "", allocErr);
            foreach (var cell in arr)
            {
                if (budget.TryChargeCell(cell) is { } cellErr)
                    return new("Csv", [], null, null, null, "", cellErr);
            }
            data.Add(arr);
        }
        headers ??= [];
        var summary = $"CSV: {data.Count} rows, {headers.Count} columns";
        if (encodingWarning != null) summary += $"; encoding warning: {encodingWarning}";
        return new("Csv", [new AttachmentSheet("Sheet1", false, headers, data)], text, null, null, summary, null);
    }

    private static ParseOutcome ParseXlsxSafe(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
            return new("Excel", [], null, null, null, "", "Invalid XLSX archive.");

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxZipEntries)
            return new("Excel", [], null, null, null, "", "Workbook exceeds maximum entry limit.");
        if (zip.GetEntry("[Content_Types].xml") == null || zip.GetEntry("xl/workbook.xml") == null)
            return new("Excel", [], null, null, null, "", "Workbook is missing required OpenXML entries.");
        if (zip.Entries.Any(e => e.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase)))
            return new("Excel", [], null, null, null, "", "Macro-enabled workbooks are not allowed.");

        var budgetError = ValidateZipBudget(zip);
        if (budgetError != null)
            return new("Excel", [], null, null, null, "", budgetError);

        long remainingBudget = MaxInflatedBytes;
        var shared = ReadSharedStrings(zip, ref remainingBudget);
        var tableBudget = new AgentTableBudget();
        var sheets = new List<AttachmentSheet>();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Take(50))
        {
            var doc = LoadXmlBounded(entry, ref remainingBudget);
            var sheet = BuildSheet(Path.GetFileNameWithoutExtension(entry.Name), doc, shared, tableBudget, out var sheetError);
            if (sheetError != null)
                return new("Excel", [], null, null, null, "", sheetError);
            sheets.Add(sheet!);
        }
        return new("Excel", sheets, null, null, null, $"Excel: {sheets.Count} sheet(s)", null);
    }

    private static string? ValidateZipBudget(ZipArchive zip)
    {
        long totalDeclared = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.Length < 0 || entry.Length > MaxInflatedBytes)
                return "Workbook entry exceeds maximum inflated size.";
            totalDeclared += entry.Length;
            if (totalDeclared > MaxInflatedBytes)
                return "Workbook exceeds maximum inflated size.";
            if (entry.Length > RatioCheckFromBytes && (entry.CompressedLength <= 0 || entry.Length / entry.CompressedLength > MaxCompressionRatio))
                return "Workbook compression ratio exceeds safety limit.";
        }
        return null;
    }

    private static AttachmentSheet? BuildSheet(
        string name, XDocument doc, List<string> shared, AgentTableBudget budget, out string? error)
    {
        error = null;
        var rows = new List<string[]>();
        foreach (var r in doc.Descendants().Where(x => x.Name.LocalName == "row").Take(MaxRowsPerSheet))
        {
            var cells = ReadRow(r, shared);
            if (budget.TryChargeAllocation(cells.Length) is { } allocErr) { error = allocErr; return null; }
            foreach (var cell in cells)
                if (budget.TryChargeCell(cell) is { } cellErr) { error = cellErr; return null; }
            rows.Add(cells);
        }

        var headers = rows.Count > 0 ? rows[0].ToList() : [];
        var width = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Length));
        headers.AddRange(Enumerable.Repeat("", Math.Max(0, width - headers.Count)));
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Length < width)
                rows[i] = [.. rows[i], .. Enumerable.Repeat("", width - rows[i].Length)];
        return new AttachmentSheet(name, false, headers, rows.Skip(1).ToList());
    }

    private static char DetectDelimiter(string text)
    {
        var lines = text.Split('\n', 11).Take(10).Where(l => l.Length > 0).ToList();
        char[] candidates = [',', ';', '\t', '|'];
        var best = ',';
        var bestCount = 0;
        foreach (var c in candidates)
        {
            var hits = lines.Count(l => l.Contains(c));
            if (hits > bestCount) { bestCount = hits; best = c; }
        }
        return best;
    }

    private static List<List<string>>? ReadCsvRecords(string text, char delimiter)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') inQuotes = false;
                else if (field.Length < MaxCellChars) field.Append(ch);
            }
            else if (ch == '"' && field.Length == 0) inQuotes = true;
            else if (ch == delimiter) { if (row.Count < MaxColumnsPerRow) row.Add(field.ToString()); field.Clear(); }
            else if (ch is '\n' or '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                if (row.Count < MaxColumnsPerRow) row.Add(field.ToString());
                field.Clear();
                records.Add(row);
                row = [];
                if (records.Count > MaxRowsPerSheet + 5) break;
            }
            else if (field.Length < MaxCellChars) field.Append(ch);
        }
        if (inQuotes) return null;
        if (field.Length > 0 || row.Count > 0) { if (row.Count < MaxColumnsPerRow) row.Add(field.ToString()); records.Add(row); }
        return records;
    }

    private static bool HasDisallowedControlBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return false;
        if (Array.IndexOf(bytes, (byte)0) >= 0) return true;
        var sampleLen = Math.Min(bytes.Length, 4096);
        var ctrl = 0;
        for (var i = 0; i < sampleLen; i++)
        {
            var b = bytes[i];
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D)) ctrl++;
        }
        return ctrl * 50 > sampleLen;
    }

    private static (string Text, string? Warning) DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), null);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), null);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), null);

        // Try strict UTF-8 first; if it fails, fall back to Windows-1256 (legacy Persian/ANSI CSV).
        try
        {
            return (StrictUtf8.GetString(bytes), null);
        }
        catch (DecoderFallbackException)
        {
            var win1256 = Encoding.GetEncoding(1256);
            return (win1256.GetString(bytes), "non-UTF-8 bytes were decoded as Windows-1256; verify the characters");
        }
    }

    private static bool TryReadImageDimensions(string ext, byte[] b, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (ext == ".png" && b.Length >= 24)
        {
            width = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20, 4));
            return true;
        }
        if (ext is ".jpg" or ".jpeg")
            return TryReadJpegDimensions(b, out width, out height);
        if (ext == ".webp" && b.Length >= 30)
            return TryReadWebpDimensions(b, out width, out height);
        return false;
    }

    private static bool TryReadJpegDimensions(byte[] b, out int width, out int height)
    {
        width = 0;
        height = 0;
        var pos = 2;
        while (pos + 8 < b.Length)
        {
            if (b[pos] != 0xFF) return false;
            var marker = b[pos + 1];
            pos += 2;
            if (marker is 0xD8 or 0xD9 or 0x01 || (marker is >= 0xD0 and <= 0xD7)) continue;
            if (pos + 2 > b.Length) return false;
            var segLen = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(pos, 2));
            if (segLen < 2 || pos + segLen > b.Length) return false;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF && segLen >= 7)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(pos + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(pos + 5, 2));
                return true;
            }
            pos += segLen;
        }
        return false;
    }

    private static bool TryReadWebpDimensions(byte[] b, out int width, out int height)
    {
        width = 0;
        height = 0;
        var chunk = Encoding.ASCII.GetString(b, 12, 4);
        if (chunk == "VP8X" && b.Length >= 30)
        {
            width = 1 + (b[24] | (b[25] << 8) | (b[26] << 16));
            height = 1 + (b[27] | (b[28] << 8) | (b[29] << 16));
            return true;
        }
        if (chunk == "VP8L" && b.Length >= 25 && b[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21, 4));
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
            return true;
        }
        if (chunk == "VP8 " && b.Length >= 30)
        {
            // RFC 6386 keyframe: 3-byte frame tag then the 9D 01 2A sync lands at offset 23 exactly;
            // width-1/height-1 follow in 16-bit LE fields (top 2 bits are scales, masked off).
            if (b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A) return false;
            width = (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26, 2)) & 0x3FFF) + 1;
            height = (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28, 2)) & 0x3FFF) + 1;
            return true;
        }
        return false;
    }

    private static XDocument LoadXmlBounded(ZipArchiveEntry entry, ref long remainingBudget)
    {
        using var raw = entry.Open();
        using var copy = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = raw.Read(buffer, 0, buffer.Length)) > 0)
        {
            remainingBudget -= read;
            if (remainingBudget < 0) throw new InvalidDataException("Workbook inflated size exceeded safety budget.");
            copy.Write(buffer, 0, read);
        }
        copy.Position = 0;
        using var reader = XmlReader.Create(copy, SafeXmlSettings);
        return XDocument.Load(reader);
    }

    private static List<string> ReadSharedStrings(ZipArchive zip, ref long remainingBudget)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return [];
        var doc = LoadXmlBounded(entry, ref remainingBudget);
        return doc.Descendants().Where(x => x.Name.LocalName == "si")
            .Select(si => string.Concat(si.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)))
            .ToList();
    }

    private static string[] ReadRow(XElement row, List<string> shared)
    {
        var cells = new List<string>();
        foreach (var cell in row.Elements().Where(x => x.Name.LocalName == "c"))
        {
            var cellRef = cell.Attribute("r")?.Value;
            var index = AgentCellReference.ColumnIndex(cellRef);
            if (!string.IsNullOrEmpty(cellRef))
            {
                // A present but unreadable ref must be skipped, not appended at the current
                // position: that would silently move a far-right value into the wrong column.
                if (index < 0 || index >= MaxColumnsPerRow) continue;
            }
            else
            {
                index = cells.Count;
            }

            if (index >= MaxColumnsPerRow) break;
            while (cells.Count < index) cells.Add("");
            var value = ReadCellValue(cell, shared);
            if (index < cells.Count) cells[index] = value;
            else cells.Add(value);
        }
        while (cells.Count > 0 && string.IsNullOrEmpty(cells[^1])) cells.RemoveAt(cells.Count - 1);
        return [.. cells];
    }

    private static string ReadCellValue(XElement cell, List<string> shared)
    {
        var type = cell.Attribute("t")?.Value;
        var val = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value
            ?? cell.Descendants().FirstOrDefault(e => e.Name.LocalName == "t")?.Value ?? "";
        if (type == "s" && int.TryParse(val, out var idx) && idx >= 0 && idx < shared.Count)
            val = shared[idx];
        return val.Length > MaxCellChars ? val[..MaxCellChars] : val;
    }

    private static bool MatchesImageMagic(string ext, byte[] b) => ext switch
    {
        ".png" => b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47,
        ".jpg" or ".jpeg" => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        ".webp" => b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50,
        _ => false
    };
}
