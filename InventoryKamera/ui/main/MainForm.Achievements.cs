using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace InventoryKamera
{
    public partial class MainForm
    {
        private void SetScanOptionsEnabled(bool enabled)
        {
            foreach (var control in new Control[] { Weapons_CheckBox, Artifacts_Checkbox, Characters_CheckBox,
                CharDevItems_CheckBox, Materials_CheckBox, achievementsCheckBox, OutputPath_TextBox, FileSelectButton })
                control.Enabled = enabled;
        }

        private void UpdateAchievementProgress(AchievementScanResult result, AchievementCategory category, int page)
        {
            string count = result.Completed.Count + " / " + (result.ExpectedCompleted?.ToString() ?? "?");
            Invoke((MethodInvoker)delegate
            {
                achievementCountLabel.Text = count;
            });
            UserInterface.ReportScanProgress(ScanPhase.Achievements,
                result.Categories.Count(coverage => coverage.ReachedBottom), data.AchievementCatalog?.Categories.Count,
                count + " completed achievements");
        }

        private void UpdateAchievementPreview(Bitmap row, AchievementObservation observation)
        {
            if (IsDisposed || !IsHandleCreated) return;
            // The recognizer disposes its crop when this synchronous callback returns.
            Invoke((MethodInvoker)delegate
            {
                SetAchievementPreviewMode(true);
                UserInterface.SetAchievement(row, observation);
            });
        }

        private bool ExportAchievements(string directory)
        {
            try
            {
                var paths = AchievementExport.Write(data.Achievements, directory);
                foreach (var path in paths) Logger.Info("Saved achievement export: {0}", path);
                if (!data.Achievements.Complete)
                    Logger.Warn("Achievement exports are partial: {0}/{1} completed IDs, {2} unresolved rows. See the Kamera report for coverage and issues.",
                        data.Achievements.Completed.Count, data.Achievements.ExpectedCompleted?.ToString() ?? "?",
                        data.Achievements.Observations.Count(x => !string.IsNullOrEmpty(x.Error)));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not export achievements; confirmed results are retained for manual export");
                UserInterface.AddError("Achievement export failed: " + ex.Message + ". Use Export to try again.");
                return false;
            }
        }

        private void RecordAchievementFailure(bool selected, string directory, Exception exception)
        {
            if (!selected || data == null) return;
            data.Achievements.Complete = false;
            data.Achievements.Issues.Add(exception.Message);
            data.Achievements.FinishedAt = DateTime.UtcNow;
            ExportAchievements(directory);
        }

        private void InitializeAchievementMenu()
        {
            var options = menuStrip1.Items.Cast<ToolStripItem>().OfType<ToolStripMenuItem>()
                .FirstOrDefault(item => item.Text.Replace("&", "").Equals("Options", StringComparison.OrdinalIgnoreCase));
            if (options == null) return;
            var update = new ToolStripMenuItem("Update Achievement Catalog");
            update.Click += async (sender, args) =>
            {
                if (running) return;
                update.Enabled = false;
                try
                {
                    var catalog = await Task.Run(() => AchievementCatalog.Load(true));
                    MessageBox.Show(catalog.Warning ?? "Achievement catalog updated: " + catalog.AchievementCount + " IDs.",
                        "Achievement Catalog", MessageBoxButtons.OK, catalog.Warning == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Achievement Catalog", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { update.Enabled = true; }
            };
            options.DropDownItems.Add(update);
        }
    }
}
