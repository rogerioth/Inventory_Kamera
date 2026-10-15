using InventoryKamera.ui;
using Microsoft.WindowsAPICodePack.Dialogs;
using Newtonsoft.Json;
using NHotkey;
using NHotkey.WindowsForms;
using Octokit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WindowsInput.Native;
using Application = System.Windows.Forms.Application;

namespace InventoryKamera
{
    public partial class MainForm : Form
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private static Thread scannerThread;
        private static InventoryKamera data = new InventoryKamera();
        private static DatabaseManager databaseManager = new DatabaseManager();

        private const string UpdateFailedMessage = "Unable to update game data. Please check the log for more details";
        private const string UpdateFailedCaption = "Update failed";

        private int Delay;

        private bool running = false;

        public MainForm()
        {
            // Initialize OCR (and the lookup tables) here, before
            // InitializeComponent: controls wired to ParentChanged/TextChanged
            // handlers that touch GenshinProcesor fire mid-initialization, so
            // this is the earliest safe point. A broken Tesseract install
            // (missing tessdata, antivirus quarantine, missing VC++ runtime)
            // otherwise surfaces later as an opaque TypeInitializationException
            // (issues #536/#366/#287).
            try
            {
                GenshinProcesor.EnsureInitialized();
            }
            catch (Exception ex)
            {
                // The real cause sits in the inner exception thrown by the
                // static constructor, including the fix-it instructions.
                var cause = ex.InnerException ?? ex;
                Logger.Fatal(ex, "Tesseract initialization failed; exiting");
                MessageBox.Show($"{cause.Message}\n\nInventory Kamera will now close. See the 'logging' folder for details.",
                    "OCR Initialization Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            InitializeComponent();
            InitializeResponsiveLayout();
            InitializeAchievementMenu();

            Language_ComboBox.SelectedItem = "ENG";

#if DEBUG
            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(4);
#else
            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
#endif
            Logger.Info("Inventory Kamera version {0}", version);

            Text = $"Inventory Kamera V{version}";

            UserInterface.Init(new UserInterface.Controls
            {
                GearPictureBox = GearPictureBox,
                ArtifactOutput = ArtifactOutput_TextBox,
                CharacterName = CharacterName_PictureBox,
                CharacterLevel = CharacterLevel_PictureBox,
                CharacterTalents = new[] { CharacterTalent1_PictureBox, CharacterTalent2_PictureBox, CharacterTalent3_PictureBox },
                CharacterOutput = CharacterOutput_TextBox,
                WeaponCount = WeaponsScannedCount_Label,
                WeaponMax = WeaponsMax_Labell,
                ArtifactCount = ArtifactsScanned_Label,
                ArtifactMax = ArtifactsMax_Label,
                CharacterCount = CharactersScanned_Label,
                ProgramStatus = ProgramStatus_Label,
                ErrorLog = ErrorLog_TextBox,
                NavigationImage = Navigation_Image,
                ScanProgress = UpdateScanProgress,
            });
        }

        private static double ScannerDelayValue(int value)
        {
            switch (value)
            {
                case 0:
                    return 0.5;

                case 1:
                    return 1;

                case 2:
                    return 1.5;

                default:
                    return 1;
            }
        }

        private void Hotkey_Pressed(object sender, HotkeyEventArgs e)
        {
            Logger.Info("Hotkey pressed");
            e.Handled = true;
            // Check if scanner is running
            if (scannerThread != null && scannerThread.IsAlive)
            {
                if (data != null && data.RequestAchievementStop())
                {
                    UserInterface.SetProgramStatus("Stopping achievements...");
                    return;
                }
                // Stop navigating weapons/artifacts
                scannerThread.Abort();

                UserInterface.SetProgramStatus("Scan Stopped");

                Navigation.Reset();
            }
        }

        private void ResetUI()
        {
            Navigation.Reset();

            // Need to invoke method from the UI's handle, not the worker thread
            BeginInvoke((MethodInvoker)delegate { RemoveHotkey(); });
            Logger.Info("Hotkey removed");
        }

        private static void RemoveHotkey()
        {
            try
            {
                // HotkeyManager.Remove is a no-op when the hotkey is not
                // registered, but Unregister throws if Win32 rejects the call.
                HotkeyManager.Current.Remove("Stop");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to remove the 'Stop' hotkey");
            }
        }

        public static void UnexpectedError(string error)
        {
            if (scannerThread != null && scannerThread.IsAlive)
            {
                UserInterface.AddError(error);
            }
        }

        // The output (and logging) locations must be writable BEFORE the scan
        // starts: a disconnected removable/network drive ("device is not
        // ready"), read-only folder or invalid path would otherwise crash the
        // scan after the data was gathered. Cheap probe: create the directory
        // and write+delete a temporary file in it.
        private static bool ValidateOutputDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Logger.Warn("Scan not started: no output directory set");
                MessageBox.Show("Please set an output directory before scanning.", "Output Directory", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                Directory.CreateDirectory(path);

                string probe = Path.Combine(path, $"kamera_write_probe_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Output directory '{0}' is not usable", path);
                MessageBox.Show($"The output directory cannot be used:\n{path}\n\n{ex.Message}\n\nChoose a writable output directory before scanning.",
                    "Output Directory Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            // OCR/lookup-table initialization (with its failure dialog) happens
            // in the constructor, before InitializeComponent.

            if (Properties.Settings.Default.UpgradeNeeded)
            {
                try
                {
                    Properties.Settings.Default.Upgrade();
                    Logger.Info("Application settings loaded from previous version");
                }
                catch (Exception) { /* Intentionally ignored: fall back to the current settings if the upgrade fails. */ }
                Properties.Settings.Default.UpgradeNeeded = false;
                Properties.Settings.Default.Save();
            }

            UpdateKeyTextBoxes();

            Delay = ScannerDelay_TrackBar.Value;

            ProgramStatus_Label.Text = "Ready";
            if (string.IsNullOrWhiteSpace(OutputPath_TextBox.Text))
            {
                OutputPath_TextBox.Text = Directory.GetCurrentDirectory() + @"\GenshinData";
            }
            
        }

        private void UpdateKeyTextBoxes()
        { 
            Navigation.inventoryKey = (VirtualKeyCode)Properties.Settings.Default.InventoryKey;
            Navigation.characterKey = (VirtualKeyCode)Properties.Settings.Default.CharacterKey;
            Navigation.slotOneKey = (VirtualKeyCode)Properties.Settings.Default.Slot1Key;

            inventoryToolStripTextBox.Text = new KeysConverter().ConvertToString((Keys)Navigation.inventoryKey);
            characterToolStripTextBox.Text = new KeysConverter().ConvertToString((Keys)Navigation.characterKey);
            slot1StripTextBox.Text = new KeysConverter().ConvertToString((Keys)Navigation.slotOneKey);

            // Make sure text boxes show key glyph and not "OEM..."
            if (inventoryToolStripTextBox.Text.ToUpper().Contains("OEM"))
            {
                inventoryToolStripTextBox.Text = KeyCodeToUnicode((Keys)Navigation.inventoryKey);
            }
            if (characterToolStripTextBox.Text.ToUpper().Contains("OEM"))
            {
                characterToolStripTextBox.Text = KeyCodeToUnicode((Keys)Navigation.characterKey);
            }
            if (slot1StripTextBox.Text.ToUpper().Contains("OEM"))
            {
                slot1StripTextBox.Text = KeyCodeToUnicode((Keys)Navigation.slotOneKey);
            }
        }

        private void ValidateCustomName(object sender, EventArgs e)
        {
            var textbox = sender as TextBox;
            var name = textbox.Text;

            // This handler is wired to ParentChanged, so it also fires while
            // InitializeComponent is running - on a fresh install the lookup
            // tables may not exist yet (they are generated by the updater).
            if (GenshinProcesor.Characters == null || GenshinProcesor.Characters.Count == 0)
                return;

            if (!string.IsNullOrWhiteSpace(name))
            {
                // Match the folding used when the name is stored, so names like
                // "Éula" are flagged as colliding with the real character.
                if (GenshinProcesor.Characters.ContainsKey(name.FoldToOcrName()))
                {
                    textbox.BackColor = Color.Yellow;
                }
                else
                {
                    textbox.BackColor = Color.White;
                }
            }
        }

        private void DisplayCustomNameTooltip(object sender, EventArgs e)
        {
            var textbox = sender as TextBox;
            
            if (textbox.BackColor == Color.Yellow)
            {
                var tooltip = new ToolTip();
                tooltip.Show($"{textbox.Text} already exists as a character's name.\n" +
                    $"This may affect equipping items to characters and is not fully supported yet.", textbox);
            }
        }

        private void StartButton_Clicked(object sender, EventArgs e)
        {
            if (running)
                return;

            ResetScanUi();

            Logger.Info("Starting scan");

            if (!ValidateScanSelection()) return;
            GenshinProcesor.ReloadData();
            if (LookupTablesMissing()) return;

            // Refuse to start when the output location cannot be written to;
            // ValidateOutputDirectory surfaces the reason to the user.
            if (ValidateOutputDirectory(OutputPath_TextBox.Text))
            {
                LaunchScannerThread();
            }
            else
            {
                Logger.Warn("Scan not started: output directory is not usable");
            }
        }

        private void ResetScanUi()
        {
            // A forced GC.Collect() used to trim memory here before the scan
            // starts; removed because the runtime reclaims it on its own.
            UserInterface.ResetAll();
            SetAchievementPreviewMode(false);
            achievementCountLabel.Text = "0 / ?";
            ResetScanProgress();
            ResetScanLogs();
        }

        private static bool ValidateScanSelection()
        {
            var selected = Properties.Settings.Default;
            if (!selected.ScanWeapons && !selected.ScanArtifacts && !selected.ScanCharacters &&
                !selected.ScanCharDevItems && !selected.ScanMaterials && !selected.ScanAchievements)
            {
                MessageBox.Show("Select at least one scan type.", "Scan Items", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }

        private static bool LookupTablesMissing()
        {
            var selected = Properties.Settings.Default;
            if (selected.ScanWeapons && GenshinProcesor.Weapons.Count == 0 ||
                selected.ScanCharacters && GenshinProcesor.Characters.Count == 0 ||
                selected.ScanCharDevItems && GenshinProcesor.DevItems.Count == 0 ||
                selected.ScanMaterials && GenshinProcesor.Materials.Count == 0)
            {
                MessageBox.Show("Lookup tables needed by the selected scan types are missing. Run 'Options > Update Lookup Tables' first.",
                    "Lookup Tables Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }
            return false;
        }

        private void LaunchScannerThread()
        {
            UserInterface.SetProgramStatus("Scanning");

            if (running)
            {
                Logger.Debug("Already running");
                return;
            }
            running = true;
            SetScanOptionsEnabled(false);
            ReplaceScannerData(new InventoryKamera { AchievementProgress = UpdateAchievementProgress, AchievementPreview = UpdateAchievementPreview });
            string outputDirectory = OutputPath_TextBox.Text;
            bool scanAchievements = Properties.Settings.Default.ScanAchievements;
            bool scanArtifacts = Properties.Settings.Default.ScanArtifacts;
            bool scanCharacters = Properties.Settings.Default.ScanCharacters;
            ManualExportButton.Enabled = false;

            HotkeyManager.Current.AddOrReplace("Stop", Keys.Enter, Hotkey_Pressed);
            Logger.Info("Hotkey registered");
            var settings = Properties.Settings.Default;
            var gameVersion = new DatabaseManager().LocalVersion.ToString(2);
            var options =
                $"\n\tGame Version Data:\t\t\t {gameVersion}\n" +
                $"\tWeapons:\t\t\t\t {settings.ScanWeapons}\n" +
                $"\tArtifacts:\t\t\t\t {settings.ScanArtifacts}\n" +
                $"\tCharacters:\t\t\t\t {settings.ScanCharacters}\n" +
                $"\tDev Items:\t\t\t\t {settings.ScanCharDevItems}\n" +
                $"\tMaterials:\t\t\t\t {settings.ScanMaterials}\n" +
                $"\tAchievements:\t\t\t {settings.ScanAchievements}\n" +
                $"\tMin Weapon Rarity:\t\t {settings.MinimumWeaponRarity}\n" +
                $"\tMin Weapon Level:\t\t {settings.MinimumWeaponLevel}\n" +
                $"\tEquip Weapons:\t\t\t {settings.EquipWeapons}\n" +
                $"\tMin Artifact Rarity:\t {settings.MinimumArtifactRarity}\n" +
                $"\tMin Artifact Level:\t\t {settings.MinimumArtifactLevel}\n" +
                $"\tEquip Artifacts:\t\t {settings.EquipArtifacts}\n" +
                $"\tDelay:\t\t\t\t\t {settings.ScannerDelay}";

            Logger.Info("Scan settings: {0}", options);

            CreateScannerThread(() => RunScannerScan(scanAchievements, scanArtifacts, scanCharacters, outputDirectory));
            StartScan_Button.Enabled = false;
            StartScannerThread();
        }

        // The scanner state below is static and shared with the hotkey/abort
        // paths; instance UI handlers route their writes through these helpers.
        private static void ReplaceScannerData(InventoryKamera value)
        {
            data = value;
        }

        private static void CreateScannerThread(ThreadStart scanAction)
        {
            scannerThread = new Thread(scanAction)
            {
                IsBackground = true
            };
        }

        private static void StartScannerThread()
        {
            scannerThread.Start();
        }

        private void RunScannerScan(bool scanAchievements, bool scanArtifacts, bool scanCharacters, string outputDirectory)
        {
            try
            {
                // Repair caches made by older artifact generators even
                // when version.txt already says the current game version.
                // Finish the update before any game input or OCR workers.
                if (scanArtifacts) EnsureArtifactLookupReady();
                if (scanCharacters) EnsureTravelerLookupReady();
                if (scanAchievements)
                {
                    UserInterface.SetProgramStatus("Scanning: loading achievement catalog");
                    data.AchievementCatalog = AchievementCatalog.Load();
                }
                // Get Screen Location and Size
                Navigation.Initialize();
                ValidateDisplayResolution();

                // Add navigation delay
                Navigation.SetDelay(ScannerDelayValue(Delay));


                // The Data object of json object
                data.GatherData();
                if (scanAchievements) FinishAchievementProgress();

                // Scanning is finished. Enter must not abort the thread while files are being saved.
                Invoke((MethodInvoker)RemoveHotkey);

                ExportAndReportScan(scanAchievements, scanCharacters, outputDirectory);
            }
            catch (ThreadAbortException ex)
            {
                // Workers can get stuck if the thread is aborted or an exception is raised
                data?.StopImageProcessorWorkers();
                if (data == null)
                {
                    Logger.Warn(ex, "Scan thread aborted before scan data was created; nothing to export");
                    UserInterface.SetProgramStatus("Scan stopped");
                    return;
                }
                if (scanAchievements)
                {
                    data.Achievements.Cancelled = true;
                    data.Achievements.Complete = false;
                    ExportAchievements(outputDirectory);
                }
                UserInterface.SetProgramStatus("Scan stopped");
            }
            catch (CharacterNavigationException ex)
            {
                RecordNavigationFailure(ex, scanAchievements, outputDirectory);
            }
            catch (NotImplementedException ex)
            {
                RecordAchievementFailure(scanAchievements, outputDirectory, ex);
                UserInterface.AddError("    [FAILURE] Scan aborted: " + ex);
                UserInterface.SetProgramStatus("Scan aborted", ok: false);
            }
            catch (Exception ex)
            {
                RecordAbortedScan(ex, scanAchievements, outputDirectory);
            }
            finally
            {
                ResetUI();
                running = false;
                ManualExportButton.Invoke((MethodInvoker)delegate
                {
                    ManualExportButton.Enabled = data != null && data.HasData;
                    SetScanOptionsEnabled(true);
                    StartScan_Button.Enabled = true;
                });
                MainForm_Activate();
            }
        }

        private static void EnsureArtifactLookupReady()
        {
            var artifactDatabase = new DatabaseManager();
            if (artifactDatabase.ArtifactUpdateRequired)
            {
                UserInterface.SetProgramStatus("Scanning: updating artifact database");
                if (artifactDatabase.UpdateArtifactData() == UpdateStatus.Fail)
                    throw new InvalidOperationException("The artifact lookup table could not be updated. Check the update error in the log and retry the scan.");
                GenshinProcesor.ReloadData();
            }
            if (GenshinProcesor.Artifacts.Count == 0)
                throw new InvalidOperationException("The artifact lookup table is empty. Update lookup tables before scanning artifacts.");
        }

        private static void EnsureTravelerLookupReady()
        {
            var characterDatabase = new DatabaseManager();
            if (characterDatabase.TravelerUpdateRequired)
            {
                UserInterface.SetProgramStatus("Scanning: updating Traveler database");
                if (characterDatabase.UpdateTravelerData() == UpdateStatus.Fail)
                    throw new InvalidOperationException("The Traveler lookup could not be updated. Check the update error in the log and retry the scan.");
                GenshinProcesor.ReloadData();
            }
        }

        private static void ValidateDisplayResolution()
        {
            List<Size> sizes = new List<Size>
            {
                new Size(16,9),
                new Size(8,5),
            };

            if (!sizes.Contains(Navigation.GetAspectRatio()))
            {
                throw new NotImplementedException($"{Navigation.GetSize().Width}x{Navigation.GetSize().Height} is an unsupported resolution.");
            }

            using (Bitmap screenshot = Navigation.CaptureWindow())
            {
                if (Navigation.GetSize() != screenshot.Size) throw new FormatException("Window size and screenshot size mismatch. Please make sure the game is not in a fullscreen mode.");
            }

            Logger.Info("Resolution: {0}x{1}", Navigation.GetSize().Width, Navigation.GetSize().Height);
        }

        private void ExportAndReportScan(bool scanAchievements, bool scanCharacters, string outputDirectory)
        {
            Good good = null;
            if (data.HasInventoryData)
            {
                good = new Good(data);
                good.WriteToJSON(outputDirectory);
            }
            bool exportSucceeded = !scanAchievements || ExportAchievements(outputDirectory);
            bool scansComplete = (!scanCharacters || data.CharacterScanComplete) &&
                (!scanAchievements || data.Achievements.Complete);
            string status;
            if (!exportSucceeded) status = "Export failed";
            else if (scanAchievements && data.Achievements.Cancelled) status = "Scan stopped";
            else if (!scansComplete) status = "Finished (partial)";
            else status = "Finished";
            UserInterface.SetProgramStatus(status, ok: exportSucceeded && scansComplete);
            if (good != null && !data.Achievements.Cancelled) OpenOptimizerDialog(good);
        }

        private void RecordNavigationFailure(CharacterNavigationException ex, bool scanAchievements, string outputDirectory)
        {
            data?.StopImageProcessorWorkers();
            if (data != null && data.HasInventoryData)
            {
                new Good(data).WriteToJSON(outputDirectory);
                Logger.Warn(ex, "Saved partial inventory after character navigation stopped; no further game input was sent");
            }
            RecordAchievementFailure(scanAchievements, outputDirectory, ex);
            UserInterface.AddError("    [FAILURE] Character navigation stopped: " + ex.Message);
            UserInterface.SetProgramStatus("Scan stopped (navigation)", ok: false);
        }

        private void RecordAbortedScan(Exception ex, bool scanAchievements, string outputDirectory)
        {
            // Workers can get stuck if the thread is aborted or an exception is raised
            data?.StopImageProcessorWorkers();
            while (ex.InnerException != null) ex = ex.InnerException;
            RecordAchievementFailure(scanAchievements, outputDirectory, ex);
            UserInterface.AddError("    [FAILURE] Scan aborted: " + ex);
            UserInterface.SetProgramStatus("Scan aborted", ok: false);
        }

        private void OpenOptimizerDialog(Good data, bool skip = false)
        {
            // Issue #530: while the global "Stop" hotkey (Enter) is registered
            // it consumes Enter system-wide, so a modal dialog can never be
            // answered with the keyboard. The scan no longer needs the stop
            // key once the data is gathered and exported, so unregister it
            // before showing any dialog. Removal is idempotent, and the scan
            // thread path must run it on the UI thread, synchronously so the
            // hotkey is really gone when the dialog appears.
            if (InvokeRequired)
                Invoke((MethodInvoker)RemoveHotkey);
            else
                RemoveHotkey();

            if (!skip)
            {
                var message = "Scan complete! Would you like to upload the database to Genshin Optimizer?";
                var result = MessageBox.Show(message, "Scan Complete", MessageBoxButtons.YesNo);
                if (result == DialogResult.No)
                    return;
            }
            var t = new Thread(() => Clipboard.SetText(data.ToString()));
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            MessageBox.Show("Content copied to your clipboard! Paste the content into the textbox when prompted.", "Data Copied", MessageBoxButtons.OK);
            var optimizerHome = "https://frzyc.github.io/genshin-optimizer/#/setting";
            Process.Start(new ProcessStartInfo(optimizerHome) { UseShellExecute = true });

        }

        private void Github_Label_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var projectHome = "https://github.com/rogerioth/Inventory_Kamera/";
            Process.Start(projectHome);
        }

        private void Releases_Label_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var releasePage = "https://github.com/rogerioth/Inventory_Kamera/releases/latest";
            Process.Start(releasePage);
        }

        private void IssuesPage_Label_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var issuesPage = "https://github.com/rogerioth/Inventory_Kamera/issues";
            Process.Start(issuesPage);
        }

        private void FileSelectButton_Click(object sender, EventArgs e)
        {
            // A nicer file browser
            CommonOpenFileDialog d = new CommonOpenFileDialog
            {
                InitialDirectory = !Directory.Exists(OutputPath_TextBox.Text) ? Directory.GetCurrentDirectory() : OutputPath_TextBox.Text,
                IsFolderPicker = true
            };

            if (d.ShowDialog() == CommonFileDialogResult.Ok)
            {
                OutputPath_TextBox.Text = d.FileName;
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
            RemoveHotkey();
            NLog.LogManager.Shutdown();
        }

        private static void SaveSettings()
        {
            Properties.Settings.Default.Save();
        }

        private void ScannerDelay_TrackBar_ValueChanged(object sender, EventArgs e)
        {
            Delay = ((TrackBar)sender).Value;
        }

        private void Exit_MenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void OptionsMenuItem_KeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;

            // Virtual keys for 0-9, A-Z
            bool vk = e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.Z;
            // Numpad keys and function keys (internally accepts up to F24)
            bool np = e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.F24;
            // OEM keys (Keys that vary depending on keyboard layout)
            bool oem = e.KeyCode >= Keys.Oem1 && e.KeyCode <= Keys.Oem7;
            // Arrow keys, spacebar, INS, DEL, HOME, END, PAGEUP, PAGEDOWN
            bool misc = e.KeyCode == Keys.Space || (e.KeyCode >= Keys.Left && e.KeyCode <= Keys.Down) || (e.KeyCode >= Keys.Prior && e.KeyCode <= Keys.Home) || e.KeyCode == Keys.Insert || e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back;

            // Validate that key is an acceptable Genshin keybind.
            if (!vk && !np && !oem && !misc)
            {
                Logger.Debug("Invalid {Key} key pressed", e.KeyCode);
                return;
            }
            ToolStripTextBox s = (ToolStripTextBox)sender;

            // Needed to differentiate between NUMPAD numbers and numbers at top of keyboard
            s.Text = np || e.KeyCode == Keys.Back ? new KeysConverter().ConvertToString(e.KeyCode) : KeyCodeToUnicode(e.KeyData);

            // Spacebar or upper navigation keys (INSERT-PAGEDOWN keys) make textbox empty
            if (string.IsNullOrWhiteSpace(s.Text) || string.IsNullOrEmpty(s.Text))
            {
                s.Text = new KeysConverter().ConvertToString(e.KeyCode);
            }


            switch (s.Tag)
            {
                case "InventoryKey":
                    Navigation.inventoryKey = (VirtualKeyCode)e.KeyCode;
                    Logger.Debug("Inv key set to: {Key}", Navigation.inventoryKey);
                    Properties.Settings.Default.InventoryKey = e.KeyValue;
                    break;

                case "CharacterKey":
                    Navigation.characterKey = (VirtualKeyCode)e.KeyCode;
                    Logger.Debug("Char key set to: {Key}", Navigation.characterKey);
                    Properties.Settings.Default.CharacterKey = e.KeyValue;
                    break;

                case "slot1Key":
                    Navigation.slotOneKey = (VirtualKeyCode)e.KeyCode;
                    Logger.Debug("Slot 1 key set to: {Key}", Navigation.slotOneKey);
                    Properties.Settings.Default.Slot1Key = e.KeyValue;
                    break;

                default:
                    break;
            }
        }

        private void DatabaseUpdateMenuItem_Click(object sender, EventArgs e)
        {
            var status = databaseManager.UpdateGameData();
            switch (status)
            {
                case UpdateStatus.Fail:
                    MessageBox.Show(UpdateFailedMessage, UpdateFailedCaption, buttons: MessageBoxButtons.OK, icon: MessageBoxIcon.Stop);
                    break;
                case UpdateStatus.Success:
                    GenshinProcesor.ReloadData();
                    MessageBox.Show($"Update for game version {databaseManager.LocalVersion.ToString(2)} successful.", "Update status", buttons: MessageBoxButtons.OK, icon: MessageBoxIcon.Information);
                    Logger.Info("Updated game date to {0}", databaseManager.LocalVersion.ToString(2));
                    break;
                case UpdateStatus.Skipped:
                    if (MessageBox.Show($"No update necessary! You are already using the latest game data ({databaseManager.LocalVersion.ToString(2)})." +
                        $" Would you like to force an update?",
                        "Already Up to Date",
                        buttons: MessageBoxButtons.YesNo,
                        icon: MessageBoxIcon.Information) == DialogResult.Yes)
                    {
                        status = databaseManager.UpdateGameData(force: true);
                        switch (status)
                        {
                            case UpdateStatus.Fail:
                                MessageBox.Show(UpdateFailedMessage, UpdateFailedCaption, buttons: MessageBoxButtons.OK, icon: MessageBoxIcon.Stop);
                                break;
                            default:
                                GenshinProcesor.ReloadData();
                                MessageBox.Show($"Update for game version {databaseManager.LocalVersion.ToString(2)} successful.", "Update success", buttons: MessageBoxButtons.OK, icon: MessageBoxIcon.Information);
                                Logger.Info("Successfully updated game data to {0}", new DatabaseManager().LocalVersion.ToString(2));
                                break;
                        }
                    }
                    break;
                default:
                    break;
            }
        }

        #region Unicode Helper Functions

        // Needed to display OEM keys as glyphs from keyboard. Should work for other languages
        // and keyboard layouts but only tested with QWERTY layout.
        private static string KeyCodeToUnicode(Keys key)
        {
            byte[] keyboardState = new byte[255];
            bool keyboardStateStatus = GetKeyboardState(keyboardState);

            if (!keyboardStateStatus)
            {
                return "";
            }
            uint virtualKeyCode = (uint)key;
            uint scanCode = MapVirtualKey(virtualKeyCode, 0);
            IntPtr inputLocaleIdentifier = GetKeyboardLayout(0);

            StringBuilder result = new StringBuilder();
            ToUnicodeEx(virtualKeyCode, scanCode, keyboardState, result, 5, 0, inputLocaleIdentifier);

            return result.ToString();
        }

        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        #endregion Unicode Helper Functions

        private void ExportFolderMenuItem_Click(object sender, EventArgs e)
        {
            if (Directory.Exists(OutputPath_TextBox.Text) || Directory.CreateDirectory(OutputPath_TextBox.Text).Exists)
            {
                Process.Start($@"{OutputPath_TextBox.Text}");
            }
            else
            {
                Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
            }
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            _ = CheckForKameraUpdatesAsync();
            CheckForGenshinUpdates();
        }

        private static async Task CheckForKameraUpdatesAsync()
        {
            var client = new GitHubClient(new ProductHeaderValue("Inventory_Kamera"));
            try
            {
                var latest = await client.Repository.Release.GetLatest("rogerioth", "Inventory_Kamera");
                if (!Version.TryParse(latest.TagName.TrimStart('v', 'V'), out Version parsedVersion)
                    || parsedVersion.Build < 0)
                {
                    Logger.Warn("Could not check for Kamera updates: release tag '{0}' is not a supported version.", latest.TagName);
                    return;
                }

                // Release tags describe major.minor.patch; assembly revisions are build metadata.
                var latestVersion = new Version(parsedVersion.Major, parsedVersion.Minor, parsedVersion.Build);
                var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
                var currentVersion = new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
                if (currentVersion.CompareTo(latestVersion) < 0)
                {
                    var message = $"A new version of Inventory Kamera is available.\n\n" +
                        $"Current Version: {currentVersion}\nLatest Version: {latestVersion}\n\n" +
                        $"Would you like to download the update?";
                    var result = MessageBox.Show(message, "Inventory Kamera Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (result == DialogResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo(latest.HtmlUrl) { UseShellExecute = true });
                    }
                }
            }
            catch (NotFoundException exception) { Logger.Debug(exception, "No published Kamera release is available yet."); }
            catch (RateLimitExceededException exception) { Logger.Warn(exception, "GitHub rate limit exceeded while checking for Kamera updates. Try again later."); }
            catch (Exception exception) { Logger.Warn(exception, "Could not check for Kamera updates; startup will continue."); }
        }

        private static void CheckForGenshinUpdates()
        {
            // Local update manager; the shared `databaseManager` field stays
            // untouched for the update-manager UI path.
            var updateManager = new DatabaseManager();
            try
            {
                var updatesAvailable = updateManager.UpdateAvailable();
                if (updatesAvailable)
                    OfferGameUpdate(updateManager);
                else
                    RepairStaleLookupCaches(updateManager);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not check for list updates");
                MessageBox.Show("Could not check for updates. Consider trying again in an hour or so.", "Game Version Update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            Properties.Settings.Default.LastUpdateCheck = DateTime.Now;
        }

        private static void OfferGameUpdate(DatabaseManager updateManager)
        {
            var message = "A new version for Genshin Impact has been found. Would you like to update Kamera's lookup tables? (Recommended)";
            var result = MessageBox.Show(message, "Game Version Update", MessageBoxButtons.YesNo);
            if (result == DialogResult.Yes)
            {
                switch (updateManager.UpdateGameData())
                {
                    case UpdateStatus.Fail:
                        MessageBox.Show(UpdateFailedMessage, UpdateFailedCaption, buttons: MessageBoxButtons.OK, icon: MessageBoxIcon.Stop);
                        break;
                    case UpdateStatus.Success:
                        // Reflect the freshly downloaded tables in
                        // memory; a fresh install starts with none.
                        GenshinProcesor.ReloadData();
                        MessageBox.Show($"Update for game version {updateManager.LocalVersion.ToString(2) } successful.", "Update status", MessageBoxButtons.OK, icon: MessageBoxIcon.Information);
                        Logger.Info("Updated game data to {0}", updateManager.LocalVersion.ToString(2));
                        break;
                    default:
                        break;
                }
            }
            else if (result == DialogResult.No)
            {
                MessageBox.Show("Update skipped. Please know that skipping this update will likely result in incorrect scans.\n" +
                    "\nYou may check for updates again on restarting this application or by using the update manager found" +
                    " under 'options'", "Update declined", MessageBoxButtons.OK);
            }
        }

        private static void RepairStaleLookupCaches(DatabaseManager updateManager)
        {
            bool cacheRepairsSucceeded = true;
            if (updateManager.ArtifactUpdateRequired)
            {
                Logger.Info("Repairing artifact lookup table generated by an older updater...");
                if (updateManager.UpdateArtifactData() == UpdateStatus.Fail)
                {
                    Logger.Error("Artifact lookup table repair failed; the previous table was preserved. Artifact scans will retry the update before starting.");
                    cacheRepairsSucceeded = false;
                }
                else GenshinProcesor.ReloadData();
            }
            if (updateManager.TravelerUpdateRequired)
            {
                Logger.Info("Repairing Traveler lookup generated by an older updater...");
                if (updateManager.UpdateTravelerData() == UpdateStatus.Fail)
                {
                    Logger.Error("Traveler lookup repair failed; the previous table was preserved. Character scans will retry the update before starting.");
                    cacheRepairsSucceeded = false;
                }
                else GenshinProcesor.ReloadData();
            }
            if (cacheRepairsSucceeded)
                Logger.Info("Current game data is up to date with data for {0}", updateManager.LocalVersion.ToString(2));
        }

        private void Export_Button_Click(object sender, EventArgs e)
        {
            if (data.Achievements.HasData)
            {
                if (!ValidateOutputDirectory(OutputPath_TextBox.Text)) return;
                if (!ExportAchievements(OutputPath_TextBox.Text)) return;
                if (data.HasInventoryData) new Good(data).WriteToJSON(OutputPath_TextBox.Text);
                MessageBox.Show("Scan files saved to:\n" + OutputPath_TextBox.Text,
                    "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (data.HasInventoryData) OpenOptimizerDialog(new Good(data), true);
        }

        private void MainForm_Activate()
        {
            BeginInvoke((MethodInvoker)delegate { Activate(); });
        }

        private void ErrorLog_Label_Click(object sender, EventArgs e)
        {
            // Resolve the same relative "./logging" folder NLog writes to
            // (see Program.cs); GetFullPath uses the working directory just
            // like the previous shell-resolved relative path did.
            Process.Start(new ProcessStartInfo(Path.GetFullPath(@"logging")) { UseShellExecute = true });
        }

        private void updateExecutablesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new ExecutablesForm().Show();
        }
    }
}
