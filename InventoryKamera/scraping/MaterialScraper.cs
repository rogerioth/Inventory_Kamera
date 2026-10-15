using Accord.Imaging;
using Accord.Imaging.Filters;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Text.RegularExpressions;

namespace InventoryKamera
{
    [Serializable]
	public struct Material : ISerializable
	{
		public string name;
		public int count;

		public Material(string _name, int _count)
		{
			name = _name;
			count = _count;
		}

		public void GetObjectData(SerializationInfo info, StreamingContext context) => info.AddValue(name, count);

		// Serialization constructor: restores the single name→count entry that
		// GetObjectData emits, so previously serialized materials round-trip.
		private Material(SerializationInfo info, StreamingContext context)
		{
			var entry = info.GetEnumerator();
			entry.MoveNext();
			name = entry.Name;
			count = (int)entry.Value;
		}

		public override int GetHashCode()
		{
			return name.GetHashCode();
		}

		public override bool Equals(object obj)
		{
			return obj is Material material && name == material.name;
		}
	}

	internal class MaterialScraper : InventoryScraper
	{
		private static NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();


		public MaterialScraper()
		{
			inventoryPage = InventoryPage.CharacterDevelopmentItems;
		}

		public MaterialScraper(InventoryPage section) : base()
		{
			inventoryPage = section;
		}

		internal void SetInventoryPage(InventoryPage page)
		{
			if (materialPages.Contains(page)) inventoryPage = page;
		}

