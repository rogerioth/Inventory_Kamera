using Accord.Imaging;
using Accord.Imaging.Filters;
using NLog;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace InventoryKamera
{

    public enum InventoryPage
    {
        Weapons,
        Artifacts,
        CharacterDevelopmentItems,
        Food,
        Materials,
        Gadget,
        Quest,
        PreciousItems,
        Furnishings,
    }

    public enum Quality
    {
        INVALID,
        ONESTAR,
        TWOSTAR,
        THREESTAR,
        FOURSTAR,
        FIVESTAR
    }

    internal static class InventoryPageExtension
    {
        public static string ToString(this InventoryPage page)
        {
            switch (page)
            {
                case InventoryPage.CharacterDevelopmentItems:
                    return "CharDevItems";
                default:
                    return page.ToString();
            }
        }
    }

    internal class InventoryScraper
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        protected InventoryPage inventoryPage;

        protected bool SortByLevel = false;

        protected readonly List<InventoryPage> materialPages;

        private List<Rectangle> prevRect;
        private int prevColumn = 0;
        private int prevRow = 0;

        public InventoryScraper() 
        {
            materialPages = new List<InventoryPage>();

            materialPages.AddRange(Enum.GetValues(typeof(InventoryPage)).Cast<InventoryPage>());
            materialPages.Remove(InventoryPage.Weapons);
            materialPages.Remove(InventoryPage.Artifacts);
        }

        internal bool StopScanning { get; set; }

        /// <summary>
        /// Gets the item card on the right side of the in-game screen
        /// </summary>
        /// <returns>An image of the in-game item card</returns>
        internal Bitmap GetItemCard()
        {
            Rectangle cardRectangle = new Rectangle();

            using (var window = Navigation.CaptureWindow())
            {

                cardRectangle.X = (int)(window.Width * 0.6807);
                cardRectangle.Y = (int)(window.Height * (Navigation.IsNormal ? 0.1102 : 0.0989));

                cardRectangle.Width = (int)(window.Width * 0.2573);
                cardRectangle.Height = (int)(window.Height * (Navigation.IsNormal ? 0.7787 : 0.8022));


                return GenshinProcesor.CopyBitmap(window, cardRectangle);
            }

        }

        /// <summary>
        /// Parses the item name from an item card's nameplate
        /// </summary>
        /// <param name="nameplate">The item nameplate to parse</param>
        /// <returns>String parsed from nameplate</returns>
        internal string ScanItemName(Bitmap nameplate)
        {
            GenshinProcesor.SetGamma(0.2, 0.2, 0.2, ref nameplate);
            Bitmap n = GenshinProcesor.ConvertToGrayscale(nameplate);
            GenshinProcesor.SetInvert(ref n);

            // Analyze
            string text = Regex.Replace(GenshinProcesor.AnalyzeText(n, Tesseract.PageSegMode.SingleBlock).ToLower(), @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);

            n.Dispose();

            return text;
        }

        /// <summary>
        /// Parses the number of items from for the current inventory page.
        /// </summary>
        /// <remarks>Note: This is primarily for the Weapons, Artifacts, and Furnishing inventories since they have
        /// specific inventory item counts and capacities. 
        /// All other inventory pages have a "shared" inventory item counts and capacities.</remarks>
        /// <param name="inventoryPage">The current inventory page</param>
        /// <returns>The number of unique items for the current inventory</returns>
        /// <exception cref="FormatException">The inventory item count could not be found where it is expected</exception>
        internal int ScanItemCount()
        {
            //Find weapon count
            Rectangle region = new Rectangle(
                x: (int)(1030 / 1280.0 * Navigation.GetWidth()),
                y: (int)(20 / 720.0 * Navigation.GetHeight()),
                width: (int)(175 / 1280.0 * Navigation.GetWidth()),
                height: (int)(25 / 720.0 * Navigation.GetHeight()));

            using (Bitmap countBitmap = Navigation.CaptureRegion(region))
            {
                UserInterface.SetNavigation_Image(countBitmap);

                Bitmap n = GenshinProcesor.ConvertToGrayscale(countBitmap);
                GenshinProcesor.SetContrast(60.0, ref n);
                GenshinProcesor.SetInvert(ref n);

                string text = GenshinProcesor.AnalyzeText(n).Trim();
                n.Dispose();

                // Remove any non-numeric and '/' characters
                text = Regex.Replace(text, @"[^0-9/]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);

                if (string.IsNullOrWhiteSpace(text) || Properties.Settings.Default.LogScreenshots)
                {
                    SaveInventoryBitmap(countBitmap, "ItemCount.png");
                    SaveInventoryBitmap(Navigation.CaptureWindow(), $"InventoryWindow_{Navigation.GetWidth()}x{Navigation.GetHeight()}.png");
                    if (string.IsNullOrWhiteSpace(text)) throw new FormatException($"Unable to locate {inventoryPage} item count.");
                }

                int count;
                string pageCapacity;
                switch (inventoryPage)
                {
                    case InventoryPage.Artifacts:
                        pageCapacity = "1800";
                        break;
                    default:
                        pageCapacity = "2000";
                        break;
                }

                if (Regex.IsMatch(text, "/", RegexOptions.None, RegexTimeouts.RegexTimeout)) // Check for slash
                {
                    count = int.Parse(text.Split('/')[0]);
                }
                else if (Regex.Matches(text, pageCapacity, RegexOptions.None, RegexTimeouts.RegexTimeout).Count == 1) // Remove the inventory capacity from number
                {
                    text = text.Replace(pageCapacity, string.Empty);
                    count = int.Parse(text);
                }
                else // Extreme worst case
                {
                    count = 2000;
                    Logger.Debug("Defaulted to 2000 for inventory page capacity");
                }

                return count;
            }
        }

        /// <summary>
        /// Parses the sorting method for the current inventory
        /// </summary>
        /// <param name="inventoryPage">The current inventory page</param>
        /// <returns>The inventory page's current sorting method</returns>
        internal string CurrentSortingMethod()
        {
            Rectangle region;
            switch (inventoryPage)
            {
                case InventoryPage.Weapons:
                    region = new Rectangle(
                        x: (int)(100.0 / 1280.0 * Navigation.GetWidth()),
                        y: (int)(660.0 / 720.0 * Navigation.GetHeight()),
                        width: (int)(175.0 / 1280.0 * Navigation.GetWidth()),
                        height: (int)(40.0 / 720.0 * Navigation.GetHeight()));
                    break;
                case InventoryPage.Artifacts:
                    // Same sort-button row as the weapons page; the active sort
                    // mode is read back from the screenshot below.
                    region = new Rectangle(
                        x: (int)(140.0 / 1280.0 * Navigation.GetWidth()),
                        y: (int)(660.0 / 720.0 * Navigation.GetHeight()),
                        width: (int)(175.0 / 1280.0 * Navigation.GetWidth()),
                        height: (int)(40.0 / 720.0 * Navigation.GetHeight()));
                    break;
                default:
                    throw new NotImplementedException($"{inventoryPage} cannot be sorted");
            }

            using (var bm = Navigation.CaptureRegion(region))
            {
                var g = GenshinProcesor.ConvertToGrayscale(bm);
                var mode = GenshinProcesor.AnalyzeText(g).Trim().ToLower();
                if (mode.Contains("level")) return "level";
                return mode.Contains("quality") ? "quality" : null;
            }
        }

        internal (List<Rectangle> rectangles, int cols, int rows) ProcessScreenshot(Bitmap screenshot, double weight = 0)
        {
            // Size of an item card is the same in 16:10 and 16:9. Also accounts for character icon and resolution size.
            double base_aspect_width = 1280.0;
            double base_aspect_height = 720.0;
            var icon = new Rectangle(
                x: 0,
                y: 0,
                width: (int)(screenshot.Width * 0.0651),
                height: (int)(screenshot.Height * (Navigation.IsNormal ? 0.1417: 0.1289)));

            if (Navigation.GetAspectRatio() == new Size(8, 5))
            {
                base_aspect_height = 800.0;
            }

            // Filter for relative size of items in inventory, give or take a few pixels
            int iconMinHeight = icon.Height - ((int)(icon.Height * 0.15));
            int iconMaxHeight = icon.Height + ((int)(icon.Height * 0.15));
            int iconMinWidth = icon.Width - ((int)(icon.Width * 0.15));
            int iconMaxWidth = icon.Width + ((int)(icon.Width * 0.15));
            using (BlobCounter blobCounter = new BlobCounter
            {
                FilterBlobs = true,
                MinHeight = (int)(iconMinHeight * (1 - weight)),
                MaxHeight = (int)(iconMaxHeight * (1 + weight)),
                MinWidth = (int)(iconMinWidth * (1 - weight)),
                MaxWidth = (int)(iconMaxWidth * (1 + weight)),
            })
            {
                // Image pre-processing
                screenshot = new KirschEdgeDetector().Apply(screenshot); // Algorithm to find edges. Really good but can take ~1s
                screenshot = new Grayscale(0.2125, 0.7154, 0.0721).Apply(screenshot);
                screenshot = new Threshold(75).Apply(screenshot); // Convert to black and white only based on pixel intensity			

                blobCounter.ProcessImage(screenshot);
                // Note: Processing won't always detect all item rectangles on screen. Since the
                // background isn't a solid color it's a bit trickier to filter out.

                // Don't save overlapping blobs
                List<Rectangle> rectangles = new List<Rectangle>();
                List<Rectangle> blobRects = blobCounter.GetObjectsRectangles().ToList();

                // A capture taken during a UI animation can contain no item blobs at
                // all. Treat it as an unusable page (empty result) so the caller's
                // bounded retry handles it instead of throwing out of the scan.
                if (blobRects.Count == 0)
                {
                    Logger.Warn("No item blobs detected on the screenshot.");
                    return (rectangles, 0, 0);
                }

                int minWidth = blobRects[0].Width;
                int minHeight = blobRects[0].Height;
                foreach (var rect in blobRects)
                {
                    bool add = true;
                    foreach (var item in rectangles)
                    {
                        Rectangle r1 = rect;
                        Rectangle r2 = item;
                        Rectangle intersect = Rectangle.Intersect(r1, r2);
                        if (intersect.Width > r1.Width * .1)
                        {
                            add = false;
                            break;
                        }
                    }
                    if (add)
                    {
                        minWidth = Math.Min(minWidth, rect.Width);
                        minHeight = Math.Min(minHeight, rect.Height);
                        rectangles.Add(rect);
                    }
                }

                // Determine X and Y coordinates for columns and rows, respectively
                var colCoords = new List<int>();
                var rowCoords = new List<int>();

                foreach (var item in rectangles)
                {
                    bool addX = true;
                    bool addY = true;
                    foreach (var x in colCoords)
                    {
                        var xC = item.Center().X;
                        if (x - 75 / base_aspect_width * screenshot.Width <= xC && xC <= x + 75 / base_aspect_width * screenshot.Width)
                        {
                            addX = false;
                            break;
                        }
                    }
                    foreach (var y in rowCoords)
                    {
                        var yC = item.Center().Y;
                        if (y - 100 / base_aspect_height * screenshot.Height <= yC && yC <= y + 100 / base_aspect_height * screenshot.Height)
                        {
                            addY = false;
                            break;
                        }
                    }
                    if (addX)
                    {
                        colCoords.Add(item.Center().X);
                    }
                    if (addY)
                    {
                        rowCoords.Add(item.Center().Y);
                    }
                }

                // Going to use X,Y coordinate pairings to build rectangles around. Items that might have been missed
                // This is quite accurate and algorithmically puts rectangles over all items on the screen that were missed.
                // The center of each of these rectangles should be a good enough spot to click.
                rectangles.Clear();
                colCoords.Sort();
                rowCoords.Sort();

                colCoords.RemoveAll(col => col > screenshot.Width * 0.65);

                foreach (var row in rowCoords)
                {
                    foreach (var col in colCoords)
                    {
                        int x = (int)(col - (minWidth * .5));
                        int y = (int)(row - (minHeight * .5));

                        rectangles.Add(new Rectangle(x, y, minWidth, minHeight));
                    }
                }

                // Remove some rectangles that somehow overlap each other. Don't think this happens
                // but it doesn't hurt to double check.
                for (int i = 0; i < rectangles.Count - 1; i++)
                {
                    for (int j = i + 1; j < rectangles.Count; j++)
                    {
                        Rectangle r1 = rectangles[i];
                        Rectangle r2 = rectangles[j];
                        Rectangle intersect = Rectangle.Intersect(r1, r2);
                        if (intersect.Width > r1.Width * .2)
                        {
                            rectangles.RemoveAt(j);
                        }
                    }
                }

                // Sort by row then by column within each row
                rectangles = rectangles.OrderBy(r => r.Top).ThenBy(r => r.Left).ToList();

                var avgWidth = rectangles.Average(r => r.Width);
                var avgHeight = rectangles.Average (r => r.Height);

                rectangles.ForEach(r =>
                {
                    r.Width = (int)avgWidth;
                    r.Height = (int)avgHeight;
                });

                return (rectangles, colCoords.Count, rowCoords.Count);
            }
        }

        internal (List<Rectangle> rectangles, int cols, int rows) GetPageOfItems(int pageNum, bool acceptLess = false)
        {
            // Screenshot of inventory
            using (Bitmap screenshot = Navigation.CaptureWindow())
            // Disposed by the using on every path; explicit Dispose calls here
            // could run twice (once in the try, once in the catch) on the
            // "no page found" exception path.
            using (Bitmap processedScreenshot = new Bitmap(screenshot))
            {
                using (Graphics g = Graphics.FromImage(processedScreenshot))
                using (var brush = new SolidBrush(Color.Black))
                {
                    // Fill Top region
                    switch (inventoryPage)
                    {
                        case InventoryPage.Artifacts:
                            g.FillRectangle(brush, 0, 0, processedScreenshot.Width, (int)(processedScreenshot.Height * 0.143));
                            break;
                        default:
                            g.FillRectangle(brush, 0, 0, processedScreenshot.Width, (int)(processedScreenshot.Height * 0.09));
                            break;
                    }

                    // Fill Left region
                    g.FillRectangle(brush, 0, 0, (int)(processedScreenshot.Width * 0.05), processedScreenshot.Height);

                    // Fill Right region
                    g.FillRectangle(brush, (int)(processedScreenshot.Width * 0.7), 0, processedScreenshot.Width, processedScreenshot.Height);

                    // Fill Bottom Region
                    g.FillRectangle(brush, 0, (int)(processedScreenshot.Height * 0.9), processedScreenshot.Width, processedScreenshot.Height);
                }
                try
                {
                    List<Rectangle> rectangles;
                    int cols, rows, itemCount, counter = 0;
                    double weight = 0;
                    // On 16:10 the artifact page fits a fifth row of items
                    // (40 per page instead of 32).
                    // (Ported from taiwenlee/Inventory_Kamera (MIT): f3ee8527)
                    int itemPerPage = (inventoryPage != InventoryPage.Artifacts || !Navigation.IsNormal) ? 40 : 32;
                    do
                    {
                        (rectangles, cols, rows) = ProcessScreenshot(processedScreenshot, weight);
                        itemCount = rows * cols;
                        // An empty detection is never acceptable, even on a partial page:
                        // accepting it would leave the walk with nothing to click and
                        // could stall the scan.
                        if (itemCount != itemPerPage && !(acceptLess && itemCount > 0))
                        {
                            Logger.Warn("Unable to locate full page of weapons with weight {0}; detected {1} rows and {2} columns of items", weight, rows, cols);

                            // Generate rectangles
                            using (Bitmap copy = (Bitmap)screenshot.Clone())
                            {
                                SaveInventoryBitmap(copy, $"{inventoryPage}Inventory{pageNum}_{cols}x{rows}.png");
                                using (Graphics g = Graphics.FromImage(copy))
                                    rectangles.ForEach(r => g.DrawRectangle(new Pen(Color.Green, 2), r));
                                SaveInventoryBitmap(copy, $"{inventoryPage}Inventory{pageNum}_{cols}x{rows} - weight {weight}.png");
#if DEBUG
#endif
                            }
                        }
                        else break;

                        if (itemCount <= 32)
                            weight += 0.125;
                        else
                        { weight -= 0.095; ++counter; }
                        weight = Math.Min(weight, 1);
                        rectangles = null;
                    }
                    while (itemCount != itemPerPage && weight < 1 && counter < 25);

                    if (Properties.Settings.Default.LogScreenshots && rectangles != null)
                    {
                        SaveInventoryBitmap(screenshot, $"{inventoryPage}Inventory.png");
                        using (Graphics g = Graphics.FromImage(screenshot))
                            rectangles.ForEach(r => g.DrawRectangle(new Pen(Color.Green, 2), r));

                        SaveInventoryBitmap(screenshot, $"{inventoryPage}Inventory{pageNum}_{cols}x{rows} - weight {weight}.png");
                    }

                    if (rectangles == null)
                    {
                        Logger.Warn("Could not find {0} items in inventory. Re-using previous item page.", itemPerPage);

                        return prevRect == null ?
                            throw new ArgumentNullException(nameof(pageNum), "Could not find first page of items!")
                            :
                            (prevRect, prevColumn, prevRow);
                    }
                    else
                    {
                        prevRect = rectangles; prevColumn = cols; prevRow = rows;
                        return (rectangles, cols, rows);
                    }

                }
                catch (Exception)
                {
                    SaveInventoryBitmap(screenshot, $"{inventoryPage}Inventory.png");
                    throw;
                }
            }

        }

        /// <summary>
        /// Determines the quality of an item based on it's nameplate
        /// </summary>
        /// <param name="nameplate">The an item card's nameplate</param>
        /// <returns>An integer representing quality from 0 - 5 (invalid - 5 star)</returns>
        internal static int GetQuality(Bitmap nameplate)
        {
            var averageColor = new ImageStatistics(nameplate);

            Color fiveStar = Color.FromArgb(255, 188, 105, 50);
            Color fourStar = Color.FromArgb(255, 161, 86, 224);
            Color threeStar = Color.FromArgb(255, 81, 127, 203);
            Color twoStar = Color.FromArgb(255, 42, 143, 114);
            Color oneStar = Color.FromArgb(255, 114, 119, 138);

            var colors = new List<Color> { Color.Black, oneStar, twoStar, threeStar, fourStar, fiveStar };

            var c = GenshinProcesor.ClosestColor(colors, averageColor);

            return colors.IndexOf(c);
        }

        /// <summary>
        /// Extracts a bitmap copy of an item card's nameplate
        /// </summary>
        /// <param name="card">Bitmap of the item card</param>
        /// <returns>A bitmap copy of the item card's nameplate</returns>
        internal static Bitmap GetItemNameBitmap(Bitmap card)
        {
            return GenshinProcesor.CopyBitmap(card,
                new Rectangle(
                    x: 0,
                    y: 0,
                    width: card.Width,
                    height: (int)(card.Height * (Navigation.IsNormal ? 0.07 : 0.06))));
        }

        /// <summary>
        /// Extracts a bitmap copy of an item card's lock status icon
        /// </summary>
        /// <param name="card">Bitmap of the item card</param>
        /// <param name="isSanctified">Whether the card shows the Sanctifying Elixir banner, which shifts the icon down</param>
        /// <returns>A bitmap copy of the item card's lock status icon</returns>
        internal static Bitmap GetLockedBitmap(Bitmap card, bool isSanctified = false)
        {
			// Ported from taiwenlee/Inventory_Kamera (MIT): 5e4687c6, fea6e1bb.
			double baseY = Navigation.IsNormal ? 0.353 : 0.309;
			double sanctifiedShift = Navigation.IsNormal ? 0.0520 : 0.0471;
			double yShift = isSanctified ? sanctifiedShift : 0.0;

            return GenshinProcesor.CopyBitmap(card,
                new Rectangle(
                    x: (int)(card.Width * 0.75),
                    y: (int)(card.Height * (baseY + yShift)),
                    width: (int)(card.Width * 0.0955),
                    height: (int)(card.Height * (Navigation.IsNormal ? 0.055 : 0.0495))));
        }

        /// <summary>
        /// Extracts a bitmap copy of an item card's equipped character status
        /// </summary>
        /// <param name="card">Bitmap of the item card</param>
        /// <remarks>Note: This method is only useful for equippable items (artifacts and weapons)</remarks>
        /// <returns>A bitmap copy of the item card's equipped character status.</returns>
        internal static Bitmap GetEquippedBitmap(Bitmap card)
        {
            return GenshinProcesor.CopyBitmap(card,
                new Rectangle(
                    x: (int)(card.Width * 0.15),
                    y: (int)(card.Height * (Navigation.IsNormal ? 0.938 : 0.943)),
                    width: card.Width,
                    height: card.Height));
        }

        internal void SaveInventoryBitmap(Bitmap image, string filename)
        {
            string directory = materialPages.Contains(inventoryPage)
                ? "./logging/Materials"
                : Path.Combine("./logging", inventoryPage.ToString());

            // Diagnostic saves must never abort a scan (GDI+ generic errors,
            // missing directories, disconnected or full drives). Make sure the
            // directory exists, retry once on failure, and warn instead of
            // throwing.
            string file = Path.Combine(directory, filename);
            try
            {
                Directory.CreateDirectory(directory);
                try
                {
                    image.Save(file);
                }
                catch (Exception)
                {
                    // Single retry for transient GDI+/IO failures (e.g. the
                    // logging directory was just reset by another thread).
                    Thread.Sleep(100);
                    image.Save(file);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Could not save diagnostic screenshot {0}. Continuing scan...", file);
            }
        }

    }
}