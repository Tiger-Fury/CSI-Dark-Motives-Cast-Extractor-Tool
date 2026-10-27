using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CSI.CastTool.Core;

namespace CSI.CastTool.Checks;

internal static class Program
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        passed++; Console.WriteLine("PASS: " + name);
    }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL: expected rejection: " + name);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string folder = Path.Combine(Environment.GetEnvironmentVariable("CAST_TOOL_CHECKS_ROOT") ?? Path.GetTempPath(), "CSI-CastTool-Checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            RunSynthetic(folder);
            RunInterface(folder);
            if (args.Length == 2 && args[0] == "--cast") RunReal(folder, args[1]);
            else if (args.Length != 0) throw new ArgumentException("Usage: CSI.CastTool.Checks [--cast path-to-existing.cct]");
            Console.WriteLine($"All {passed} checks passed. Fixture files: {folder}"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Fixtures retained at " + folder); return 1; }
    }
    private static void RunSynthetic(string root)
    {
        string source = Path.Combine(root, "fixture.cct"), project = Path.Combine(root, "fixture-project");
        byte[] baseline = Fixture(); File.WriteAllBytes(source, baseline);
        var parsed = CastArchive.Read(baseline); Check(parsed.Payloads.Count == 3, "synthetic archive parsed");
        var rawEdit = new Dictionary<uint, byte[]>(parsed.Payloads) { [98] = "longer replacement synthetic metadata to check initial segment size updates"u8.ToArray() };
        var rawRebuilt = CastArchive.Read(parsed.Rebuild(rawEdit));
        Check(rawRebuilt.Payloads[98].SequenceEqual(rawEdit[98]), "resized raw initial-segment replacement verified");
        CastProject.Extract(source, project);
        Check(File.Exists(Path.Combine(project, "Images/resource-1032-ediM.png")), "JPEG exported as PNG");
        string unchanged = Path.Combine(root, "unchanged.cct"); var report = CastProject.Repack(project, unchanged);
        Check(report.Changes.Count == 0, "unchanged project preserves resource bytes");
        EqualPayloads(parsed, CastArchive.Read(File.ReadAllBytes(unchanged)));
        string image = Path.Combine(project, "Images/resource-1032-ediM.png");
        using (var replacement = new Bitmap(160, 100)) { using var g = Graphics.FromImage(replacement); g.Clear(Color.Magenta); replacement.Save(image, ImageFormat.Png); }
        string fit = Path.Combine(root, "fit.cct"); report = CastProject.Repack(project, fit);
        var fitted = CastArchive.Read(File.ReadAllBytes(fit));
        Check(report.Changes.Count == 1 && report.Changes[0].Id == 1032, "edited PNG detected");
        Check(parsed.Payloads.Where(x => !x.Value.SequenceEqual(fitted.Payloads[x.Key])).Select(x => x.Key).SequenceEqual(new uint[] { 1032 }), "only intended image payload changes");
        using (var stream = new MemoryStream(fitted.Payloads[1032])) using (var im = Image.FromStream(stream)) Check(im.Size == new Size(80, 50), "default fitting preserves original dimensions");
        string full = Path.Combine(root, "full.cct"); CastProject.Repack(project, full, false);
        using (var stream = new MemoryStream(CastArchive.Read(File.ReadAllBytes(full)).Payloads[1032])) using (var im = Image.FromStream(stream)) Check(im.Size == new Size(160, 100), "full-size option retains input resolution");
        Reject(() => CastProject.Repack(project, full), "existing output rejected");
        Reject(() => CastProject.Extract(source, project), "nonempty project rejected");
        string raw = Path.Combine(project, "Resources/resource-1032-ediM.bin"); byte[] oldRaw = File.ReadAllBytes(raw); File.WriteAllBytes(raw, [1, 2, 3]);
        Reject(() => CastProject.Repack(project, Path.Combine(root, "conflict.cct")), "simultaneous raw and PNG edit rejected"); File.WriteAllBytes(raw, oldRaw);
        using (var transparent = new Bitmap(80, 50, PixelFormat.Format32bppArgb)) transparent.Save(image, ImageFormat.Png);
        Reject(() => CastProject.Repack(project, Path.Combine(root, "alpha.cct")), "transparent JPEG replacement rejected");
        string originalPath = Path.Combine(project, "original.cct"); byte[] tampered = (byte[])baseline.Clone(); tampered[^1] ^= 1; File.WriteAllBytes(originalPath, tampered);
        Reject(() => CastProject.Repack(project, Path.Combine(root, "tampered.cct")), "tampered original rejected"); File.WriteAllBytes(originalPath, baseline);
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(Path.Combine(project, "project.json")), CastProject.JsonOptions)!;
        manifest.Entries[0].Raw = "../outside.bin"; File.WriteAllBytes(Path.Combine(root, "outside.bin"), [1]);
        File.WriteAllText(Path.Combine(project, "project.json"), JsonSerializer.Serialize(manifest, CastProject.JsonOptions));
        Reject(() => CastProject.Repack(project, Path.Combine(root, "traversal.cct")), "project path traversal rejected");
        Reject(() => CastArchive.Read(baseline[..^1]), "truncated archive rejected");
        Reject(() => CastArchive.Read("not a director cast"u8.ToArray()), "unsupported format rejected");
        Check(File.ReadAllBytes(source).SequenceEqual(baseline), "source cast remains untouched");
    }
    private static void RunReal(string root, string source)
    {
        byte[] before = File.ReadAllBytes(source); var original = CastArchive.Read(before);
        string project = Path.Combine(root, "real-project"), output = Path.Combine(root, "real-repacked.cct");
        Console.WriteLine(CastProject.Extract(source, project)); var report = CastProject.Repack(project, output);
        EqualPayloads(original, CastArchive.Read(File.ReadAllBytes(output)));
        Check(report.Changes.Count == 0, "actual cast unchanged round trip");
        string replacementPath = Path.Combine(project, "Images", "resource-1032-ediM.png");
        if (File.Exists(replacementPath))
        {
            using (var bitmap = new Bitmap(1600, 1000)) { using var g = Graphics.FromImage(bitmap); g.Clear(Color.Lime); bitmap.Save(replacementPath, ImageFormat.Png); }
            string editedOutput = Path.Combine(root, "real-edited.cct"); var editedReport = CastProject.Repack(project, editedOutput);
            var edited = CastArchive.Read(File.ReadAllBytes(editedOutput));
            Check(editedReport.Changes.Count == 1 && original.Payloads.Where(x => !x.Value.SequenceEqual(edited.Payloads[x.Key])).Select(x => x.Key).SequenceEqual(new uint[] { 1032 }), "actual cast PNG replacement changes only resource 1032");
            using var stream = new MemoryStream(edited.Payloads[1032]); using var image = Image.FromStream(stream);
            Check(image.Size == new Size(800, 500), "actual cast image fitting verified");
        }
        Check(File.ReadAllBytes(source).SequenceEqual(before), "actual installed source untouched");
        Console.WriteLine("Real-cast output: " + output);
    }
    private static void RunInterface(string root)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware); Application.EnableVisualStyles();
        using var form = new global::CSI.CastTool.MainForm();
        form.ShowInTaskbar = false; form.Opacity = 0; form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000); form.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(root, "interface-preview.png"), ImageFormat.Png);
        static IEnumerable<Control> All(Control control) => control.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(All(x)));
        Check(All(form).Any(x => x.Text == "Made by Tiger Fury" && x.Visible), "interface contains visible creator credit");
        Check(form.Icon != null && All(form).OfType<Button>().Count() == 2 && All(form).OfType<CheckBox>().Single().Checked, "window icon, actions and safe fitting default initialized");
        Console.WriteLine("Interface preview: " + Path.Combine(root, "interface-preview.png"));
    }
    private static void EqualPayloads(CastArchive expected, CastArchive actual)
    {
        Check(expected.Payloads.Count == actual.Payloads.Count && expected.Payloads.All(x => actual.Payloads.TryGetValue(x.Key, out var b) && x.Value.SequenceEqual(b)), "every rebuilt resource payload verified");
    }
    // Minimal valid compressed-cast fixture: metadata in ILS plus a complete JPEG resource.
    private static byte[] Fixture()
    {
        using var image = new Bitmap(80, 50); using (var g = Graphics.FromImage(image)) g.Clear(Color.Navy);
        using var jpeg = new MemoryStream(); image.Save(jpeg, ImageFormat.Jpeg); byte[] media = jpeg.ToArray();
        byte[] metadata = "synthetic metadata: dimensions 80x50"u8.ToArray(), key = new byte[12];
        using var ils = new MemoryStream(); V(ils, 3); ils.Write(key); V(ils, 98); ils.Write(metadata); byte[] packedIls = Z(ils.ToArray());
        using var table = new MemoryStream(); V(table, 1); V(table, 1033); V(table, 4);
        void Row(uint id, uint offset, uint stored, uint size, string tag)
        {
            foreach (uint n in new[] { id, offset, stored, size, 0u }) V(table, n); Tag(table, tag);
        }
        Row(2, 0, (uint)packedIls.Length, (uint)ils.Length, "ILS "); Row(3, uint.MaxValue, 12, 12, "KEY*");
        Row(98, uint.MaxValue, (uint)metadata.Length, (uint)metadata.Length, "CASt");
        Row(1032, (uint)packedIls.Length, (uint)media.Length, (uint)media.Length, "ediM");
        using var map = new MemoryStream(); V(map, 0); V(map, (uint)table.Length); map.Write(Z(table.ToArray()));
        using var file = new MemoryStream(); file.Write("XFIR"u8); file.Write(new byte[4]); file.Write("CDGF"u8);
        Tag(file, "Fver"); V(file, 1); file.WriteByte(1); Tag(file, "Fcdr"); byte[] codec = Z([0, 0]); V(file, (uint)codec.Length); file.Write(codec);
        Tag(file, "ABMP"); V(file, (uint)map.Length); map.Position = 0; map.CopyTo(file);
        Tag(file, "FGEI"); V(file, 0); file.Write(packedIls); file.Write(media);
        byte[] data = file.ToArray(); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length - 8); return data;
    }
    private static void Tag(Stream stream, string tag) => stream.Write(Encoding.ASCII.GetBytes(new string(tag.Reverse().ToArray())));
    private static void V(Stream stream, uint value)
    {
        var bytes = new List<byte> { (byte)(value & 127) }; while ((value >>= 7) != 0) bytes.Add((byte)((value & 127) | 128)); bytes.Reverse(); stream.Write(bytes.ToArray());
    }
    private static byte[] Z(byte[] bytes)
    {
        using var output = new MemoryStream(); using (var zip = new ZLibStream(output, CompressionLevel.SmallestSize, true)) zip.Write(bytes); return output.ToArray();
    }
}
