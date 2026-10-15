using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using WindowsInput.Native;

namespace InventoryKamera.Tests
{
    /// <summary>Small offline regression harness. This program never sends input to the game.</summary>
    internal static class AchievementTests
    {
        private static int failures;
        private static int passed;

        private static int Main(string[] args)
        {
            string screenshot = null, captureDirectory = null, sidebarDirectory = null, categoryCaptureDirectory = null,
                selectionCaptureDirectory = null;
            for (int index = 0; index < args.Length; index++)
            {
                if ((args[index] != "--screenshot" && args[index] != "--capture-directory" &&
                    args[index] != "--sidebar-directory" && args[index] != "--category-capture-directory" &&
                    args[index] != "--selection-capture-directory") || index + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Usage: AchievementTests.exe [--screenshot <reference image path>] [--capture-directory <achievement capture directory>] [--sidebar-directory <navigation capture directory>] [--category-capture-directory <saved category and completion failure frames>] [--selection-capture-directory <position-sensitive category frames>]");
                    return 2;
                }
                if (args[index] == "--screenshot") screenshot = args[++index];
                else if (args[index] == "--capture-directory") captureDirectory = args[++index];
                else if (args[index] == "--sidebar-directory") sidebarDirectory = args[++index];
                else if (args[index] == "--category-capture-directory") categoryCaptureDirectory = args[++index];
                else selectionCaptureDirectory = args[++index];
            }
            Run("Compatible achievement schemas", ExportSchemas);
            Run("Zero completions and partial export files", EmptyAndPartialExports);
            Run("Category and duplicate-title separation", DuplicateTitles);
            Run("Numeric tier goals and ambiguity", TierResolution);
            Run("OCR 0/O title repair requires independent evidence", GuardedConfusableTitles);
            Run("Tier completion expansion and conflicting stars", TierCompletions);
            Run("Overlapping pages and provisional rereads", OverlapAndRereads);
            Run("Conflicting overlap remains unresolved", StickyCompletionConflict);
            Run("Measured movement scales page scrolling", AdaptivePageScrolling);
            Run("Completeness requires coverage and stable totals", Completeness);
            Run("Sidebar scrolling requires positive thumb and ordering evidence", SidebarNavigationEvidence);
            Run("Straight Roman strokes require corroborated category suffixes", GuardedSidebarRomans);
            Run("Local unreadable category retry is bounded and stops safely", LocalCategoryRetry);

            if (screenshot != null) Run("Reference screenshot OCR replay", () => ScreenshotReplay(screenshot));
            if (captureDirectory != null) Run("Real capture headers, clipped cards and sidebar replay", () => CaptureReplay(captureDirectory));
            if (sidebarDirectory != null) Run("Saved sidebar navigation and reset replay", () => SidebarReplay(sidebarDirectory));
            if (categoryCaptureDirectory != null)
            {
                Run("Saved wrapped category suffixes and selection replay", () => CategorySuffixCaptureReplay(categoryCaptureDirectory));
                Run("Saved completed stamp replay", () => CompletedStampCaptureReplay(categoryCaptureDirectory));
            }
            if (selectionCaptureDirectory != null)
                Run("Position-sensitive sidebar category selection replay", () => SelectionCaptureReplay(selectionCaptureDirectory));
            Console.WriteLine("Achievement regressions: {0} passed, {1} failed.{2}", passed, failures,
                args.Length == 0 ? " Screenshot replay was not requested." : string.Empty);
            return failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine("FAIL " + name + ": " + exception.GetBaseException().Message);
            }
        }

        private static void ExportSchemas()
        {
            var result = new AchievementScanResult { Complete = true };
            result.Completed.Add(1001, 0);
            result.Completed.Add(2001, 1);
            JsonEqual(@"{""achievement"":{""0"":{""1001"":true},""1"":{""2001"":true}}}",
                AchievementExport.ToPaimon(result));
            JsonEqual(@"{""achievements"":{""1001"":{""done"":true},""2001"":{""done"":true}}}",
                AchievementExport.ToSeelie(result));
            JsonEqual(@"{""Version"":1,""Data"":{""1001"":0,""2001"":1}}", AchievementExport.ToAkasha(result));
        }

