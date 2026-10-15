using Accord.Imaging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static InventoryKamera.Artifact;

namespace InventoryKamera
{
    internal class ArtifactScraper : InventoryScraper
	{
		private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        internal Action ItemQueued { get; set; }

		// How many times an item card is captured before the artifact is skipped
		private const int MaxCardReadAttempts = 3;

		public ArtifactScraper() 
		{
			inventoryPage = InventoryPage.Artifacts;
            SortByLevel = Properties.Settings.Default.MinimumArtifactLevel > 0;
        }

        public void ScanArtifacts(int count = 0)
		{
			// Get Max artifacts from screen
			int artifactCount = count == 0 ? ScanItemCount() : count;
			int page = 1;

			// Clear leftover filters before the first page is captured, otherwise the
			// filter UI can still be on screen when the page is screenshotted and item
			// detection fails.
			// (Ported from taiwenlee/Inventory_Kamera (MIT): 63ba93ff)
			ClearFilters();

			var (rectangles, cols, rows) = GetPageOfItems(page);
			int fullPage = cols * rows;
			int totalRows = (int)Math.Ceiling(artifactCount / (decimal)cols);
			int cardsQueued = 0;
			int rowsQueued = 0;
			UserInterface.SetArtifact_Max(artifactCount);
            UserInterface.ReportScanProgress(ScanPhase.Artifacts, 0, artifactCount);

			StopScanning = false;

			Logger.Info("    Found {0} artifacts in the inventory", artifactCount);

			// Go through artifact list
			while (cardsQueued < artifactCount)
			{
				Logger.Info("    Capturing artifact inventory page {0}...", page);
				Logger.Debug("Located {0} possible item locations on page.", rectangles.Count);

				int cardsRemaining = artifactCount - cardsQueued;
				// Go through each "page" of items and queue. In the event that not a full page of
				// items are scrolled to, offset the index of rectangle to start clicking from
				for (int i = cardsRemaining < fullPage ? ( rows - ( totalRows - rowsQueued ) ) * cols : 0; i < rectangles.Count; i++)
				{
					Rectangle item = rectangles[i];
					Navigation.SetCursor(item.Center().X, item.Center().Y);
					Navigation.Click();
					Navigation.SystemWait(Navigation.Speed.SelectNextInventoryItem);

					// Queue card for scanning. If the card could not be read, re-capture
					// it a bounded number of times before skipping the item so that a
					// single unreadable (e.g. sanctified) artifact can never stop or
					// stall the rest of the scan.
					int attempts = 1;
					while (!QueueScan(cardsQueued))
					{
						if (attempts >= MaxCardReadAttempts)
						{
							Logger.Error("    [FAILURE] Artifact #{0}: card could not be read after {1} attempts. Skipping artifact.", cardsQueued, attempts);
							break;
						}
						attempts++;
						Navigation.SetCursor(item.Center().X, item.Center().Y);
						Navigation.Click();
						Navigation.SystemWait(Navigation.Speed.SelectNextInventoryItem);
					}
					cardsQueued++;
                    UserInterface.ReportScanProgress(ScanPhase.Artifacts, cardsQueued, artifactCount);
					if (cardsQueued >= artifactCount || StopScanning)
					{
						if (StopScanning)
						{
							Logger.Info("    Artifact capture finished at the configured filters");
						}
						else
						{
							Logger.Info("    Artifact capture finished: {0} of {1} cards queued", cardsQueued, artifactCount);
						}
                        UserInterface.FinishScanPhase(ScanPhase.Artifacts, cardsQueued, artifactCount,
                            StopScanning ? "Stopped at configured filters" : null);
						return;
					}
				}

				Logger.Debug("Finished queuing page of artifacts. Scrolling...");

				rowsQueued += rows;

				// Page done, now scroll
				// If the number of remaining scans is shorter than a full page then
				// only scroll a few rows
				if (totalRows - rowsQueued <= rows)
				{
					for (int i = 0; i < 10 * ( totalRows - rowsQueued ) - 1; i++)
					{
						Navigation.sim.Mouse.VerticalScroll(-1);
						Navigation.Wait(1);
					}
					Navigation.SystemWait(Navigation.Speed.Fast);
				}
				else
				{
                    
                    for (int i = 0; i < 10 * rows - 1; i++)
					{
						Navigation.sim.Mouse.VerticalScroll(-1);
						Navigation.Wait(1);
					}
                    // Scroll back one to keep it from getting too crazy.
                    // On 16:10 the artifact grid pages differently, so roll back
                    // more often to keep the scroll position from drifting.
                    // (Ported from taiwenlee/Inventory_Kamera (MIT): a5d1c776)
                    var rollbackPeriod = Navigation.IsNormal ? 12 : 3;
                    if (page % rollbackPeriod == 0)
                    {
						Logger.Debug("Scrolled back one");
						Navigation.sim.Mouse.VerticalScroll(1);
						Navigation.Wait(1);
                    }
                    Navigation.SystemWait(Navigation.Speed.Fast);
				}
				++page;
				(rectangles, cols, rows) = GetPageOfItems(page, acceptLess: totalRows - rowsQueued <= fullPage);
			}
            UserInterface.FinishScanPhase(ScanPhase.Artifacts, cardsQueued, artifactCount);
		}

