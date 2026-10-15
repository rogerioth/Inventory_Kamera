using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using WindowsInput.Native;

namespace InventoryKamera
{
    internal enum AchievementSelectionFailureKind
    {
        None, LabelUnreadable, DetailMissing, SidebarStalled, SelectionUnverified, SearchLimit
    }

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
        private const int MaximumSidebarRecoveries = 3;
        private const int MaximumNavigationEvidence = 160;
        private const string CategoryEvidencePrefix = "category_";
        private const string AchievementsLogDirectory = "logging/achievements";
        private const string SidebarResetContext = "reset_";
        private readonly AchievementCatalog catalog;
        private readonly AchievementRecognizer recognizer;
        private readonly IAchievementNavigation navigation;
        private readonly Func<Bitmap, List<AchievementCategoryEntryOnScreen>> sidebarReader;
        private readonly Func<Bitmap, bool> detailScreenReader;
        private readonly Dictionary<int, string> selectionFailures = new Dictionary<int, string>();
        private CancellationToken cancellation;
        private int sidebarRecoveries, navigationEvidence;
        private bool evidenceLimitLogged;
        private string lastSelectionFailure;
        internal AchievementSelectionFailureKind LastSelectionFailureKind { get; private set; }
        public Action<AchievementScanResult, AchievementCategory, int> Progress { get; set; }
        public Action<Bitmap, AchievementObservation> RowPreview
        {
            get { return recognizer.RowPreview; }
            set { recognizer.RowPreview = value; }
        }

        public AchievementScraper(AchievementCatalog catalog, IAchievementNavigation navigation = null)
            : this(catalog, navigation, null, null)
        {
        }

        internal AchievementScraper(AchievementCatalog catalog, IAchievementNavigation navigation,
            Func<Bitmap, List<AchievementCategoryEntryOnScreen>> readSidebar, Func<Bitmap, bool> isDetailScreen)
        {
            this.catalog = catalog;
            recognizer = new AchievementRecognizer(catalog);
            this.navigation = navigation ?? new AchievementNavigation();
            sidebarReader = readSidebar ?? new Func<Bitmap, List<AchievementCategoryEntryOnScreen>>(recognizer.ReadCategories);
            detailScreenReader = isDetailScreen ?? new Func<Bitmap, bool>(AchievementRecognizer.IsDetailScreen);
        }