        private static void EmptyAndPartialExports()
        {
            var result = new AchievementScanResult();
            result.Categories.Add(new AchievementCategoryScan { CategoryId = 0, Pages = 1 });
            Check(result.HasData, "A scanned page with zero completions must remain exportable.");
            JsonEqual(@"{""achievement"":{}}", AchievementExport.ToPaimon(result));
            JsonEqual(@"{""achievements"":{}}", AchievementExport.ToSeelie(result));
            JsonEqual(@"{""Version"":1,""Data"":{}}", AchievementExport.ToAkasha(result));
            string directory = Path.Combine(Path.GetTempPath(), "InventoryKamera-achievement-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var paths = AchievementExport.Write(result, directory);
                Equal(4, paths.Count, "Export file count");
                Check(paths.All(path => File.Exists(path) && Path.GetFileName(path).Contains("_partial")),
                    "Every incomplete export filename must contain _partial.");
                var report = JObject.Parse(File.ReadAllText(paths.Single(path => Path.GetFileName(path).StartsWith("kamera_"))));
                Equal("InventoryKameraAchievements", (string)report["format"], "Report format");
                Equal(false, (bool)report["scan"]["Complete"], "Report completion flag");
                Equal(0, (int)report["completedCount"], "Report zero-completion count");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static void DuplicateTitles()
        {
            var catalog = FixtureCatalog();
            var first = catalog.Categories.Single(category => category.Id == 0);
            var second = catalog.Categories.Single(category => category.Id == 1);
            Equal(0, catalog.MatchCategory("Wonders of the World").Id, "Matched category");
            Equal(1001, AchievementCatalog.Resolve(first, "Shared Name", "Reach the top of the tower.", 5).Tier.Id,
                "First category's duplicate title");
            Equal(1002, AchievementCatalog.Resolve(first, "Shared Name", "Open 10 chests.", 5).Tier.Id,
                "Description separates two same-title families");
            Equal(2001, AchievementCatalog.Resolve(second, "Shared Name", "Reach the top of the tower.", 5).Tier.Id,
                "Second category's duplicate title");
            Check(!AchievementCatalog.Resolve(first, "Shared Name", "", 5).Success,
                "A duplicate title without distinguishing description must remain ambiguous.");
        }

        private static void TierResolution()
        {
            var catalog = FixtureCatalog();
            var category = catalog.Categories[0];
            var match = AchievementCatalog.Resolve(category, "Harvest", "Collect 50 flowers.", null);
            Check(match.Success, "An exact numeric goal should resolve the tier.");
            Equal(1102, match.Tier.Id, "Tier selected by goal 50");
            Check(!AchievementCatalog.Resolve(category, "Harvest", "Collect 51 flowers.", null).Success,
                "A conflicting visible goal must not be corrected to a nearby tier.");
            Check(!AchievementCatalog.Resolve(category, "Harvest", "", null).Success,
                "A repeated tier title without description or reward must remain ambiguous.");
            var differentTitles = AchievementCatalog.Resolve(category, "Tundra Gunsmith (II)", "Build 20 weapons.", 10);
            Check(differentTitles.Success, "Families may use a different title on each tier.");
            Equal(1302, differentTitles.Tier.Id, "A family's renamed second tier");
        }

        private static void TierCompletions()
        {
            var catalog = FixtureCatalog();
            var family = catalog.Categories[0].Families.Single(value => value.Id == 1101);
            var last = family.Tiers[2];
            var unfinished = new AchievementObservation { CompletedStars = 2, Progress = "40/100" };
            AchievementRecognizer.Reconcile(unfinished, family, last);
            Check(string.IsNullOrEmpty(unfinished.Error), "A visible unfinished third tier with two stars is consistent.");
            Sequence(new[] { 1101, 1102 }, unfinished.CompletedIds, "Earlier tiers of unfinished third tier");
            var completed = new AchievementObservation { CompletedStars = 3, CompletedText = true };
            AchievementRecognizer.Reconcile(completed, family, last);
            Sequence(new[] { 1101, 1102, 1103 }, completed.CompletedIds, "Completed last tier expands all IDs");
            var claimable = new AchievementObservation { CompletedStars = 2, Claimable = true };
            AchievementRecognizer.Reconcile(claimable, family, last);
            Sequence(new[] { 1101, 1102, 1103 }, claimable.CompletedIds, "Claimable tier before final star lights");
            var conflicting = new AchievementObservation { CompletedStars = 3, Progress = "40/100" };
            AchievementRecognizer.Reconcile(conflicting, family, last);
            Check(!string.IsNullOrEmpty(conflicting.Error) && conflicting.CompletedIds.Count == 0,
                "Conflicting completion evidence must not export completed IDs.");
        }

        private static void GuardedConfusableTitles()
        {
            const string description = "Successfully conduct cultural exchange with the hilichurls in \"Language Exchange.\"";
            var catalog = AchievementCatalog.FromJson(new JObject {
                ["10"] = Category("Olah!: Series I", 1,
                    Definition(84501, "...Odomu?", description, 5),
                    Definition(84502, "Yo dala?", "Successfully conduct cultural exchange with the hilichurls in \"Poetry Exchange.\"", 5))
            }.ToString());
            var category = catalog.Categories[0];
            var resolved = AchievementCatalog.Resolve(category, "...0Odomu?", description, 5);
            Check(resolved.Success, "The duplicate 0/O glyph is recoverable with exact independent evidence.");
            Equal(84501, resolved.Tier.Id, "Canonical repaired Olah achievement ID");
            Check(!AchievementCatalog.Resolve(category, "...0Odomu?", description, null).Success,
                "OCR-confusable title repair requires a known reward.");
            Check(!AchievementCatalog.Resolve(category, "...0Odomu?", description, 10).Success,
                "A conflicting reward must reject OCR-confusable title repair.");
            Check(!AchievementCatalog.Resolve(category, "...0Odomu?", "Successfully conduct cultural exchange with the hilichurls in \"Poetry Exchange.\"", 5).Success,
                "A neighboring achievement's description must not justify the title repair.");
            var ambiguous = AchievementCatalog.FromJson(new JObject {
                ["10"] = Category("Olah!: Series I", 1,
                    Definition(84501, "...Odomu?", description, 5),
                    Definition(84503, "...Odomu?", description, 5))
            }.ToString());
            Check(!AchievementCatalog.Resolve(ambiguous.Categories[0], "...0Odomu?", description, 5).Success,
                "Indistinguishable repaired candidates must remain ambiguous.");
            var numeric = AchievementCatalog.FromJson(new JObject {
                ["10"] = Category("Olah!: Series I", 1,
                    Definition(84501, "...Odomu?", "Collect 10 flowers from the northern garden.", 5))
            }.ToString());
            Check(!AchievementCatalog.Resolve(numeric.Categories[0], "...0Odomu?", "Collect 11 flowers from the northern garden.", 5).Success,
                "Even nearly identical descriptions cannot repair conflicting numeric goals.");
        }

        private static void OverlapAndRereads()
        {
            var catalog = FixtureCatalog();
            var category = catalog.Categories[0];
            var result = new AchievementScanResult();
            var accumulator = new AchievementScanAccumulator(result);
            var provisional = Row(null, "Shared Name", "Reach the top of the tower.");
            provisional.Error = "Title match was uncertain.";
            accumulator.AddPage(category, 1, new List<AchievementObservation> { provisional });
            var resolved = Row(1001, provisional.Title, provisional.Description, 1001);
            accumulator.AddPage(category, 2, new List<AchievementObservation> { resolved });
            Equal(1, result.Observations.Count, "Resolved reread replaces exact provisional row");
            Equal(1, result.Completed.Count, "Resolved completion count");
            Check(string.IsNullOrEmpty(result.Observations[0].Error), "Provisional error must not survive a good reread.");
            accumulator.AddPage(category, 3, new List<AchievementObservation> { Row(1001, resolved.Title, resolved.Description, 1001) });
            Equal(1, result.Completed.Count, "Overlap must not count the same ID twice");
            accumulator.AddPage(category, 4, new List<AchievementObservation> { provisional });
            Equal(1, result.Observations.Count, "A noisy provisional reread must not duplicate the known row");
            Check(string.IsNullOrEmpty(result.Observations[0].Error), "A later uncertain read must not replace a verified row.");
            var other = Row(1002, "Shared Name", "Open 10 chests.", 1002);
            Check(AchievementScraper.RowsOverlap(new[] { resolved }, new[] { resolved, other }), "Shared row overlap");
            Check(!AchievementScraper.RowsOverlap(new[] { resolved }, new[] { other }), "Same title is not a family identity");
            accumulator.AddPage(catalog.Categories[1], 1, new List<AchievementObservation> {
                Row(2001, resolved.Title, resolved.Description, 2001)
            });
            Equal(2, result.Observations.Count, "Same title/description in another category stays separate");
            Equal(2, result.Completed.Count, "Category-separated completed IDs");
        }

        private static void StickyCompletionConflict()
        {
            var catalog = FixtureCatalog();
            var category = catalog.Categories[0];
            var result = new AchievementScanResult { ExpectedCompleted = 0, FinalExpectedCompleted = 0 };
            var accumulator = new AchievementScanAccumulator(result);
            accumulator.AddPage(category, 1, new List<AchievementObservation> {
                Row(1101, "Harvest", "Collect 100 flowers.", 1101, 1102)
            });
            Equal(2, result.Completed.Count, "Initial consistent completed predecessors");
            var conflicting = Row(1101, "Harvest", "Collect 100 flowers.", 1101, 1102, 1103);
            conflicting.Confidence = 86;
            accumulator.AddPage(category, 2, new List<AchievementObservation> { conflicting });
            Equal(0, result.Completed.Count, "Conflicting captures must remove the family's completed IDs");
            accumulator.AddPage(category, 3, new List<AchievementObservation> {
                Row(1101, "Harvest", "Collect 100 flowers.", 1101, 1102)
            });
            Equal(1, result.Observations.Count, "The conflicting family remains one observation");
            Check(!string.IsNullOrEmpty(result.Observations[0].Error),
                "A third clear capture must not erase the earlier completion contradiction.");
            Equal(0, result.Completed.Count, "Repeated original evidence must not restore conflicted completed IDs");
            foreach (var value in catalog.Categories)
                result.Categories.Add(new AchievementCategoryScan { CategoryId = value.Id, Pages = 3, ReachedBottom = true });
            accumulator.Finish(catalog);
            Check(!result.Complete, "A sticky unresolved contradiction must keep the scan partial even when totals agree.");
        }

        private static void AdaptivePageScrolling()
        {
            var previous = new[] { RowAt(1001, 400), RowAt(1101, 600) };
            var smallMovement = new[] { RowAt(1001, 328), RowAt(1101, 528) };
            Equal(33, AchievementScraper.AdjustScrollStep(4, previous, smallMovement, 840),
                "Four notches moving 72 pixels should grow toward a viewport jump");
            var fullPrevious = new[] { RowAt(1001, 700), RowAt(1101, 825) };
            var fullMovement = new[] { RowAt(1001, 150), RowAt(1101, 275) };
            Equal(34, AchievementScraper.AdjustScrollStep(32, fullPrevious, fullMovement, 840),
                "A 550-pixel jump should approximately retain its current step");
            Equal(4, AchievementScraper.AdjustScrollStep(4, previous,
                new[] { RowAt(2001, 328), RowAt(1301, 528) }, 840),
                "Missing overlap provides no movement calibration");
            Equal(4, AchievementScraper.AdjustScrollStep(4, previous,
                new[] { RowAt(1001, 400), RowAt(1101, 600) }, 840),
                "Stationary rows must not inflate the scroll step");
            Equal(48, AchievementScraper.AdjustScrollStep(32, previous,
                new[] { RowAt(1001, 388), RowAt(1101, 588) }, 840),
                "Measured tiny movement respects the maximum step of 48");
        }

        private static void Completeness()
        {
            var catalog = FixtureCatalog();
            var result = new AchievementScanResult { ExpectedCompleted = 0, FinalExpectedCompleted = 0 };
            foreach (var category in catalog.Categories)
                result.Categories.Add(new AchievementCategoryScan { CategoryId = category.Id, Pages = 1, ReachedBottom = true });
            var accumulator = new AchievementScanAccumulator(result);
            accumulator.Finish(catalog);
            Check(result.Complete && result.HasData, "Verified zero-completion scans are complete and exportable.");
            result.Categories[0].ReachedBottom = false;
            accumulator.Finish(catalog);
            Check(!result.Complete, "No movement without verified bottom must remain partial.");
            result.Categories[0].ReachedBottom = true;
            result.FinalExpectedCompleted = 1;
            accumulator.Finish(catalog);
            Check(!result.Complete, "Changing global counter must remain partial.");
            result.FinalExpectedCompleted = 0;
            result.Cancelled = true;
            accumulator.Finish(catalog);
            Check(!result.Complete, "Cancellation must remain partial.");
        }

        private static void ScreenshotReplay(string path)
        {
            var source = new JObject {
                ["0"] = Category("Wonders of the World", 1,
                    new JArray(Definition(3101, "Magnum Opus", "Hold 6 different Lunar Arcana at once.", 5),
                        Definition(3102, "Magnum Opus", "Hold 12 different Lunar Arcana at once.", 10)),
                    Definition(3201, "Winter Night's Letter", "Complete \"Secret Tsarist Assignment: Part I.\"", 5),
                    Definition(3301, "Tales of Monstrous Madness", "Collect the entire \"Toki Alley Tales\" series.", 5),
                    new JArray(Definition(3401, "Zoo Tycoon", "Use the Omni-Ubiquity Net item to capture 1 wild animal.", 5),
                        Definition(3402, "Zoo Tycoon", "Use the Omni-Ubiquity Net item to capture 30 wild animals.", 10),
                        Definition(3403, "Zoo Tycoon", "Use the Omni-Ubiquity Net item to capture 100 wild animals.", 20)),
                    new JArray(Definition(3501, "It's Yesterday Once More", "Activate 10 tunes in the Serenitea Pot using the Euphonium Unbound furnishing series.", 5),
                        Definition(3502, "It's Yesterday Once More", "Activate 30 tunes in the Serenitea Pot using the Euphonium Unbound furnishing series.", 10),
                        Definition(3503, "It's Yesterday Once More", "Activate 60 tunes in the Serenitea Pot using the Euphonium Unbound furnishing series.", 20)),
                    Definition(3601, "Overlooking View", "Reach the very top of Qingyun Peak.", 5))
            };
            var catalog = AchievementCatalog.FromJson(source.ToString());
            var recognizer = new AchievementRecognizer(catalog);
            using (var image = new Bitmap(path))
            {
                Equal(6, AchievementRecognizer.DetectRows(image).Count, "Only six full rows should be detected");
                Check(AchievementRecognizer.IsDetailScreen(image), "Reference image should be recognized as category details.");
                Equal((int?)1605, AchievementRecognizer.ReadCompletedTotal(image), "Global completed achievement counter");
                var rows = recognizer.ReadPage(image, catalog.Categories[0], 1);
                Equal(6, recognizer.LastOcrRowCount, "The first viewport OCRs all six rows");
                Equal(0, recognizer.LastReusedRowCount, "The first viewport has no cached rows");
                Equal(6, rows.Count, "Read full-row count");
                Sequence(new[] { "Magnum Opus", "Winter Night's Letter", "Tales of Monstrous Madness", "Zoo Tycoon", "It's Yesterday Once More", "Overlooking View" },
                    rows.Select(row => row.Title), "Read titles");
                Sequence(new[] { 10, 5, 5, 20, 20, 5 }, rows.Select(row => row.Reward ?? -1), "Read rewards");
                Equal("0/1", rows[0].Progress, "First unfinished progress");
                Equal("0/1", rows[1].Progress, "Second unfinished progress");
                Sequence(new[] { "2026-08-02", "2022-06-14", "2022-09-02", "2020-10-20" },
                    rows.Skip(2).Select(row => row.CompletionDate), "Completion dates");
                Equal((int?)201, rows[3].Total, "Zoo Tycoon completed total");
                Equal((int?)181, rows[4].Total, "It's Yesterday Once More completed total");
                Check(rows.All(row => string.IsNullOrEmpty(row.Error)), "Reference row errors: " + string.Join("; ", rows.Select(row => row.Error)));
                Sequence(new[] { 3101 }, rows[0].CompletedIds, "Only the preceding completed Magnum tier is exported");
                Equal(0, rows[1].CompletedIds.Count, "Second unfinished row completion count");
                Equal(9, rows.Sum(row => row.CompletedIds.Count), "Completed IDs include verified earlier tiers");
                Check(!AchievementScraper.ScrollbarAtBottom(image), "The first category page is not its bottom.");
                var reread = recognizer.ReadPage(image, catalog.Categories[0], 2);
                Equal(0, recognizer.LastOcrRowCount, "An identical second viewport needs no row OCR");
                Equal(6, recognizer.LastReusedRowCount, "An identical second viewport reuses all six rows");
                Equal(6, reread.Count, "Cached viewport row count");
                Check(reread.All(row => row.Page == 2), "Reused observations must carry the new page number.");
                Check(rows.All(row => row.Page == 1), "Updating cached page numbers must not mutate earlier observations.");
                Check(!ReferenceEquals(rows[0].CompletedIds, reread[0].CompletedIds),
                    "Reused observations require independent completed-ID lists.");
                reread[0].CompletedIds.Clear();
                Sequence(new[] { 3101 }, rows[0].CompletedIds,
                    "Mutating a reused completed-ID list must not change the first viewport");
            }
        }

        private static AchievementCatalog FixtureCatalog()
        {
            return AchievementCatalog.FromJson(new JObject {
                ["0"] = Category("Wonders of the World", 1,
                    Definition(1001, "Shared Name", "Reach the top of the tower.", 5),
                    Definition(1002, "Shared Name", "Open 10 chests.", 5),
                    new JArray(Definition(1101, "Harvest", "Collect 10 flowers.", 5),
                        Definition(1102, "Harvest", "Collect 50 flowers.", 10),
                        Definition(1103, "Harvest", "Collect 100 flowers.", 20)),
                    new JArray(Definition(1301, "Tundra Gunsmith (I)", "Build 10 weapons.", 5),
                        Definition(1302, "Tundra Gunsmith (II)", "Build 20 weapons.", 10),
                        Definition(1303, "Tundra Gunsmith (III)", "Build 30 weapons.", 20))),
                ["1"] = Category("Challenger: Series I", 2,
                    Definition(2001, "Shared Name", "Reach the top of the tower.", 5))
            }.ToString());
        }

        private static void CaptureReplay(string directory)
        {
            directory = Path.GetFullPath(directory);
            Check(Directory.Exists(directory), "The achievement capture directory does not exist: " + directory);
            string catalogPath = null;
            for (var ancestor = new DirectoryInfo(directory); ancestor != null; ancestor = ancestor.Parent)
            {
                var candidate = Path.Combine(ancestor.FullName, "inventorylists", "achievements.json");
                if (File.Exists(candidate)) { catalogPath = candidate; break; }
            }
            Check(catalogPath != null, "No inventorylists/achievements.json was found beside a capture-directory ancestor.");
            // Replay uses the catalog saved by the real scan and never downloads or sends game input.
            var catalog = AchievementCatalog.FromJson(File.ReadAllText(catalogPath));
            var category = catalog.Categories.Single(value => value.Id == 1);
            var recognizer = new AchievementRecognizer(catalog);
            var previewed = new List<AchievementObservation>();
            recognizer.RowPreview = (bitmap, observation) => previewed.Add(observation);
            var result = new AchievementScanResult();
            var accumulator = new AchievementScanAccumulator(result);
            var expectedIds = new[] { new[] { 80001, 80002, 80003, 80004, 80005 }, new[] { 80002, 80003, 80004, 80005, 80006 } };
            for (int page = 1; page <= 2; page++)
            {
                string path = Path.Combine(directory, "category_1_page_" + page + ".png");
                Check(File.Exists(path), "Required real capture is missing: " + path);
                previewed.Clear();
                using (var image = new Bitmap(path))
                {
                    Equal(page == 1, AchievementScraper.ScrollbarAtTop(image),
                        "Mortal Travails page " + page + " scrollbar top position excludes the pinned header");
                    Equal(page == 2, AchievementScraper.ScrollbarAtBottom(image),
                        "Mortal Travails page " + page + " scrollbar bottom position");
                    Equal(5, AchievementRecognizer.DetectRows(image).Count,
                        "Mortal Travails page " + page + " excludes the pinned header and clipped cards");
                    var rows = recognizer.ReadPage(image, category, page);
                    Equal(5, rows.Count, "Mortal Travails page " + page + " full-card count");
                    Check(rows.All(row => string.IsNullOrEmpty(row.Error)),
                        "Real completed-card errors on page " + page + ": " + string.Join("; ", rows.Select(row => row.Title + ": " + row.Error)));
                    Sequence(expectedIds[page - 1].Select(id => (int?)id), rows.Select(row => row.MatchedId),
                        "All full completed cards resolve to their canonical IDs on page " + page);
                    Check(rows.All(row => row.CompletedText && row.CompletedIds.Count == 1),
                        "Every full Mortal Travails card must contribute one confirmed completion.");
                    Check(rows.All(row => row.Top >= image.Height * .26 &&
                        row.Title.IndexOf("Achievement Progress", StringComparison.OrdinalIgnoreCase) < 0),
                        "The pinned progress header must never become an achievement observation.");
                    Equal(recognizer.LastOcrRowCount, previewed.Count, "Only newly recognized complete cards receive previews");
                    Check(previewed.All(row => expectedIds[page - 1].Contains(row.MatchedId ?? -1)),
                        "The progress header and clipped top/bottom fragments must never receive a row preview.");
                    if (page == 2) Check(previewed.All(row => row.MatchedId != 80001),
                        "The clipped top of The Wind and The Star Traveler must not receive a preview.");
                    accumulator.AddPage(category, page, rows);
                }
            }
            Sequence(Enumerable.Range(80001, 6), result.Completed.Keys.OrderBy(id => id),
                "The overlapping completed category contributes exactly six distinct IDs");

            string sidebarPath = Path.Combine(directory, "category_3_page_3.png");
            Check(File.Exists(sidebarPath), "Required real sidebar capture is missing: " + sidebarPath);
            using (var image = new Bitmap(sidebarPath))
            {
                var entries = recognizer.ReadCategories(image);
                var target = entries.SingleOrDefault(entry => entry.Category != null && entry.Category.Id == 4);
                Check(target != null, "The complete Mondstadt category name must be recognized despite nearby percentages and clipped sidebar cards.");
                Equal("Mondstadt: The City of Wind and Song", target.Category.Name, "Real sidebar target category");
                Check(!target.Selected && target.Bounds.Top >= image.Height * .16 && target.Bounds.Bottom <= image.Height * .935,
                    "The recognized Mondstadt category must be eligible for the scanner's safe category selection.");
                Check(entries.Any(entry => entry.Selected && entry.Category != null && entry.Category.Id == 3),
                    "The currently selected Hero's Journey category remains recognized.");
            }

            var olah = catalog.Categories.Single(value => value.Id == 10);
            var olahRecognizer = new AchievementRecognizer(catalog);
            var olahResult = new AchievementScanResult();
            var olahAccumulator = new AchievementScanAccumulator(olahResult);
            for (int page = 1; page <= 2; page++)
            {
                string path = Path.Combine(directory, "category_10_page_" + page + ".png");
                Check(File.Exists(path), "Required real Olah capture is missing: " + path);
                using (var image = new Bitmap(path))
                {
                    var rows = olahRecognizer.ReadPage(image, olah, page);
                    Equal(2, rows.Count, "Olah complete-card count on page " + page);
                    Check(rows.All(row => string.IsNullOrEmpty(row.Error)),
                        "The real Olah reread must not leave an unresolved 0/O variant: " + string.Join("; ", rows.Select(row => row.Title + ": " + row.Error)));
                    Sequence(new[] { (int?)84501, (int?)84502 }, rows.Select(row => row.MatchedId),
                        "Both real Olah cards resolve on page " + page);
                    olahAccumulator.AddPage(olah, page, rows);
                }
            }
            Equal(2, olahResult.Observations.Count, "Olah overlapping rereads do not create a third provisional duplicate");
            Check(olahResult.Observations.All(row => string.IsNullOrEmpty(row.Error)),
                "No duplicate Olah error remains after both real pages are reconciled.");
            Sequence(new[] { 84501, 84502 }, olahResult.Completed.Keys.OrderBy(id => id),
                "Olah exports exactly its two canonical completions");

            string realmPath = Directory.GetFiles(directory, "*_category_19_not_visible.png")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).LastOrDefault();
            if (realmPath != null)
            {
                using (var image = new Bitmap(realmPath))
                {
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        var entries = recognizer.ReadCategories(image);
                        var target = entries.SingleOrDefault(entry => entry.Category != null && entry.Category.Id == 19);
                        Check(target != null && !target.Selected,
                            "Realm Beyond II must remain recognizable on successive sidebar read " + attempt);
                        Equal("A Realm Beyond: Series II", target.Category.Name, "Realm II canonical category name");
                        Check(target.Bounds.Top >= image.Height * .16 && target.Bounds.Bottom <= image.Height * .935,
                            "Realm II has full eligible category-selection bounds on read " + attempt);
                        Check(Math.Abs(target.Bounds.Top - image.Height * 855.0 / 1080) <= 3 &&
                            Math.Abs(target.Bounds.Bottom - image.Height * 951.0 / 1080) <= 3,
                            "Realm II bounds follow the visible full tile, rather than just its title.");
                        Check(entries.Any(entry => entry.Selected && entry.Category != null && entry.Category.Id == 18),
                            "The selected Realm Beyond I must retain its canonical category on read " + attempt);
                    }
                }
            }
        }