        private static void ClearFilters()
        {

            using (var x = Navigation.CaptureRegion(
                x: (int)((Navigation.IsNormal ? 0.0750 : 0.0757) * Navigation.GetWidth()),
                y: (int)((Navigation.IsNormal ? 0.8522 : 0.8678) * Navigation.GetHeight()),
                width: (int)((Navigation.IsNormal ? 0.2244 : 0.2236) * Navigation.GetWidth()),
                height: (int)((Navigation.IsNormal ? 0.0422 : 0.0367) * Navigation.GetHeight())))
            {
				// AnalyzeText always returns a non-null string, and the Trim() above
				// would already have thrown for a null, so the null check is dead.
				var t = GenshinProcesor.AnalyzeText(x).Trim().ToLower();
				if (t.Contains("filter"))
				{
					Navigation.ClearArtifactFilters();
				}
				// Give the filter UI time to close before the next capture.
				// (Ported from taiwenlee/Inventory_Kamera (MIT): 63ba93ff)
				Navigation.SystemWait(Navigation.Speed.Slow);
            }
        }

        public bool QueueScan(int id)
		{
			var card = GetItemCard();
            Bitmap name, gearSlot, mainStat, subStats, level, equipped, locked, sanctify;
			bool isSanctified;

			name = GetItemNameBitmap(card);
			equipped = GetEquippedBitmap(card);
			gearSlot = GetGearSlotBitmap(card);
			mainStat = GetMainStatBitmap(card);

			// Artifacts crafted with Sanctifying Elixir show a small purple banner on
			// the card that shifts the level, substat and lock regions downward.
			// Detect the banner so those regions can be read from their shifted
			// positions instead of reading the banner itself (unreadable level and
			// substats).
			// (Ported from taiwenlee/Inventory_Kamera (MIT): 5e4687c6)
			sanctify = GetSanctifyBitmap(card);
			Color sanctifiedColor = Color.FromArgb(255, 220, 192, 255); // Light purple sanctified banner
			Color sanctifyStatus = sanctify.GetPixel(10, 10);
			isSanctified = GenshinProcesor.CompareColors(sanctifiedColor, sanctifyStatus);

			// These regions move down when the sanctified banner is present
			locked = GetLockedBitmap(card, isSanctified);
			level = GetLevelBitmap(card, isSanctified);
			subStats = GetSubstatsBitmap(card, isSanctified);


			// Separate to all pieces of artifact and add to pics
			List<Bitmap> artifactImages = new List<Bitmap>
			{
				name, //0
				gearSlot,
				mainStat,
				level,
				subStats,
				equipped, //5
				locked,
				sanctify,
				card
			};

            bool belowRarity = GetRarity(name) < Properties.Settings.Default.MinimumArtifactRarity;
            int readLevel = ScanArtifactLevel(level);
			// An unreadable level (-1) means the card could not be read. It must not be
			// treated as "below the minimum level": doing so stopped a level-filtered
			// scan dead on the first unreadable (e.g. sanctified) card. The caller
			// re-captures the card a bounded number of times and skips the artifact if
			// it stays unreadable.
			bool belowLevel = readLevel >= 0 && readLevel < Properties.Settings.Default.MinimumArtifactLevel;
			bool unreadable = readLevel < 0;
            StopScanning = (SortByLevel && belowLevel) || (!SortByLevel && belowRarity);

			if (StopScanning || belowRarity || belowLevel)
            {
				artifactImages.ForEach(i => i.Dispose());
				return true;
            }

			if (unreadable)
			{
				// Leave the last capture behind for diagnostics before asking the
				// caller to retry.
				SaveInventoryBitmap(card, $"unreadable_artifact{id}.png");
				artifactImages.ForEach(i => i.Dispose());
				return false;
			}

            // Send images to Worker Queue
            InventoryKamera.workerQueue.Enqueue(new OcrImageCollection(artifactImages, "artifact", id));
            ItemQueued?.Invoke();
			return true;
		}

