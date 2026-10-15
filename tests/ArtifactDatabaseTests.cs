using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

namespace InventoryKamera.Tests
{
    /// <summary>Offline lookup generation checks. No game input or cache writes.</summary>
    internal static class ArtifactDatabaseTests
    {
        private static int passed, failed;

        private static int Main(string[] args)
        {
            string sourceDirectory = null, captureDirectory = null;
            for (int index = 0; index < args.Length; index++)
            {
                if (index + 1 >= args.Length ||
                    (args[index] != "--source-directory" && args[index] != "--capture-directory"))
                {
                    Console.Error.WriteLine("Usage: ArtifactDatabaseTests.exe [--source-directory <saved source JSON directory>] [--capture-directory <saved artifact captures>]");
                    return 2;
                }
                if (args[index] == "--source-directory") sourceDirectory = args[++index];
                else captureDirectory = args[++index];
            }
            if (captureDirectory != null && sourceDirectory == null)
            {
                Console.Error.WriteLine("Capture replay requires --source-directory to build an isolated lookup in memory.");
                return 2;
            }
            Run("Five-slot sets and circlet-only Prayer preserve the lookup schema", ValidLookup);
            Run("Disabled sets do not require names or pieces", DisabledSets);
            Run("Incomplete or ambiguous sets reject the entire generation", InvalidSources);
            if (sourceDirectory != null)
                Run("Saved 7.1 sources and optional artifact-name capture replay", () => SourceReplay(sourceDirectory, captureDirectory));
            Console.WriteLine("Artifact database regressions: {0} passed, {1} failed.", passed, failed);
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine("FAIL " + name + ": " + exception.GetBaseException().Message);
            }
        }

        private sealed class Fixture
        {
            public JArray Sets = new JArray();
            public JArray Affixes = new JArray();
            public JArray Pieces = new JArray();
            public Dictionary<string, string> Text = new Dictionary<string, string>();
            public JObject Build() => DatabaseManager.BuildArtifactLookup(Sets, Affixes, Pieces, Text);
        }

        private static Fixture CreateFixture()
        {
            var fixture = new Fixture();
            fixture.Sets.Add(new JObject { ["setId"] = 10001, ["equipAffixId"] = 210001,
                ["containsList"] = new JArray(101, 102, 103, 104, 105) });
            fixture.Sets.Add(new JObject { ["setId"] = 15009, ["equipAffixId"] = 215009,
                ["containsList"] = new JArray(201) });
            fixture.Affixes.Add(new JObject { ["id"] = 210001, ["nameTextMapHash"] = 1 });
            fixture.Affixes.Add(new JObject { ["id"] = 210001, ["level"] = 1, ["nameTextMapHash"] = 1 });
            fixture.Affixes.Add(new JObject { ["id"] = 215009, ["nameTextMapHash"] = 2 });
            fixture.Text.Add("1", "Known Set");
            fixture.Text.Add("2", "Prayers for Illumination");
            string[] slots = { "EQUIP_BRACER", "EQUIP_NECKLACE", "EQUIP_SHOES", "EQUIP_RING", "EQUIP_DRESS" };
            string[] names = { "Known Flower", "Known Plume", "Known Sands", "Known Goblet", "Known Circlet" };
            for (int index = 0; index < slots.Length; index++)
            {
                int id = 101 + index;
                fixture.Pieces.Add(new JObject { ["id"] = id, ["setId"] = 10001,
                    ["equipType"] = slots[index], ["nameTextMapHash"] = id });
                fixture.Text.Add(id.ToString(), names[index]);
            }
            fixture.Pieces.Add(new JObject { ["id"] = 201, ["setId"] = 15009,
                ["equipType"] = "EQUIP_DRESS", ["nameTextMapHash"] = 201 });
            fixture.Text.Add("201", "Illumination Crown");
            return fixture;
        }

        private static void ValidLookup()
        {
            JObject lookup = CreateFixture().Build();
            Equal(2, lookup.Count, "Enabled set count");
            JObject set = (JObject)lookup["knownset"];
            Equal("Known Set", (string)set["setName"], "Display name");
            Equal("KnownSet", (string)set["GOOD"], "GOOD set key");
            Equal("knownset", (string)set["normalizedName"], "Normalized set key");
            var pieces = (JObject)set["artifacts"];
            string[] slots = { "flower", "plume", "sands", "goblet", "circlet" };
            Check(pieces.Properties().Select(property => property.Name).OrderBy(value => value)
                .SequenceEqual(slots.OrderBy(value => value)), "All five slots must be mapped by equipType.");
            Equal("Known Flower", (string)pieces["flower"]["artifactName"], "Flower display name");
            Equal("KnownFlower", (string)pieces["flower"]["GOOD"], "Flower GOOD key");
            Equal("knownflower", (string)pieces["flower"]["normalizedName"], "Flower OCR key");
            var prayer = (JObject)lookup["prayersforillumination"]["artifacts"];
            Equal(1, prayer.Count, "Prayer piece count");
            Equal("Illumination Crown", (string)prayer["circlet"]["artifactName"], "Prayer circlet");
        }

        private static void DisabledSets()
        {
            Fixture fixture = CreateFixture();
            foreach (int id in new[] { 15000, 15004, 15012 })
                fixture.Sets.Add(new JObject { ["setId"] = id, ["disableFilter"] = 1 });
            Equal(2, fixture.Build().Count, "Disabled sets and the unnamed template must be excluded without source references.");
        }

