using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace InventoryKamera.Tests
{
    /// <summary>Small offline regression harness. This program never sends input to the game.</summary>
    internal static class AchievementTests
    {
        private static int failures;
        private static int passed;

        private static int Main(string[] args)
        {
            string screenshot = null, captureDirectory = null;
            for (int index = 0; index < args.Length; index++)
            {
                if ((args[index] != "--screenshot" && args[index] != "--capture-directory") || index + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Usage: AchievementTests.exe [--screenshot <reference image path>] [--capture-directory <achievement capture directory>]");
                    return 2;
                }
                if (args[index] == "--screenshot") screenshot = args[++index];
                else captureDirectory = args[++index];
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

            if (screenshot != null) Run("Reference screenshot OCR replay", () => ScreenshotReplay(screenshot));
            if (captureDirectory != null) Run("Real capture headers, clipped cards and sidebar replay", () => CaptureReplay(captureDirectory));
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
            Equal(1001, catalog.Resolve(first, "Shared Name", "Reach the top of the tower.", 5).Tier.Id,
                "First category's duplicate title");
            Equal(1002, catalog.Resolve(first, "Shared Name", "Open 10 chests.", 5).Tier.Id,
                "Description separates two same-title families");
            Equal(2001, catalog.Resolve(second, "Shared Name", "Reach the top of the tower.", 5).Tier.Id,
                "Second category's duplicate title");
            Check(!catalog.Resolve(first, "Shared Name", "", 5).Success,
                "A duplicate title without distinguishing description must remain ambiguous.");
        }

        private static void TierResolution()
        {
            var catalog = FixtureCatalog();
            var category = catalog.Categories[0];
            var match = catalog.Resolve(category, "Harvest", "Collect 50 flowers.", null);
            Check(match.Success, "An exact numeric goal should resolve the tier.");
            Equal(1102, match.Tier.Id, "Tier selected by goal 50");
            Check(!catalog.Resolve(category, "Harvest", "Collect 51 flowers.", null).Success,
                "A conflicting visible goal must not be corrected to a nearby tier.");
            Check(!catalog.Resolve(category, "Harvest", "", null).Success,
                "A repeated tier title without description or reward must remain ambiguous.");
            var differentTitles = catalog.Resolve(category, "Tundra Gunsmith (II)", "Build 20 weapons.", 10);
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
            var resolved = catalog.Resolve(category, "...0Odomu?", description, 5);
            Check(resolved.Success, "The duplicate 0/O glyph is recoverable with exact independent evidence.");
            Equal(84501, resolved.Tier.Id, "Canonical repaired Olah achievement ID");
            Check(!catalog.Resolve(category, "...0Odomu?", description, null).Success,
                "OCR-confusable title repair requires a known reward.");
            Check(!catalog.Resolve(category, "...0Odomu?", description, 10).Success,
                "A conflicting reward must reject OCR-confusable title repair.");
            Check(!catalog.Resolve(category, "...0Odomu?", "Successfully conduct cultural exchange with the hilichurls in \"Poetry Exchange.\"", 5).Success,
                "A neighboring achievement's description must not justify the title repair.");
            var ambiguous = AchievementCatalog.FromJson(new JObject {
                ["10"] = Category("Olah!: Series I", 1,
                    Definition(84501, "...Odomu?", description, 5),
                    Definition(84503, "...Odomu?", description, 5))
            }.ToString());
            Check(!ambiguous.Resolve(ambiguous.Categories[0], "...0Odomu?", description, 5).Success,
                "Indistinguishable repaired candidates must remain ambiguous.");
            var numeric = AchievementCatalog.FromJson(new JObject {
                ["10"] = Category("Olah!: Series I", 1,
                    Definition(84501, "...Odomu?", "Collect 10 flowers from the northern garden.", 5))
            }.ToString());
            Check(!numeric.Resolve(numeric.Categories[0], "...0Odomu?", "Collect 11 flowers from the northern garden.", 5).Success,
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
                Check(recognizer.IsDetailScreen(image), "Reference image should be recognized as category details.");
                Equal((int?)1605, recognizer.ReadCompletedTotal(image), "Global completed achievement counter");
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