        private static AchievementCatalog LoadCaptureCatalog(string directory)
        {
            string catalogPath = null;
            for (var ancestor = new DirectoryInfo(Path.GetFullPath(directory)); ancestor != null; ancestor = ancestor.Parent)
            {
                string candidate = Path.Combine(ancestor.FullName, "inventorylists", "achievements.json");
                if (File.Exists(candidate)) { catalogPath = candidate; break; }
            }
            Check(catalogPath != null, "Category replay requires the real saved catalog beside a capture-directory ancestor.");
            return AchievementCatalog.FromJson(File.ReadAllText(catalogPath));
        }

        private static void CategorySuffixCaptureReplay(string directory)
        {
            directory = Path.GetFullPath(directory);
            var catalog = LoadCaptureCatalog(directory);
            var recognizer = new AchievementRecognizer(catalog);
            Check(catalog.MatchCategory("Fontaine: Dance of the Dew-White Springs") == null
                && catalog.MatchCategory("Natlan: The Land of Fire and Competition") == null,
                "An unreadable Roman suffix cannot choose between categories with the same title base.");
            int[][] expected = { new[] { 30, 31, 35, 38, 37, 41, 42, 45 }, new[] { 45, 46, 47, 48, 50, 52, 51, 55 } };
            int[] targets = { 42, 51 };
            int[] targetTops = { 792, 794 };
            for (int index = 0; index < targets.Length; ++index)
            {
                string path = Directory.GetFiles(directory, "*_category_" + targets[index] + "_visible_range_unreadable.png")
                    .OrderBy(value => Path.GetFileName(value), StringComparer.Ordinal).LastOrDefault();
                Check(path != null, "The saved category " + targets[index] + " failure frame is missing.");
                using (var frame = new Bitmap(path))
                {
                    var entries = recognizer.ReadCategories(frame).OrderBy(entry => entry.Bounds.Top).ToList();
                    PrintCategoryEntries(path, entries);
                    Sequence(expected[index].Select(id => (int?)id), entries.Select(entry => entry.Category?.Id),
                        "Every full sidebar row has its exact canonical ID in category " + targets[index] + " failure frame");
                    Check(entries.All(entry => !entry.Selected), "The failed-frame visible categories are unselected.");
                    Check(entries.All(entry => entry.Bounds.Top >= frame.Height * .16 && entry.Bounds.Bottom <= frame.Height * .935),
                        "Only full eligible sidebar tiles may be returned; the clipped bottom fragment must remain excluded.");
                    var target = entries.Single(entry => entry.Category.Id == targets[index]);
                    Check(Math.Abs(target.Bounds.Top - frame.Height * targetTops[index] / 1080.0) <= 3
                        && Math.Abs(target.Bounds.Height - frame.Height * 96.0 / 1080.0) <= 3,
                        "The wrapped first-series category retains its whole tile rather than just the title line.");
                    for (int row = 1; row < entries.Count; ++row)
                        Check(entries[row - 1].Bounds.Bottom <= entries[row].Bounds.Top, "Recognized category tiles cannot overlap.");
                    Console.WriteLine("  REPLAY category {0}: full IDs [{1}]; first-series suffix retained", targets[index], string.Join(",", expected[index]));
                }
            }
            foreach (int selectedId in new[] { 45, 55 })
            {
                string path = Path.Combine(directory, "category_" + selectedId + "_page_1.png");
                Check(File.Exists(path), "Saved selected category reference is missing: " + path);
                using (var frame = new Bitmap(path))
                {
                    var entries = recognizer.ReadCategories(frame);
                    PrintCategoryEntries(path, entries);
                    Sequence(new[] { (int?)selectedId }, entries.Where(entry => entry.Selected).Select(entry => entry.Category?.Id),
                        "The selected second-series tile must verify its own canonical category");
                    var selected = entries.Single(entry => entry.Selected);
                    Check(selected.Bounds.Top >= frame.Height * .16 && selected.Bounds.Bottom <= frame.Height * .935,
                        "The selected category has complete eligible selection-verification bounds.");
                    Check(selected.Category.Id != (selectedId == 45 ? 42 : 51),
                        "A selected second-series category cannot confirm the neighboring first-series category.");
                }
            }
        }