        public void Scan(AchievementScanResult result, CancellationToken cancellationToken)
        {
            cancellation = cancellationToken;
            sidebarRecoveries = navigationEvidence = 0;
            evidenceLimitLogged = false;
            selectionFailures.Clear();
            var accumulator = new AchievementScanAccumulator(result);
            result.StartedAt = DateTime.UtcNow;
            Directory.CreateDirectory(AchievementsLogDirectory);
            try
            {
                OpenAchievements();
                using (var frame = Capture()) result.ExpectedCompleted = AchievementRecognizer.ReadCompletedTotal(frame);
                if (!result.ExpectedCompleted.HasValue)
                    result.Issues.Add("The game's completed achievement total could not be read.");
                ResetSidebar();

                int consecutiveSelectionFailures = 0;
                foreach (var category in catalog.Categories.OrderBy(x => x.Order))
                {
                    CheckStop();
                    var coverage = new AchievementCategoryScan { CategoryId = category.Id, Name = category.Name };
                    result.Categories.Add(coverage);
                    if (!SelectCategory(category))
                    {
                        consecutiveSelectionFailures = NextNavigationFailureCount(consecutiveSelectionFailures, LastSelectionFailureKind);
                        if (consecutiveSelectionFailures >= 3)
                        {
                            Logger.Warn("Achievement sidebar navigation failed for {0} consecutive categories; recovering before continuing", consecutiveSelectionFailures);
                            // On success the failure counter is reset below, before the
                            // category is scanned; only a failed recovery bails out.
                            if (!RecoverSidebar("consecutive_selection_failures") || !SelectCategory(category))
                            {
                                coverage.Issues.Add(CategorySelectionIssue);
                                selectionFailures[category.Id] = lastSelectionFailure;
                                Progress?.Invoke(result, category, coverage.Pages);
                                throw new InvalidOperationException("The achievement sidebar could not be verified after bounded recovery; remaining categories were not visited.");
                            }
                        }
                        else
                        {
                            coverage.Issues.Add(CategorySelectionIssue);
                            selectionFailures[category.Id] = lastSelectionFailure;
                            Logger.Warn("Achievement category {0} '{1}' could not be selected: {2}", category.Id, category.Name, lastSelectionFailure);
                            Progress?.Invoke(result, category, coverage.Pages);
                            continue;
                        }
                    }
                    consecutiveSelectionFailures = 0;
                    selectionFailures.Remove(category.Id);
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
                    ResetSidebar("selection_retry");
                    foreach (var coverage in missed)
                    {
                        CheckStop();
                        var category = catalog.Categories.Single(x => x.Id == coverage.CategoryId);
                        if (!SelectCategory(category))
                        {
                            selectionFailures[category.Id] = lastSelectionFailure;
                            Logger.Warn("Achievement category {0} '{1}' still could not be selected: {2}; preserving partial results", category.Id, category.Name, lastSelectionFailure);
                            continue;
                        }
                        coverage.Issues.Remove(CategorySelectionIssue);
                        selectionFailures.Remove(category.Id);
                        Logger.Info("Scanning achievements: {0} (selection retry)", category.Name);
                        ScanCategory(result, coverage, category, accumulator);
                        Progress?.Invoke(result, category, coverage.Pages);
                    }
                }
                using (var frame = Capture()) result.FinalExpectedCompleted = AchievementRecognizer.ReadCompletedTotal(frame);
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
                if (!result.Cancelled && !cancellation.IsCancellationRequested && !result.FinalExpectedCompleted.HasValue)
                {
                    try { using (var frame = Capture()) result.FinalExpectedCompleted = AchievementRecognizer.ReadCompletedTotal(frame); }
                    catch (Exception ex) { Logger.Debug(ex, "Could not read the final completed achievement total"); }
                }
                accumulator.Finish(catalog);
                LogCoverageSummary(result);
                Logger.Info("Achievement scan {0}: {1}/{2} completed IDs, {3} unresolved rows",
                    result.Complete ? "complete" : "partial", result.Completed.Count,
                    result.ExpectedCompleted?.ToString() ?? "?", result.Observations.Count(x => !string.IsNullOrEmpty(x.Error)));
            }
        }