		public void Scan_Materials(ref Inventory inventory)
		{
            var phase = inventoryPage == InventoryPage.CharacterDevelopmentItems
                ? ScanPhase.CharacterDevelopmentItems : ScanPhase.Materials;
            int recorded = 0;
            UserInterface.ReportScanProgress(phase, recorded);
            // Visiting an item and exporting its quantity are separate: an unread
            // quantity must not become a fabricated zero or prevent the end check.
            var visitedNames = new HashSet<string>(inventory.Materials.Select(m => m.name));
			if (!inventory.Materials.Contains(new Material("Mora", 0)))
			{
				Logger.Info("    Scanning Mora balance...");
				inventory.Materials.Add(new Material("Mora", ScanMora()));
			}

			int scrollCount = 0;

			Material material = new Material(null, 0);
			Material previousMaterial = new Material(null, -1);

			List<Rectangle> rectangles;
			int page = 1;

			// Set when a repeat material ends the page walk; execution then falls
			// through to the final-page pass (the former `goto LastPage`).
			bool lastPageReached = false;

			// Keep scanning while not repeating any items names
			while (true)
			{
				int rows, cols;
				// Find all items on the screen
				(rectangles, cols, rows) = GetPageOfItems(page, acceptLess: true);

				// Remove last row. Sometimes the bottom of a page of items is caught which results
				// in a faded quantity that can't be parsed. Removing slightly increases the number of pages that
				// need to be scrolled but it's fine.
				var r = rectangles.Take(rectangles.Count() - cols).ToList();

				Logger.Info("    Scanning {0} inventory page {1}...", inventoryPage, page);
				Logger.Debug("Located {0} possible item locations on page.", rectangles.Count);

				foreach (var rectangle in r)
				{
					// Select Material
					Navigation.SetCursor(rectangle.Center().X, rectangle.Center().Y);
					Navigation.Click();
					Navigation.SystemWait(Navigation.Speed.SelectNextInventoryItem);

					material.name = ScanMaterialName(out Bitmap nameplate);
					material.count = 0;

					// Check if new material has been found
					if (!string.IsNullOrEmpty(material.name) && visitedNames.Contains(material.name))
					{
						Logger.Debug("Repeat material found. Scrolling until end");
						nameplate.Dispose();
						// Skip the rest of this page and the scroll to the next one;
						// the final-page pass below takes over.
						lastPageReached = true;
						break;
					}
					else
					{
						if (!string.IsNullOrEmpty(material.name))
						{
							if (!GenshinProcesor.IsValidMaterial(material.name))
							{
								// Unknown material (e.g. a newly added item that is missing from
								// the material data). Report and skip instead of aborting or
								// exporting an unrecognized name.
								Logger.Error("        [FAILURE] Material '{0}' could not be matched to the material data. Skipping material.", material.name);
							}
							else
							{
								// Scan Material Quantity
								Logger.Info("        Scanning {0}'s quantity...", material.name);
                                visitedNames.Add(material.name);
                                int? count = ScanMaterialCountWithContext(rectangle, page,
                                    r.IndexOf(rectangle) + 1, material.name, out Bitmap quantity);
                                using (quantity)
                                {
                                    if (count.HasValue)
                                    {
                                        material.count = count.Value;
                                        if (inventory.Materials.Add(material))
                                            UserInterface.ReportScanProgress(phase, ++recorded);
                                        Logger.Info("            {0}: quantity {1}", material.name, material.count);
                                        UserInterface.ResetCharacterDisplay();
                                        UserInterface.SetMaterial(nameplate, quantity, material.name, material.count);
                                    }
                                }

								previousMaterial.name = material.name;
							}
						}
					}
					nameplate.Dispose();
					Navigation.Wait(150);
				}

				// Same exit point as the former `goto LastPage`: the remaining
				// page-advance work below is skipped when a repeat was found.
				if (lastPageReached) break;

				Navigation.SetCursor(r.Last().Center().X, r.Last().Center().Y);
				Navigation.Click();
				Navigation.Wait(150);

				Logger.Debug("Finished page of materials. Scrolling...");

				// Scroll to next page
				for (int i = 0; i < rows - 1; i++)
				{
					scrollCount++;

					// scroll down
					// While-loop form of the former `for (k = 0; k < 10; k++)`: the
					// skip below bumps `k` inside the body, and the increment at the
					// loop end preserves the original 0..7,9 iteration sequence.
					int k = 0;
					while (k < 10)
					{
						Navigation.sim.Mouse.VerticalScroll(-1);
						// skip a scroll
						if (( k == 7 ) && ( ( scrollCount % 3 ) == 0 ))
						{
							k++;
							if (scrollCount % 9 == 0)
							{
								if (scrollCount == 18)
								{
									scrollCount = 0;
								}
								else
								{
									Navigation.sim.Mouse.VerticalScroll(-1);
								}
							}
						}
						Navigation.SystemWait(Navigation.Speed.InventoryScroll);
						k++;
					}
				}
				Navigation.SystemWait(Navigation.Speed.Normal);
				++page;
			}

			Logger.Info("    Scanning the final {0} inventory page...", inventoryPage);
			// scroll down as much as possible
			for (int i = 0; i < 20; i++)
			{
				Navigation.sim.Mouse.VerticalScroll(-1);
				Navigation.SystemWait(Navigation.Speed.InventoryScroll);
			}

			Navigation.Wait(500);

			(rectangles, _, _) = GetPageOfItems(page, acceptLess: true);
			bool passby = true;
			for (int i = rectangles.Count - 1; i >= 0; i--) // Click through but backwards to short-circuit after new materials
			{
				// Select Material
				Rectangle rectangle = rectangles[i];
				Navigation.SetCursor(rectangle.Center().X, rectangle.Center().Y);
				Navigation.Click();
				Navigation.SystemWait(Navigation.Speed.SelectNextInventoryItem);

				material.name = ScanMaterialName(out Bitmap nameplate);
				material.count = 0;

				bool visited = !string.IsNullOrEmpty(material.name) && visitedNames.Contains(material.name);
				if (visited && passby)
                {
                    nameplate.Dispose();
                    continue;
                }

				if (!visited)
				{
					if (!string.IsNullOrEmpty(material.name))
					{
						if (!GenshinProcesor.IsValidMaterial(material.name))
						{
							// Unknown material (e.g. a newly added item that is missing from
							// the material data). Report and skip instead of aborting or
							// exporting an unrecognized name.
							Logger.Error("        [FAILURE] Material '{0}' could not be matched to the material data. Skipping material.", material.name);
							nameplate.Dispose();
							continue;
						}

						// Scan Material Number
						Logger.Info("        Scanning {0}'s quantity...", material.name);
                        visitedNames.Add(material.name);
                        int? count = ScanMaterialCountWithContext(rectangle, page, i + 1,
                            material.name, out Bitmap quantity);
                        using (quantity)
                        {
                            if (count.HasValue)
                            {
                                material.count = count.Value;
                                if (inventory.Materials.Add(material))
                                    UserInterface.ReportScanProgress(phase, ++recorded);
                                Logger.Info("            {0}: quantity {1}", material.name, material.count);
                                UserInterface.ResetCharacterDisplay();
                                UserInterface.SetMaterial(nameplate, quantity, material.name, material.count);
                            }
                        }
						passby = false; // New material found so break on next old material
					}
				}
				else
				{
					Logger.Debug("Last material visited, {0}.", material.name);
					nameplate.Dispose();
					break;
				}
				Navigation.Wait(150);
				nameplate.Dispose();
			}
            UserInterface.FinishScanPhase(phase, recorded);
		}

