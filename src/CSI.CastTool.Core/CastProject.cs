using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CSI.CastTool.Core;

public sealed class ProjectManifest
{
    public string Format { get; set; } = "CSI-Cast-Tool-project";
    public string Version { get; set; } = "2.0";
    public string SourceName { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public List<ProjectEntry> Entries { get; set; } = [];
}
public sealed class ProjectEntry
{
    public uint Id { get; set; }
    public string Tag { get; set; } = "";
    public string Raw { get; set; } = "";
    public string RawSha256 { get; set; } = "";
    public string? Image { get; set; }
    public string? ImageSha256 { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}
public sealed record ImageChange(uint Id, string Kind, int[]? InputSize = null, int[]? EmbeddedSize = null);
public sealed record RepackReport(string SourceSha256, string OutputSha256, int ResourcesVerified, List<ImageChange> Changes, bool FitImages, bool InGameTested = false);

public static class CastProject
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private static string HashFile(string path) => CastArchive.Hash(File.ReadAllBytes(path));
    private static string Stem(CastResource row) => $"resource-{row.Id}-{new string(row.Tag.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray())}";
    private static void WriteNew(string path, byte[] bytes)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes);
    }
    public static string Extract(string source, string projectFolder)
    {
        source = Path.GetFullPath(source); projectFolder = Path.GetFullPath(projectFolder);
        if (Directory.Exists(projectFolder) && Directory.EnumerateFileSystemEntries(projectFolder).Any())
            throw new IOException("Choose a new or empty project folder.");
        byte[] original = File.ReadAllBytes(source); var archive = CastArchive.Read(original);
        Directory.CreateDirectory(projectFolder); Directory.CreateDirectory(Path.Combine(projectFolder, "Resources")); Directory.CreateDirectory(Path.Combine(projectFolder, "Images"));
        WriteNew(Path.Combine(projectFolder, "original.cct"), original);
        var manifest = new ProjectManifest { SourceName = Path.GetFileName(source), SourceSha256 = CastArchive.Hash(original) };
        var previews = new List<ProjectEntry>();
        foreach (var row in archive.Resources.Where(x => x.Id != 2))
        {
            byte[] bytes = archive.Payloads[row.Id]; string stem = Stem(row), relative = $"Resources/{stem}.bin";
            WriteNew(Path.Combine(projectFolder, relative), bytes);
            var entry = new ProjectEntry { Id = row.Id, Tag = row.Tag, Raw = relative, RawSha256 = CastArchive.Hash(bytes) };
            if (bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }))
            {
                using var stream = new MemoryStream(bytes); using var image = System.Drawing.Image.FromStream(stream, true, true);
                if (image.RawFormat.Guid == ImageFormat.Jpeg.Guid)
                {
                    entry.Image = $"Images/{stem}.png"; entry.Width = image.Width; entry.Height = image.Height;
                    using var output = new FileStream(Path.Combine(projectFolder, entry.Image), FileMode.CreateNew);
                    image.Save(output, ImageFormat.Png); output.Close();
                    entry.ImageSha256 = HashFile(Path.Combine(projectFolder, entry.Image));
                    if (image.Width >= 300 && image.Height >= 200) previews.Add(entry);
                }
            }
            manifest.Entries.Add(entry);
        }
        WriteNew(Path.Combine(projectFolder, "project.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        MakeContactSheets(projectFolder, previews);
        return $"Extracted {manifest.Entries.Count} resources and {manifest.Entries.Count(x => x.Image != null)} editable PNGs.\r\nProject: {projectFolder}";
    }
    private static string ProjectPath(string folder, string relative)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A project path points outside the project.");
        for (string? current = path; current != null && current.StartsWith(root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked project files or folders are unsupported.");
        return path;
    }
    public static RepackReport Repack(string projectFolder, string destination, bool fitImages = true)
    {
        projectFolder = Path.GetFullPath(projectFolder); destination = Path.GetFullPath(destination);
        string reportPath = destination + ".report.json";
        if (File.Exists(destination) || File.Exists(reportPath)) throw new IOException("Output or report already exists. Choose a NEW filename; existing files are never overwritten.");
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(ProjectPath(projectFolder, "project.json")), JsonOptions)
            ?? throw new InvalidDataException("Project manifest is empty.");
        if (manifest.Format != "CSI-Cast-Tool-project") throw new InvalidDataException("Not a CSI Cast Tool project.");
        byte[] original = File.ReadAllBytes(ProjectPath(projectFolder, "original.cct"));
        if (!string.Equals(CastArchive.Hash(original), manifest.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Project original.cct has changed. Extract a fresh project.");
        var archive = CastArchive.Read(original); var payloads = new Dictionary<uint, byte[]>(archive.Payloads);
        if (manifest.Entries.Count != payloads.Count || !manifest.Entries.Select(x => x.Id).Order().SequenceEqual(payloads.Keys.Order()))
            throw new InvalidDataException("Incomplete or duplicate project entries.");
        var changes = new List<ImageChange>();
        foreach (var entry in manifest.Entries)
        {
            byte[] baseline = archive.Payloads[entry.Id]; byte[] candidate = File.ReadAllBytes(ProjectPath(projectFolder, entry.Raw));
            bool rawChanged = !candidate.AsSpan().SequenceEqual(baseline);
            if (entry.Image != null)
            {
                string imagePath = ProjectPath(projectFolder, entry.Image);
                if (!string.Equals(HashFile(imagePath), entry.ImageSha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (rawChanged) throw new InvalidDataException($"Resource {entry.Id}: both PNG and binary changed. Edit only one.");
                    if (!baseline.AsSpan().StartsWith(new byte[] { 255, 216, 255 })) throw new InvalidDataException("PNG replacement is only supported for complete JPEG resources.");
                    using var originalStream = new MemoryStream(baseline); using var originalImage = System.Drawing.Image.FromStream(originalStream, true, true);
                    using var edited = new Bitmap(imagePath);
                    EnsureOpaque(edited);
                    Size embedded = fitImages ? originalImage.Size : edited.Size;
                    candidate = EncodeJpeg(edited, embedded);
                    changes.Add(new(entry.Id, "PNG replacement", [edited.Width, edited.Height], [embedded.Width, embedded.Height]));
                }
            }
            if (rawChanged) changes.Add(new(entry.Id, "Raw replacement"));
            payloads[entry.Id] = candidate;
        }
        byte[] output = archive.Rebuild(payloads);
        var report = new RepackReport(CastArchive.Hash(original), CastArchive.Hash(output), payloads.Count, changes, fitImages);
        byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // Exclusive creation also protects against races between validation and saving.
        WriteNew(destination, output); WriteNew(reportPath, reportBytes);
        return report;
    }
    private static void EnsureOpaque(Bitmap image)
    {
        using var rgba = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(rgba)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImageUnscaled(image, 0, 0); }
        var data = rgba.LockBits(new Rectangle(Point.Empty, rgba.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[checked(image.Width * 4)];
            for (int y = 0; y < image.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 3; x < row.Length; x += 4)
                    if (row[x] != 255) throw new InvalidDataException("JPEG cannot preserve transparency. Use an opaque replacement PNG.");
            }
        }
        finally { rgba.UnlockBits(data); }
    }
    private static byte[] EncodeJpeg(System.Drawing.Image image, Size size)
    {
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality; graphics.CompositingQuality = CompositingQuality.HighQuality;
            using var attributes = new ImageAttributes(); attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(image, new Rectangle(Point.Empty, size), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
        var codec = ImageCodecInfo.GetImageEncoders().Single(x => x.FormatID == ImageFormat.Jpeg.Guid);
        using var options = new EncoderParameters(1); options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 98L);
        using var output = new MemoryStream(); bitmap.Save(output, codec, options); return output.ToArray();
    }
    private static void MakeContactSheets(string folder, List<ProjectEntry> previews)
    {
        for (int start = 0; start < previews.Count; start += 24)
        {
            var page = previews.Skip(start).Take(24).ToArray();
            using var sheet = new Bitmap(1000, ((page.Length + 3) / 4) * 190); using var g = Graphics.FromImage(sheet);
            using var font = new Font("Segoe UI", 10); g.Clear(Color.FromArgb(32, 32, 32)); g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int i = 0; i < page.Length; i++)
            {
                var entry = page[i]; using var image = System.Drawing.Image.FromFile(Path.Combine(folder, entry.Image!));
                double scale = Math.Min(240.0 / image.Width, 155.0 / image.Height); int x = i % 4 * 250, y = i / 4 * 190;
                g.DrawImage(image, x, y, (int)(image.Width * scale), (int)(image.Height * scale));
                g.DrawString($"ID {entry.Id} | {image.Width} × {image.Height}", font, Brushes.White, x + 4, y + 158);
            }
            using var file = new FileStream(Path.Combine(folder, $"Contact-sheet-{start / 24 + 1:00}.jpg"), FileMode.CreateNew);
            sheet.Save(file, ImageFormat.Jpeg);
        }
    }
}