        private void OpenAchievements()
        {
            using (var frame = Capture())
            {
                if (AchievementRecognizer.IsDetailScreen(frame)) return;
                if (AchievementRecognizer.ReadCompletedTotal(frame).HasValue)
                {
                    navigation.Click(.10, .25);
                    Pause(800);
                    using (var details = Capture())
                        if (AchievementRecognizer.IsDetailScreen(details)) return;
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
                            if (AchievementRecognizer.IsDetailScreen(overview)) return;
                            if (!AchievementRecognizer.ReadCompletedTotal(overview).HasValue)
                            {
                                SaveNavigationFrame(overview, "achievements_click_failed");
                                throw new InvalidOperationException("The Achievements screen could not be verified after clicking. See logging/achievements for the before/after screenshots.");
                            }
                        }
                        navigation.Click(.10, .25);
                        Pause(800);
                        using (var details = Capture()) if (AchievementRecognizer.IsDetailScreen(details)) return;
                        throw new InvalidOperationException("The game did not open the achievement list.");
                    }
                }
                navigation.PressKey(Navigation.escapeKey);
                Pause(650);
            }
            throw new InvalidOperationException("Open the Paimon menu or an achievement category, then scan again.");
        }

        internal bool SelectCategory(AchievementCategory category)
        {
            string last = null;
            int stationary = 0, unreadWithinRange = 0, previousDirection = 0;
            bool localNudgeUsed = false;
            List<AchievementCategoryEntryOnScreen> beforeNudge = null;
            Rectangle nudgeBounds = Rectangle.Empty;
            int nudgeNotches = 0;
            LastSelectionFailureKind = AchievementSelectionFailureKind.SearchLimit;
            lastSelectionFailure = "Sidebar search exceeded its bounded attempt limit.";
            Logger.Info("Selecting achievement category {0} '{1}' (order {2})", category.Id, category.Name, category.Order);
            for (int attempt = 0; attempt < 32; attempt++)
            {
                CheckStop();
                int direction, step;
                using (var frame = Capture())
                {
                    if (!VerifySelectionDetail(frame, category, "select"))
                    {
                        return false;
                    }
                    var entries = ReadSidebar(frame, "select", category.Id, attempt + 1);
                    if (beforeNudge != null)
                    {
                        Rectangle shiftedBounds;
                        if (!SidebarNudgeMoved(beforeNudge, entries, nudgeNotches, frame.Height, nudgeBounds, out shiftedBounds))
                        {
                            LastSelectionFailureKind = AchievementSelectionFailureKind.LabelUnreadable;
                            lastSelectionFailure = "The local sidebar position change could not be verified with a full target tile.";
                            SaveCategoryFailure(frame, category.Id, "local_nudge_unverified", entries);
                            return false;
                        }
                        Logger.Info("Achievement category {0} local sidebar nudge verified: {1} notches, tile {2} -> {3}",
                            category.Id, nudgeNotches, nudgeBounds, shiftedBounds);
                        beforeNudge = null;
                    }
                    var entry = entries.FirstOrDefault(x => x.Category?.Id == category.Id && IsFullSidebarEntry(x, frame));
                    if (entry != null)
                    {
                        if (entry.Selected)
                        {
                            LastSelectionFailureKind = AchievementSelectionFailureKind.None;
                            Logger.Info("Achievement category {0} selection verified at {1}", category.Id, entry.Bounds);
                            return true;
                        }
                        // Use the verified tile from this capture for the first click. A second
                        // OCR pass can lose an otherwise clear Roman numeral in the same label.
                        SaveNavigationFrame(frame, CategoryEvidencePrefix + category.Id + "_before_select");
                        return SelectVisibleCategory(category, entry, frame.Width, frame.Height);
                    }
                    direction = SidebarScrollDirection(category, entries);
                    string signature = SidebarSignature(frame, entries);
                    stationary = signature == last ? stationary + 1 : 0;
                    last = signature;
                    if (direction == 0)
                    {
                        // Re-read a missing label inside the current known range before
                        // moving away from a category that may already be visible.
                        if (++unreadWithinRange >= 3)
                        {
                            Rectangle candidateBounds = Rectangle.Empty;
                            int nudge = !localNudgeUsed
                                ? SidebarUnreadableNudge(category, entries, frame.Height, out candidateBounds)
                                : 0;
                            if (nudge != 0 && !(nudge < 0 ? SidebarAtTop(frame) : SidebarAtBottom(frame)))
                            {
                                // Context supplies a safe position change, never a guessed
                                // category identity or click. Reacquire after verified motion.
                                beforeNudge = entries;
                                nudgeBounds = candidateBounds;
                                nudgeNotches = nudge;
                                localNudgeUsed = true;
                                unreadWithinRange = stationary = previousDirection = 0;
                                last = null;
                                SaveNavigationFrame(frame, CategoryEvidencePrefix + category.Id + "_before_local_nudge");
                                Logger.Info("Achievement category {0} label unreadable after three captures; nudging sidebar {1} notches around full tile {2}",
                                    category.Id, nudge, candidateBounds);
                                ScrollSidebar(nudge, CategoryEvidencePrefix + category.Id + "_local_nudge");
                                continue;
                            }
                            LastSelectionFailureKind = AchievementSelectionFailureKind.LabelUnreadable;
                            lastSelectionFailure = entries.Any(x => x.Category != null)
                                ? "The requested category was unreadable within the visible sidebar range."
                                : "No known full sidebar categories could be read.";
                            SaveCategoryFailure(frame, category.Id, "visible_range_unreadable", entries);
                            return false;
                        }
                    }
                    else
                    {
                        unreadWithinRange = 0;
                        bool atEnd = direction < 0 ? SidebarAtTop(frame) : SidebarAtBottom(frame);
                        if (atEnd || stationary >= 2)
                        {
                            string sidebarEnd = direction < 0 ? "top." : "bottom.";
                            lastSelectionFailure = atEnd
                                ? "The requested category was not recognized at the verified sidebar " + sidebarEnd
                                : "The sidebar did not move after two wheel requests.";
                            if (atEnd && stationary < 2) direction = 0;
                            else
                            {
                                LastSelectionFailureKind = atEnd ? AchievementSelectionFailureKind.LabelUnreadable
                                    : AchievementSelectionFailureKind.SidebarStalled;
                                SaveCategoryFailure(frame, category.Id, atEnd ? "endpoint_unreadable" : "scroll_stalled", entries);
                                return false;
                            }
                        }
                    }
                    if (direction != 0 && previousDirection != 0 && direction != previousDirection)
                    {
                        Logger.Debug("Achievement category {0}: reversing sidebar search from {1} to {2}", category.Id, previousDirection, direction);
                        stationary = 0;
                    }
                    var known = entries.Where(x => x.Category != null).Select(x => Math.Abs(x.Category.Order - category.Order)).ToList();
                    step = known.Count > 0 && known.Min() <= 3 ? 4 : 12;
                }
                if (direction == 0) Pause(350);
                else
                {
                    previousDirection = direction;
                    ScrollSidebar(direction * step, CategoryEvidencePrefix + category.Id + "_search");
                }
            }
            lastSelectionFailure = "Sidebar search exceeded its bounded attempt limit.";
            LastSelectionFailureKind = AchievementSelectionFailureKind.SearchLimit;
            using (var frame = Capture()) SaveCategoryFailure(frame, category.Id, "search_limit", ReadSidebar(frame, "search_limit", category.Id, 32));
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
                            if (!VerifySelectionDetail(frame, category, "reacquire")) return false;
                            var entries = ReadSidebar(frame, "reacquire", category.Id, recognitionAttempt + 1);
                            entry = entries.FirstOrDefault(x => x.Category?.Id == category.Id && IsFullSidebarEntry(x, frame));
                            width = frame.Width;
                            height = frame.Height;
                            if (entry != null) break;
                            if (recognitionAttempt == 2)
                            {
                                lastSelectionFailure = "The category tile could not be reacquired after clicking.";
                                LastSelectionFailureKind = AchievementSelectionFailureKind.LabelUnreadable;
                                SaveCategoryFailure(frame, category.Id, "not_visible", entries);
                            }
                        }
                        if (recognitionAttempt < 2) Pause(300);
                    }
                    if (entry == null)
                    {
                        lastSelectionFailure = "The category tile could not be reacquired after clicking.";
                        LastSelectionFailureKind = AchievementSelectionFailureKind.LabelUnreadable;
                        return false;
                    }
                }
                if (entry.Selected)
                {
                    LastSelectionFailureKind = AchievementSelectionFailureKind.None;
                    Logger.Info("Achievement category {0} '{1}' selection verified while reacquiring its tile", category.Id, category.Name);
                    return true;
                }
                double clickX = (entry.Bounds.Left + entry.Bounds.Width * .5) / width;
                double clickY = (entry.Bounds.Top + entry.Bounds.Height * .5) / height;
                Logger.Debug("Achievement category {0} click {1}/3: tile {2}, client ({3:F0},{4:F0}), normalized ({5:F4},{6:F4})",
                    category.Id, clickAttempt + 1, entry.Bounds, clickX * width, clickY * height, clickX, clickY);
                navigation.Click(clickX, clickY);
                Pause(650);
                // Selection changes the label's font/color and can take several frames
                // to settle. Recheck before repeating the click at its current location.
                for (int confirmation = 0; confirmation < 3; confirmation++)
                {
                    using (var selected = Capture())
                    {
                        if (!VerifySelectionDetail(selected, category, "confirm_click")) return false;
                        var entries = ReadSidebar(selected, "confirm_click_" + (clickAttempt + 1), category.Id, confirmation + 1);
                        if (entries.Any(e => e.Category?.Id == category.Id && e.Selected && IsFullSidebarEntry(e, selected)))
                        {
                            LastSelectionFailureKind = AchievementSelectionFailureKind.None;
                            Logger.Info("Achievement category {0} '{1}' selection verified after click {2}", category.Id, category.Name, clickAttempt + 1);
                            return true;
                        }
                        if (clickAttempt == 2 && confirmation == 2)
                        {
                            lastSelectionFailure = "Three clicks did not produce a verified selected category tile.";
                            LastSelectionFailureKind = entries.Any(e => e.Selected && e.Category != null
                                && e.Category.Id != category.Id && IsFullSidebarEntry(e, selected))
                                ? AchievementSelectionFailureKind.SelectionUnverified : AchievementSelectionFailureKind.LabelUnreadable;
                            SaveCategoryFailure(selected, category.Id, "selection_failed", entries);
                        }
                    }
                    if (confirmation < 2) Pause(300);
                }
            }
            lastSelectionFailure = "Three clicks did not produce a verified selected category tile.";
            return false;
        }

        private bool VerifySelectionDetail(Bitmap frame, AchievementCategory category, string context)
        {
            if (detailScreenReader(frame)) return true;
            LastSelectionFailureKind = AchievementSelectionFailureKind.DetailMissing;
            lastSelectionFailure = "The achievement detail screen was not visible during sidebar selection.";
            SaveNavigationFrame(frame, CategoryEvidencePrefix + category.Id + "_" + context + "_detail_missing");
            Logger.Warn("Achievement category {0}: {1}", category.Id, lastSelectionFailure);
            return false;
        }

        internal static int NextNavigationFailureCount(int previous, AchievementSelectionFailureKind kind)
        {
            return kind == AchievementSelectionFailureKind.DetailMissing || kind == AchievementSelectionFailureKind.SidebarStalled
                || kind == AchievementSelectionFailureKind.SelectionUnverified || kind == AchievementSelectionFailureKind.SearchLimit
                ? previous + 1 : 0;
        }

        private void SaveCategoryFailure(Bitmap frame, int categoryId, string step,
            IEnumerable<AchievementCategoryEntryOnScreen> entries)
        {
            string path = SaveNavigationFrame(frame, CategoryEvidencePrefix + categoryId + "_" + step);
            Logger.Warn("Achievement category {0} sidebar {1}: {2}; thumb {3}; evidence {4}; entries {5}",
                categoryId, step, lastSelectionFailure, SidebarThumbBounds(frame)?.ToString() ?? "unreadable",
                path ?? "navigation evidence limit reached", FormatSidebar(entries));
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
            // While-loop form of the former `for (page = 1; page <= 1000; page++)`:
            // the retry path below (`continue`) re-reads the same page number,
            // exactly like the previous `page--; continue;` plus loop increment.
            int page = 1;
            while (page <= 1000)
            {
                CheckStop();
                using (var frame = Capture())
                {
                    if (!AchievementRecognizer.IsDetailScreen(frame))
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
                        // Retry the same page number: the step change above undoes
                        // the gap-creating scroll, so this page is read again.
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
                page++;
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

        private void ResetSidebar(string context = "initial", bool allowRecovery = true)
        {
            string last = null;
            int stationary = 0;
            Logger.Info("Resetting achievement sidebar to its top ({0})", context);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                using (var frame = Capture())
                {
                    var entries = ReadSidebar(frame, SidebarResetContext + context, null, attempt + 1);
                    if (SidebarAtTop(frame))
                    {
                        Logger.Info("Achievement sidebar top verified ({0}), thumb {1}", context, SidebarThumbBounds(frame));
                        return;
                    }
                    string signature = SidebarSignature(frame, entries);
                    stationary = signature == last ? stationary + 1 : 0;
                    last = signature;
                    if (!AchievementRecognizer.IsDetailScreen(frame) || stationary >= 2 || attempt == 19)
                    {
                        string path = SaveNavigationFrame(frame, "sidebar_reset_" + context + "_failed");
                        Logger.Warn("Achievement sidebar reset failed ({0}), attempt {1}/20, stationary {2}, thumb {3}, top {4}, bottom {5}; evidence {6}; entries {7}",
                            context, attempt + 1, stationary, SidebarThumbBounds(frame)?.ToString() ?? "unreadable",
                            SidebarAtTop(frame), SidebarAtBottom(frame), path ?? "navigation evidence limit reached", FormatSidebar(entries));
                        break;
                    }
                }
                ScrollSidebar(-24, SidebarResetContext + context);
            }
            if (allowRecovery && RecoverSidebar(SidebarResetContext + context)) return;
            throw new InvalidOperationException("The achievement category list could not be reset to its top.");
        }

        private bool RecoverSidebar(string reason)
        {
            if (sidebarRecoveries >= MaximumSidebarRecoveries)
            {
                Logger.Warn("Achievement sidebar recovery limit {0} reached ({1})", MaximumSidebarRecoveries, reason);
                return false;
            }
            sidebarRecoveries++;
            Logger.Warn("Recovering achievement sidebar {0}/{1} ({2})", sidebarRecoveries, MaximumSidebarRecoveries, reason);
            try
            {
                using (var frame = Capture())
                {
                    var entries = ReadSidebar(frame, "before_recovery", null, sidebarRecoveries);
                    SaveNavigationFrame(frame, "sidebar_before_recovery_" + sidebarRecoveries);
                    if (!AchievementRecognizer.IsDetailScreen(frame))
                    {
                        Logger.Warn("Achievement sidebar recovery stopped: detail screen was not verified; entries {0}", FormatSidebar(entries));
                        return false;
                    }
                }
                Logger.Debug("Achievement sidebar recovery: pressing Escape to return to the category overview");
                navigation.PressKey(Navigation.escapeKey);
                Pause(800);
                using (var overview = Capture())
                {
                    bool detail = AchievementRecognizer.IsDetailScreen(overview);
                    int? total = AchievementRecognizer.ReadCompletedTotal(overview);
                    SaveNavigationFrame(overview, "sidebar_recovery_overview_" + sidebarRecoveries);
                    Logger.Info("Achievement sidebar recovery overview: detail {0}, completed total {1}", detail, total?.ToString() ?? "unreadable");
                    if (detail || !total.HasValue) return false;
                }
                Logger.Debug("Achievement sidebar recovery: opening first overview tile at normalized (0.1000,0.2500)");
                navigation.Click(.10, .25);
                Pause(800);
                using (var detail = Capture())
                {
                    bool verified = AchievementRecognizer.IsDetailScreen(detail);
                    SaveNavigationFrame(detail, "sidebar_recovery_detail_" + sidebarRecoveries);
                    Logger.Info("Achievement sidebar recovery detail screen verified: {0}", verified);
                    if (!verified) return false;
                }
                ResetSidebar("recovery_" + sidebarRecoveries, false);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (ThreadAbortException) { throw; }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Achievement sidebar recovery failed ({0})", reason);
                return false;
            }
        }

        private void ScrollSidebar(int signedNotches, string context)
        {
            Logger.Debug("Achievement sidebar wheel ({0}): {1} notches at normalized (0.2200,0.5500)", context, signedNotches);
            navigation.Scroll(true, signedNotches);
            Pause(400);
        }

        private List<AchievementCategoryEntryOnScreen> ReadSidebar(Bitmap frame, string context, int? target, int attempt)
        {
            var entries = sidebarReader(frame);
            Logger.Debug("Achievement sidebar read ({0}), target {1}, attempt {2}, frame {3}x{4}, thumb {5}, top {6}, bottom {7}: {8}",
                context, target?.ToString() ?? "none", attempt, frame.Width, frame.Height,
                SidebarThumbBounds(frame)?.ToString() ?? "unreadable", SidebarAtTop(frame), SidebarAtBottom(frame), FormatSidebar(entries));
            return entries;
        }

        private static string FormatSidebar(IEnumerable<AchievementCategoryEntryOnScreen> entries)
        {
            return string.Join(" | ", entries.Select(x => string.Format("bounds={0}, selected={1}, category={2}, order={3}, text='{4}'",
                x.Bounds, x.Selected, x.Category?.Id.ToString() ?? "?", x.Category?.Order.ToString() ?? "?", x.RawText)));
        }

        private static bool IsFullSidebarEntry(AchievementCategoryEntryOnScreen entry, Bitmap frame)
        {
            return IsFullSidebarBounds(entry.Bounds, frame.Height);
        }

        private static bool IsFullSidebarBounds(Rectangle bounds, int frameHeight)
        {
            return bounds.Width > 0 && bounds.Height > 0
                && bounds.Top > (int)(162.0 / 1080 * frameHeight)
                && bounds.Bottom < (int)(1018.0 / 1080 * frameHeight);
        }

        internal static int SidebarUnreadableNudge(AchievementCategory target,
            IList<AchievementCategoryEntryOnScreen> entries, int frameHeight, out Rectangle candidateBounds)
        {
            candidateBounds = Rectangle.Empty;
            if (target == null || entries == null || frameHeight <= 0) return 0;
            var full = entries.Where(x => IsFullSidebarBounds(x.Bounds, frameHeight)).OrderBy(x => x.Bounds.Top).ToList();
            var known = full.Where(x => x.Category != null).ToList();
            for (int index = 1; index < known.Count; index++)
                if (known[index - 1].Category.Order >= known[index].Category.Order) return 0;
            var lower = known.LastOrDefault(x => x.Category.Order < target.Order);
            var upper = known.FirstOrDefault(x => x.Category.Order > target.Order);
            if (lower == null || upper == null || lower.Bounds.Bottom > upper.Bounds.Top) return 0;
            var between = full.Where(x => x.Bounds.Top >= lower.Bounds.Bottom && x.Bounds.Bottom <= upper.Bounds.Top).ToList();
            int expected = upper.Category.Order - lower.Category.Order - 1;
            int targetIndex = target.Order - lower.Category.Order - 1;
            if (between.Count != expected || targetIndex < 0 || targetIndex >= between.Count) return 0;
            var candidate = between[targetIndex];
            if (candidate.Category != null) return 0;
            string prefix = SidebarCategoryPrefix(target.Name);
            if (prefix.Length == 0 || SidebarCategoryPrefix(candidate.RawText) != prefix) return 0;
            candidateBounds = candidate.Bounds;
            // Move the candidate toward the middle, away from either clipping edge.
            // Verify the resulting movement and full bounds before trusting any reread.
            return candidate.Bounds.Top + candidate.Bounds.Height / 2 < frameHeight * 590.0 / 1080 ? -3 : 3;
        }

        private static string SidebarCategoryPrefix(string text)
        {
            string prefix = Regex.Replace(text ?? "", @"\s*[\(\[][^\)\]]+[\)\]]\s*$", "", RegexOptions.None, RegexTimeouts.RegexTimeout);
            return AchievementCatalog.Normalize(prefix);
        }

        internal static bool SidebarNudgeMoved(IList<AchievementCategoryEntryOnScreen> before,
            IList<AchievementCategoryEntryOnScreen> after, int signedNotches, int frameHeight,
            Rectangle candidateBounds, out Rectangle shiftedBounds)
        {
            shiftedBounds = Rectangle.Empty;
            if (before == null || after == null || signedNotches == 0 || frameHeight <= 0) return false;
            var movements = new List<int>();
            foreach (var group in before.Where(x => x.Category != null).GroupBy(x => x.Category.Id))
            {
                var matches = after.Where(x => x.Category?.Id == group.Key).Take(2).ToList();
                if (group.Count() == 1 && matches.Count == 1)
                    movements.Add(group.First().Bounds.Top - matches[0].Bounds.Top);
            }
            // Two distinct canonical labels must move together. A new OCR spelling
            // or an animated scrollbar alone is not proof of sidebar movement.
            if (movements.Count < 2) return false;
            movements.Sort();
            int movement = movements[movements.Count / 2];
            int tolerance = Math.Max(3, frameHeight * 8 / 1080);
            if (movement == 0 || Math.Sign(movement) != Math.Sign(signedNotches)
                || movements.Any(value => Math.Abs(value - movement) > tolerance)) return false;
            shiftedBounds = candidateBounds;
            shiftedBounds.Offset(0, -movement);
            return IsFullSidebarBounds(shiftedBounds, frameHeight);
        }

        private static string SidebarSignature(Bitmap frame, IEnumerable<AchievementCategoryEntryOnScreen> entries)
        {
            int scale = Math.Max(1, frame.Height / 540);
            var thumb = SidebarThumbBounds(frame);
            return (thumb.HasValue ? thumb.Value.Top / scale + ":" + thumb.Value.Bottom / scale : "no-thumb") + "|"
                + string.Join("|", entries.Where(x => x.Category != null).OrderBy(x => x.Bounds.Top)
                    .Select(x => x.Category.Id + ":" + x.Bounds.Top / scale));
        }

        internal static int SidebarScrollDirection(AchievementCategory target, IList<AchievementCategoryEntryOnScreen> entries)
        {
            var orders = entries.Where(x => x.Category != null).Select(x => x.Category.Order).ToList();
            if (orders.Count == 0) return 0;
            if (target.Order < orders.Min()) return -1;
            return target.Order > orders.Max() ? 1 : 0;
        }

        internal static Rectangle? SidebarThumbBounds(Bitmap frame)
        {
            if (frame == null || frame.Width < 1 || frame.Height < 1) return null;
            int x = Math.Min(frame.Width - 1, (int)(676.0 / 1920 * frame.Width));
            int top = (int)(180.0 / 1080 * frame.Height);
            int bottom = Math.Min(frame.Height - 1, (int)(992.0 / 1080 * frame.Height));
            int start = -1, longestTop = -1, longestLength = 0;
            int halfWidth = Math.Max(1, frame.Width / 1920);
            for (int y = top; y <= bottom + 1; y++)
            {
                int light = 0, samples = 0;
                if (y <= bottom)
                    for (int sx = Math.Max(0, x - halfWidth); sx <= Math.Min(frame.Width - 1, x + halfWidth); sx++)
                    {
                        Color c = frame.GetPixel(sx, y);
                        if (c.R >= 175 && c.G >= 170 && c.B >= 155 && Math.Abs(c.R - c.G) <= 24 && Math.Abs(c.G - c.B) <= 24) light++;
                        samples++;
                    }
                bool thumb = samples > 0 && light * 2 > samples;
                if (thumb && start < 0) start = y;
                if (!thumb && start >= 0)
                {
                    if (y - start > longestLength) { longestTop = start; longestLength = y - start; }
                    start = -1;
                }
            }
            if (longestLength < Math.Max(8, frame.Height * 16 / 1080) || longestLength > (bottom - top) / 2) return null;
            return new Rectangle(Math.Max(0, x - halfWidth), longestTop, Math.Min(frame.Width - 1, x + halfWidth) - Math.Max(0, x - halfWidth) + 1, longestLength);
        }

        internal static bool SidebarAtTop(Bitmap frame)
        {
            var thumb = SidebarThumbBounds(frame);
            return thumb.HasValue && thumb.Value.Top <= (int)(180.0 / 1080 * frame.Height) + Math.Max(2, frame.Height * 4 / 1080);
        }

        internal static bool SidebarAtBottom(Bitmap frame)
        {
            var thumb = SidebarThumbBounds(frame);
            return thumb.HasValue && thumb.Value.Bottom >= (int)(992.0 / 1080 * frame.Height) - Math.Max(2, frame.Height * 4 / 1080);
        }

        private void LogCoverageSummary(AchievementScanResult result)
        {
            int verified = result.Categories.Count(x => x.ReachedBottom && x.Issues.Count == 0);
            var missing = catalog.Categories.OrderBy(x => x.Order).Where(category =>
                !result.Categories.Any(x => x.CategoryId == category.Id && x.ReachedBottom && x.Issues.Count == 0)).ToList();
            Logger.Info("Achievement coverage: verified {0}/{1} categories, attempted {2}, missing {3}; completed total initial {4}, final {5}; retained {6} completed IDs from {7} rows; cancelled {8}",
                verified, catalog.Categories.Count, result.Categories.Count, missing.Count,
                result.ExpectedCompleted?.ToString() ?? "unreadable", result.FinalExpectedCompleted?.ToString() ?? "unreadable",
                result.Completed.Count, result.Observations.Count, result.Cancelled);
            foreach (var category in missing)
            {
                var coverage = result.Categories.FirstOrDefault(x => x.CategoryId == category.Id);
                string reason;
                if (!selectionFailures.TryGetValue(category.Id, out reason))
                {
                    if (coverage == null) reason = "Not visited.";
                    else if (coverage.Issues.Count > 0) reason = string.Join("; ", coverage.Issues);
                    else reason = "Scrollbar bottom was not verified.";
                }
                Logger.Warn("Achievement category missing {0} '{1}': {2}; retained pages {3}, bottom verified {4}",
                    category.Id, category.Name, reason, coverage?.Pages ?? 0, coverage?.ReachedBottom ?? false);
            }
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
            frame.Save(Path.Combine(AchievementsLogDirectory, CategoryEvidencePrefix + category + "_page_" + page + ".png"));
        }
        private string SaveNavigationFrame(Bitmap frame, string step)
        {
            if (navigationEvidence >= MaximumNavigationEvidence)
            {
                if (!evidenceLimitLogged)
                {
                    Logger.Warn("Achievement navigation evidence limit {0} reached; further navigation states are logged without additional images", MaximumNavigationEvidence);
                    evidenceLimitLogged = true;
                }
                return null;
            }
            try
            {
                string path = Path.GetFullPath(Path.Combine(AchievementsLogDirectory, DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff") + "_" + step + ".png"));
                frame.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                navigationEvidence++;
                Logger.Debug("Achievement navigation screenshot: {0}", path);
                return path;
            }
            catch (Exception ex) { Logger.Warn(ex, "Could not save achievement navigation evidence"); return null; }
        }
    }
}