        // Shared crop math for card regions whose y position (and sanctified
        // shift) differ between 16:9 and 16:10. All fractions are relative to
        // the captured card; values are the field-tested ones per aspect ratio.
        private static Bitmap CropCardRegion(Bitmap card, bool isSanctified,
            double x, double width,
            double baseYNormal, double baseY16x10,
            double shiftNormal, double shift16x10,
            double heightNormal, double height16x10)
        {
            double baseY = Navigation.IsNormal ? baseYNormal : baseY16x10;
            double sanctifiedShift = Navigation.IsNormal ? shiftNormal : shift16x10;
            double yShift = isSanctified ? sanctifiedShift : 0.0;

            return GenshinProcesor.CopyBitmap(card, new Rectangle(
                x: (int)(card.Width * x),
                y: (int)(card.Height * (baseY + yShift)),
                width: (int)(card.Width * width),
                height: (int)(card.Height * (Navigation.IsNormal ? heightNormal : height16x10))));
        }

        private static Bitmap GetSubstatsBitmap(Bitmap card, bool isSanctified = false)
        {
			// Ported from taiwenlee/Inventory_Kamera (MIT): 5e4687c6, fea6e1bb.
			// The region is widened and extended downward so the "unactivated"
			// substat row introduced in Genshin 6.0 is captured by the OCR.
			// (Ported from taiwenlee/Inventory_Kamera (MIT): b8ee5b1d)
			return CropCardRegion(card, isSanctified,
				x: 0.0605, width: 0.8297,
				baseYNormal: 0.4216, baseY16x10: 0.3682,
				shiftNormal: 0.0520, shift16x10: 0.0471,
				heightNormal: 0.2301, height16x10: 0.1573);
        }

        private static Bitmap GetMainStatBitmap(Bitmap card)
        {
			// Height is identical for 16:9 and 16:10 today; re-split it if
			// real 16:10 values ever diverge.
			return GenshinProcesor.CopyBitmap(card, new Rectangle(
				x: (int)(card.Width * 0.0405),
				y: (int)(card.Height * (Navigation.IsNormal ? 0.1722 : 0.1477)),
				width: (int)(card.Width * 0.4555),
				height: (int)(card.Height * 0.0416)));
        }

        private static Bitmap GetLevelBitmap(Bitmap card, bool isSanctified = false)
        {
			// Ported from taiwenlee/Inventory_Kamera (MIT): 5e4687c6, fea6e1bb.
			return CropCardRegion(card, isSanctified,
				x: 0.0506, width: 0.1417,
				baseYNormal: 0.3634, baseY16x10: 0.3197,
				shiftNormal: 0.0520, shift16x10: 0.0465,
				heightNormal: 0.0416, height16x10: 0.0347);
        }

        private static Bitmap GetSanctifyBitmap(Bitmap card)
        {
			// Ported from taiwenlee/Inventory_Kamera (MIT): 5e4687c6, fea6e1bb.
			return GenshinProcesor.CopyBitmap(card, new Rectangle(
				x: (int)(card.Width * 0.0),
				y: (int)(card.Height * (Navigation.IsNormal ? 0.3333 : 0.2941)),
				width: (int)(card.Width * 0.0606),
				height: (int)(card.Height * (Navigation.IsNormal ? 0.0526 : 0.0470))));
        }

        private static Bitmap GetGearSlotBitmap(Bitmap card)
        {
            return GenshinProcesor.CopyBitmap(card, new Rectangle(
                x: (int)(card.Width * 0.0405),
                y: (int)(card.Height * (Navigation.IsNormal ? 0.07720 : 0.0663)),
                width: (int)(card.Width * 0.4757),
                height: (int)(card.Height * (Navigation.IsNormal ? 0.0475 : 0.0809))));
        }

