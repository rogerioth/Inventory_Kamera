using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class TravelerTests
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1) throw new ArgumentException("Pass saved Traveler source JSON directory and optional character header directory.");
            Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var application = Assembly.LoadFrom(Path.Combine(Environment.CurrentDirectory, "InventoryKamera.exe"));
            var database = application.GetType("InventoryKamera.DatabaseManager", true);
            var build = database.GetMethod("BuildTravelerData", BindingFlags.NonPublic | BindingFlags.Static);
            var avatars = Source(args[0], "AvatarExcelConfigData.json");
            var depots = Source(args[0], "AvatarSkillDepotExcelConfigData.json");
            var skills = Source(args[0], "AvatarSkillExcelConfigData.json");
            var talents = Source(args[0], "AvatarTalentExcelConfigData.json");
            var text = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(Path.Combine(args[0], "TextMap_MediumEN.json")));
            var traveler = (JObject)build.Invoke(null, new object[] { avatars, depots, skills, talents, text });
            Check(((JArray)traveler["Element"]).Count == 7, "Traveler must support all seven elements.");
            Check(traveler["ConstellationOrder"]["cryo"].Values<string>().SequenceEqual(new[] { "burst", "skill" }),
                "Cryo C3 must boost burst and C5 must boost skill.");
            var reordered = (JObject)build.Invoke(null, new object[] {
                new JArray(avatars.Reverse()), new JArray(depots.Reverse()),
                new JArray(skills.Reverse()), new JArray(talents.Reverse()), text });
            Check(JToken.DeepEquals(traveler, reordered), "Skill and talent source order must not affect Traveler generation.");
            var incomplete = new JArray(skills.Where(row => (int?)row["id"] != 10127));
            bool rejected = false;
            try { build.Invoke(null, new object[] { avatars, depots, incomplete, talents, text }); }
            catch (TargetInvocationException exception) { rejected = exception.GetBaseException() is InvalidDataException; }
            Check(rejected, "Missing Cryo skill data must reject generation rather than publish an incomplete Traveler.");
            Console.WriteLine("PASS seven Traveler elements, Cryo C3/C5 and source-order independence");

            var processor = application.GetType("InventoryKamera.GenshinProcesor", true);
            var characters = (Dictionary<string, JObject>)processor.GetField("Characters", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            characters["traveler"] = traveler;
            processor.GetMethod("UpdateCharacterName", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { "Traveler", "Layca" });
            var valid = processor.GetMethod("IsValidCharacter", BindingFlags.NonPublic | BindingFlags.Static);
            Check((bool)valid.Invoke(null, new object[] { "TravelerCryo" }), "TravelerCryo GOOD key must be valid.");
            Check(!(bool)valid.Invoke(null, new object[] { "TravelerUnknown" }), "Unsupported Traveler suffix must be rejected.");
            Check(!(bool)valid.Invoke(null, new object[] { "NotATraveler" }), "Containing Traveler must not establish identity.");
            var matches = processor.GetMethod("CharacterMatchesElement", BindingFlags.NonPublic | BindingFlags.Static);
            var findName = processor.GetMethod("FindClosestCharacterName", BindingFlags.NonPublic | BindingFlags.Static);
            string resolved = (string)findName.Invoke(null, new object[] { "layca", 90 });
            Check(resolved == "Traveler" && (bool)matches.Invoke(null, new object[] { resolved, "Cryo" }), "Custom Traveler alias must resolve to Cryo Traveler.");
            Check(!(bool)matches.Invoke(null, new object[] { "TravelerCryo", "Pyro" }), "GOOD element suffix must match the observed element.");
            var character = application.GetType("InventoryKamera.Character", true);
            object model = Activator.CreateInstance(character);
            character.GetProperty("NameGOOD").SetValue(model, "Traveler");
            character.GetProperty("Element").SetValue(model, "Cryo");
            Check((bool)character.GetMethod("HasValidElement").Invoke(model, null), "Cryo Traveler model validation must succeed.");
            Check((string)JObject.Parse(JsonConvert.SerializeObject(model))["key"] == "TravelerCryo", "GOOD export must use TravelerCryo.");
            Console.WriteLine("PASS custom Traveler alias, element validation and TravelerCryo export");

            if (args.Length > 1)
            {
                var header = application.GetType("InventoryKamera.CharacterScraper", true)
                    .GetMethod("TryReadCharacterHeader", BindingFlags.NonPublic | BindingFlags.Static);
                using (var image = new Bitmap(Path.Combine(args[1], "unknown_layca.png")))
                {
                    var parameters = new object[] { image, null, null, null, null };
                    Check((bool)header.Invoke(null, parameters), "Saved Layca header must resolve: " + parameters[4]);
                    Check((string)parameters[1] == "Traveler" && (string)parameters[2] == "Cryo", "Saved header must resolve to Cryo Traveler.");
                    Console.WriteLine("PASS saved Cryo / Layca header");
                }
                foreach (string name in new[] { "lucy", "roger" })
                {
                    string path = Path.Combine(args[1], "unknown_" + name + ".png");
                    if (!File.Exists(path)) continue;
                    using (var image = new Bitmap(path))
                    {
                        var parameters = new object[] { image, null, null, null, null };
                        Check(!(bool)header.Invoke(null, parameters), "Unknown Cryo header must stay unresolved: " + name);
                        Console.WriteLine("PASS unknown Cryo / {0} stays unresolved", name);
                    }
                }
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL " + exception.GetBaseException().Message);
            return 1;
        }
    }

    private static JArray Source(string directory, string filename) => JArray.Parse(File.ReadAllText(Path.Combine(directory, filename)));
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
