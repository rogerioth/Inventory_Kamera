using Accord.Imaging;
using NLog;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace InventoryKamera
{
    internal class WeaponScraper : InventoryScraper
    {
		private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

		public WeaponScraper()
        {
            inventoryPage = InventoryPage.Weapons;
            SortByLevel = Properties.Settings.Default.MinimumWeaponLevel > 1;
        }

        public void ScanWeapons(int count = 0)
        {
            // Determine maximum number of weapons to scan
            int weaponCount = count == 0 ? ScanItemCount() : count;
            int page = 0;
            var (rectangles, cols, rows) = GetPageOfItems(page);
            int fullPage = cols * rows;
            int totalRows = (int)Math.Ceiling(weaponCount / (decimal)cols);
            int cardsQueued = 0;
            int rowsQueued = 0;
            int offset = 0;
            UserInterface.SetWeapon_Max(weaponCount);

            // Determine Delay if delay has not been found before
            // Scraper.FindDelay(rectangles);

            StopScanning = false;

            Logger.Info("Found {0} for weapon count.", weaponCount);

            SelectSortingMethod();

            // Go through weapon list
            while (cardsQueued < weaponCount)
            {
                Logger.Debug("Scanning weapon page {0}", page);
                Logger.Debug("Located {0} possible item locations on page.", rectangles.Count);

                int cardsRemaining = weaponCount - cardsQueued;
                // Go through each "page" of items and queue. In the event that not a full page of
                // items are scrolled to, offset the index of rectangle to start clicking from
                for (int i = cardsRemaining < fullPage ? (rows - (totalRows - rowsQueued)) * cols : 0; i < rectangles.Count; i++)
                {
                    Rectangle item = rectangles[i];
                    Navigation.SetCursor(item.Center().X, item.Center().Y + offset);
                    Navigation.Click();
                    Navigation.SystemWait(Navigation.Speed.SelectNextInventoryItem);

                    // Queue card for scanning
                    QueueScan(cardsQueued);
                    cardsQueued++;
                    if (cardsQueued >= weaponCount || this.StopScanning)
                    {
                        if (StopScanning) Logger.Info("Stopping weapon scan based on filtering");
                        else Logger.Info("Stopping weapon scan based on scans queued ({0} of {1})", cardsQueued, weaponCount);
                        return;
                    }
                }
                Logger.Debug("Finished queuing page of weapons. Scrolling...");

                rowsQueued += rows;

                // Page done, now scroll
                // If the number of remaining scans is shorter than a full page then
                // only scroll a few rows
                if (totalRows - rowsQueued <= rows)
                {
                    if (Navigation.GetAspectRatio() == new Size(8, 5))
                    {
                        offset = 35; // Lazy fix
                    }
                    for (int i = 0; i < 10 * (totalRows - rowsQueued) - 1; i++)
                    {
                        Navigation.sim.Mouse.VerticalScroll(-1);
                        Navigation.Wait(1);
                    }
                    Navigation.SystemWait(Navigation.Speed.Fast);
                }
                else
                {
                    // Scroll back one to keep it from getting too crazy
                    if (rowsQueued % 15 == 0)
                    {
                        Navigation.sim.Mouse.VerticalScroll(1);
                    }
                    for (int i = 0; i < 10 * rows - 1; i++)
                    {
                        Navigation.sim.Mouse.VerticalScroll(-1);
                        Navigation.Wait(1);
                    }
                    Navigation.SystemWait(Navigation.Speed.Fast);
                }
                ++page;
                (rectangles, cols, rows) = GetPageOfItems(page, acceptLess: totalRows - rowsQueued <= fullPage);
            }

            void SelectLevelSorting()
            {
                Navigation.SetCursor(
                    X: (int)(230 / 1280.0 * Navigation.GetWidth()),
                    Y: (int)(680 / 720.0 * Navigation.GetHeight()));
                Navigation.Click();
                Navigation.Wait();
                Navigation.SetCursor(
                    X: (int)(250 / 1280.0 * Navigation.GetWidth()),
                    Y: (int)(575 / 720.0 * Navigation.GetHeight()));
                Navigation.Click();
                Navigation.Wait();
            }

            void SelectQualitySorting()
            {
                Navigation.SetCursor(
                                        X: (int)(230 / 1280.0 * Navigation.GetWidth()),
                                        Y: (int)(680 / 720.0 * Navigation.GetHeight()));
                Navigation.Click();
                Navigation.Wait();
                Navigation.SetCursor(
                    X: (int)(250 / 1280.0 * Navigation.GetWidth()),
                    Y: (int)(615 / 720.0 * Navigation.GetHeight()));
                Navigation.Click();
                Navigation.Wait();
            }

            void SelectSortingMethod()
            {
                if (SortByLevel)
                {
                    Logger.Debug("Sorting by level to optimize scan time.");
                    // Check if sorted by level
                    if (CurrentSortingMethod() != "level")
                    {
                        Logger.Debug("Not already sorting by level...");
                        // If not, sort by level
                        SelectLevelSorting();
                    }
                    Logger.Debug("Inventory is sorted by level.");
                }
                else
                {
                    Logger.Debug("Sorting by quality to optimize scan time.");
                    // Check if sorted by quality
                    if (CurrentSortingMethod() != "quality")
                    {
                        Logger.Debug("Not already sorting by quality...");
                        // If not, sort by quality
                        SelectQualitySorting();
                    }
                    Logger.Debug("Inventory is sorted by quality");
                }
            }
        }

        private void QueueScan(int id)
        {
			var card = GetItemCard();

            Bitmap name, level, refinement, equipped, locked;
            name = GetItemNameBitmap(card);
            locked = GetLockedBitmap(card);
            equipped = GetEquippedBitmap(card);
            level = GetLevelBitmap(card);
            refinement = GetRefinementBitmap(card);

            //Navigation.DisplayBitmap(name);
            //Navigation.DisplayBitmap(locked);
            //Navigation.DisplayBitmap(equipped);
            //Navigation.DisplayBitmap(level);
            //Navigation.DisplayBitmap(refinement);

            // Separate to all pieces of card
            List<Bitmap> weaponImages = new List<Bitmap>
            {
                name, //0
                level,
                refinement,
                locked,
                equipped,
                card //5
            };

            bool a = false;

            bool belowRarity = GetQuality(name) < Properties.Settings.Default.MinimumWeaponRarity;
            bool belowLevel = ScanLevel(level, ref a) < Properties.Settings.Default.MinimumWeaponLevel;
            StopScanning = (SortByLevel && belowLevel) || (!SortByLevel && belowRarity);

            if (StopScanning || belowRarity || belowLevel)
            {
                weaponImages.ForEach(i => i.Dispose());
                return;
            }

            // Send images to worker queue
            InventoryKamera.workerQueue.Enqueue(new OCRImageCollection(weaponImages, "weapon", id));
        }

        Bitmap GetLevelBitmap(Bitmap card)
        {
            return GenshinProcesor.CopyBitmap(card,
                new Rectangle(
                    x: (int)(card.Width * 0.060),
                    y: (int)(card.Height * (Navigation.IsNormal ? 0.367 : 0.320)),
                    width: (int)(card.Width * 0.262),
                    height: (int)(card.Height * (Navigation.IsNormal ? 0.035 : 0.033))));
        }

        Bitmap GetRefinementBitmap(Bitmap card, double expand = 0.0)
        {
            // Baseline crop region of the refinement rank box (calibrated
            // fractions - do not change them). The optional `expand` grows the
            // region by a fraction of its own width/height on every side and is
            // used ONLY by the retry strategies in ScanRefinement: the rank box
            // border sits right against the digit, so a slightly wider crop
            // gives OCR padding without moving the baseline geometry.
            Rectangle region = new Rectangle(
                x: (int)(card.Width * (Navigation.IsNormal ? 0.058 : 0.057)),
                y: (int)(card.Height * (Navigation.IsNormal ? 0.417 : 0.364)),
                width: (int)(card.Width * (Navigation.IsNormal ? 0.074 : 0.075)),
                height: (int)(card.Height * (Navigation.IsNormal ? 0.038 : 0.034)));
            if (expand > 0.0)
                region.Inflate((int)(region.Width * expand), (int)(region.Height * expand));
            return GenshinProcesor.CopyBitmap(card, region);
        }

        public async Task<Weapon> CatalogueFromBitmapsAsync(List<Bitmap> bm, int id)
		{
			// Init Variables
			string name = null;
			int level = -1;
			bool ascended = false;
			int refinementLevel = -1;
			bool locked = false;
			string equippedCharacter = null;
			int rarity = 0;

			if (bm.Count >= 4)
			{
				int w_name = 0; int w_level = 1; int w_refinement = 2; int w_lock = 3; int w_equippedCharacter = 4; int w_card = 5;

				// Check for Rarity
				rarity = GetQuality(bm[w_name]);

				// Check for equipped color
				Color equippedColor = Color.FromArgb(255, 255, 231, 187);
				Color equippedStatus = bm[w_equippedCharacter].GetPixel(5, 5);
				bool b_equipped = GenshinProcesor.CompareColors(equippedColor, equippedStatus);

				// Check for lock color
				Color lockedColor = Color.FromArgb(255, 70, 80, 100); // Dark area around red lock
				Color lockStatus = bm[w_lock].GetPixel(5, 5);
				locked = GenshinProcesor.CompareColors(lockedColor, lockStatus);

				List<Task> tasks = new List<Task>();

				var taskName = Task.Run(() =>
				{
					name = ScanWeaponName(ScanItemName(bm[w_name]));
				});
				var taskLevel = Task.Run(() => level = ScanLevel(bm[w_level], ref ascended));
				var taskRefinement = Task.Run(() => refinementLevel = ScanRefinement(bm[w_refinement], bm.Count > w_card ? bm[w_card] : null));
				var taskEquipped = Task.Run(() => equippedCharacter = ScanEquippedCharacter(bm[w_equippedCharacter]));

				tasks.Add(taskName);
				tasks.Add(taskLevel);
				tasks.Add(taskRefinement);

				if (b_equipped)
				{
					tasks.Add(taskEquipped);
				}

				await Task.WhenAll(tasks.ToArray());

				// Refinement fallback: never drop a weapon with a valid name/level
				// just because OCR could not read the rank box. Dropped weapons
				// are the actual harm in upstream issues #482/#515/#535, and a
				// possibly-wrong refinement rank 1 on a handful of weapons is
				// strictly better than omitting the weapon from the export. So
				// default to rank 1 (the most common value), save the offending
				// crop, and warn so it can be reviewed. 1-2 star weapons have no
				// refinement UI at all; the Weapon constructor already assigns
				// them rank 1, so they skip this path.
				if (rarity > 2 && (refinementLevel < 1 || 5 < refinementLevel))
				{
					string screenshot = SaveRefinementFailureScreenshot(bm[w_refinement], id);
					Logger.Warn("Weapon #{0}: refinement OCR failed after all strategies, defaulting to refinement rank 1. Screenshot: {1}", id, screenshot);
					refinementLevel = 1;
				}
			}
			return new Weapon(name, level, ascended, refinementLevel, locked, equippedCharacter, id, rarity);
		}

        public bool IsEnhancementMaterial(Bitmap nameBitmap)
		{
			string material = ScanEnchancementOreName(nameBitmap);
			return !string.IsNullOrWhiteSpace(material) && GenshinProcesor.enhancementMaterials.Contains(material.ToLower());
		}

		public string ScanEnchancementOreName(Bitmap bm)
		{
			// Analyze
			string name = GenshinProcesor.FindClosestMaterialName(ScanItemName(bm), minConfidence: 95);

			return name;
		}

        #region Task Methods

		private string ScanWeaponName(string name)
        {
            return GenshinProcesor.FindClosestWeapon(name);
        }

        public int ScanLevel(Bitmap bm, ref bool ascended)
		{
			Bitmap n = GenshinProcesor.ConvertToGrayscale(bm);
			GenshinProcesor.SetInvert(ref n);

			string text = GenshinProcesor.AnalyzeText(n).Trim();
			n.Dispose();
			text = Regex.Replace(text, @"(?![\d/]).", string.Empty);

			if (text.Contains('/'))
			{
				string[] temp = text.Split(new[] { '/' }, 2);

				if (temp.Length == 2)
				{
					if (int.TryParse(temp[0], out int level) && int.TryParse(temp[1], out int maxLevel))
					{
						maxLevel = (int)Math.Round(maxLevel / 10.0, MidpointRounding.AwayFromZero) * 10;
						ascended = 20 <= level && level < maxLevel;
						return level;
					}
				}
			}
			return -1;
		}

		// Refinement rank OCR is the flakiest read of the weapon scan (upstream
		// issues #482/#515/#535): the rank box is tiny, its border sits right
		// against the digit, and the box turns gold at max rank, so a single pass
		// can return "" or an out-of-range number. Run a short, BOUNDED list of
		// strategies in order and stop at the first in-range (1-5) result; never
		// loop on failure (the old unbounded scale ladder wasted time and the
		// "" -> retry pattern could stall a worker). If every strategy fails,
		// return -1 and let CatalogueFromBitmapsAsync keep the weapon with
		// refinement rank 1 instead of dropping it.
		//   1. Baseline crop as-is, default OCR settings (previous behavior,
		//      succeeds for the vast majority of weapons).
		//   2. Baseline crop re-read with a digits-only whitelist: the box holds
		//      a single digit, so restricting the charset rescues noisy reads.
		//   3. Baseline crop at 2x scale with the digits-only whitelist: small
		//      windowed resolutions (e.g. 1600x900) leave too few pixels for the
		//      LSTM otherwise.
		//   4. Slightly wider crop (25% inflation, see GetRefinementBitmap) at
		//      2x scale with the digits-only whitelist: escapes the tight box
		//      border; the surrounding pixels do not change color at max rank.
		public int ScanRefinement(Bitmap image, Bitmap card = null)
		{
			int result = OcrRefinement(image, scale: 1.0, numbersOnly: false);
			if (result > 0) return result;

			result = OcrRefinement(image, scale: 1.0, numbersOnly: true);
			if (result > 0) return result;

			result = OcrRefinement(image, scale: 2.0, numbersOnly: true);
			if (result > 0) return result;

			if (card != null)
			{
				using (Bitmap wider = GetRefinementBitmap(card, expand: 0.25))
					result = OcrRefinement(wider, scale: 2.0, numbersOnly: true);
				if (result > 0) return result;
			}

			return -1;
		}

		// One bounded OCR pass over the refinement crop. Returns the parsed rank
		// (1-5) or -1 if the read was empty/unusable.
		private static int OcrRefinement(Bitmap image, double scale, bool numbersOnly)
		{
			using (Bitmap scaled = GenshinProcesor.ScaleImage(image, scale))
			{
				Bitmap processed = GenshinProcesor.ConvertToGrayscale(scaled);
				GenshinProcesor.SetInvert(ref processed);
				using (processed)
				{
					string text = GenshinProcesor.AnalyzeText(processed, numbersOnly: numbersOnly).Trim();
					text = Regex.Replace(text, @"[^\d]", string.Empty);

					if (int.TryParse(text, out int refinementLevel) && 1 <= refinementLevel && refinementLevel <= 5)
					{
						return refinementLevel;
					}

					Logger.Debug("Refinement OCR attempt (scale {0}, digits-only {1}) did not return a rank, read: '{2}'", scale, numbersOnly, text);
				}
			}
			return -1;
		}

		// Saves the refinement crop that failed OCR so the read can be reviewed
		// offline; mirrors the ./logging/weapons/weapon{id}/refinement/ layout
		// that the invalid-weapon path in data/InventoryKamera.cs writes.
		private static string SaveRefinementFailureScreenshot(Bitmap refinement, int id)
		{
			try
			{
				string path = $"./logging/weapons/weapon{id}/refinement";
				System.IO.Directory.CreateDirectory(path);
				string file = path + "/refinement_fallback.png";
				refinement.Save(file);
				return file;
			}
			catch (Exception ex)
			{
				Logger.Warn(ex, "Could not save refinement failure screenshot for weapon #{0}", id);
				return "<not saved>";
			}
		}

		public string ScanEquippedCharacter(Bitmap bm)
		{
			Bitmap n = GenshinProcesor.ConvertToGrayscale(bm);
			GenshinProcesor.SetContrast(60.0, ref n);

			string extractedString = GenshinProcesor.AnalyzeText(n);
			n.Dispose();

			if (extractedString != "")
			{
				var regexItem = new Regex("Equipped:");
				if (regexItem.IsMatch(extractedString))
				{
					var name = extractedString.Split(':')[1];

					name = Regex.Replace(name, @"[\W]", string.Empty).ToLower();
					name = GenshinProcesor.FindClosestCharacterName(name);

					return name;
				}
			}
			// artifact has no equipped character
			return null;
		}

		#endregion Task Methods
	}
}