		private int ScanMora()
		{
			var region = new Rectangle(
				x: (int)(125 / 1280.0 * Navigation.GetWidth()),
				y: (int)(665 / 720.0 * Navigation.GetHeight()),
				width: (int)(300 / 1280.0 * Navigation.GetWidth()),
				height: (int)(30 / 720.0 * Navigation.GetHeight()));

			if (Navigation.GetAspectRatio() == new Size(8, 5))
			{
				region.Y = (int)( 740 / 800.0 * Navigation.GetHeight() );
			}

			using (var screenshot = Navigation.CaptureRegion(region))
			{
				string mora = ParseMoraFromScreenshot(screenshot);

				if (int.TryParse(mora, out int count))
				{
					UserInterface.ResetCharacterDisplay();
					UserInterface.SetMora(screenshot, count);
					Logger.Info("        Mora: {0}", count);
                    if (Properties.Settings.Default.LogScreenshots) 
						SaveInventoryBitmap(screenshot, "mora.png");
				}
				else
				{
					UserInterface.SetNavigation_Image(screenshot);
					UserInterface.AddError("        [FAILURE] Could not read the Mora balance. Recorded quantity: 0.");
					SaveInventoryBitmap(screenshot, "mora.png");
				}
				return count;
			}
		}

		public static string ParseMoraFromScreenshot(Bitmap screenshot)
		{
			using (var gray = GenshinProcesor.ConvertToGrayscale(screenshot))
			{
				var invert = (Bitmap)gray.Clone();
				GenshinProcesor.SetInvert(ref invert);
				var input = GenshinProcesor.AnalyzeText(invert).Split(' ').ToList();
				Logger.Debug("Scanned mora input: {0}", input.ToString());
				input.RemoveAll(e => Regex.IsMatch(e.Trim(), @"[^0-9]", RegexOptions.None, RegexTimeouts.RegexTimeout) || string.IsNullOrWhiteSpace(e.Trim()));
				var mora = input.LastOrDefault();
				Logger.Debug("Parsed mora input: {0}", mora);
				return mora;
			}
		}

