using System;
using System.Drawing;
using System.Windows.Forms;

namespace InventoryKamera
{
    public partial class MainForm
    {
        private ProgressBar scanActivity;
        private float layoutScale = 1F;
        private CheckBox achievementsCheckBox;
        private Label achievementCountLabel;
        private TableLayoutPanel previewColumns;
        private TableLayoutPanel equipmentPreview;
        private Control characterPreview;
        private bool achievementPreviewActive;

        // Reuse the designer controls so their settings bindings, tooltips and
        // event handlers remain intact. The menu strip is deliberately untouched.
        private void InitializeResponsiveLayout()
        {
            SuspendLayout();
            layoutScale = CurrentAutoScaleDimensions.Width / 96F;
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(240, 240, 240);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimumSize = new Size(900, 610);
            ClientSize = new Size(960, 590);

            foreach (Control control in Controls)
            {
                if (control != menuStrip1)
                    StyleContent(control);
            }
            StyleContent(ScannerOutput_Panel);

            var split = new SplitContainer
            {
                Name = "ScanAndLogsSplit",
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                FixedPanel = FixedPanel.Panel2,
                Panel2Collapsed = true,
                SplitterWidth = 6,
                Size = new Size(960, 566),
                Panel1MinSize = 260,
                Panel2MinSize = 130,
                TabIndex = 1
            };

            var page = Grid("ScannerPage", 1, 2);
            page.Padding = new Padding(12, 8, 12, 8);
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            split.Panel1.Controls.Add(page);

            var scroll = new Panel { Name = "ScannerScroll", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            var columns = Grid("ScannerColumns", 2, 1);
            columns.MinimumSize = new Size(828, 506);
            columns.Dock = DockStyle.Top;
            columns.Height = columns.MinimumSize.Height;
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 312));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            scroll.Controls.Add(columns);
            scroll.AutoScrollMinSize = columns.MinimumSize;
            scroll.SizeChanged += (sender, args) =>
                columns.Height = Math.Max(columns.MinimumSize.Height, scroll.ClientSize.Height);
            page.Controls.Add(scroll, 0, 0);

            var settings = Grid("ScannerSettings", 1, 4);
            settings.Margin = new Padding(0, 0, 10, 0);
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
            settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            columns.Controls.Add(settings, 0, 0);
            settings.Controls.Add(BuildScanItems(), 0, 0);
            settings.Controls.Add(BuildFilters(), 0, 1);
            settings.Controls.Add(BuildNames(), 0, 2);
            settings.Controls.Add(BuildScannerSettings(), 0, 3);

            var workspace = Grid("ScanWorkspace", 1, 3);
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
            workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
            columns.Controls.Add(workspace, 1, 0);
            workspace.Controls.Add(BuildScanStatus(), 0, 0);
            workspace.Controls.Add(BuildPreviews(), 0, 1);
            workspace.Controls.Add(BuildOutput(), 0, 2);

            var footer = Grid("ScannerFooter", 2, 1);
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var showLogs = new CheckBox { Name = "ShowLogsCheckBox", Text = "Show Logs", AutoSize = true, Anchor = AnchorStyles.Left, TabIndex = 0 };
            footer.Controls.Add(showLogs, 0, 0);
            var links = Flow("HelpLinks");
            links.Anchor = AnchorStyles.Right;
            links.AutoSize = true;
            links.Dock = DockStyle.None;
            foreach (var link in new[] { Github_Label, Releases_Label, IssuesPage_Label })
            {
                link.AutoSize = true;
                link.Margin = new Padding(14, 7, 0, 0);
                link.LinkColor = Color.Blue;
                links.Controls.Add(link);
            }
            Releases_Label.Text = "Latest Releases";
            footer.Controls.Add(links, 1, 0);
            page.Controls.Add(footer, 0, 1);

