using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace InventoryKamera
{
    public partial class MainForm
    {
        private ProgressBar scanPhaseProgress;
        private Label scanPhaseCaption;
        private ScanPhase? activeScanPhase;
        private ScanCounterPill weaponPill, artifactPill, characterPill, achievementPill;
        private int? characterTotal;
        private int activePhaseCurrent;

        private void InitializeScanProgress()
        {
            foreach (var label in new[] { WeaponsScannedCount_Label, WeaponsMax_Labell, ArtifactsScanned_Label,
                ArtifactsMax_Label, CharactersScanned_Label, achievementCountLabel })
                label.TextChanged += (sender, args) => RefreshScanPills();
            WeaponsMax_Labell.Text = "?";
            ArtifactsMax_Label.Text = "?";
            RefreshScanPills();
        }

        private void RefreshScanPills()
        {
            weaponPill.Text = "Weapons   " + WeaponsScannedCount_Label.Text + " / " + WeaponsMax_Labell.Text;
            artifactPill.Text = "Artifacts   " + ArtifactsScanned_Label.Text + " / " + ArtifactsMax_Label.Text;
            characterPill.Text = "Characters   " + CharactersScanned_Label.Text + " / " + (characterTotal?.ToString() ?? "?");
            achievementPill.Text = "Achievements   " + achievementCountLabel.Text;
            weaponPill.Active = activeScanPhase == ScanPhase.Weapons;
            artifactPill.Active = activeScanPhase == ScanPhase.Artifacts;
            characterPill.Active = activeScanPhase == ScanPhase.Characters;
            achievementPill.Active = activeScanPhase == ScanPhase.Achievements;
        }

        private void ResetScanProgress()
        {
            activeScanPhase = null;
            activePhaseCurrent = 0;
            characterTotal = null;
            scanPhaseProgress.Value = 0;
            scanPhaseCaption.Text = "Current phase progress";
            RefreshScanPills();
        }

        private void UpdateScanProgress(ScanProgressUpdate progress)
        {
            // OCR workers may finish an older phase after capture has moved on.
            if (progress.Starting)
            {
                activeScanPhase = progress.Phase;
                activePhaseCurrent = 0;
                ProgramStatus_Label.Text = "Scanning " + progress.Name.ToLowerInvariant();
                ProgramStatus_Label.ForeColor = Color.Green;
            }
            if (progress.Phase != activeScanPhase) return;
            activePhaseCurrent = Math.Max(activePhaseCurrent, progress.Current);
            if (progress.Phase == ScanPhase.Characters && progress.Total.HasValue)
                characterTotal = progress.Total;
            int percentage = progress.Total.HasValue && progress.Total.Value > 0
                ? (int)Math.Min(100, 100L * activePhaseCurrent / progress.Total.Value) : progress.Percentage;
            scanPhaseProgress.Value = percentage;
            string units = progress.Phase == ScanPhase.Achievements ? "categories" :
                progress.Phase == ScanPhase.EquipmentProcessing ? "processed" :
                progress.Phase == ScanPhase.Weapons || progress.Phase == ScanPhase.Artifacts ? "inspected" : "read";
            string count = activePhaseCurrent + " / " + (progress.Total?.ToString() ?? "?");
            scanPhaseCaption.Text = progress.Name + "  ·  " + count + " " + units +
                (progress.Total.HasValue ? "  ·  " + percentage + "%" : "") +
                (string.IsNullOrWhiteSpace(progress.Detail) ? "" : "  ·  " + progress.Detail);
            scanPhaseCaption.AccessibleDescription = scanPhaseCaption.Text;
            RefreshScanPills();
        }

        private void FinishAchievementProgress()
        {
            var result = data.Achievements;
            int visited = result.Categories.Count(category => category.ReachedBottom);
            int? total = data.AchievementCatalog?.Categories.Count;
            string detail = result.Completed.Count + " / " + (result.ExpectedCompleted?.ToString() ?? "?") + " completed achievements";
            if (result.Complete) UserInterface.FinishScanPhase(ScanPhase.Achievements, visited, total, detail);
            else UserInterface.ReportScanProgress(ScanPhase.Achievements, visited, total, detail);
        }
    }

    // A pill is one painted line: its name and numbers cannot wrap independently.
    internal sealed class ScanCounterPill : Control
    {
        private bool active;
        public Color Accent { get; set; }
        public bool Active
        {
            get => active;
            set { if (active != value) { active = value; Invalidate(); } }
        }

        public ScanCounterPill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            AccessibleName = Text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 4 || Height < 4) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(1, 1, Width - 3, Height - 3);
            float diameter = bounds.Height;
            using (var path = new GraphicsPath())
            {
                path.AddArc(bounds.X, bounds.Y, diameter, diameter, 90, 180);
                path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 180);
                path.CloseFigure();
                var fill = Color.FromArgb(238 + Accent.R / 16, 238 + Accent.G / 16, 238 + Accent.B / 16);
                using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(Active ? Accent : Color.FromArgb(205 + Accent.R / 8,
                    205 + Accent.G / 8, 205 + Accent.B / 8), Active ? 2F : 1F)) e.Graphics.DrawPath(pen, path);
            }
            var textBounds = Rectangle.Inflate(ClientRectangle, -10, 0);
            TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, Accent,
                TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