		public string ScanMaterialName(out Bitmap nameplate)
		{
			// Grab item name on right
			var refWidth = 1280.0;
			var refHeight = Navigation.GetAspectRatio() == new Size(16,9) ? 720.0 : 800.0;

			var width = Navigation.GetWidth();
			var height = Navigation.GetHeight();

			var reference = new Rectangle(872, 80, 327, 37);

			// Nameplate is in the same place in 16:9 and 16:10
			var region= new RECT(
				Left:   (int)( reference.Left   / refWidth  * width),
				Top:    (int)( reference.Top    / refHeight * height),
				Right:  (int)( reference.Right  / refWidth  * width),
				Bottom: (int)( reference.Bottom / refHeight * height));

			Bitmap bm = Navigation.CaptureRegion(region);
			nameplate = (Bitmap)bm.Clone();

			// Alter Image
			GenshinProcesor.SetGamma(0.2, 0.2, 0.2, ref bm);
			Bitmap n = GenshinProcesor.ConvertToGrayscale(bm);
			GenshinProcesor.SetInvert(ref n);

			string text = GenshinProcesor.AnalyzeText(n,Tesseract.PageSegMode.Auto);
			text = Regex.Replace(text, @"[\W\s]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout).ToLower();

			//UI
			n.Dispose();
			bm.Dispose();

			if (inventoryPage == InventoryPage.CharacterDevelopmentItems)
				return GenshinProcesor.FindClosestDevelopmentName(text);

			if (inventoryPage == InventoryPage.Materials)
				return GenshinProcesor.FindClosestMaterialName(text);

			return null;
		}

        // Retain the legacy caller contract, while the scanner below keeps a
        // failed read distinct from the quantity zero.
        public static int ScanMaterialCount(Rectangle rectangle, out Bitmap quantity)
        {
            quantity = Navigation.CaptureRegion(GetQuantityRegion(rectangle));
            int? count = ParseMaterialCount(quantity, out string diagnostics);
            if (!count.HasValue) Logger.Warn("Quantity OCR failed: {0}", diagnostics);
            return count ?? 0;
        }

        private static RECT GetQuantityRegion(Rectangle rectangle)
        {
            return new RECT(rectangle.X, (int)(rectangle.Y + 0.8 * rectangle.Height),
                rectangle.Right, rectangle.Bottom + 10);
        }

        private int? ScanMaterialCountWithContext(Rectangle rectangle, int page, int item,
            string name, out Bitmap quantity)
        {
            var region = GetQuantityRegion(rectangle);
            string context = $"section={inventoryPage}, page={page}, item={item}, name={name}, " +
                $"itemRect={rectangle}, quantityRect=({region.Left},{region.Top},{region.Width},{region.Height})";
            string prefix = $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{inventoryPage}_page_{page}_item_{item}_{name}";
            quantity = Navigation.CaptureRegion(region);
            int? count = ParseMaterialCount(quantity, out string diagnostics);
            bool recovered = diagnostics.Contains("focused-crop=");
            if (Properties.Settings.Default.LogScreenshots || recovered)
                SaveInventoryBitmap(quantity, $"{name}_Quantity.png");
            if (count.HasValue)
            {
                if (recovered)
                    Logger.Info("Quantity OCR recovered from focused digits crop: {0}; count={1}; evidence={2}; {3}",
                        context, count.Value, QuantityEvidencePath($"{name}_Quantity.png"), diagnostics);
                else Logger.Debug("Quantity OCR: {0}; count={1}; {2}", context, count.Value, diagnostics);
                return count;
            }

            // Only failed reads pay for a new capture and another OCR pass.
            string firstFile = prefix + "_attempt_1.png";
            SaveInventoryBitmap(quantity, firstFile);
            SaveInventoryBitmap(quantity, $"{name}_Quantity.png");
            Logger.Warn("Quantity OCR failed; retrying one fresh capture: {0}; evidence={1}; {2}",
                context, QuantityEvidencePath(firstFile), diagnostics);
            Navigation.Wait(150);
            Bitmap retry = Navigation.CaptureRegion(region);
            quantity.Dispose();
            quantity = retry;
            count = ParseMaterialCount(quantity, out diagnostics);
            string secondFile = prefix + "_attempt_2.png";
            SaveInventoryBitmap(quantity, secondFile);
            SaveInventoryBitmap(quantity, $"{name}_Quantity.png");
            if (count.HasValue)
            {
                Logger.Info("Quantity OCR recovered after capture retry: {0}; count={1}; evidence={2}; {3}",
                    context, count.Value, QuantityEvidencePath(secondFile), diagnostics);
                return count;
            }

            Logger.Error("Quantity remains unreadable; omitting item from export: {0}; evidence={1}, {2}; {3}",
                context, QuantityEvidencePath(firstFile), QuantityEvidencePath(secondFile), diagnostics);
            UserInterface.AddError($"            [FAILURE] Could not read {name}'s quantity on {inventoryPage} page {page}, item {item}. " +
                $"Item omitted from export. Evidence: {QuantityEvidencePath(secondFile)}");
            return null;
        }

        private static string QuantityEvidencePath(string filename)
        {
            return Path.GetFullPath(Path.Combine("logging", "Materials", filename));
        }

        // Pure screenshot entry point for replaying a saved quantity crop. The
        // successful exact-integer path is unchanged; extra OCR is failure-only.
        internal static int? ParseMaterialCount(Bitmap screenshot, out string diagnostics)
        {
            var reads = new List<string>();
            var votes = new Dictionary<int, int>();
            var range = new Accord.IntRange(0, 150);
            for (double scale = 1; scale <= 3; scale += 0.5)
            {
                using (Bitmap scaled = GenshinProcesor.ResizeImage(screenshot,
                    Math.Max(1, (int)(screenshot.Width * scale)), Math.Max(1, (int)(screenshot.Height * scale))))
                {
                    Bitmap copy = (Bitmap)scaled.Clone();
                    try
                    {
                        GenshinProcesor.FilterColors(ref copy, range, range, range);
                        for (int x = 0; x < copy.Width; x++)
                            for (int y = 0; y < copy.Height * 0.25; y++) copy.SetPixel(x, y, Color.White);
                        using (Bitmap gray = GenshinProcesor.ConvertToGrayscale(copy))
                        using (Bitmap inverted = new Invert().Apply(gray))
                        using (Bitmap binary = new Threshold(50).Apply(inverted))
                        {
                            string raw = ReadQuantityText(binary, Tesseract.PageSegMode.SingleLine, false,
                                $"legacy scale={scale:0.0}", reads);
                            if (TryPositiveQuantity(raw, out int exact))
                            {
                                diagnostics = string.Join("; ", reads) + $"; decision=exact:{exact}";
                                return exact;
                            }
                            // Punctuation around a digit run can be OCR noise.
                            // Letters are not converted to guessed digits.
                            if (!string.IsNullOrEmpty(raw) && !raw.Any(char.IsLetter))
                            {
                                Match digits = Regex.Match(raw, @"^[^0-9]*([0-9]+)[^0-9]*$", RegexOptions.None, RegexTimeouts.RegexTimeout);
                                if (digits.Success && TryPositiveQuantity(digits.Groups[1].Value, out int candidate))
                                    AddQuantityVote(votes, candidate);
                            }
                        }
                    }
                    finally { copy.Dispose(); }
                }
            }
            int? winner = ResolveQuantityVotes(votes);
            if (winner.HasValue)
            {
                diagnostics = string.Join("; ", reads) + $"; decision=vote:{winner.Value}; votes={FormatQuantityVotes(votes)}";
                return winner;
            }

            // The card-bottom capture can contain rarity stars and the selected
            // card's bottom border. Locate the complete digit line from its ink
            // rather than cutting at a fixed percentage of the capture height.
            Rectangle? ink = FindQuantityInkBounds(screenshot);
            reads.Add("focused-crop=" + (ink.HasValue ? ink.Value.ToString() : "no digit ink"));
            votes.Clear();
            if (ink.HasValue)
            {
                using (Bitmap digits = screenshot.Clone(ink.Value, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                using (Bitmap scaled = GenshinProcesor.ResizeImage(digits, digits.Width * 4, digits.Height * 4))
                using (Bitmap gray = GenshinProcesor.ConvertToGrayscale(scaled))
                {
                    // A clear border improves segmentation for tiny quantities.
                    using (Bitmap padded = new Bitmap(gray.Width + 24, gray.Height + 24))
                    {
                        using (Graphics graphics = Graphics.FromImage(padded))
                        {
                            graphics.Clear(Color.White);
                            graphics.DrawImageUnscaled(gray, 12, 12);
                        }
                        foreach (var mode in new[] { Tesseract.PageSegMode.SingleLine, Tesseract.PageSegMode.SingleWord })
                        {
                            string raw = ReadQuantityText(padded, mode, true, "focused grayscale " + mode, reads);
                            if (TryPositiveQuantity(raw, out int candidate)) AddQuantityVote(votes, candidate);
                            using (Bitmap paddedGray = GenshinProcesor.ConvertToGrayscale(padded))
                            using (Bitmap binary = new Threshold(160).Apply(paddedGray))
                            {
                                raw = ReadQuantityText(binary, mode, true, "focused threshold160 " + mode, reads);
                                if (TryPositiveQuantity(raw, out candidate)) AddQuantityVote(votes, candidate);
                            }
                        }
                    }
                }
            }
            winner = ResolveQuantityVotes(votes);
            diagnostics = string.Join("; ", reads) + $"; decision={(winner.HasValue ? winner.Value.ToString() : "unresolved")}; votes={FormatQuantityVotes(votes)}";
            return winner;
        }

        private static string ReadQuantityText(Bitmap image, Tesseract.PageSegMode mode, bool numbersOnly,
            string label, List<string> reads)
        {
            try
            {
                string raw = GenshinProcesor.AnalyzeText(image, mode, numbersOnly).Trim();
                reads.Add(label + " raw='" + raw.Replace("\r", "\\r").Replace("\n", "\\n") + "'");
                return raw;
            }
            catch (ThreadAbortException) { throw; }
            catch (Exception ex)
            {
                reads.Add(label + " OCR error=" + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static bool TryPositiveQuantity(string text, out int value)
        {
            value = 0;
            return !string.IsNullOrEmpty(text) && Regex.IsMatch(text, @"^[0-9]+$", RegexOptions.None, RegexTimeouts.RegexTimeout) &&
                int.TryParse(text, out value) && value > 0;
        }

        private static void AddQuantityVote(Dictionary<int, int> votes, int quantity)
        {
            votes[quantity] = votes.TryGetValue(quantity, out int count) ? count + 1 : 1;
        }

        // Return the quantity (key), never its frequency. Blank/zero reads cannot
        // outvote positive evidence, and a tie or single noisy read is unresolved.
        internal static int? ResolveQuantityVotes(Dictionary<int, int> votes)
        {
            var ranked = votes.Where(v => v.Key > 0 && v.Value > 0).OrderByDescending(v => v.Value).ToList();
            if (ranked.Count == 0 || ranked[0].Value < 2 ||
                (ranked.Count > 1 && ranked[0].Value == ranked[1].Value)) return null;
            return ranked[0].Key;
        }

        private static string FormatQuantityVotes(Dictionary<int, int> votes)
        {
            return string.Join(",", votes.OrderBy(v => v.Key).Select(v => v.Key + "x" + v.Value));
        }

        private static Rectangle? FindQuantityInkBounds(Bitmap image)
        {
            int margin = Math.Max(2, image.Width / 30);
            int interiorWidth = image.Width - 2 * margin;
            if (interiorWidth <= 0) return null;
            Rectangle? candidate = null;
            int left = image.Width, right = -1, top = -1, bottom = -1;
            for (int y = 0; y <= image.Height; y++)
            {
                int rowLeft = image.Width, rowRight = -1, inkCount = 0;
                if (y < image.Height)
                {
                    for (int x = margin; x < image.Width - margin; x++)
                    {
                        Color color = image.GetPixel(x, y);
                        int high = Math.Max(color.R, Math.Max(color.G, color.B));
                        int low = Math.Min(color.R, Math.Min(color.G, color.B));
                        if (high > 175 || high - low > 75) continue;
                        inkCount++;
                        rowLeft = Math.Min(rowLeft, x); rowRight = Math.Max(rowRight, x);
                    }
                }
                // Broad horizontal ink is a frame/background, not quantity
                // glyphs. It must not enlarge the text crop to the whole tile.
                bool textRow = inkCount > 0 && inkCount < interiorWidth * 0.6;
                if (textRow)
                {
                    if (top < 0) top = y;
                    bottom = y;
                    left = Math.Min(left, rowLeft); right = Math.Max(right, rowRight);
                    continue;
                }
                if (top >= 0 && top > 0 && bottom < image.Height - 1 &&
                    bottom - top + 1 >= Math.Max(3, (int)(image.Height * 0.12)) &&
                    right - left + 1 < interiorWidth * 0.85 &&
                    left <= image.Width * 0.6 && right >= image.Width * 0.4)
                {
                    // Quantities are centered below the stars. The lowest
                    // eligible line wins; edge fragments and wide star bands
                    // cannot stand in for the complete quantity line.
                    int paddedLeft = Math.Max(margin, left - 2);
                    int paddedRight = Math.Min(image.Width - margin - 1, right + 2);
                    int paddedTop = Math.Max(0, top - 2);
                    int paddedBottom = Math.Min(image.Height - 1, bottom + 2);
                    candidate = Rectangle.FromLTRB(paddedLeft, paddedTop, paddedRight + 1, paddedBottom + 1);
                }
                left = image.Width; right = -1; top = -1; bottom = -1;
            }
            return candidate;
        }
    }
}