            // Retire the old absolute-position containers and redundant captions.
            // The legacy error box stays owned by the form for UserInterface.Init.
            foreach (Control control in new Control[] { ScannerOutput_Panel, ErrorLog_TextBox, ErrorLog_Label,
                ErrorReport_Label, ScanItemsList_Label, label2, label3, MinimumWeaponLevelLabel,
                MinimumArtifactLevelLabel, WeaponArtifactOutput_TextBox_Label, CharacterOutput_TextBox_Label })
                control.Visible = false;

            Controls.Add(split);
            menuStrip1.BringToFront();
            split.BringToFront();
            InitializeLogPanel(split, showLogs);
            // InitializeComponent has already scaled the existing menu. Scale
            // the newly assembled content once, using the same system DPI.
            if (Math.Abs(layoutScale - 1F) > 0.01F)
            {
                split.Scale(new SizeF(layoutScale, layoutScale));
                split.Panel1MinSize = LayoutPixels(260);
                split.Panel2MinSize = LayoutPixels(120);
                scroll.AutoScrollMinSize = columns.MinimumSize;
                savedLogPanelHeight = LayoutPixels(220);
                MinimumSize = new Size(LayoutPixels(900), LayoutPixels(610));
                ClientSize = new Size(LayoutPixels(960), LayoutPixels(590));
            }
            ResumeLayout(true);
        }

        private int LayoutPixels(int value)
        {
            return (int)Math.Round(value * layoutScale);
        }

        private GroupBox BuildScanItems()
        {
            var group = Section("Scan Items");
            var grid = Grid("ScanItems", 2, 3);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddCell(grid, Weapons_CheckBox, 0, 0, 0);
            AddCell(grid, Artifacts_Checkbox, 0, 1, 1);
            AddCell(grid, Characters_CheckBox, 0, 2, 2);
            AddCell(grid, CharDevItems_CheckBox, 1, 0, 3);
            AddCell(grid, Materials_CheckBox, 1, 1, 4);
            achievementsCheckBox = new CheckBox { Name = "Achievements_CheckBox", Text = "Achievements", AutoSize = true };
            achievementsCheckBox.DataBindings.Add("Checked", Properties.Settings.Default, "ScanAchievements", true,
                DataSourceUpdateMode.OnPropertyChanged);
            achievementsCheckBox.CheckedChanged += (sender, args) =>
                ManualExportButton.Text = achievementsCheckBox.Checked || data.Achievements.HasData ? "Export Scan Files" : "Open Genshin Optimizer";
            ManualExportButton.Text = achievementsCheckBox.Checked ? "Export Scan Files" : "Open Genshin Optimizer";
            AddCell(grid, achievementsCheckBox, 1, 2, 5);
            group.Controls.Add(grid);
            return group;
        }

        private GroupBox BuildFilters()
        {
            var group = Section("Filters");
            var grid = Grid("ScanFilters", 4, 3);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            AddCell(grid, Caption("Min. rarity"), 1, 0);
            AddCell(grid, Caption("Min. level"), 2, 0);
            AddCell(grid, Caption("Equip"), 3, 0);
            AddCell(grid, Caption("Weapons"), 0, 1);
            AddCell(grid, Caption("Artifacts"), 0, 2);
            AddCell(grid, WeaponRarityControl, 1, 1, 0);
            AddCell(grid, MinimumWeaponLevelControl, 2, 1, 1);
            AddCell(grid, EquipWeaponsCheckBox, 3, 1, 2);
            AddCell(grid, ArtifactRarityControl, 1, 2, 3);
            AddCell(grid, numericUpDown1, 2, 2, 4);
            AddCell(grid, EquipArtifactsCheckBox, 3, 2, 5);
            foreach (var input in new[] { WeaponRarityControl, MinimumWeaponLevelControl, ArtifactRarityControl, numericUpDown1 })
            {
                input.Width = 58;
                input.AccessibleName = input == WeaponRarityControl ? "Minimum weapon rarity" :
                    input == MinimumWeaponLevelControl ? "Minimum weapon level" :
                    input == ArtifactRarityControl ? "Minimum artifact rarity" : "Minimum artifact level";
            }
            EquipWeaponsCheckBox.Text = EquipArtifactsCheckBox.Text = string.Empty;
            EquipWeaponsCheckBox.AccessibleName = "Equip weapons";
            EquipArtifactsCheckBox.AccessibleName = "Equip artifacts";
            group.Controls.Add(grid);
            return group;
        }

