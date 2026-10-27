#nullable enable
namespace CSI.CastTool;

partial class MainForm
{
    private System.ComponentModel.IContainer? components;
    private Button extractButton = null!;
    private Button repackButton = null!;
    private CheckBox fitCheck = null!;
    private RichTextBox logBox = null!;
    private ProgressBar progressBar = null!;
    private Label statusLabel = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        var layout = new TableLayoutPanel();
        var title = new Label(); var credit = new Label(); var description = new Label();
        fitCheck = new CheckBox(); var buttons = new FlowLayoutPanel();
        extractButton = new Button(); repackButton = new Button(); logBox = new RichTextBox();
        progressBar = new ProgressBar(); statusLabel = new Label();
        SuspendLayout(); layout.SuspendLayout(); buttons.SuspendLayout();
        layout.Dock = DockStyle.Fill; layout.Padding = new Padding(20); layout.ColumnCount = 1; layout.RowCount = 9;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        title.Text = "CSI Cast Tool"; title.Font = new Font("Segoe UI", 20, FontStyle.Bold); title.AutoSize = true;
        credit.Text = "Made by Tiger Fury"; credit.AutoSize = true; credit.Margin = new Padding(3, 0, 3, 14);
        description.Text = "Extract a cast → edit PNGs in Images → repack to a NEW cast.\r\nSupports the XFIR / FGDC format tested with CSI: Dark Motives.";
        description.AutoSize = true; description.Margin = new Padding(3, 0, 3, 14);
        fitCheck.Text = "Fit edited PNGs to original dimensions (recommended for scene alignment)";
        fitCheck.Checked = true; fitCheck.AutoSize = true; fitCheck.Margin = new Padding(3, 0, 3, 14);
        buttons.AutoSize = true; buttons.Dock = DockStyle.Fill; buttons.Margin = new Padding(0);
        extractButton.Text = "Extract cast…"; extractButton.AutoSize = true; extractButton.Padding = new Padding(12, 6, 12, 6);
        repackButton.Text = "Repack project…"; repackButton.AutoSize = true; repackButton.Padding = new Padding(12, 6, 12, 6);
        extractButton.Click += ExtractButton_Click; repackButton.Click += RepackButton_Click;
        buttons.Controls.Add(extractButton); buttons.Controls.Add(repackButton);
        var note = new Label { Text = "Images retain their original scene dimensions by default. Full-size embedding is experimental.", AutoSize = true, Margin = new Padding(3, 12, 3, 12) };
        logBox.ReadOnly = true; logBox.Dock = DockStyle.Fill; logBox.BackColor = SystemColors.Window;
        logBox.Font = new Font("Segoe UI", 10); logBox.DetectUrls = false; logBox.Margin = new Padding(3, 0, 3, 12);
        progressBar.Dock = DockStyle.Fill; progressBar.MarqueeAnimationSpeed = 30;
        statusLabel.AutoSize = true; statusLabel.Text = "Ready"; statusLabel.Margin = new Padding(3, 8, 3, 0);
        layout.Controls.Add(title, 0, 0); layout.Controls.Add(credit, 0, 1); layout.Controls.Add(description, 0, 2);
        layout.Controls.Add(fitCheck, 0, 3); layout.Controls.Add(buttons, 0, 4); layout.Controls.Add(note, 0, 5);
        layout.Controls.Add(logBox, 0, 6); layout.Controls.Add(progressBar, 0, 7); layout.Controls.Add(statusLabel, 0, 8);
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(840, 590); MinimumSize = new Size(800, 560); Font = new Font("Segoe UI", 10);
        Text = "CSI Cast Tool 2.0 — Tiger Fury"; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(layout); buttons.ResumeLayout(false); buttons.PerformLayout(); layout.ResumeLayout(false); layout.PerformLayout(); ResumeLayout(false);
    }
}