        private static void PrintCategoryEntries(string path, IEnumerable<AchievementCategoryEntryOnScreen> entries)
        {
            Console.WriteLine("  REPLAY sidebar {0}", Path.GetFileName(path));
            foreach (var entry in entries)
                Console.WriteLine("    ID={0}, selected={1}, bounds={2}, raw=\"{3}\"",
                    entry.Category == null ? "unresolved" : entry.Category.Id.ToString(), entry.Selected, entry.Bounds,
                    (entry.RawText ?? string.Empty).Replace("\r", string.Empty).Replace("\n", " | "));
        }

        private static void CompletedStampCaptureReplay(string directory)
        {
            directory = Path.GetFullPath(directory);
            var catalog = LoadCaptureCatalog(directory);
            var wonders = catalog.Categories.Single(category => category.Id == 0);
            int[] pages = { 112, 121 }, ids = { 82082, 82175 };
            string[] dates = { "2021-02-07", "2023-08-15" };
            for (int index = 0; index < pages.Length; ++index)
            {
                string path = Path.Combine(directory, "category_0_page_" + pages[index] + ".png");
                Check(File.Exists(path), "Saved completed-stamp failure page is missing: " + path);
                using (var frame = new Bitmap(path))
                {
                    var row = new AchievementRecognizer(catalog).ReadPage(frame, wonders, pages[index])
                        .SingleOrDefault(value => value.MatchedId == ids[index]);
                    Check(row != null, "The saved completed row must retain its canonical ID " + ids[index]);
                    Check(row.CompletedText && row.CompletedStars == 1 && string.IsNullOrEmpty(row.Error),
                        "The complete stamp must resolve the known single achievement cleanly: " + row.Title + ": " + row.Error);
                    Equal(dates[index], row.CompletionDate, "Completed single achievement date");
                    Sequence(new[] { ids[index] }, row.CompletedIds, "Previously unresolved single achievement contributes its confirmed completion");
                    Console.WriteLine("  REPLAY page {0}: completed canonical ID {1}, date {2}", pages[index], ids[index], row.CompletionDate);
                }
                var family = wonders.Families.Single(value => value.Id == ids[index]);
                var conflict = new AchievementObservation { CompletedText = true, CompletedStars = 1, CompletionDate = dates[index], Progress = "0/1" };
                AchievementRecognizer.Reconcile(conflict, family, family.Tiers[0]);
                Check(!string.IsNullOrEmpty(conflict.Error) && conflict.CompletedIds.Count == 0,
                    "A stamp/date reread cannot override positive unfinished-progress evidence.");
            }
        }