        public static async Task<Artifact> CatalogueFromBitmapsAsync(List<Bitmap> bm, int id)
		{
			Logger.Info("Starting scan of artifact #{0}\n    Scanning artifact #{0}'s set, slot, rarity and level...\n    Scanning artifact #{0}'s main stat and substats...\n    Scanning artifact #{0}'s lock and equipped character...", id);
			// Init Variables
			string gearSlot = null;
			string mainStat = null;
			string setName = null;
			string equippedCharacter = null;
			List<SubStat> subStats = new List<SubStat>();
			// (Ported from taiwenlee/Inventory_Kamera (MIT): b8ee5b1d)
			List<SubStat> unactivatedSubStats = new List<SubStat>();
			int rarity = 0;
			int level = 0;
			bool _lock = false;

			if (bm.Count >= 6)
			{
				int a_name = 0; int a_gearSlot = 1; int a_mainStat = 2; int a_level = 3; int a_subStats = 4; int a_equippedCharacter = 5; int a_lock = 6; 
				// Get Rarity
				rarity = GetRarity(bm[a_name]);

				// Check for equipped color
				Color equippedColor = Color.FromArgb(255, 255, 231, 187);
				Color equippedStatus = bm[a_equippedCharacter].GetPixel(5, 5);
				bool b_equipped = GenshinProcesor.CompareColors(equippedColor, equippedStatus);

				// Check for lock color
				Color lockedColor = Color.FromArgb(255, 70, 80, 100); // Dark area around red lock
				Color lockStatus = bm[a_lock].GetPixel(10, 10);
				_lock = GenshinProcesor.CompareColors(lockedColor, lockStatus);

				// Improved Scanning using multi threading
				List<Task> tasks = new List<Task>();

				var taskGear  = Task.Run(() => gearSlot = ScanArtifactGearSlot(bm[a_gearSlot]));
				var taskMain  = taskGear.ContinueWith( (antecedent) => mainStat = ScanArtifactMainStat(bm[a_mainStat], antecedent.Result));
				var taskLevel = Task.Run(() => level = ScanArtifactLevel(bm[a_level]));
				var taskSubs  = Task.Run(() => (subStats, unactivatedSubStats) = ScanArtifactSubStats(bm[a_subStats]));
				var taskEquip = Task.Run(() => equippedCharacter = ScanArtifactEquippedCharacter(bm[a_equippedCharacter]));
				var taskName = Task.Run(() => setName = ScanArtifactSet(bm[a_name]));

				tasks.Add(taskGear);
				tasks.Add(taskMain);
				tasks.Add(taskLevel);
				tasks.Add(taskSubs);
				tasks.Add(taskName);
				if (b_equipped)
				{
					tasks.Add(taskEquip);
				}

				await Task.WhenAll(tasks.ToArray());
			}
			return new Artifact(setName, rarity, level, gearSlot, mainStat, subStats, unactivatedSubStats, equippedCharacter, id, _lock);
		}

		private static int GetRarity(Bitmap bm)
		{
			var averageColor = new ImageStatistics(bm);

			Color fiveStar = Color.FromArgb(255, 188, 105, 50);
			Color fourStar = Color.FromArgb(255, 161, 86, 224);
			Color threeStar = Color.FromArgb(255, 81, 127, 203);
			Color twoStar = Color.FromArgb(255, 42, 143, 114);
			Color oneStar = Color.FromArgb(255, 114, 119, 138);

			var colors = new List<Color> { Color.Black, oneStar, twoStar, threeStar, fourStar, fiveStar };

			var c = GenshinProcesor.ClosestColor(colors, averageColor);

			return colors.IndexOf(c);
		}

		public static bool IsEnhancementMaterial(Bitmap card)
		{
			Rect reference = Navigation.GetAspectRatio() == new Size(16, 9) ?
				new Rect(new Rectangle(862, 80, 327, 560)) : (Rect)new Rectangle(862, 80, 328, 640);
			Bitmap nameBitmap = card.Clone(new Rect(
				Left: 0,
				Top: 0,
				Right: card.Width,
				Bottom: (int)( 38.0 / reference.Height * card.Height )), card.PixelFormat);
			string material = ScanEnhancementMaterialName(nameBitmap);
			return !string.IsNullOrWhiteSpace(material) && GenshinProcesor.enhancementMaterials.Contains(material.ToLower());
		}

