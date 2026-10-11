using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using WindowsInput.Native;

namespace InventoryKamera
{
    public interface IAchievementNavigation
    {
        Bitmap Capture();
        void Click(double x, double y);
        void Scroll(bool categories, int signedNotches);
        void PressKey(VirtualKeyCode key);
        void Wait(int milliseconds);
    }

    internal sealed class AchievementNavigation : IAchievementNavigation
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        public Bitmap Capture() { return Navigation.CaptureWindow(); }
        public void Click(double x, double y)
        {
            int targetX = (int)(x * Navigation.GetWidth()), targetY = (int)(y * Navigation.GetHeight());
            if (!Navigation.SetCursor(targetX, targetY))
                throw new InvalidOperationException("The cursor could not reach the achievement button.");
            Logger.Debug("Achievement click at client ({0}, {1})", targetX, targetY);
            // Let the game observe the cursor before sending the click. Keep it here
            // through the caller's UI wait, just like the existing inventory navigation.
            Navigation.Wait(100);
            Navigation.Click();
        }
        public void Scroll(bool categories, int signedNotches)
        {
            if (!Navigation.SetCursor((int)((categories ? .22 : .66) * Navigation.GetWidth()),
                (int)(.55 * Navigation.GetHeight())))
                throw new InvalidOperationException("The cursor could not reach the achievement list.");
            Navigation.Wait(100);
            Navigation.Scroll(signedNotches > 0 ? Navigation.Direction.DOWN : Navigation.Direction.UP,
                Math.Abs(signedNotches), 35);
        }
        private static void ParkCursor() { Navigation.SetCursor((int)(.975 * Navigation.GetWidth()), (int)(.965 * Navigation.GetHeight())); }
        public void PressKey(VirtualKeyCode key) { Navigation.sim.Keyboard.KeyPress(key); }
        public void Wait(int milliseconds)
        {
            Navigation.SystemWait((float)(milliseconds * 2));
            // Only move away once the click/wheel has been consumed. Then let hover
            // decorations disappear before the next OCR capture.
            ParkCursor();
            Navigation.Wait(100);
        }
    }

    /// <summary>One writer reconciles overlapping viewports; completion is never inferred from absence.</summary>
    public sealed class AchievementScanAccumulator
    {
        private readonly AchievementScanResult result;
        private readonly HashSet<string> conflictingRows = new HashSet<string>();
        public AchievementScanAccumulator(AchievementScanResult result) { this.result = result; }

        public void AddPage(AchievementCategory category, int page, List<AchievementObservation> rows)
        {
            foreach (var row in rows)
            {
                row.CategoryId = category.Id;
                row.Page = page;
                string rowKey = row.CategoryId + ":" + Identity(row);
                if (conflictingRows.Contains(rowKey))
                    row.Error = "Completion changed between overlapping captures; rescan this category.";
                var previous = result.Observations.FirstOrDefault(x => x.CategoryId == row.CategoryId &&
                    row.FamilyId.HasValue && x.FamilyId == row.FamilyId);
                if (previous == null)
                {
                    // A later clear capture can resolve a formerly provisional row.
                    // Only migrate exact title/description evidence, and only when
                    // it identifies one prior row. Distinct resolved families must
                    // never be merged merely because their text is identical.
                    string provisionalIdentity = ProvisionalIdentity(row);
                    var candidates = result.Observations.Where(x => x.CategoryId == row.CategoryId &&
                        (!row.FamilyId.HasValue || !x.FamilyId.HasValue) &&
                        ProvisionalIdentity(x) == provisionalIdentity).Take(2).ToList();
                    if (candidates.Count == 1) previous = candidates[0];
                }
                if (previous == null) result.Observations.Add(row);
                else if (string.IsNullOrEmpty(row.Error) && string.IsNullOrEmpty(previous.Error) &&
                    !previous.CompletedIds.OrderBy(x => x).SequenceEqual(row.CompletedIds.OrderBy(x => x)))
                {
                    conflictingRows.Add(rowKey);
                    row.Error = "Completion changed between overlapping captures; rescan this category.";
                    result.Issues.Add("Conflicting completion evidence in " + category.Name + ": " + row.Title);
                    result.Observations[result.Observations.IndexOf(previous)] = row;
                }
                else if (string.IsNullOrEmpty(row.Error) &&
                    (!string.IsNullOrEmpty(previous.Error) || row.Confidence >= previous.Confidence))
                {
                    result.Observations[result.Observations.IndexOf(previous)] = row;
                }
            }
            result.Completed.Clear();
            foreach (var row in result.Observations.Where(x => string.IsNullOrEmpty(x.Error)))
                foreach (int id in row.CompletedIds) result.Completed[id] = row.CategoryId;
        }

        internal static string Identity(AchievementObservation row)
        {
            return row.FamilyId.HasValue ? "id:" + row.FamilyId.Value : ProvisionalIdentity(row);
        }

        private static string ProvisionalIdentity(AchievementObservation row)
        {
            return Normalize(row.Title) + "|" + Normalize(row.Description);
        }

        private static string Normalize(string value)
        {
            return new string((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        public void Finish(AchievementCatalog catalog)
        {
            result.FinishedAt = DateTime.UtcNow;
            result.Complete = !result.Cancelled && result.ExpectedCompleted.HasValue &&
                result.ExpectedCompleted == result.FinalExpectedCompleted &&
                result.Completed.Count == result.ExpectedCompleted.Value && result.Issues.Count == 0 &&
                result.Observations.All(x => string.IsNullOrEmpty(x.Error)) &&
                catalog.Categories.All(x => result.Categories.Any(c => c.CategoryId == x.Id && c.ReachedBottom && c.Issues.Count == 0));
        }
    }

    /// <summary>Walks category and achievement lists without entering achievement names in search.</summary>
    public sealed class AchievementScraper
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private const string CategorySelectionIssue = "Category could not be selected or verified.";
        private readonly AchievementCatalog catalog;
        private readonly AchievementRecognizer recognizer;
        private readonly IAchievementNavigation navigation;
        private CancellationToken cancellation;
        public Action<AchievementScanResult, AchievementCategory, int> Progress { get; set; }
        public Action<Bitmap, AchievementObservation> RowPreview
        {
            get { return recognizer.RowPreview; }
            set { recognizer.RowPreview = value; }
        }

        public AchievementScraper(AchievementCatalog catalog, IAchievementNavigation navigation = null)
        {
            this.catalog = catalog;
            recognizer = new AchievementRecognizer(catalog);
            this.navigation = navigation ?? new AchievementNavigation();
        }

        public void Scan(AchievementScanResult result, CancellationToken cancellationToken)
        {
            cancellation = cancellationToken;
            var accumulator = new AchievementScanAccumulator(result);
            result.StartedAt = DateTime.UtcNow;
            Directory.CreateDirectory("logging/achievements");
            try
            {
                OpenAchievements();
                using (var frame = Capture()) result.ExpectedCompleted = recognizer.ReadCompletedTotal(frame);
                if (!result.ExpectedCompleted.HasValue)
                    result.Issues.Add("The game's completed achievement total could not be read.");
                ResetSidebar();

                foreach (var category in catalog.Categories.OrderBy(x => x.Order))
                {
                    CheckStop();
                    var coverage = new AchievementCategoryScan { CategoryId = category.Id, Name = category.Name };
                    result.Categories.Add(coverage);
                    if (!SelectCategory(category))
                    {
                        coverage.Issues.Add(CategorySelectionIssue);
                        Logger.Warn("Achievement category '{0}' could not be selected; continuing with the remaining categories", category.Name);
                        continue;
                    }
                    Logger.Info("Scanning achievements: {0}", category.Name);
                    ScanCategory(result, coverage, category, accumulator);
                    Progress?.Invoke(result, category, coverage.Pages);
                }
                var missed = result.Categories.Where(x => x.Issues.Contains(CategorySelectionIssue)).ToList();
                if (missed.Count > 0)
                {
                    // Finish visiting the rest before recovering an uncertain selection.
                    // Restarting the sidebar gives missed labels another chance in a fresh position.
                    Logger.Info("Retrying {0} achievement categories that could not be selected", missed.Count);
                    ResetSidebar();
                    foreach (var coverage in missed)
                    {
                        CheckStop();
                        var category = catalog.Categories.Single(x => x.Id == coverage.CategoryId);
                        if (!SelectCategory(category))
                        {
                            Logger.Warn("Achievement category '{0}' still could not be selected; preserving partial results", category.Name);
                            continue;
                        }
                        coverage.Issues.Remove(CategorySelectionIssue);
                        Logger.Info("Scanning achievements: {0} (selection retry)", category.Name);
                        ScanCategory(result, coverage, category, accumulator);
                        Progress?.Invoke(result, category, coverage.Pages);
                    }
                }
                using (var frame = Capture()) result.FinalExpectedCompleted = recognizer.ReadCompletedTotal(frame);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                Logger.Info("Achievement scan stopped; {0} confirmed completed IDs retained", result.Completed.Count);
            }
            catch (ThreadAbortException)
            {
                result.Cancelled = true;
                throw;
            }
            catch (Exception ex)
            {
                result.Issues.Add(ex.Message);
                Logger.Error(ex, "Achievement scan could not finish");
            }
            finally
            {
                accumulator.Finish(catalog);
                Logger.Info("Achievement scan {0}: {1}/{2} completed IDs, {3} unresolved rows",
                    result.Complete ? "complete" : "partial", result.Completed.Count,
                    result.ExpectedCompleted?.ToString() ?? "?", result.Observations.Count(x => !string.IsNullOrEmpty(x.Error)));
            }
        }

        private void OpenAchievements()
        {
            using (var frame = Capture())
            {
                if (recognizer.IsDetailScreen(frame)) return;
                if (recognizer.ReadCompletedTotal(frame).HasValue)
                {
                    navigation.Click(.10, .25);
                    Pause(800);
                    using (var details = Capture())
                        if (recognizer.IsDetailScreen(details)) return;
                    throw new InvalidOperationException("Could not open an achievement category from the overview.");
                }
            }
            // Accept the normal Paimon-menu starting point, or open it from the world.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using (var frame = Capture())
                using (var menu = Crop(frame, .065, .305, .32, .67))
                {
                    string text = GenshinProcesor.AnalyzeText(menu, Tesseract.PageSegMode.SparseText);
                    if (text.IndexOf("Achievements", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        SaveNavigationFrame(frame, "before_achievements_click");
                        navigation.Click(.345, .387);
                        Pause(900);
                        using (var overview = Capture())
                        {
                            if (recognizer.IsDetailScreen(overview)) return;
                            if (!recognizer.ReadCompletedTotal(overview).HasValue)
                            {
                                SaveNavigationFrame(overview, "achievements_click_failed");
                                throw new InvalidOperationException("The Achievements screen could not be verified after clicking. See logging/achievements for the before/after screenshots.");
                            }
                        }
                        navigation.Click(.10, .25);
                        Pause(800);
                        using (var details = Capture()) if (recognizer.IsDetailScreen(details)) return;
                        throw new InvalidOperationException("The game did not open the achievement list.");
                    }
                }
                navigation.PressKey(Navigation.escapeKey);
                Pause(650);
            }
            throw new InvalidOperationException("Open the Paimon menu or an achievement category, then scan again.");
        }

        private bool SelectCategory(AchievementCategory category)
        {
            string last = null;
            int stationary = 0;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                CheckStop();
                using (var frame = Capture())
                {
                    var entries = recognizer.ReadCategories(frame);
                    var entry = entries.FirstOrDefault(x => x.Category?.Id == category.Id &&
                        x.Bounds.Top >= frame.Height * .16 && x.Bounds.Bottom <= frame.Height * .935);
                    if (entry != null)
                    {
                        if (entry.Selected) return true;
                        // Use the verified tile from this capture for the first click. A second
                        // OCR pass can lose an otherwise clear Roman numeral in the same label.
                        return SelectVisibleCategory(category, entry, frame.Width, frame.Height);
                    }
                    string signature = string.Join("|", entries.Select(x => x.RawText + ":" + x.Bounds.Top));
                    stationary = signature == last ? stationary + 1 : 0;
                    if (stationary >= 2)
                    {
                        SaveCategoryFailure(frame, category.Id, "not_found", entries);
                        return false;
                    }
                    last = signature;
                }
                navigation.Scroll(true, 4);
                Pause(400);
            }
            return false;
        }

        private bool SelectVisibleCategory(AchievementCategory category,
            AchievementCategoryEntryOnScreen entry, int width, int height)
        {
            for (int clickAttempt = 0; clickAttempt < 3; clickAttempt++)
            {
                if (clickAttempt > 0)
                {
                    // Selecting a tile can reposition the sidebar. Reacquire its current
                    // bounds before a retry, allowing a transient OCR miss to settle.
                    entry = null;
                    for (int recognitionAttempt = 0; recognitionAttempt < 3; recognitionAttempt++)
                    {
                        using (var frame = Capture())
                        {
                            var entries = recognizer.ReadCategories(frame);
                            entry = entries.FirstOrDefault(x => x.Category?.Id == category.Id &&
                                x.Bounds.Top >= frame.Height * .16 && x.Bounds.Bottom <= frame.Height * .935);
                            width = frame.Width;
                            height = frame.Height;
                            if (entry != null) break;
                            if (recognitionAttempt == 2)
                                SaveCategoryFailure(frame, category.Id, "not_visible", entries);
                        }
                        if (recognitionAttempt < 2) Pause(300);
                    }
                    if (entry == null) return false;
                }
                if (entry.Selected) return true;
                navigation.Click((entry.Bounds.Left + entry.Bounds.Width * .5) / width,
                    (entry.Bounds.Top + entry.Bounds.Height * .5) / height);
                Pause(650);
                // Selection changes the label's font/color and can take several frames
                // to settle. Recheck before repeating the click at its current location.
                for (int confirmation = 0; confirmation < 3; confirmation++)
                {
                    using (var selected = Capture())
                    {
                        var entries = recognizer.ReadCategories(selected);
                        if (entries.Any(x => x.Category?.Id == category.Id && x.Selected)) return true;
                        if (clickAttempt == 2 && confirmation == 2)
                        {
                            SaveCategoryFailure(selected, category.Id, "selection_failed", entries);
                        }
                    }
                    if (confirmation < 2) Pause(300);
                }
            }
            return false;
        }

        private static void SaveCategoryFailure(Bitmap frame, int categoryId, string step,
            IEnumerable<AchievementCategoryEntryOnScreen> entries)
        {
            SaveNavigationFrame(frame, "category_" + categoryId + "_" + step);
            Logger.Debug("Achievement sidebar recognition: {0}", string.Join(" | ", entries.Select(x =>
                string.Format("y={0}, selected={1}, category={2}, text='{3}'", x.Bounds.Top,
                    x.Selected, x.Category?.Id.ToString() ?? "?", x.RawText))));
        }

        private void ScanCategory(AchievementScanResult result, AchievementCategoryScan coverage,
            AchievementCategory category, AchievementScanAccumulator accumulator)
        {
            // Reset only the right-hand list, independently of sidebar scrolling.
            ResetAchievementList();
            List<AchievementObservation> previous = null;
            string previousSignature = null;
            int stationary = 0;
            int step = 28;
            for (int page = 1; page <= 1000; page++)
            {
                CheckStop();
                using (var frame = Capture())
                {
                    if (!recognizer.IsDetailScreen(frame))
                        throw new InvalidOperationException("The achievement list closed or changed during scanning.");
                    var rows = recognizer.ReadPage(frame, category, page);
                    string signature = string.Join("|", rows.Select(x => AchievementScanAccumulator.Identity(x) + ":" + x.Top));
                    if (rows.Count == 0)
                    {
                        coverage.Issues.Add("No complete achievement rows could be read.");
                        SaveFrame(frame, category.Id, page);
                        return;
                    }
                    if (previous != null && signature != previousSignature && !RowsOverlap(previous, rows))
                    {
                        // Undo a step that would leave a gap, then retry with a smaller step.
                        navigation.Scroll(false, -step);
                        Pause(400);
                        if (step == 1)
                        {
                            coverage.Issues.Add("Scrolling lost row overlap; scan coverage cannot be verified.");
                            SaveFrame(frame, category.Id, page);
                            return;
                        }
                        step = Math.Max(1, step / 2);
                        navigation.Scroll(false, step);
                        Pause(400);
                        page--;
                        continue;
                    }
                    accumulator.AddPage(category, page, rows);
                    coverage.Pages = page;
                    Progress?.Invoke(result, category, page);
                    Logger.Debug("Achievement page {0}: OCR {1}, reused {2}, wheel notches {3}",
                        page, recognizer.LastOcrRowCount, recognizer.LastReusedRowCount, step);
                    if (Properties.Settings.Default.LogScreenshots || rows.Any(x => !string.IsNullOrEmpty(x.Error)))
                        SaveFrame(frame, category.Id, page);
                    stationary = previousSignature == signature ? stationary + 1 : 0;
                    if (stationary >= 2 || stationary >= 1 && ScrollbarAtBottom(frame))
                    {
                        // No movement alone can mean lost focus; a bottom thumb is also required.
                        coverage.ReachedBottom = ScrollbarAtBottom(frame);
                        if (!coverage.ReachedBottom)
                            coverage.Issues.Add("List stopped moving before the scrollbar reached its bottom.");
                        return;
                    }
                    if (previous != null && !ScrollbarAtBottom(frame))
                        step = AdjustScrollStep(step, previous, rows,
                            (int)(963.0 / 1080 * frame.Height) - AchievementRecognizer.GetAchievementListTop(frame));
                    previous = rows;
                    previousSignature = signature;
                }
                navigation.Scroll(false, step);
                Pause(400);
            }
            coverage.Issues.Add("Achievement list exceeded the page limit.");
        }

        internal static int AdjustScrollStep(int currentStep, IList<AchievementObservation> previous,
            IList<AchievementObservation> current, int viewportHeight)
        {
            var movements = new List<int>();
            foreach (var row in current.Where(x => x.FamilyId.HasValue && string.IsNullOrEmpty(x.Error)))
            {
                var old = previous.FirstOrDefault(x => x.FamilyId == row.FamilyId && string.IsNullOrEmpty(x.Error));
                if (old != null && old.Top > row.Top) movements.Add(old.Top - row.Top);
            }
            if (movements.Count == 0 || viewportHeight <= 0) return currentStep;
            movements.Sort();
            double movement = movements[movements.Count / 2];
            // Advance most of the panel, keeping a full row or two as a coverage anchor.
            return Math.Max(1, Math.Min(48, (int)Math.Round(currentStep * viewportHeight * .70 / movement)));
        }

        public static bool RowsOverlap(IList<AchievementObservation> first, IList<AchievementObservation> second)
        {
            var identities = new HashSet<string>(first.Select(AchievementScanAccumulator.Identity));
            return second.Any(x => identities.Contains(AchievementScanAccumulator.Identity(x)));
        }

        public static bool ScrollbarAtBottom(Bitmap frame)
        {
            // The thumb is near-white; the recessed track is brown-gray. Ignore the cap arrows.
            int x = (int)(1838.0 / 1920 * frame.Width);
            int top = AchievementRecognizer.GetAchievementListTop(frame);
            int bottom = (int)(963.0 / 1080 * frame.Height);
            int bright = 0, last = -1;
            for (int y = top; y <= bottom; y++)
            {
                Color color = frame.GetPixel(Math.Min(frame.Width - 1, x), y);
                if (color.R >= 235 && color.G >= 232 && color.B >= 227)
                {
                    bright++;
                    last = y;
                }
            }
            return bright >= Math.Max(3, frame.Height / 360) && last >= bottom - Math.Max(2, frame.Height / 360);
        }

        internal static bool ScrollbarAtTop(Bitmap frame)
        {
            int x = Math.Min(frame.Width - 1, (int)(1838.0 / 1920 * frame.Width));
            int top = AchievementRecognizer.GetAchievementListTop(frame);
            int bottom = (int)(963.0 / 1080 * frame.Height);
            int bright = 0, first = -1;
            for (int y = top; y <= bottom; y++)
            {
                Color color = frame.GetPixel(x, y);
                if (color.R >= 235 && color.G >= 232 && color.B >= 227)
                {
                    bright++;
                    if (first < 0) first = y;
                }
            }
            return bright >= Math.Max(3, frame.Height / 360) && first <= top + Math.Max(2, frame.Height / 360);
        }

        private void ResetAchievementList()
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                using (var frame = Capture()) if (ScrollbarAtTop(frame)) return;
                navigation.Scroll(false, -20);
                Pause(350);
            }
            throw new InvalidOperationException("The achievement list could not be reset to its top.");
        }

        private void ResetSidebar()
        {
            var first = catalog.Categories.OrderBy(x => x.Order).First();
            for (int attempt = 0; attempt < 30; attempt++)
            {
                using (var frame = Capture())
                    if (recognizer.ReadCategories(frame).Any(x => x.Category?.Id == first.Id)) return;
                navigation.Scroll(true, -20);
                Pause(350);
            }
            throw new InvalidOperationException("The achievement category list could not be reset to its top.");
        }

        private Bitmap Capture()
        {
            CheckStop();
            var frame = navigation.Capture();
            if (frame.Width <= 0 || frame.Height <= 0) { frame.Dispose(); throw new InvalidOperationException("Game window is unavailable."); }
            return frame;
        }
        private void CheckStop() { cancellation.ThrowIfCancellationRequested(); }
        private void Pause(int milliseconds) { CheckStop(); navigation.Wait(milliseconds); CheckStop(); }
        private static Bitmap Crop(Bitmap frame, double x, double y, double width, double height)
        {
            return frame.Clone(new Rectangle((int)(x * frame.Width), (int)(y * frame.Height),
                (int)(width * frame.Width), (int)(height * frame.Height)), System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        }
        private static void SaveFrame(Bitmap frame, int category, int page)
        {
            frame.Save(Path.Combine("logging/achievements", "category_" + category + "_page_" + page + ".png"));
        }
        private static void SaveNavigationFrame(Bitmap frame, string step)
        {
            try
            {
                string path = Path.Combine("logging/achievements", DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff") + "_" + step + ".png");
                frame.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Logger.Debug("Achievement navigation screenshot: {0}", path);
            }
            catch (Exception ex) { Logger.Warn(ex, "Could not save achievement navigation evidence"); }
        }
    }
}