        private static void SelectionCaptureReplay(string directory)
        {
            directory = Path.GetFullPath(directory);
            var catalog = LoadCaptureCatalog(directory);
            int[] fontaineIds = { 35, 38, 37, 41, 42, 45, 46, 47 };
            int[] natlanIds = { 46, 47, 48, 50, 52, 51, 55, 54 };
            string[] paths = Directory.GetFiles(directory, "*_visible_range_unreadable.png")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
            Equal(5, paths.Length, "Selection replay requires four current failures plus the former Natlan II failure");
            foreach (string path in paths)
            {
                string name = Path.GetFileName(path);
                bool fontaine = name.Contains("_category_45_") || name.Contains("_category_46_");
                bool firstNatlan = name.Contains("20261014_054219689_");
                int selectedId = fontaine ? 42 : firstNatlan ? 51 : 46;
                int selectedTop = fontaine ? 606 : firstNatlan ? 678 : 174;
                int targetId = name.Contains("_category_45_") ? 45 : name.Contains("_category_46_") ? 46 : 55;
                using (var frame = new Bitmap(path))
                {
                    var entries = AssertSidebarRows(catalog, frame, fontaine ? fontaineIds : natlanIds, selectedId, name);
                    var target = entries.Single(entry => entry.Category.Id == targetId);
                    Check(!target.Selected, "The requested category remains distinct from the positively selected neighboring category.");
                    Check(target.Bounds.Height >= frame.Height * 80.0 / 1080,
                        "The requested Roman-suffix title must retain a full tile for safe category selection.");
                    var selected = entries.Single(entry => entry.Selected);
                    Equal((int)Math.Round(frame.Width * 52.0 / 1920), selected.Bounds.Left,
                        "Selected tile bounds include the wider highlighted tile");
                    Check(Math.Abs(selected.Bounds.Width - frame.Width * 616.0 / 1920) <= 2
                        && Math.Abs(selected.Bounds.Height - frame.Height * 100.0 / 1080) <= 3
                        && Math.Abs(selected.Bounds.Top - frame.Height * selectedTop / 1080.0) <= 3,
                        "The selected canonical category retains its full highlighted bounds.");
                }
            }
        }