        private GroupBox BuildNames()
        {
            var group = Section("Names");
            var grid = Grid("CustomNames", 2, 2);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddCell(grid, label1, 0, 0);
            AddCell(grid, travelerNameTextBox, 1, 0, 0, true);
            AddCell(grid, label4, 0, 1);
            AddCell(grid, wandererNameTextBox, 1, 1, 1, true);
            group.Controls.Add(grid);
            return group;
        }

        private GroupBox BuildScannerSettings()
        {
            var group = Section("Scanner");
            var grid = Grid("ScannerPreferences", 2, 4);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddCell(grid, Language_Label, 0, 0);
            AddCell(grid, Language_ComboBox, 1, 0, 0);
            Language_ComboBox.Width = 76;
            AddCell(grid, LogScreenshotsCheckBox, 0, 1, 1);
            grid.SetColumnSpan(LogScreenshotsCheckBox, 2);
            AddCell(grid, ScannerDelay_Label, 0, 2);
            ScannerDelay_Label.Text = "Scanner Delay:";
            ScannerDelay_TrackBar.AutoSize = false;
            AddCell(grid, ScannerDelay_TrackBar, 1, 2, 2, true);
            ScannerDelay_TrackBar.Dock = DockStyle.Fill;
            var speeds = Grid("ScannerSpeeds", 3, 1);
            for (int i = 0; i < 3; i++) speeds.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            AddCell(speeds, FastScannerDelay_Label, 0, 0);
            AddCell(speeds, MidScannerDelay_Label, 1, 0);
            AddCell(speeds, SlowScannerDelay_Label, 2, 0);
            MidScannerDelay_Label.Anchor = AnchorStyles.Top;
            SlowScannerDelay_Label.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            FastScannerDelay_Label.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            grid.Controls.Add(speeds, 1, 3);
            group.Controls.Add(grid);
            return group;
        }

