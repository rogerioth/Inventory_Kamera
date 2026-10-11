using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace InventoryKamera
{
    public static class AchievementExport
    {
        public static JObject ToPaimon(AchievementScanResult result)
        {
            RequireResult(result);
            var categories = new JObject();
            foreach (var completed in result.Completed.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key))
            {
                var categoryId = completed.Value.ToString(CultureInfo.InvariantCulture);
                var category = categories[categoryId] as JObject;
                if (category == null)
                {
                    category = new JObject();
                    categories[categoryId] = category;
                }
                category[completed.Key.ToString(CultureInfo.InvariantCulture)] = true;
            }
            return new JObject { ["achievement"] = categories };
        }

        public static JObject ToSeelie(AchievementScanResult result)
        {
            RequireResult(result);
            var achievements = new JObject();
            foreach (var completed in result.Completed.OrderBy(pair => pair.Key))
                achievements[completed.Key.ToString(CultureInfo.InvariantCulture)] = new JObject { ["done"] = true };
            return new JObject { ["achievements"] = achievements };
        }

        public static JObject ToAkasha(AchievementScanResult result)
        {
            RequireResult(result);
            var achievements = new JObject();
            foreach (var completed in result.Completed.OrderBy(pair => pair.Key))
                achievements[completed.Key.ToString(CultureInfo.InvariantCulture)] = completed.Value;
            return new JObject { ["Version"] = 1, ["Data"] = achievements };
        }

        public static List<string> Write(AchievementScanResult result, string outputDirectory)
        {
            RequireResult(result);
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("An achievement output directory is required.", nameof(outputDirectory));
            outputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outputDirectory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var suffix = result.Complete ? string.Empty : "_partial";
            string identifier = stamp + suffix;
            int collision = 1;
            var prefixes = new[] { "paimon-moe_achievements_", "seelie-me_achievements_", "akasha_achievements_", "kamera_achievements_report_" };
            while (prefixes.Any(prefix => File.Exists(Path.Combine(outputDirectory, prefix + identifier + ".json"))))
                identifier = stamp + suffix + "_" + (collision++).ToString(CultureInfo.InvariantCulture);
            var report = new JObject
            {
                ["format"] = "InventoryKameraAchievements",
                ["version"] = 1,
                ["catalogSource"] = AchievementCatalog.CatalogUrl,
                ["completedCount"] = result.Completed.Count,
                ["scan"] = JObject.FromObject(result)
            };
            var documents = new[] { ToPaimon(result), ToSeelie(result), ToAkasha(result), report };
            var paths = new List<string>();
            for (int i = 0; i < documents.Length; i++)
            {
                var path = Path.Combine(outputDirectory, prefixes[i] + identifier + ".json");
                // Publish only a fully written document. File.Move also refuses to overwrite another export.
                string json = documents[i].ToString(Formatting.Indented);
                string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(json);
                    File.Move(temporaryPath, path);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                paths.Add(path);
            }
            return paths;
        }

        private static void RequireResult(AchievementScanResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.Completed == null) throw new ArgumentException("The completed achievement map is missing.", nameof(result));
            if (result.Completed.Any(pair => pair.Key <= 0 || pair.Value < 0))
                throw new ArgumentException("Completed achievements must use valid canonical IDs and category IDs.", nameof(result));
        }
    }
}
