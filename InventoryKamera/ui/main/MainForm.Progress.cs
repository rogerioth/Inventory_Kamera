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
        private ToolTip scanCounterHelp;
        private int? characterTotal;
        private bool characterTotalIsProvisional;
        private int activePhaseCurrent;
        private int characterInspected;
        private bool characterCoverageComplete;
        private decimal appliedWeaponRarity, appliedWeaponLevel, appliedArtifactRarity, appliedArtifactLevel;
        private bool scanFilterSnapshotAvailable;
        private int achievementConfirmed, achievementVerifiedCategories, achievementUnresolved;
        private int? achievementExpected, achievementCategoryTotal;
        private bool achievementSnapshotAvailable, achievementCoverageComplete, achievementCancelled;

        private void InitializeScanProgress()
        {
            scanCounterHelp = new ToolTip(components) { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };
            foreach (var label in new[] { WeaponsScannedCount_Label, WeaponsMax_Labell, ArtifactsScanned_Label,
                ArtifactsMax_Label, CharactersScanned_Label, achievementCountLabel })
                label.TextChanged += (sender, args) => RefreshScanPills();
            foreach (var input in new[] { WeaponRarityControl, MinimumWeaponLevelControl, ArtifactRarityControl, numericUpDown1 })
                input.ValueChanged += (sender, args) => RefreshScanPills();
            ProgramStatus_Label.TextChanged += (sender, args) => RefreshScanPills();
            WeaponsMax_Labell.Text = "?";
            ArtifactsMax_Label.Text = "?";
            RefreshScanPills();
        }

        private void RefreshScanPills()
        {
            weaponPill.Text = "Weapons   " + WeaponsScannedCount_Label.Text + " / " + WeaponsMax_Labell.Text;
            artifactPill.Text = "Artifacts   " + ArtifactsScanned_Label.Text + " / " + ArtifactsMax_Label.Text;
            characterPill.Text = "Characters   " + CharactersScanned_Label.Text + " / " + (characterTotal?.ToString() ?? "?") +
                (characterTotalIsProvisional ? "..." : "");
            achievementPill.Text = "Achievements   " + achievementCountLabel.Text;
            weaponPill.Active = activeScanPhase == ScanPhase.Weapons;
            artifactPill.Active = activeScanPhase == ScanPhase.Artifacts;
            characterPill.Active = activeScanPhase == ScanPhase.Characters;
            achievementPill.Active = activeScanPhase == ScanPhase.Achievements;
            RefreshScanCounterTooltips();
        }

        private void RefreshScanCounterTooltips()
        {
            if (scanCounterHelp == null) return;
            SetScanCounterTooltip(weaponPill, EquipmentCounterTooltip("Weapons", WeaponsScannedCount_Label.Text,
                WeaponsMax_Labell.Text, scanFilterSnapshotAvailable ? appliedWeaponRarity : WeaponRarityControl.Value,
                scanFilterSnapshotAvailable ? appliedWeaponLevel : MinimumWeaponLevelControl.Value,
                WeaponRarityControl.Value, MinimumWeaponLevelControl.Value));
            SetScanCounterTooltip(artifactPill, EquipmentCounterTooltip("Artifacts", ArtifactsScanned_Label.Text,
                ArtifactsMax_Label.Text, scanFilterSnapshotAvailable ? appliedArtifactRarity : ArtifactRarityControl.Value,
                scanFilterSnapshotAvailable ? appliedArtifactLevel : numericUpDown1.Value,
                ArtifactRarityControl.Value, numericUpDown1.Value));
            int acceptedCharacters;
            int.TryParse(CharactersScanned_Label.Text, out acceptedCharacters);
            SetScanCounterTooltip(characterPill, characterTotalIsProvisional
                ? "Characters: " + CharactersScanned_Label.Text + " accepted for export / " + (characterTotal?.ToString() ?? "?") +
                    " owned characters found so far.\nCounting the roster; the total grows as unique characters are found.\n" +
                    "The final total is confirmed after a full cycle and restoration of the starting character. Full character scanning follows."
                : "Characters: " + CharactersScanned_Label.Text + " valid, unique characters accepted for export / " +
                (characterTotal?.ToString() ?? "?") + " owned characters in a verified roster cycle.\n" +
                "Inspected: " + characterInspected + "; inspected but not exported: " + Math.Max(0, characterInspected - acceptedCharacters) + ".\n" +
                (characterTotal.HasValue ? "The total counts owned characters, not the character catalog.\n" : "? means the owned roster total could not be verified or has not been read yet.\n") +
                (characterCoverageComplete ? "Roster coverage verified with no omitted characters." :
                    "Roster coverage is not yet fully verified; unreadable or skipped characters leave partial export data."));
            string achievementCount = achievementSnapshotAvailable
                ? achievementConfirmed + " / " + (achievementExpected?.ToString() ?? "?") : achievementCountLabel.Text;
            string achievementGap = achievementSnapshotAvailable && achievementExpected.HasValue
                ? achievementConfirmed <= achievementExpected.Value
                    ? "Completed IDs still unconfirmed: " + (achievementExpected.Value - achievementConfirmed) + ".\n"
                    : "Confirmed IDs exceed the game's total by " + (achievementConfirmed - achievementExpected.Value) +
                        "; this mismatch needs verification.\n"
                : "";
            SetScanCounterTooltip(achievementPill, "Achievements: " + achievementCount + ".\n" +
                "Left: confirmed completed tier IDs. Right: the game's global completed-achievement counter.\n" +
                "Each completed tier counts separately; duplicate and ambiguous reads do not increase this counter.\n" +
                achievementGap +
                (achievementSnapshotAvailable
                    ? "Verified category bottoms: " + achievementVerifiedCategories + " / " + (achievementCategoryTotal?.ToString() ?? "?") +
                        "; categories still unverified: " + (achievementCategoryTotal.HasValue
                            ? Math.Max(0, achievementCategoryTotal.Value - achievementVerifiedCategories).ToString() : "?") +
                        "; unresolved rows: " + achievementUnresolved + ".\n" +
                        (achievementCancelled ? "Scan cancelled; export coverage is partial.\n" : achievementCoverageComplete
                            ? "Complete coverage and the game total have been verified.\n" : "Coverage is partial or still being verified; missing categories and unreadable rows can leave a gap.\n")
                    : "? means the game's completed total has not been read yet.\n") +
                "The phase progress bar counts verified categories, not completed achievement IDs.");
        }

        private string EquipmentCounterTooltip(string name, string accepted, string total,
            decimal rarity, decimal level, decimal currentRarity, decimal currentLevel)
        {
            string filters = "rarity at least " + rarity.ToString("0") + " stars; level at least " + level.ToString("0");
            string nextFilters = scanFilterSnapshotAvailable && (rarity != currentRarity || level != currentLevel)
                ? "\nCurrent filters for the next scan: rarity at least " + currentRarity.ToString("0") +
                    " stars; level at least " + currentLevel.ToString("0") + "." : "";
            return name + ": " + accepted + " valid items accepted for export / " + total + " inventory items reported by the scanner.\n" +
                (scanFilterSnapshotAvailable ? "Filters used for this scan: " : "Selected filters: ") + filters + ".\n" +
                "Normally, the total is read from the whole inventory before these filters; a filtered eligible total is not calculated.\n" +
                "Filtered-out, unreadable, or invalid items do not increase the accepted count.\n" +
                "The progress bar counts inspected cards; accepted results can keep increasing while OCR finishes." +
                (total == "2000" ? "\n2000 can also be a fallback scan limit when the inventory counter cannot be parsed." : "") +
                (total == "?" ? "\n? means the inventory total has not been read yet." : "") + nextFilters;
        }

        private void SetScanCounterTooltip(Control control, string text)
        {
            scanCounterHelp.SetToolTip(control, text);
            control.AccessibleDescription = text;
            // Painted pills currently have no children; retain the same hover text
            // if a label or other child is added to a pill later.
            foreach (Control child in control.Controls) SetScanCounterTooltip(child, text);
        }

        private void SetAchievementCounterSnapshot(int confirmed, int? expected, int verifiedCategories,
            int? categoryTotal, int unresolved, bool complete, bool cancelled)
        {
            achievementConfirmed = confirmed;
            achievementExpected = expected;
            achievementVerifiedCategories = verifiedCategories;
            achievementCategoryTotal = categoryTotal;
            achievementUnresolved = unresolved;
            achievementCoverageComplete = complete;
            achievementCancelled = cancelled;
            achievementSnapshotAvailable = true;
            achievementCountLabel.Text = confirmed + " / " + (expected?.ToString() ?? "?");
            RefreshScanPills();
        }

        private void ResetScanProgress()
        {
            activeScanPhase = null;
            activePhaseCurrent = 0;
            characterTotal = null;
            characterTotalIsProvisional = false;
            characterInspected = 0;
            characterCoverageComplete = false;
            achievementConfirmed = achievementVerifiedCategories = achievementUnresolved = 0;
            achievementExpected = achievementCategoryTotal = null;
            achievementSnapshotAvailable = achievementCoverageComplete = achievementCancelled = false;
            appliedWeaponRarity = WeaponRarityControl.Value;
            appliedWeaponLevel = MinimumWeaponLevelControl.Value;
            appliedArtifactRarity = ArtifactRarityControl.Value;
            appliedArtifactLevel = numericUpDown1.Value;
            scanFilterSnapshotAvailable = true;
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
            if (progress.Phase == ScanPhase.Characters)
            {
                characterTotal = progress.Total;
                characterTotalIsProvisional = progress.TotalIsProvisional;
                characterInspected = activePhaseCurrent;
                characterCoverageComplete = progress.Finished;
            }
            int percentage = progress.TotalIsProvisional ? 0 : progress.Total.HasValue && progress.Total.Value > 0
                ? (int)Math.Min(100, 100L * activePhaseCurrent / progress.Total.Value) : progress.Percentage;
            scanPhaseProgress.Value = percentage;
            string units = progress.Phase == ScanPhase.Achievements ? "categories" :
                progress.Phase == ScanPhase.EquipmentProcessing ? "processed" :
                progress.Phase == ScanPhase.Weapons || progress.Phase == ScanPhase.Artifacts ? "inspected" : "read";
            string count = activePhaseCurrent + " / " + (progress.Total?.ToString() ?? "?");
            scanPhaseCaption.Text = progress.TotalIsProvisional
                ? progress.Name + "  ·  " + (progress.Total?.ToString() ?? "0") + " found  ·  Counting roster"
                : progress.Name + "  ·  " + count + " " + units +
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
            RefreshAchievementCounterSnapshot(result, visited, total);
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
