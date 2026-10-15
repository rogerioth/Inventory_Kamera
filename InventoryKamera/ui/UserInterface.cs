using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace InventoryKamera
{
	public static class UserInterface
	{
		private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

		// Artifacts and Weapons
		private static PictureBox gear_PictureBox;

		private static TextBox gear_TextBox;

		// Character
		private static PictureBox cName_PictureBox;

		private static PictureBox cLevel_PictureBox;
		private static PictureBox[] cTalent_PictureBoxes = new PictureBox[3];
		private static TextBox character_TextBox;

		// Counters
		private static Label weaponCount_Label;

		private static Label weaponMax_Label;

		private static Label artifactCount_Label;
		private static Label artifactMax_Label;

		private static Label characterCount_Label;

		// Status
		private static Label programStatus_Label;
        private static Action<ScanProgressUpdate> scanProgress;

		// Error box
		private static TextBox error_TextBox;

		// Current Images
		private static PictureBox navigation_PictureBox;

		/// <summary>
		/// The controls and callbacks the scanner UI exposes to <see cref="UserInterface"/>.
		/// Collected once by the main form and handed to <see cref="Init"/> as a single object.
		/// </summary>
		public sealed class Controls
		{
			public PictureBox GearPictureBox;
			public TextBox ArtifactOutput;
			public PictureBox CharacterName;
			public PictureBox CharacterLevel;
			public PictureBox[] CharacterTalents;
			public TextBox CharacterOutput;
			public Label WeaponCount;
			public Label WeaponMax;
			public Label ArtifactCount;
			public Label ArtifactMax;
			public Label CharacterCount;
			public Label ProgramStatus;
			public TextBox ErrorLog;
			public PictureBox NavigationImage;
			public Action<ScanProgressUpdate> ScanProgress;
		}

		public static void Init(Controls controls)
		{
			// Artifacts and Weapons
			gear_PictureBox = controls.GearPictureBox;
			gear_TextBox = controls.ArtifactOutput;

			// Characters
			cName_PictureBox = controls.CharacterName;
			cLevel_PictureBox = controls.CharacterLevel;
			cTalent_PictureBoxes = controls.CharacterTalents;
			character_TextBox = controls.CharacterOutput;

			// Counters
			weaponCount_Label = controls.WeaponCount;
			weaponMax_Label = controls.WeaponMax;
			artifactCount_Label = controls.ArtifactCount;
			artifactMax_Label = controls.ArtifactMax;
			characterCount_Label = controls.CharacterCount;

			// Status
			programStatus_Label = controls.ProgramStatus;
            scanProgress = controls.ScanProgress;

			// Error
			error_TextBox = controls.ErrorLog;

			// Navigation Image
			navigation_PictureBox = controls.NavigationImage;
		}

		private static void UpdateElements(Bitmap bm, string text, PictureBox pictureBox, TextBox textBox)
		{
			UpdatePictureBox(bm, pictureBox);
			UpdateTextBox(text, textBox);
		}

		private static void UpdatePictureBox(Bitmap bm, PictureBox pictureBox)
		{
            Bitmap clone = null;
            bool assigned = false;
			try
			{
                clone = new Bitmap(bm.Width, bm.Height);
				using (var copy = Graphics.FromImage(clone))
				{
					copy.DrawImage(bm, 0, 0);
				}
				MethodInvoker pictureBoxAction = delegate
			{
                var previous = pictureBox.Image;
				pictureBox.Image = clone;
                assigned = true;
                previous?.Dispose();
				pictureBox.Refresh();
			};
				pictureBox.Invoke(pictureBoxAction);
			}
			catch (Exception e)
			{
                if (!assigned) clone?.Dispose();
				Logger.Debug($"Problem updating picturebox {0}\n{1}", pictureBox.Name, e);
			}
		}

		private static void UpdateTextBox(string text, TextBox textBox)
		{
			try
			{
				MethodInvoker textBoxAction = delegate
				{
					textBox.AppendText(text.Replace("\n", Environment.NewLine));
					textBox.AppendText(Environment.NewLine);
					textBox.Refresh();
				};
				textBox.Invoke(textBoxAction);
			}
			catch (Exception e)
			{
				Logger.Debug($"Problem updating picturebox {0}\n{1}", textBox.Name, e);
			}
		}

		private static void UpdateLabel(string text, Label label)
		{
			try
			{
				MethodInvoker labelAction =  delegate
			{
				label.Text = text;
				label.Refresh();
			};
                // Counter labels are retained as backing values for the pills;
                // dispatch through the visible status control, which owns a handle.
				programStatus_Label.Invoke(labelAction);
			}
			catch (Exception e)
			{
				Logger.Debug($"Problem updating picturebox {0}\n{1}", label.Name, e);

			}
		}

		public static void SetGear(Bitmap bm, Weapon weapon)
		{
			ResetGearDisplay();
			SetGearPictureBox(bm);
			SetGearTextBox(weapon.ToString());
		}

		public static void SetGear(Bitmap bm, Artifact artifact)
		{
			ResetGearDisplay();
			SetGearPictureBox(bm);
			SetGearTextBox(artifact.ToString());
		}

		public static void SetGearPictureBox(Bitmap bm)
		{
			UpdatePictureBox(bm, gear_PictureBox);
		}

        public static void SetAchievement(Bitmap row, AchievementObservation observation)
        {
            SetGearPictureBox(row);
            string state;
            if (!string.IsNullOrEmpty(observation.Error)) state = "Needs review: " + observation.Error;
            else if (observation.Claimable) state = "Completed (reward unclaimed)";
            else if (observation.CompletedText) state = "Completed";
            else state = "In progress: " + observation.Progress;
            string text = observation.Title + Environment.NewLine + observation.Description + Environment.NewLine +
                "Status: " + state + Environment.NewLine + "Primogems: " + (observation.Reward?.ToString() ?? "?");
            if (!string.IsNullOrEmpty(observation.CompletionDate)) text += Environment.NewLine + "Completed on: " + observation.CompletionDate;
            if (string.IsNullOrEmpty(observation.Error)) text += Environment.NewLine + "Completed tiers: " + observation.CompletedIds.Count;
            gear_TextBox.Invoke((MethodInvoker)delegate { gear_TextBox.Text = text; });
        }

		public static void SetGearTextBox(string text)
		{
			UpdateTextBox(text, gear_TextBox);
		}

		internal static void SetMainCharacterName(string text)
		{
			UpdateTextBox($"Traveler name: {text}", character_TextBox);
		}

		public static void SetCharacter_NameAndElement(Bitmap bm, string name, string element)
		{
			UpdateElements(bm, $"Name: {name}\nElement: {element}", cName_PictureBox, character_TextBox);
		}

		public static void SetCharacter_Level(Bitmap bm, int level, int maxLevel)
		{
			UpdateElements(bm, $"Level: {level} / {maxLevel}", cLevel_PictureBox, character_TextBox);
		}

		public static void SetCharacter_Constellation(int level)
		{
			UpdateTextBox($"Constellation: {level}", character_TextBox);
		}

		internal static void SetMaterial(Bitmap nameplate, Bitmap quantity, string name, int count)
		{
			UpdateElements(nameplate, $"Name: {name}", cName_PictureBox, character_TextBox);
			UpdateElements(quantity, $"Count: {count}", cLevel_PictureBox, character_TextBox);
		}

		public static void SetMora(Bitmap mora, int count)
		{
			UpdateElements(mora, $"Mora: {count}", navigation_PictureBox, character_TextBox);
		}


		public static void SetCharacter_Talent(Bitmap bm, string text, int i)
		{
			if (i > -1 && i < 3)
			{
				UpdatePictureBox(bm, cTalent_PictureBoxes[i]);
				UpdateTextBox($"Talent {i + 1}: {text}", character_TextBox);
			}
		}

		public static void SetWeapon_Max(int value)
		{
			UpdateLabel(value.ToString(), weaponMax_Label);
			Logger.Info("Parsed {value} weapons to scan", value);
		}

		public static void SetArtifact_Max(int value)
		{
			UpdateLabel(value.ToString(), artifactMax_Label);
			Logger.Info("Parsed {value} artifacts to scan", value);
		}

		public static void IncrementArtifactCount()
		{
            IncrementCount(artifactCount_Label);
		}

		public static void IncrementWeaponCount()
		{
            IncrementCount(weaponCount_Label);
		}

		public static void IncrementCharacterCount()
		{
            IncrementCount(characterCount_Label);
		}

        private static void IncrementCount(Label label)
        {
            programStatus_Label.Invoke((MethodInvoker)delegate
            {
                label.Text = (int.Parse(label.Text) + 1).ToString();
            });
        }

        public static void BeginScanPhase(ScanPhase phase, int? total = null, string detail = null)
        {
            PublishScanProgress(new ScanProgressUpdate(phase, 0, total, detail, true, false));
        }

        public static void ReportScanProgress(ScanPhase phase, int current, int? total = null, string detail = null,
            bool totalIsProvisional = false)
        {
            PublishScanProgress(new ScanProgressUpdate(phase, current, total, detail, false, false, totalIsProvisional));
        }

        public static void FinishScanPhase(ScanPhase phase, int current, int? total = null, string detail = null)
        {
            PublishScanProgress(new ScanProgressUpdate(phase, current, total ?? current, detail, false, true));
        }

        private static void PublishScanProgress(ScanProgressUpdate progress)
        {
            if (scanProgress == null) return;
            programStatus_Label.Invoke((MethodInvoker)delegate { scanProgress(progress); });
        }

		public static void SetProgramStatus(string status, bool ok = true)
		{
			MethodInvoker statusAction = delegate
			{
				programStatus_Label.Text = status;
				programStatus_Label.ForeColor = ok ? Color.Green : Color.Red;
				programStatus_Label.Refresh();
			};

			programStatus_Label.Invoke(statusAction);
		}

		public static void AddError(string error)
		{
			// The expandable log panel consumes NLog entries in batches. Do not
			// duplicate errors into the retired, hidden textbox on the UI thread.
			Logger.Error(error);
		}

		public static void SetNavigation_Image(Bitmap bm)
		{
			UpdatePictureBox(bm, navigation_PictureBox);
		}

		public static void ResetCharacterDisplay()
		{
			MethodInvoker nameAction = delegate { cName_PictureBox.Image = null; };
			MethodInvoker levelAction = delegate { cLevel_PictureBox.Image = null; };
			MethodInvoker talentAction_1 = delegate { cTalent_PictureBoxes[0].Image = null; };
			MethodInvoker talentAction_2 = delegate { cTalent_PictureBoxes[1].Image = null; };
			MethodInvoker talentAction_3 = delegate { cTalent_PictureBoxes[2].Image = null; };
			MethodInvoker textAction = delegate { character_TextBox.Clear(); };

			cName_PictureBox.Invoke(nameAction);
			cLevel_PictureBox.Invoke(levelAction);
			cTalent_PictureBoxes[0].Invoke(talentAction_1);
			cTalent_PictureBoxes[1].Invoke(talentAction_2);
			cTalent_PictureBoxes[2].Invoke(talentAction_3);
			character_TextBox.Invoke(textAction);
		}

		public static void ResetGearDisplay()
		{
            MethodInvoker gearAction = delegate
            {
                var previous = gear_PictureBox.Image;
                gear_PictureBox.Image = null;
                previous?.Dispose();
            };

			MethodInvoker textAction = delegate { gear_TextBox.Clear(); };

			gear_PictureBox.Invoke(gearAction);
			gear_TextBox.Invoke(textAction);
		}

		public static void ResetCounters()
		{
            programStatus_Label.Invoke((MethodInvoker)delegate
            {
                characterCount_Label.Text = "0";
                weaponCount_Label.Text = "0";
                weaponMax_Label.Text = "?";
                artifactCount_Label.Text = "0";
                artifactMax_Label.Text = "?";
            });
		}

		public static void ResetErrors()
		{
			MethodInvoker textAction = delegate { error_TextBox.Clear(); };

			error_TextBox.Invoke(textAction);
		}

		public static void ResetAll()
		{
			ResetGearDisplay();

			ResetCharacterDisplay();

			ResetCounters();

			ResetErrors();
		}
	}
}