        private static void GuardedSidebarRomans()
        {
            string[] suffixes = { "I", "II", "III" };
            Equal("II", AchievementRecognizer.ResolveSidebarRomanEvidence(true, true, 2, suffixes,
                "1", "II", "(II)", "(II)"), "Two verified straight strokes repair a collapsed numeral read");
            Equal(string.Empty, AchievementRecognizer.ResolveSidebarRomanEvidence(true, true, 2, suffixes,
                "1", "I", "(I)", "(I)"), "Physical stroke count cannot override the corroborating suffix");
            Equal(string.Empty, AchievementRecognizer.ResolveSidebarRomanEvidence(false, true, 2, suffixes,
                "1", "II", "(II)", "(II)"), "A suffix cannot repair an unreadable independent title prefix");
            Equal(string.Empty, AchievementRecognizer.ResolveSidebarRomanEvidence(true, false, 2, suffixes,
                "1", "II", "(II)", "(II)"), "An unbracketed stroke cannot repair a series label");
            Equal(string.Empty, AchievementRecognizer.ResolveSidebarRomanEvidence(true, true, 2, new[] { "I", "III" },
                "1", "II", "(II)", "(II)"), "A physical count cannot create a nonexistent canonical suffix");
            for (int count = 1; count <= 3; ++count)
            {
                using (var frame = new Bitmap(32, 28))
                using (var graphics = Graphics.FromImage(frame))
                {
                    graphics.Clear(Color.Black);
                    for (int column = 0; column < count; ++column)
                        graphics.FillRectangle(Brushes.White, 5 + column * 7, 4, 2, 20);
                    Equal((int?)count, AchievementRecognizer.ReadSidebarStraightICount(frame),
                        "Independent straight-stem evidence counts only the actual Roman strokes");
                }
            }
            foreach (string glyph in new[] { "V", "X", string.Empty })
            {
                using (var frame = new Bitmap(32, 32))
                using (var graphics = Graphics.FromImage(frame))
                using (var pen = new Pen(Color.White, 2))
                {
                    graphics.Clear(Color.Black);
                    if (glyph == "V")
                    {
                        graphics.DrawLine(pen, 5, 4, 15, 26);
                        graphics.DrawLine(pen, 15, 26, 25, 4);
                    }
                    else if (glyph == "X")
                    {
                        graphics.DrawLine(pen, 5, 4, 25, 26);
                        graphics.DrawLine(pen, 25, 4, 5, 26);
                    }
                    Check(!AchievementRecognizer.ReadSidebarStraightICount(frame).HasValue,
                        "Diagonal or blank numeral pixels cannot become a physical straight-I count: '" + glyph + "'");
                    Equal(string.Empty, AchievementRecognizer.ResolveSidebarRomanEvidence(true, true, null, suffixes,
                        glyph, glyph, glyph, glyph), "Diagonal or absent suffix evidence cannot force an I category");
                }
            }
        }