        private static void InvalidSources()
        {
            Reject("Missing set name", fixture => fixture.Text.Remove("1"));
            Reject("Missing affix", fixture => fixture.Affixes.RemoveAt(2));
            Reject("Missing piece", fixture => fixture.Pieces.RemoveAt(0));
            Reject("Missing piece name", fixture => fixture.Text.Remove("101"));
            Reject("Unknown slot", fixture => fixture.Pieces[0]["equipType"] = "EQUIP_UNKNOWN");
            Reject("Duplicate slot", fixture => fixture.Pieces[0]["equipType"] = "EQUIP_NECKLACE");
            Reject("Wrong set ID", fixture => fixture.Pieces[0]["setId"] = 15009);
            Reject("Incomplete five-slot set", fixture => ((JArray)fixture.Sets[0]["containsList"]).RemoveAt(0));
            Reject("Ambiguous affix names", fixture =>
            {
                fixture.Text.Add("3", "Conflicting Set");
                fixture.Affixes.Add(new JObject { ["id"] = 210001, ["nameTextMapHash"] = 3 });
            });
            Reject("Duplicate normalized set name", fixture => fixture.Text["2"] = "Known-Set");
            Reject("Ambiguous piece names across sets", fixture => fixture.Text["201"] = "Known Flower");
        }

        private static void Reject(string name, Action<Fixture> mutation)
        {
            Fixture fixture = CreateFixture();
            mutation(fixture);
            try { fixture.Build(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException(name + " must throw InvalidDataException rather than return a partial lookup.");
        }

        private static void SourceReplay(string sourceDirectory, string captureDirectory)
        {
            var sets = ReadArray(sourceDirectory, "ReliquarySetExcelConfigData.json");
            var affixes = ReadArray(sourceDirectory, "EquipAffixExcelConfigData.json");
            var pieces = ReadArray(sourceDirectory, "ReliquaryExcelConfigData.json");
            // Read the saved legacy codex too; generation must not need any of its
            // display names or slot IDs to discover the new sets.
            Check(ReadArray(sourceDirectory, "ReliquaryCodexExcelConfigData.json").Count > 0, "Saved codex fixture is empty.");
            var text = JObject.Parse(File.ReadAllText(Path.Combine(sourceDirectory, "TextMap_MediumEN.json")))
                .Properties().ToDictionary(property => property.Name, property => (string)property.Value);
            JObject lookup = DatabaseManager.BuildArtifactLookup(sets, affixes, pieces, text);
            Equal(63, lookup.Count, "Saved source enabled set count");
            Equal(299, lookup.Properties().Sum(property => ((JObject)property.Value["artifacts"]).Count), "Saved source piece coverage");
            AssertSet(lookup, "scarletproof", "ScarletProof", "Scarlet Proof",
                "Honor to Your Devotion", "Glory to Your Legacy", "Time Gifted Unto You",
                "Chalice of Your Blood and Sorrow", "Testament to Your Faith");
            AssertSet(lookup, "heartofthefurnace", "HeartOfTheFurnace", "Heart of the Furnace",
                "Foundryman's Conjecture", "Foundryman's Observation", "Foundryman's Calculus",
                "Foundryman's Magnanimity", "Foundryman's Legacy");
            Console.WriteLine("Saved source coverage: {0} enabled sets, {1} pieces, both new sets complete.", lookup.Count,
                lookup.Properties().Sum(property => ((JObject)property.Value["artifacts"]).Count));
            if (captureDirectory != null) ReplayNames(lookup, captureDirectory);
        }

        private static JArray ReadArray(string directory, string filename) =>
            JArray.Parse(File.ReadAllText(Path.Combine(directory, filename)));

        private static void AssertSet(JObject lookup, string key, string good, string display, params string[] names)
        {
            JObject set = (JObject)lookup[key];
            Check(set != null, "Missing new set " + key);
            Equal(good, (string)set["GOOD"], "New set GOOD key");
            Equal(display, (string)set["setName"], "New set display name");
            var pieces = (JObject)set["artifacts"];
            Equal(5, pieces.Count, "New set full piece count");
            string[] slots = { "flower", "plume", "sands", "goblet", "circlet" };
            for (int index = 0; index < slots.Length; index++)
                Equal(names[index], (string)pieces[slots[index]]["artifactName"], "New set " + slots[index] + " name");
        }

        private static void ReplayNames(JObject lookup, string captureDirectory)
        {
            var scanName = typeof(ArtifactScraper).GetMethod("ScanArtifactSet", BindingFlags.NonPublic | BindingFlags.Static);
            Check(scanName != null, "Artifact name recognizer was not found.");
            Dictionary<string, JObject> previous = GenshinProcesor.Artifacts;
            try
            {
                GenshinProcesor.Artifacts = lookup.Properties().ToDictionary(property => property.Name, property => (JObject)property.Value);
                int recognized = 0;
                for (int number = 246; number <= 257; number++)
                {
                    string path = Path.Combine(captureDirectory, "artifact" + number, "name", "name.png");
                    Check(File.Exists(path), "Missing saved artifact-name capture " + number);
                    // The recognizer applies gamma to its input. Keep the saved
                    // capture unchanged and give it a disposable bitmap clone.
                    using (var source = new Bitmap(path))
                    using (var crop = (Bitmap)source.Clone())
                    {
                        string actual = (string)scanName.Invoke(null, new object[] { crop });
                        string expected = number == 246 || number == 250 ? "HeartOfTheFurnace" : "ScarletProof";
                        Equal(expected, actual, "Saved artifact " + number + " OCR set");
                        recognized++;
                    }
                }
                Equal(12, recognized, "Saved artifact-name replay count");
                Console.WriteLine("Saved capture replay: {0} artifact-name crops recognized (246–257).", recognized);
            }
            finally { GenshinProcesor.Artifacts = previous; }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual + ".");
        }
    }
}
