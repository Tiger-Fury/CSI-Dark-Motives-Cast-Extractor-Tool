using CSI.CastTool.Core;

namespace CSI.CastTool;

public partial class MainForm : Form
{
    private bool busy;
    public MainForm()
    {
        InitializeComponent();
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Tiger-Fury.ico");
        if (stream != null) Icon = new Icon(stream);
        Log("Original casts and edited PNGs are preserved. Existing outputs are never overwritten.");
        Log("Only complete JPEG resources export as PNG; other resources remain raw.");
    }
    private void Log(string message) => logBox.AppendText(message + Environment.NewLine + Environment.NewLine);
    private async Task RunOperation(Func<string> operation)
    {
        busy = true; extractButton.Enabled = repackButton.Enabled = fitCheck.Enabled = false;
        progressBar.Style = ProgressBarStyle.Marquee; statusLabel.Text = "Working…";
        try
        {
            string result = await Task.Run(operation); Log(result); statusLabel.Text = "Complete";
            MessageBox.Show(this, result, "Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            Log("Stopped: " + error.Message); statusLabel.Text = "Operation stopped";
            MessageBox.Show(this, error.Message, "Operation stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false; extractButton.Enabled = repackButton.Enabled = fitCheck.Enabled = true;
            progressBar.Style = ProgressBarStyle.Blocks;
        }
    }
    private async void ExtractButton_Click(object? sender, EventArgs e)
    {
        using var file = new OpenFileDialog { Title = "Choose a compressed cast", Filter = "Director compressed casts (*.cct)|*.cct|All files (*.*)|*.*" };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        using var folder = new FolderBrowserDialog { Description = "Choose the parent folder for a new extraction project", UseDescriptionForTitle = true };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        string source = file.FileName, target = Path.Combine(folder.SelectedPath, Path.GetFileNameWithoutExtension(source) + "-project");
        await RunOperation(() => CastProject.Extract(source, target));
    }
    private async void RepackButton_Click(object? sender, EventArgs e)
    {
        using var project = new OpenFileDialog { Title = "Choose project.json", Filter = "Cast project (project.json)|project.json" };
        if (project.ShowDialog(this) != DialogResult.OK) return;
        using var output = new SaveFileDialog { Title = "Save a NEW test cast", Filter = "Director compressed cast (*.cct)|*.cct", FileName = "Case_2_Art-repacked.cct", DefaultExt = "cct", OverwritePrompt = true };
        if (output.ShowDialog(this) != DialogResult.OK) return;
        bool fit = fitCheck.Checked;
        if (!fit && MessageBox.Show(this, "Full-size embedding keeps all input pixels but does not update scene scripts or geometry. Larger images may display oversized. Continue?", "Experimental option", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        string folder = Path.GetDirectoryName(project.FileName)!, destination = output.FileName;
        await RunOperation(() =>
        {
            var report = CastProject.Repack(folder, destination, fit);
            return $"Repacked and verified {report.ResourcesVerified} resources. Changed: {report.Changes.Count}.\r\nOutput: {destination}\r\nBack up the installed cast and test the replacement in-game.";
        });
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy) { e.Cancel = true; statusLabel.Text = "Please wait for the current operation to finish."; }
        base.OnFormClosing(e);
    }
}