		private static string ScanEnhancementMaterialName(Bitmap bm)
		{
			GenshinProcesor.SetGamma(0.2, 0.2, 0.2, ref bm);
			Bitmap n = GenshinProcesor.ConvertToGrayscale(bm);
			GenshinProcesor.SetInvert(ref n);

			// Analyze
			string name = Regex.Replace(GenshinProcesor.AnalyzeText(n).ToLower(), @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
			name = GenshinProcesor.FindClosestMaterialName(name);
			n.Dispose();

			return name;
		}

		#region Task Methods

		private static string ScanArtifactGearSlot(Bitmap bm)
		{
			// Process Img
			Bitmap n = GenshinProcesor.ConvertToGrayscale(bm);
			GenshinProcesor.SetContrast(80.0, ref n);
			GenshinProcesor.SetInvert(ref n);

			string gearSlot = GenshinProcesor.AnalyzeText(n).Trim().ToLower();
			gearSlot = Regex.Replace(gearSlot, @"[\W_]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
			gearSlot = GenshinProcesor.FindClosestGearSlot(gearSlot);
			n.Dispose();
			return gearSlot;
		}

		private static string ScanArtifactMainStat(Bitmap bm, string gearSlot)
		{
			switch (gearSlot)
			{
				// Flower of Life. Flat HP
				case "flower":
					return GenshinProcesor.Stats["hp"];

				// Plume of Death. Flat ATK
				case "plume":
					return GenshinProcesor.Stats["atk"];

				// Otherwise it's either sands, goblet or circlet.
				default:
					Bitmap copy = (Bitmap)bm.Clone();
					GenshinProcesor.SetContrast(100.0, ref copy);
					Bitmap n = GenshinProcesor.ConvertToGrayscale(copy);
					
					GenshinProcesor.SetThreshold(135, ref n);
					GenshinProcesor.SetInvert(ref n);

					// Get Main Stat
					string mainStat = GenshinProcesor.AnalyzeText(n).ToLower().Trim();
					

					// Remove anything not a-z as well as removes spaces/underscores
					mainStat = Regex.Replace(mainStat, @"[\W_0-9]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);

					mainStat = GenshinProcesor.FindClosestStat(mainStat, 80);

					if (mainStat == "def" || mainStat == "atk" || mainStat == "hp")
					{
						mainStat += "_";
					}
					n.Dispose();
					copy.Dispose();
					return mainStat;
			}
		}

		private static int ScanArtifactLevel(Bitmap bm)
		{
			// Process Img
			Bitmap n = GenshinProcesor.ConvertToGrayscale(bm);
			GenshinProcesor.SetContrast(80.0, ref n);
			GenshinProcesor.SetInvert(ref n);

			// numbersOnly = true => seems to interpret the '+' as a '4'
			string text = GenshinProcesor.AnalyzeText(n, Tesseract.PageSegMode.SingleWord).Trim().ToLower();
			n.Dispose();

			// Get rid of all non digits
			text = Regex.Replace(text, @"[\D]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);

			return int.TryParse(text, out int level) ? level : -1;
		}

		// Returns the parsed active substats and, separately, the "unactivated"
		// substat (Genshin 6.0) so it can be exported without corrupting the
		// regular substat list.
		// (Ported from taiwenlee/Inventory_Kamera (MIT): b8ee5b1d)
		private static (List<SubStat> active, List<SubStat> unactivated) ScanArtifactSubStats(Bitmap artifactImage)
        {
            Bitmap bm = (Bitmap)artifactImage.Clone();
			// Assigned below from the OCR text before first use.
			List<string> lines;
			List<SubStat> substats = new List<SubStat>();
			// (Ported from taiwenlee/Inventory_Kamera (MIT): b8ee5b1d)
			List<SubStat> unactivated = new List<SubStat>();
			string text;
            GenshinProcesor.SetBrightness(-30, ref bm);
            GenshinProcesor.SetContrast(85, ref bm);
			bool hasUnactivated = false;
			using (var n = GenshinProcesor.ConvertToGrayscale(bm))
			{
				text = GenshinProcesor.AnalyzeText(n, Tesseract.PageSegMode.Auto).ToLower();
			}

			// The unactivated row is rendered with an "(unactivated)" label. The
			// parenthesized literal from the reference is matched without the
			// parentheses here: OCR can drop punctuation, and no regular substat
			// line ever contains the word.
			// (Ported from taiwenlee/Inventory_Kamera (MIT): b8ee5b1d)
			if(text.Contains("unactivated"))
			{
				hasUnactivated = true;
				Logger.Debug("Artifact has an unactivated substat");
			}

            lines = new List<string>(text.Split('\n'));
            lines.RemoveAll(line => string.IsNullOrWhiteSpace(line));

			// Only cut the text once an actual header line is found. Trimming on any
			// ":" made OCR noise containing a colon drop the substats read after it.
			// (Ported from taiwenlee/Inventory_Kamera (MIT): 63ba93ff)
			var index = lines.FindIndex(line =>
				Regex.IsMatch(line, @"(piece|set|2-)", RegexOptions.None, RegexTimeouts.RegexTimeout) ||
				Regex.IsMatch(line.Trim(), @"^[A-Za-z\s]+:$", RegexOptions.None, RegexTimeouts.RegexTimeout)
			);
            if (index >= 0)
            {
                lines.RemoveRange(index, lines.Count - index);
            }

            bm.Dispose();
            for (int i = 0; i < lines.Count; i++)
            {
                var line = Regex.Replace(lines[i], @"(?:^[^a-zA-Z]*)", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout).Replace(" ", string.Empty);

				if (line.Any(char.IsDigit))
				{
					Logger.Debug("Parsing artifact substat: {0}", line);

					SubStat substat = new SubStat();
					Regex re = new Regex(@"^(.*?)(\d+.*)", RegexOptions.None, RegexTimeouts.RegexTimeout);
					var result = re.Match(line);
					var stat = Regex.Replace(result.Groups[1].Value, @"[^\w]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
					var value = result.Groups[2].Value;

					string name = line.Contains("%") ? stat + "%" : stat;

					substat.Stat = GenshinProcesor.FindClosestStat(name, 80) ?? "";

					// Remove any non digits.
					value = Regex.Replace(value, @"[^0-9]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);

					// Try to parse number
					if (!decimal.TryParse(value, out decimal parsedValue))
					{
						Logger.Debug("Failed to parse stat value from: {0}", line);
						parsedValue = -1;
					}
					substat.Value = parsedValue;

					if (substat.Value != -1 && substat.Stat.Contains("_"))
					{
						substat.Value /= 10;
					}

					if (string.IsNullOrWhiteSpace(substat.Stat) || substat.Value == -1)
					{
						Logger.Debug("Failed to parse stat from: {0}", line);
					}

					substats.Insert(i, substat);
				}
            }

			// (Ported from taiwenlee/Inventory_Kamera (MIT): 63ba93ff)
			if (substats.Count == 0)
			{
				Logger.Debug("Failed to obtain substats");
			}

			// If there is an unactivated substat, move the last parsed substat to
			// the unactivated list: the unactivated row is rendered below the
			// regular substats, so it is always the last line of the block.
			// (Ported from taiwenlee/Inventory_Kamera (MIT): b8ee5b1d, a7bf8a88)
			if(hasUnactivated && substats.Count > 0)
			{
				SubStat lastSubstat = substats[substats.Count - 1];
				unactivated.Insert(0, lastSubstat);
				substats.Remove(lastSubstat);
			}

            return (substats, unactivated);
        }

        private static string ScanArtifactEquippedCharacter(Bitmap bm)
		{
			Bitmap n = GenshinProcesor.ConvertToGrayscale(bm);
			GenshinProcesor.SetContrast(60.0, ref n);

			string equippedCharacter = GenshinProcesor.AnalyzeText(n).ToLower();
			n.Dispose();

			if (equippedCharacter != "" && equippedCharacter.Contains("equipped") && equippedCharacter.Contains(":"))
			{
				equippedCharacter = Regex.Replace(equippedCharacter.Split(':')[1], @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
				equippedCharacter = GenshinProcesor.FindClosestCharacterName(equippedCharacter);

				return equippedCharacter;
			}
			// artifact has no equipped character
			return null;
		}

		private static string ScanArtifactSet(Bitmap itemName)
        {
            GenshinProcesor.SetGamma(0.2, 0.2, 0.2, ref itemName);
            Bitmap grayscale = GenshinProcesor.ConvertToGrayscale(itemName);
            GenshinProcesor.SetInvert(ref grayscale);

            // Analyze
            using (Bitmap padded = new Bitmap((int)(grayscale.Width + grayscale.Width * .1), grayscale.Height + (int)(grayscale.Height * .5)))
            {
                using (Graphics g = Graphics.FromImage(padded))
                {
                    g.Clear(Color.White);
                    g.DrawImage(grayscale, (padded.Width - grayscale.Width) / 2, (padded.Height - grayscale.Height) / 2);

                    var scannedText = GenshinProcesor.AnalyzeText(grayscale, Tesseract.PageSegMode.Auto).ToLower().Replace("\n", " ");
                    string text = Regex.Replace(scannedText, @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
                    text = GenshinProcesor.FindClosestArtifactSetFromArtifactName(text);

					grayscale.Dispose();

					return text;
                }
            }
        }

        #endregion Task Methods
    }
}