        private static void LocalCategoryRetry()
        {
            var catalog = AchievementCatalog.FromJson(new JObject
            {
                ["42"] = Category("Fontaine: Dance of the Dew-White Springs (I)", 53,
                    Definition(80252, "First", "First series.", 5)),
                ["45"] = Category("Fontaine: Dance of the Dew-White Springs (II)", 54,
                    Definition(80268, "Second", "Second series.", 5)),
                ["46"] = Category("Fontaine: Dance of the Dew-White Springs (III)", 55,
                    Definition(80284, "Third", "Third series.", 5))
            }.ToString());
            var target = catalog.Categories.Single(category => category.Id == 45);
            var navigation = new LocalSelectionNavigation();
            var scraper = new AchievementScraper(catalog, navigation,
                frame => LocalSelectionEntries(catalog, navigation, true), frame => true);
            Check(scraper.SelectCategory(target), "A moved sidebar must reacquire and confirm the actual canonical target.");
            Equal(1, navigation.Scrolls.Count, "A category label gets only one local nudge");
            Check(navigation.Scrolls.All(step => Math.Abs(step) == 3), "The local nudge remains small.");
            Equal(1, navigation.ClickCount, "A category is clicked only after its canonical label is reacquired.");
            Equal(0, navigation.Keys.Count, "A local label miss must not exit or reopen the achievement menu.");
            Check(navigation.CaptureCount <= 12, "Local movement, reacquisition and confirmation remain bounded.");

            navigation = new LocalSelectionNavigation();
            scraper = new AchievementScraper(catalog, navigation,
                frame => LocalSelectionEntries(catalog, navigation, false), frame => true);
            Check(!scraper.SelectCategory(target), "A persistent unknown suffix must be deferred after the local retry.");
            Equal(AchievementSelectionFailureKind.LabelUnreadable, scraper.LastSelectionFailureKind,
                "A suffix OCR miss remains distinct from a navigation failure");
            Equal(1, navigation.Scrolls.Count, "An unreadable label cannot keep nudging the sidebar");
            Equal(0, navigation.ClickCount, "An unknown category cannot be clicked from contextual order alone.");
            Equal(0, navigation.Keys.Count, "A persistent local label miss cannot spend global menu recovery.");
            Check(navigation.CaptureCount <= 12, "The unreadable local retry must terminate promptly.");

            navigation = new LocalSelectionNavigation { MoveAfterNudge = false };
            scraper = new AchievementScraper(catalog, navigation,
                frame => LocalSelectionEntries(catalog, navigation, true), frame => true);
            Check(!scraper.SelectCategory(target), "A clear reread alone cannot verify that the requested nudge moved the sidebar.");
            Equal(1, navigation.Scrolls.Count, "A nudge with no positive movement remains bounded");
            Equal(0, navigation.ClickCount, "Unverified sidebar movement cannot authorize a click.");

            navigation = new LocalSelectionNavigation();
            scraper = new AchievementScraper(catalog, navigation,
                frame => LocalSelectionEntries(catalog, navigation, false), frame => navigation.Scrolls.Count == 0);
            Check(!scraper.SelectCategory(target), "Losing the achievement detail screen must stop local retries.");
            Equal(AchievementSelectionFailureKind.DetailMissing, scraper.LastSelectionFailureKind,
                "A lost screen is recorded as a navigation failure");
            Equal(1, navigation.Scrolls.Count, "A lost screen prevents a second wheel action");
            Equal(0, navigation.ClickCount, "A lost screen prevents category clicks.");
            Equal(0, navigation.Keys.Count, "A lost screen cannot trigger a local Escape recovery.");

            using (var cancel = new CancellationTokenSource())
            {
                navigation = new LocalSelectionNavigation();
                navigation.OnWait = () => { if (navigation.Scrolls.Count > 0) cancel.Cancel(); };
                scraper = new AchievementScraper(catalog, navigation,
                    frame => LocalSelectionEntries(catalog, navigation, false), frame => true);
                var cancellationField = typeof(AchievementScraper).GetField("cancellation", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(cancellationField != null, "The scanner cancellation token was not found.");
                cancellationField.SetValue(scraper, cancel.Token);
                bool stopped = false;
                try { scraper.SelectCategory(target); }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "Cancellation during the local nudge wait must propagate immediately.");
                Equal(1, navigation.Scrolls.Count, "Cancellation prevents any subsequent nudge");
                Equal(0, navigation.ClickCount, "Cancellation prevents category clicks.");
                Equal(0, navigation.Keys.Count, "Cancellation prevents recovery keys.");
            }

            Rectangle candidate;
            navigation = new LocalSelectionNavigation();
            var unreadable = LocalSelectionEntries(catalog, navigation, false);
            Equal(-3, AchievementScraper.SidebarUnreadableNudge(target, unreadable, 1080, out candidate),
                "A full unknown title between ordered anchors can move toward the viewport center");
            Equal(unreadable[1].Bounds, candidate, "The nudge keeps the actual unknown tile bounds");
            unreadable[1].RawText = "A different title (II)";
            Equal(0, AchievementScraper.SidebarUnreadableNudge(target, unreadable, 1080, out candidate),
                "Category order cannot supply a guessed identity for an unrelated title");
            unreadable = LocalSelectionEntries(catalog, navigation, false);
            unreadable[1].Bounds = new Rectangle(62, 150, 595, 96);
            Equal(0, AchievementScraper.SidebarUnreadableNudge(target, unreadable, 1080, out candidate),
                "A clipped unknown title cannot authorize the local nudge");
            Equal(0, AchievementScraper.NextNavigationFailureCount(2, AchievementSelectionFailureKind.LabelUnreadable),
                "Isolated label misses cannot trigger the global recovery threshold");
            Equal(3, AchievementScraper.NextNavigationFailureCount(2, AchievementSelectionFailureKind.DetailMissing),
                "Positive navigation failures retain the bounded recovery counter");
        }

        private static List<AchievementCategoryEntryOnScreen> LocalSelectionEntries(AchievementCatalog catalog,
            LocalSelectionNavigation navigation, bool resolveAfterNudge)
        {
            int displacement = navigation.Scrolls.Count == 0 || !navigation.MoveAfterNudge
                ? 0 : -Math.Sign(navigation.Scrolls.Last()) * 30;
            var first = catalog.Categories.Single(category => category.Id == 42);
            var target = catalog.Categories.Single(category => category.Id == 45);
            var last = catalog.Categories.Single(category => category.Id == 46);
            bool resolved = resolveAfterNudge && navigation.Scrolls.Count > 0;
            return new List<AchievementCategoryEntryOnScreen>
            {
                new AchievementCategoryEntryOnScreen { Category = first, RawText = first.Name,
                    Bounds = new Rectangle(62, 300 + displacement, 595, 96) },
                new AchievementCategoryEntryOnScreen { Category = resolved ? target : null, RawText = target.Name,
                    Selected = resolved && navigation.ClickCount > 0,
                    Bounds = new Rectangle(62, 403 + displacement, 595, 96) },
                new AchievementCategoryEntryOnScreen { Category = last, RawText = last.Name,
                    Bounds = new Rectangle(62, 506 + displacement, 595, 96) }
            };
        }

        private sealed class LocalSelectionNavigation : IAchievementNavigation
        {
            public readonly List<int> Scrolls = new List<int>();
            public readonly List<VirtualKeyCode> Keys = new List<VirtualKeyCode>();
            public int CaptureCount, ClickCount;
            public bool MoveAfterNudge = true;
            public Action OnWait;
            public Bitmap Capture() { ++CaptureCount; return new Bitmap(1920, 1080); }
            public void Scroll(bool categories, int signedNotches)
            {
                Check(categories, "A local category nudge must target only the sidebar.");
                Scrolls.Add(signedNotches);
            }
            public void Click(double x, double y) { ++ClickCount; }
            public void PressKey(VirtualKeyCode key) { Keys.Add(key); }
            public void Wait(int milliseconds) { OnWait?.Invoke(); }
        }

        private static void SidebarNavigationEvidence()
        {
            using (var frame = new Bitmap(1920, 1080))
            using (var graphics = Graphics.FromImage(frame))
            {
                graphics.Clear(Color.FromArgb(70, 90, 115));
                Check(!AchievementScraper.SidebarThumbBounds(frame).HasValue &&
                    !AchievementScraper.SidebarAtTop(frame) && !AchievementScraper.SidebarAtBottom(frame),
                    "An absent thumb must not establish either sidebar endpoint.");
                graphics.FillRectangle(Brushes.White, 675, 180, 3, 2);
                Check(!AchievementScraper.SidebarThumbBounds(frame).HasValue,
                    "A few bright pixels cannot establish a sidebar thumb.");
                graphics.Clear(Color.FromArgb(70, 90, 115));
                using (var thumb = new SolidBrush(Color.FromArgb(205, 204, 197)))
                    graphics.FillRectangle(thumb, 674, 500, 5, 104);
                Check(AchievementScraper.SidebarThumbBounds(frame).HasValue &&
                    !AchievementScraper.SidebarAtTop(frame) && !AchievementScraper.SidebarAtBottom(frame),
                    "A neutral-light middle thumb establishes position without an endpoint.");
            }
            var earlier = new AchievementCategory { Id = 101, Order = 5 };
            var later = new AchievementCategory { Id = 102, Order = 40 };
            var inside = new AchievementCategory { Id = 103, Order = 20 };
            var visible = new List<AchievementCategoryEntryOnScreen>
            {
                new AchievementCategoryEntryOnScreen { Category = new AchievementCategory { Id = 104, Order = 10 } },
                new AchievementCategoryEntryOnScreen { Category = new AchievementCategory { Id = 105, Order = 30 } }
            };
            Equal(-1, AchievementScraper.SidebarScrollDirection(earlier, visible), "Earlier categories require upward scrolling");
            Equal(1, AchievementScraper.SidebarScrollDirection(later, visible), "Later categories require downward scrolling");
            Equal(0, AchievementScraper.SidebarScrollDirection(inside, visible), "Missing inside-range OCR requires rereading rather than a guessed direction");
            Equal(0, AchievementScraper.SidebarScrollDirection(earlier, new List<AchievementCategoryEntryOnScreen>()),
                "Unknown category order supplies no scrolling direction");
        }

