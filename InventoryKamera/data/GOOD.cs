using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace InventoryKamera
{
    public class Good
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        [JsonProperty("format")]
        public string Format { get; private set; }

        [JsonProperty("version")]
        public int Version { get; private set; }

        [JsonProperty("kamera_version")]
        public string AppVersion { get; private set; }

        [JsonProperty("source")]
        public string Source { get; private set; }

        [JsonProperty("weapons", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public List<Weapon> Weapons { get; private set; }

        [JsonProperty("artifacts", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public List<Artifact> Artifacts { get; private set; }

        [JsonProperty("characters", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public List<Character> Characters { get; private set; }

        [JsonProperty("materials", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public Dictionary<string, int> Materials { get; private set; }

        public Good()
        {
            Format = "EMPTY";
            Version = 0;
            Source = "NOT FILLED";
        }

        public Good(InventoryKamera genshinData) : this()
        {
            // Get rid of VS warning since we are converting this class to JSON
            Format = "GOOD";
            Version = 2;
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            Source = "Inventory_Kamera";

            // Assign Characters
            if (genshinData.Characters.Count > 0)
            {
                Characters = new List<Character>(genshinData.Characters);

                if (!Properties.Settings.Default.EquipWeapons)
                {
                    foreach (Character character in Characters) character.Weapon = null;
                }
            }



            // Assign Weapons
            if (genshinData.Inventory.Weapons.Count > 0)
            {
                Weapons = new List<Weapon>(genshinData.Inventory.Weapons);

                if (!Properties.Settings.Default.EquipWeapons)
                {
                    foreach (Weapon weapon in Weapons) weapon.EquippedCharacter = "";
                }
            }

            // Assign Artifacts
            if (genshinData.Inventory.Artifacts.Count > 0)
            {
                Artifacts = new List<Artifact>(genshinData.Inventory.Artifacts);

                if (!Properties.Settings.Default.EquipArtifacts)
                {
                    foreach (Artifact artifact in Artifacts) artifact.EquippedCharacter = "";
                }
            }

            // Assign materials
            if (genshinData.Inventory.AllMaterials.Count > 0) Materials = new Dictionary<string, int>();
            genshinData.Inventory.AllMaterials.ToList().ForEach(material => Materials.Add(material.name, material.count));
        }

        internal void WriteToJSON(string outputDirectory)
        {
            // A failed export (disconnected drive, full disk, permissions) must
            // not surface as an unhandled exception: report it clearly and let
            // the user re-export from memory via the manual export button.
            try
            {
                // Creates directory if doesn't exist
                Directory.CreateDirectory(outputDirectory);

                // Create file with timestamp in name
                string fileName = $"genshinData_GOOD_{DateTime.Now.ToString("yyyy_MM_dd_HH_mm")}.json";
                string outputFile = Path.Combine(outputDirectory, fileName);

                // Write file
                WriteToJson(outputFile);


                if (!File.Exists(outputFile)) // did not make file
                {
                    UserInterface.AddError($"Failed to output at : {outputDirectory}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to export scan data to {0}", outputDirectory);
                UserInterface.AddError($"Failed to export scan data to '{outputDirectory}': {ex.Message}\nThe scanned data is not lost. Use the manual export button to try again, or choose a different output directory.");
            }
        }

        private void WriteToJson(string outputFile)
        {
            using (var streamWriter = new StreamWriter(outputFile))
            {
                streamWriter.WriteLine(ToString());
            }
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this).ToString();
        }
    }
}