        private GroupBox BuildScanStatus()
        {
            var group = Section("Scan");
            var grid = Grid("ScanStatus", 2, 4);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var actions = Flow("ScanActions");
            StartScan_Button.AutoSize = false;
            StartScan_Button.Size = new Size(128, 32);
            StartScan_Button.Margin = new Padding(0, 0, 14, 0);
            StartScan_Button.TabIndex = 0;
            actions.Controls.Add(StartScan_Button);
            ProgramStatus_Label.AutoSize = true;
            ProgramStatus_Label.Font = new Font(Font.FontFamily, 14F);
            ProgramStatus_Label.Margin = new Padding(0, 4, 0, 0);
            actions.Controls.Add(ProgramStatus_Label);
            grid.Controls.Add(actions, 0, 0);
            AddCell(grid, ScannerCancelInstructions_Label, 0, 1);
            scanActivity = new ProgressBar { Name = "ScanActivity", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 2), MarqueeAnimationSpeed = 0 };
            grid.Controls.Add(scanActivity, 0, 2);
            ProgramStatus_Label.TextChanged += (sender, args) => UpdateScanActivity();
            var counts = Flow("ScanCounts");
            counts.WrapContents = true;
            counts.Controls.Add(Counter(WeaponsScanned_Label, WeaponsScannedCount_Label, WeaponsScannedSlash_Label, WeaponsMax_Labell));
            counts.Controls.Add(Counter(Artifacts_Label, ArtifactsScanned_Label, ArtifactsScannedSlash_Label, ArtifactsMax_Label));
            counts.Controls.Add(Counter(Characters_Label, CharactersScanned_Label));
            achievementCountLabel = new Label { Name = "AchievementsScannedLabel", Text = "0 / ?" };
            counts.Controls.Add(Counter(new Label { Name = "AchievementsCountCaption", Text = "Achievements:" }, achievementCountLabel));
            grid.Controls.Add(counts, 0, 3);
            var navigation = Grid("NavigationPreview", 1, 2);
            navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            AddCell(navigation, Navigation_Label, 0, 0);
            Navigation_Image.Dock = DockStyle.Fill;
            Navigation_Image.SizeMode = PictureBoxSizeMode.Zoom;
            Navigation_Image.BackColor = Color.White;
            Navigation_Image.BorderStyle = BorderStyle.FixedSingle;
            navigation.Controls.Add(Navigation_Image, 0, 1);
            grid.Controls.Add(navigation, 1, 0);
            grid.SetRowSpan(navigation, 4);
            group.Controls.Add(grid);
            return group;
        }

        private void UpdateScanActivity()
        {
            bool scanning = ProgramStatus_Label.Text.StartsWith("Scanning", StringComparison.OrdinalIgnoreCase);
            scanActivity.Style = scanning ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            scanActivity.MarqueeAnimationSpeed = scanning ? 30 : 0;
            scanActivity.Value = ProgramStatus_Label.Text == "Finished" ? 100 : 0;
        }

        private GroupBox BuildPreviews()
        {
            var group = Section("Preview");
            var columns = Grid("ScanPreviews", 2, 1);
            previewColumns = columns;
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            WeaponArtifact_Label.Text = "Equipment / Achievements";
            Character_Label.Text = "Character / Item";
            equipmentPreview = (TableLayoutPanel)Preview(WeaponArtifact_Label, GearPictureBox, ArtifactOutput_TextBox);
            columns.Controls.Add(equipmentPreview, 0, 0);
            var characterImages = Grid("CharacterImages", 1, 5);
            foreach (var picture in new[] { CharacterName_PictureBox, CharacterLevel_PictureBox,
                CharacterTalent1_PictureBox, CharacterTalent2_PictureBox, CharacterTalent3_PictureBox })
            {
                picture.Dock = DockStyle.Fill;
                picture.SizeMode = PictureBoxSizeMode.Zoom;
                picture.BackColor = Color.White;
                picture.BorderStyle = BorderStyle.FixedSingle;
                picture.Margin = new Padding(0, 0, 0, 2);
                characterImages.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
                characterImages.Controls.Add(picture, 0, characterImages.Controls.Count);
            }
            characterPreview = Preview(Character_Label, characterImages, CharacterOutput_TextBox);
            columns.Controls.Add(characterPreview, 1, 0);
            group.Controls.Add(columns);
            return group;
        }

        private void SetAchievementPreviewMode(bool enabled)
        {
            if (equipmentPreview == null || achievementPreviewActive == enabled) return;
            previewColumns.SuspendLayout();
            equipmentPreview.SuspendLayout();
            try
            {
                achievementPreviewActive = enabled;
                characterPreview.Visible = !enabled;
                previewColumns.SetColumnSpan(equipmentPreview, enabled ? 2 : 1);
                equipmentPreview.RowCount = enabled ? 3 : 2;
                equipmentPreview.RowStyles.Clear();
                equipmentPreview.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
                if (enabled) equipmentPreview.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
                equipmentPreview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                equipmentPreview.SetColumnSpan(GearPictureBox, enabled ? 2 : 1);
                equipmentPreview.SetCellPosition(ArtifactOutput_TextBox, new TableLayoutPanelCellPosition(enabled ? 0 : 1, enabled ? 2 : 1));
                equipmentPreview.SetColumnSpan(ArtifactOutput_TextBox, enabled ? 2 : 1);
                ArtifactOutput_TextBox.WordWrap = enabled;
                WeaponArtifact_Label.Text = enabled ? "Achievements" : "Equipment / Achievements";
            }
            finally
            {
                equipmentPreview.ResumeLayout(true);
                previewColumns.ResumeLayout(true);
            }
        }

        private static Control Preview(Label title, Control images, TextBox output)
        {
            var grid = Grid(title.Name + "Layout", 2, 2);
            grid.Margin = new Padding(0, 0, 6, 0);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddCell(grid, title, 0, 0);
            grid.SetColumnSpan(title, 2);
            images.Dock = DockStyle.Fill;
            images.Margin = new Padding(0, 0, 6, 0);
            if (images is PictureBox picture)
            {
                picture.SizeMode = PictureBoxSizeMode.Zoom;
                picture.BorderStyle = BorderStyle.FixedSingle;
                picture.BackColor = Color.White;
            }
            grid.Controls.Add(images, 0, 1);
            output.Dock = DockStyle.Fill;
            output.WordWrap = false;
            output.ScrollBars = ScrollBars.Both;
            output.BackColor = Color.White;
            output.Margin = Padding.Empty;
            grid.Controls.Add(output, 1, 1);
            return grid;
        }

        private GroupBox BuildOutput()
        {
            var group = Section("Output");
            var grid = Grid("ExportSettings", 2, 3);
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddCell(grid, FileLocation_Label, 0, 0);
            AddCell(grid, OutputPath_TextBox, 0, 1, 0, true);
            AddCell(grid, FileSelectButton, 1, 1, 1, true);
            FileSelectButton.Height = 29;
            var actions = Flow("ExportActions");
            foreach (var button in new[] { button1, ManualExportButton })
            {
                button.AutoSize = true;
                button.Height = 29;
                button.MinimumSize = new Size(140, 29);
                button.Margin = new Padding(0, 4, 8, 0);
                actions.Controls.Add(button);
            }
            button1.TabIndex = 0;
            ManualExportButton.TabIndex = 1;
            grid.Controls.Add(actions, 0, 2);
            grid.SetColumnSpan(actions, 2);
            group.Controls.Add(grid);
            return group;
        }

        private static FlowLayoutPanel Counter(params Label[] labels)
        {
            var counter = Flow(labels[0].Name + "Counter");
            counter.Dock = DockStyle.None;
            counter.AutoSize = true;
            counter.Margin = new Padding(0, 0, 16, 0);
            foreach (var label in labels)
            {
                label.AutoSize = true;
                label.Margin = new Padding(0, 0, 3, 0);
                counter.Controls.Add(label);
            }
            return counter;
        }

        private static GroupBox Section(string title)
        {
            return new GroupBox
            {
                Name = title.Replace(" ", string.Empty) + "Group",
                Text = title,
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 6, 10, 8),
                Margin = new Padding(0, 0, 0, 8)
            };
        }

        private static TableLayoutPanel Grid(string name, int columns, int rows)
        {
            return new TableLayoutPanel
            {
                Name = name, Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows,
                Margin = Padding.Empty, Padding = Padding.Empty, TabStop = false
            };
        }

        private static FlowLayoutPanel Flow(string name)
        {
            return new FlowLayoutPanel
            {
                Name = name, Dock = DockStyle.Fill, WrapContents = false,
                Margin = Padding.Empty, Padding = Padding.Empty, TabStop = false
            };
        }

        private static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.ControlText };
        }

        private static void AddCell(TableLayoutPanel grid, Control control, int column, int row, int tabIndex = 0, bool stretch = false)
        {
            control.Dock = DockStyle.None;
            control.Anchor = AnchorStyles.Left | (stretch ? AnchorStyles.Right : AnchorStyles.None);
            control.Margin = new Padding(0, 2, 6, 2);
            control.TabIndex = tabIndex;
            if (control is Label || control is CheckBox) control.AutoSize = true;
            grid.Controls.Add(control, column, row);
        }

        private void StyleContent(Control control)
        {
            control.Font = Font;
            control.ForeColor = SystemColors.ControlText;
            if (!(control is TextBoxBase) && !(control is PictureBox) && !(control is ComboBox))
                control.BackColor = BackColor;
            foreach (Control child in control.Controls) StyleContent(child);
        }
    }
}