        private static void SidebarReplay(string directory)
        {
            directory = Path.GetFullPath(directory);
            string catalogPath = null;
            for (var ancestor = new DirectoryInfo(directory); ancestor != null; ancestor = ancestor.Parent)
            {
                string candidate = Path.Combine(ancestor.FullName, "inventorylists", "achievements.json");
                if (File.Exists(candidate)) { catalogPath = candidate; break; }
            }
            Check(catalogPath != null, "The sidebar replay requires a saved achievement catalog beside an ancestor directory.");
            var catalog = AchievementCatalog.FromJson(File.ReadAllText(catalogPath));
            string failedPath = Directory.GetFiles(directory, "*_category_1_not_found.png")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).LastOrDefault();
            Check(failedPath != null, "The saved category 1 navigation failure frame is missing.");
            string topPath = Path.Combine(directory, "category_0_page_188.png");
            string bottomPath = Path.Combine(directory, "category_70_page_4.png");
            Check(File.Exists(topPath) && File.Exists(bottomPath), "The saved sidebar top/bottom reference frames are missing.");
            int[] topIds = { 0, 17, 1, 22, 32, 43, 57, 65 };
            int[] bottomIds = { 60, 62, 63, 66, 67, 68, 69, 70 };
            using (var failed = new Bitmap(failedPath))
            using (var top = new Bitmap(topPath))
            using (var bottom = new Bitmap(bottomPath))
            {
                Check(!AchievementScraper.SidebarAtTop(failed) && AchievementScraper.SidebarAtBottom(failed),
                    "The failed category selection frame has positive sidebar-bottom evidence.");
                Check(AchievementScraper.SidebarAtTop(top) && !AchievementScraper.SidebarAtBottom(top),
                    "The Wonders page 188 frame has positive sidebar-top evidence.");
                Check(AchievementScraper.ScrollbarAtBottom(top),
                    "Sidebar top must remain independent of the achievement list being at its bottom.");
                Check(!AchievementScraper.SidebarAtTop(bottom) && AchievementScraper.SidebarAtBottom(bottom),
                    "The final category frame has positive sidebar-bottom evidence.");
                AssertSidebarRows(catalog, failed, bottomIds, null, "Original failed selection");
                AssertSidebarRows(catalog, top, topIds, 0, "Top sidebar with wrapped count labels");
                AssertSidebarRows(catalog, bottom, bottomIds, 70, "Bottom sidebar with selected final tile");
                using (var scaled = new Bitmap(900, 610))
                using (var graphics = Graphics.FromImage(scaled))
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    graphics.DrawImage(top, new Rectangle(0, 0, scaled.Width, scaled.Height));
                    Check(AchievementScraper.SidebarAtTop(scaled) && !AchievementScraper.SidebarAtBottom(scaled),
                        "Sidebar thumb coordinates scale with frame dimensions.");
                    AssertSidebarRows(catalog, scaled, topIds, 0, "Scaled selected and normal sidebar tiles");
                }
                var navigation = new SidebarReplayNavigation(failed, top);
                var scraper = new AchievementScraper(catalog, navigation);
                var reset = typeof(AchievementScraper).GetMethod("ResetSidebar", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(reset != null, "Sidebar reset helper was not found.");
                reset.Invoke(scraper, new object[] { "regression", false });
                Check(navigation.Scrolls.Count > 0 && navigation.Scrolls.All(step => step < 0),
                    "Reset from the saved bottom frame must scroll upward and terminate on positive top-thumb evidence.");
                Check(navigation.CaptureCount <= 4, "Positive sidebar-top evidence must end reset promptly.");
                Console.WriteLine("Sidebar replay: 8 full categories per frame; top/bottom thumbs and simulated upward reset verified.");
            }
        }

        private static List<AchievementCategoryEntryOnScreen> AssertSidebarRows(AchievementCatalog catalog, Bitmap frame, int[] expectedIds, int? selectedId, string label)
        {
            var entries = new AchievementRecognizer(catalog).ReadCategories(frame).OrderBy(entry => entry.Bounds.Top).ToList();
            PrintCategoryEntries(label, entries);
            Equal(expectedIds.Length, entries.Count, label + " full tile count");
            Sequence(expectedIds.Select(id => (int?)id), entries.Select(entry => entry.Category?.Id), label + " canonical category IDs");
            Sequence(selectedId.HasValue ? new[] { selectedId.Value } : new int[0],
                entries.Where(entry => entry.Selected).Select(entry => entry.Category.Id), label + " selected tile");
            Check(entries.All(entry => entry.Bounds.Top > frame.Height * 162.0 / 1080 &&
                entry.Bounds.Bottom < frame.Height * 1018.0 / 1080 && entry.Bounds.Height <= frame.Height * 113.0 / 1080),
                label + " bounds must represent full individual tiles rather than merged or clipped rows.");
            for (int index = 1; index < entries.Count; index++)
                Check(entries[index - 1].Bounds.Bottom <= entries[index].Bounds.Top, label + " tile bounds must not overlap.");
            return entries;
        }

        private sealed class SidebarReplayNavigation : IAchievementNavigation
        {
            private readonly Bitmap bottom, top;
            public readonly List<int> Scrolls = new List<int>();
            public int CaptureCount;
            public SidebarReplayNavigation(Bitmap bottomFrame, Bitmap topFrame) { bottom = bottomFrame; top = topFrame; }
            public Bitmap Capture() { ++CaptureCount; return (Bitmap)(Scrolls.Count == 0 ? bottom : top).Clone(); }
            public void Scroll(bool categories, int signedNotches)
            {
                Check(categories, "Sidebar reset must target the category list.");
                Scrolls.Add(signedNotches);
            }
            public void Click(double x, double y) { throw new InvalidOperationException("Sidebar reset must not click a category."); }
            public void PressKey(VirtualKeyCode key) { throw new InvalidOperationException("Sidebar reset must not press a key."); }
            public void Wait(int milliseconds) { }
        }

        private static JObject Category(string name, int order, params JToken[] achievements)
        {
            return new JObject { ["name"] = name, ["order"] = order, ["achievements"] = new JArray(achievements) };
        }

        private static JObject Definition(int id, string name, string description, int reward)
        {
            return new JObject { ["id"] = id, ["name"] = name, ["desc"] = description, ["reward"] = reward, ["ver"] = "1.0" };
        }

        private static AchievementObservation Row(int? family, string title, string description, params int[] completed)
        {
            return new AchievementObservation {
                FamilyId = family, MatchedId = family, Title = title, Description = description,
                Confidence = 95, CompletedIds = completed.ToList()
            };
        }

        private static AchievementObservation RowAt(int family, int top)
        {
            var row = Row(family, "Achievement", "Description");
            row.Top = top;
            return row;
        }

        private static void JsonEqual(string expected, JToken actual)
        {
            Check(JToken.DeepEquals(JToken.Parse(expected), actual), "JSON differs: " + actual);
        }

        private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string name)
        {
            Check(expected.SequenceEqual(actual), name + ": expected [" + string.Join(",", expected) +
                "], actual [" + string.Join(",", actual) + "].");
        }

        private static void Equal<T>(T expected, T actual, string name)
        {
            Check(EqualityComparer<T>.Default.Equals(expected, actual), name + ": expected " + expected + ", actual " + actual + ".");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
