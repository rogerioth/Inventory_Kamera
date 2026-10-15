using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;


namespace InventoryKamera
{
    public class DatabaseManager
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private static readonly XNamespace atomNamespace = "http://www.w3.org/2005/Atom";

        static DatabaseManager()
        {
            // GitLab requires TLS 1.2+. Opt in explicitly because .NET Framework
            // only uses the OS default when strong-crypto registry keys are set.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private string _listdir = @".\inventorylists\";

        public string ListsDir
        {
            get
            {
                Directory.CreateDirectory(_listdir);
                return _listdir;
            }
            set { _listdir = value; }
        }

        private const string WeaponsJson = "weapons.json";
        private const string ArtifactsJson = "artifacts.json";
        private const string ArtifactMetadataJson = "artifacts.metadata.json";
        private const int ArtifactGeneratorRevision = 1;
        private static readonly object ArtifactUpdateGate = new object();
        private const string CharactersJson = "characters.json";
        private const string TravelerMetadataJson = "traveler.metadata.json";
        private const int TravelerGeneratorRevision = 1;
        private const string MaterialsJson = "materials.json";

        private readonly string NewVersion = "version.txt";

        // Sanity thresholds: a generated table far below these counts means the
        // download or dump was broken, so the existing file is kept instead of
        // being replaced with a stub. Ported from taiwenlee/Inventory_Kamera (MIT): 45e89685
        private const int MinimumCharacters = 50;
        private const int MinimumArtifacts = 30;
        private const int MinimumWeapons = 100;
        private const int MinimumMaterials = 50;

        // GitLab's REST API rejects unauthenticated clients, so update checks read the
        // project's public Atom commit feed instead. Raw file URLs are unaffected.
        private const string commitsFeedURL = "https://gitlab.com/Dimbreath/animegamedata2/-/commits/main.atom";
        private const string repoBaseURL = "https://gitlab.com/Dimbreath/animegamedata2/-/raw/main/";
        private const string TextMapURL = repoBaseURL + "TextMap/TextMap_MediumEN.json";
        private const string CharactersURL = repoBaseURL + "ExcelBinOutput/AvatarExcelConfigData.json";
        private const string ConstellationsURL = repoBaseURL + "ExcelBinOutput/FetterInfoExcelConfigData.json";
        private const string TalentsURL = repoBaseURL + "ExcelBinOutput/AvatarTalentExcelConfigData.json";
        private const string SkillsURL = repoBaseURL + "ExcelBinOutput/AvatarSkillExcelConfigData.json";
        private const string SkillDepotsURL = repoBaseURL + "ExcelBinOutput/AvatarSkillDepotExcelConfigData.json";

        private const string ArtifactSetsURL = repoBaseURL + "ExcelBinOutput/ReliquarySetExcelConfigData.json";
        private const string ArtifactAffixesURL = repoBaseURL + "ExcelBinOutput/EquipAffixExcelConfigData.json";
        private const string ArtifactSetConfig = repoBaseURL + "ExcelBinOutput/ReliquaryExcelConfigData.json";

        private const string WeaponsURL = repoBaseURL + "ExcelBinOutput/WeaponExcelConfigData.json";
        private const string MaterialsURL = repoBaseURL + "ExcelBinOutput/MaterialExcelConfigData.json";

        private ConcurrentDictionary<string, string> Mappings = new ConcurrentDictionary<string, string>();

        internal Version LocalVersion = new Version();
        internal Version RemoteVersion = new Version();

        private static readonly List<string> elements = new List<string>
        {
            "Pyro",
            "Hydro",
            "Dendro",
            "Electro",
            "Anemo",
            "Cryo",
            "Geo",
        };

        private static readonly List<string> slots = new List<string>
        {
            "flower of life",
            "plume of death",
            "sands of eon",
            "goblet of eonothem",
            "circlet of logos",
        };

        public DatabaseManager()
        {
            Directory.CreateDirectory(ListsDir);


            if (!File.Exists(ListsDir + NewVersion)) File.WriteAllText(ListsDir + NewVersion, new Version().ToString());


            LocalVersion = new Version(File.ReadAllText(ListsDir + NewVersion));
        }

        public bool UpdateAvailable()
        {
            Logger.Info("Checking for newer game data...");
            RemoteVersion = CheckRemoteVersion();
            return RemoteVersion == null
                ? throw new NullReferenceException("Could not get current Genshin version.")
                : RemoteVersion.CompareTo(LocalVersion) > 0;
        }

        private Version CheckRemoteVersion()
        {
            // Commit titles follow "CNRELWin<game version>_<build hashes>",
            // e.g. "CNRELWin5.8.0_R48379043_S48511369_D48533839", so the first
            // numeric triple found in the newest entry is the game version.
            string pattern = @"(\d+\.\d+\.\d+)";

            using (var response = new HttpClient().GetAsync(commitsFeedURL))
            {
                string body = response.Result.Content.ReadAsStringAsync().Result;
                var feed = XDocument.Parse(body);

                foreach (var title in feed.Descendants(atomNamespace + "entry")
                    .Select(entry => entry.Element(atomNamespace + "title")?.Value))
                {
                    var match = Regex.Match(title ?? string.Empty, pattern, RegexOptions.None, RegexTimeouts.RegexTimeout);
                    if (match.Success)
                    {
                        var v = match.Groups[1].Value;
                        Debug.WriteLine("Latest version found {0}", v);
                        return new Version(v);
                    }
                }
            }
            return null;
        }

        public Dictionary<string, JObject> LoadCharacters()
        {
            return GetList(ListType.Characters).ToObject<Dictionary<string, JObject>>();
        }

        public Dictionary<string, string> LoadWeapons()
        {
            return GetList(ListType.Weapons).ToObject<Dictionary<string, string>>();
        }

        public Dictionary<string, JObject> LoadArtifacts()
        {
            return GetList(ListType.Artifacts).ToObject<Dictionary<string, JObject>>();
        }

        public Dictionary<string, string> LoadMaterials()
        {
            return GetList(ListType.Materials).ToObject<Dictionary<string, string>>();
        }

        public Dictionary<string, string> LoadDevItems()
        {
            return GetList(ListType.CharacterDevelopmentItems).ToObject<Dictionary<string, string>>();
        }

        private JToken GetList(ListType list)
        {
            string file = "";
            switch (list)
            {
                case ListType.Weapons:
                    file = WeaponsJson;
                    break;

                case ListType.Artifacts:
                    file = ArtifactsJson;
                    break;

                case ListType.Characters:
                    file = CharactersJson;
                    break;

                case ListType.CharacterDevelopmentItems:
                case ListType.Materials:
                    file = MaterialsJson;
                    break;

                default:
                    break;
            }

            if (!File.Exists(ListsDir + file)) throw new FileNotFoundException($"Data file does not exist for {list}.");
            string json = LoadJsonFromFile(file);
            return json == "{}"
                ? throw new FormatException($"Data file for {list} is invalid. Please try running the auto updater and try again.")
                : JToken.Parse(json);
        }

        public UpdateStatus UpdateGameData(bool force = false)
        {
            // Keep mapping lifetime and artifact/table metadata publication within
            // one gate, even when another DatabaseManager instance performs repair.
            lock (ArtifactUpdateGate) return UpdateGameDataCore(force);
        }

        private UpdateStatus UpdateGameDataCore(bool force)
        {
            try
            {
                UpdateStatus overallStatus = UpdateStatus.Skipped;
                var statusLock = new object();
                LoadMappings();

                if (force)
                {
                    Logger.Info("Forcing update for game data");
                    Properties.Settings.Default.LastUpdateCheck = DateTime.MinValue;
                    RemoteVersion = CheckRemoteVersion();
                }

                var lists = Enum.GetValues(typeof(ListType)).Cast<ListType>().ToList();

                lists.RemoveAll(e => e == ListType.CharacterDevelopmentItems);

                lists.AsParallel().ForAll(e =>
                {
                    var status = UpdateList(e, force);

                    lock (statusLock) 
                    {
                        switch (status)
                        {
                            case UpdateStatus.Fail:
                                overallStatus = UpdateStatus.Fail;
                                Logger.Error("Failed to update {0} data", e);
                                break;
                            case UpdateStatus.Success:
                                if (overallStatus != UpdateStatus.Fail)
                                    overallStatus = UpdateStatus.Success;
                                break;
                            default:
                                if (overallStatus == UpdateStatus.Skipped)
                                    overallStatus = UpdateStatus.Skipped;
                                break;
                        }
                    }
                });

                if (overallStatus != UpdateStatus.Fail)
                {

                    if (RemoteVersion != new Version())
                    {
                        LocalVersion = RemoteVersion;
                        File.WriteAllText(ListsDir + NewVersion, LocalVersion.ToString());
                    }
                }
                else
                    Logger.Error($"Could not update all information for version {RemoteVersion}");

                ReleaseMappings();
                return overallStatus;
            }
            catch (Exception e)
            {
                ReleaseMappings();
                Logger.Error(e);
                return UpdateStatus.Fail;
            }
            
        }

        public bool ArtifactUpdateRequired
        {
            get { lock (ArtifactUpdateGate) return ArtifactUpdateRequiredCore(); }
        }

        public UpdateStatus UpdateArtifactData(bool force = false)
        {
            lock (ArtifactUpdateGate)
            {
                try
                {
                    if (!force && !ArtifactUpdateRequiredCore()) return UpdateStatus.Skipped;
                    LoadMappings();
                    return UpdateArtifacts(force);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Could not repair the artifact lookup table; existing data is retained");
                    return UpdateStatus.Fail;
                }
                finally { ReleaseMappings(); }
            }
        }

        private bool ArtifactUpdateRequiredCore()
        {
            try
            {
                string tablePath = Path.Combine(ListsDir, ArtifactsJson);
                string metadataPath = Path.Combine(ListsDir, ArtifactMetadataJson);
                if (!File.Exists(tablePath) || !File.Exists(metadataPath)) return true;
                var table = JObject.Parse(File.ReadAllText(tablePath));
                var metadata = JObject.Parse(File.ReadAllText(metadataPath));
                return table.Count < MinimumArtifacts
                    || (string)metadata["format"] != "InventoryKameraArtifactCatalog"
                    || (int?)metadata["generatorRevision"] != ArtifactGeneratorRevision
                    || (int?)metadata["setCount"] != table.Count
                    || !string.Equals((string)metadata["tableSha256"], ArtifactTableHash(tablePath), StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        private static string ArtifactTableHash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        public bool TravelerUpdateRequired
        {
            get { lock (ArtifactUpdateGate) return TravelerUpdateRequiredCore(); }
        }

        public UpdateStatus UpdateTravelerData(bool force = false)
        {
            // Share the full-update/artifact gate so one update cannot clear mappings
            // while another instance is joining Traveler skill depots and talents.
            lock (ArtifactUpdateGate)
            {
                try
                {
                    if (!force && !TravelerUpdateRequiredCore()) return UpdateStatus.Skipped;
                    LoadMappings();
                    string path = Path.Combine(ListsDir, CharactersJson);
                    if (!File.Exists(path)) return UpdateCharacters(true);
                    var data = JObject.Parse(File.ReadAllText(path));
                    if (!IsPlausibleCount(ListType.Characters, data.Count)) return UpdateStatus.Fail;
                    var traveler = BuildTravelerData(
                        JArray.Parse(LoadJsonFromURLAsync(CharactersURL)),
                        JArray.Parse(LoadJsonFromURLAsync(SkillDepotsURL)),
                        JArray.Parse(LoadJsonFromURLAsync(SkillsURL)),
                        JArray.Parse(LoadJsonFromURLAsync(TalentsURL)), Mappings);
                    bool changed = UpsertTraveler(data, traveler);
                    if (changed && !SaveJsonToFile(data.ToString(Formatting.None), CharactersJson)) return UpdateStatus.Fail;
                    if (!SaveTravelerMetadata(traveler)) return UpdateStatus.Fail;
                    Logger.Info("Repaired Traveler data: {0} elements, generator revision {1}", ((JArray)traveler["Element"]).Count, TravelerGeneratorRevision);
                    return UpdateStatus.Success;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Could not repair Traveler data; existing character data is retained");
                    return UpdateStatus.Fail;
                }
                finally { ReleaseMappings(); }
            }
        }

        private bool TravelerUpdateRequiredCore()
        {
            try
            {
                string tablePath = Path.Combine(ListsDir, CharactersJson);
                string metadataPath = Path.Combine(ListsDir, TravelerMetadataJson);
                if (!File.Exists(tablePath) || !File.Exists(metadataPath)) return true;
                var data = JObject.Parse(File.ReadAllText(tablePath));
                var traveler = data["traveler"] as JObject;
                var metadata = JObject.Parse(File.ReadAllText(metadataPath));
                if (traveler == null || (string)traveler["GOOD"] != "Traveler"
                    || data.Properties().Count(IsTravelerProperty) != 1) return true;
                var supported = traveler["Element"] as JArray;
                var orders = traveler["ConstellationOrder"] as JObject;
                if (supported == null || orders == null || supported.Count != elements.Count || orders.Count != elements.Count) return true;
                foreach (string element in elements.Select(x => x.ToLowerInvariant()))
                {
                    if (supported.Count(x => (string)x == element) != 1) return true;
                    var order = orders[element] as JArray;
                    if (order == null || order.Count != 2 || order.Any(x => !new[] { "auto", "skill", "burst" }.Contains((string)x))) return true;
                }
                return (string)metadata["format"] != "InventoryKameraTravelerCatalog"
                    || (int?)metadata["generatorRevision"] != TravelerGeneratorRevision
                    || !string.Equals((string)metadata["entrySha256"], TravelerDataHash(traveler), StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        private bool SaveTravelerMetadata(JObject traveler)
        {
            var metadata = new JObject
            {
                ["format"] = "InventoryKameraTravelerCatalog",
                ["generatorRevision"] = TravelerGeneratorRevision,
                ["entrySha256"] = TravelerDataHash(traveler),
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            return SaveJsonToFile(metadata.ToString(Formatting.None), TravelerMetadataJson);
        }

        private static string TravelerDataHash(JObject traveler)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(traveler.ToString(Formatting.None))))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static bool IsTravelerProperty(JProperty property)
        {
            string good = (string)property.Value["GOOD"];
            return good == "Traveler" || good == "PlayerBoy" || good == "PlayerGirl";
        }

        private static bool UpsertTraveler(JObject data, JObject traveler)
        {
            var existing = data.Properties().Where(IsTravelerProperty).ToList();
            bool changed = existing.Count != 1 || existing[0].Name != "traveler" || !JToken.DeepEquals(existing[0].Value, traveler);
            foreach (var property in existing) property.Remove();
            data["traveler"] = traveler.DeepClone();
            return changed;
        }

        internal static JObject BuildTravelerData(JArray avatars, JArray depots, JArray skills, JArray talents,
            IDictionary<string, string> textmap)
        {
            if (avatars == null) throw new ArgumentNullException(nameof(avatars));
            if (depots == null) throw new ArgumentNullException(nameof(depots));
            if (skills == null) throw new ArgumentNullException(nameof(skills));
            if (talents == null) throw new ArgumentNullException(nameof(talents));
            if (textmap == null) throw new ArgumentNullException(nameof(textmap));
            var nativeElements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Fire"] = "pyro", ["Water"] = "hydro", ["Grass"] = "dendro", ["Electric"] = "electro",
                ["Wind"] = "anemo", ["Ice"] = "cryo", ["Rock"] = "geo"
            };
            var avatarsById = avatars.OfType<JObject>().ToLookup(row => (int?)row["id"] ?? 0);
            var depotsById = depots.OfType<JObject>().ToLookup(row => (int?)row["id"] ?? 0);
            var skillsById = skills.OfType<JObject>().ToLookup(row => (int?)row["id"] ?? 0);
            var talentsById = talents.OfType<JObject>().ToLookup(row => (int?)row["talentId"] ?? 0);
            var orders = new SortedDictionary<string, JArray>(StringComparer.Ordinal);
            foreach (int avatarId in new[] { 10000005, 10000007 })
            {
                var avatar = RequireTravelerRow(avatarsById[avatarId], "avatar " + avatarId);
                if ((string)avatar["useType"] != "AVATAR_FORMAL" || (string)avatar["weaponType"] != "WEAPON_SWORD_ONE_HAND")
                    throw new InvalidDataException("Traveler avatar " + avatarId + " is not a playable sword avatar.");
                var candidates = avatar["candSkillDepotIds"] as JArray;
                if (candidates == null || candidates.Count == 0) throw new InvalidDataException("Traveler candidate skill depots are missing.");
                var seenDepots = new HashSet<int>();
                var seenElements = new HashSet<string>(StringComparer.Ordinal);
                foreach (var candidate in candidates)
                {
                    int depotId = RequireTravelerId(candidate, "candidate depot");
                    if (!seenDepots.Add(depotId)) throw new InvalidDataException("Duplicate Traveler candidate depot " + depotId + ".");
                    var depot = RequireTravelerRow(depotsById[depotId], "skill depot " + depotId);
                    int burstId = (int?)depot["energySkill"] ?? 0;
                    if (burstId == 0) continue; // The common, elementless depot has no burst.
                    var burst = RequireTravelerRow(skillsById[burstId], "burst " + burstId);
                    string element;
                    if (!nativeElements.TryGetValue((string)burst["costElemType"] ?? "", out element) || !seenElements.Add(element))
                        throw new InvalidDataException("Traveler depot " + depotId + " has an unknown or duplicate element.");
                    var skillIds = depot["skills"] as JArray;
                    var talentIds = depot["talents"] as JArray;
                    if (skillIds == null || skillIds.Count < 2 || talentIds == null || talentIds.Count != 6)
                        throw new InvalidDataException("Traveler depot " + depotId + " lacks its skill or six constellation references.");
                    int autoId = RequireTravelerId(skillIds[0], "normal attack in depot " + depotId);
                    int skillId = RequireTravelerId(skillIds[1], "elemental skill in depot " + depotId);
                    if (autoId == skillId || skillId == burstId || autoId == burstId)
                        throw new InvalidDataException("Traveler depot " + depotId + " reuses a talent skill ID.");
                    var auto = RequireTravelerRow(skillsById[autoId], "normal attack " + autoId);
                    var skill = RequireTravelerRow(skillsById[skillId], "elemental skill " + skillId);
                    string autoName = RequireTravelerText(auto["nameTextMapHash"], textmap, "normal attack " + autoId);
                    string skillName = RequireTravelerText(skill["nameTextMapHash"], textmap, "elemental skill " + skillId);
                    string burstName = RequireTravelerText(burst["nameTextMapHash"], textmap, "burst " + burstId);
                    var ids = talentIds.Select(x => RequireTravelerId(x, "constellation in depot " + depotId)).ToList();
                    if (ids.Distinct().Count() != 6) throw new InvalidDataException("Traveler depot " + depotId + " repeats a constellation ID.");
                    var c3 = RequireTravelerRow(talentsById[ids[2]], "C3 talent " + ids[2]);
                    var c5 = RequireTravelerRow(talentsById[ids[4]], "C5 talent " + ids[4]);
                    var order = new JArray
                    {
                        TravelerUpgradeType(RequireTravelerText(c3["descTextMapHash"], textmap, "C3 talent " + ids[2]), autoId, skillId, burstId, autoName, skillName, burstName),
                        TravelerUpgradeType(RequireTravelerText(c5["descTextMapHash"], textmap, "C5 talent " + ids[4]), autoId, skillId, burstId, autoName, skillName, burstName)
                    };
                    JArray previous;
                    if (orders.TryGetValue(element, out previous) && !JToken.DeepEquals(previous, order))
                        throw new InvalidDataException("The two Traveler avatars disagree on " + element + " constellation order.");
                    orders[element] = order;
                }
                if (seenElements.Count != nativeElements.Count || nativeElements.Values.Any(x => !seenElements.Contains(x)))
                    throw new InvalidDataException("Traveler avatar " + avatarId + " does not provide all seven elemental skill depots.");
            }
            return new JObject
            {
                ["GOOD"] = "Traveler",
                ["Element"] = new JArray(orders.Keys),
                ["ConstellationName"] = new JArray("Viator", "Viatrix"),
                ["ConstellationOrder"] = new JObject(orders.Select(pair => new JProperty(pair.Key, pair.Value))),
                ["WeaponType"] = (int)WeaponType.Sword
            };
        }

        private static JObject RequireTravelerRow(IEnumerable<JObject> rows, string context)
        {
            var matches = rows.Take(2).ToList();
            if (matches.Count != 1) throw new InvalidDataException("Missing or ambiguous Traveler " + context + ".");
            return matches[0];
        }

        private static int RequireTravelerId(JToken token, string context)
        {
            long value;
            if (token == null || token.Type != JTokenType.Integer || !long.TryParse(token.ToString(), out value) || value <= 0 || value > int.MaxValue)
                throw new InvalidDataException("Invalid Traveler " + context + " ID.");
            return (int)value;
        }

        private static string RequireTravelerText(JToken hash, IDictionary<string, string> textmap, string context)
        {
            string text;
            if (hash == null || !textmap.TryGetValue(hash.ToString(), out text) || string.IsNullOrWhiteSpace(text))
                throw new InvalidDataException("No English text is mapped for Traveler " + context + ".");
            return text.Trim();
        }

        private static string TravelerUpgradeType(string description, int autoId, int skillId, int burstId,
            string autoName, string skillName, string burstName)
        {
            var links = Regex.Matches(description, @"\{LINK#S(\d+)\}", RegexOptions.None, RegexTimeouts.RegexTimeout).Cast<Match>().Select(x => int.Parse(x.Groups[1].Value)).Distinct().ToList();
            var matches = new List<string>();
            if (links.Count > 0)
            {
                if (links.Any(x => x != autoId && x != skillId && x != burstId))
                    throw new InvalidDataException("Traveler constellation links to a skill outside its depot.");
                if (links.Contains(autoId)) matches.Add("auto");
                if (links.Contains(skillId)) matches.Add("skill");
                if (links.Contains(burstId)) matches.Add("burst");
            }
            else
            {
                // Older elemental constellations embed exact mapped names instead of links.
                if (description.IndexOf(autoName, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add("auto");
                if (description.IndexOf(skillName, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add("skill");
                if (description.IndexOf(burstName, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add("burst");
            }
            if (matches.Count != 1) throw new InvalidDataException("Traveler constellation has no unambiguous talent upgrade.");
            return matches[0];
        }

        private void LoadMappings()
        {
            lock (Mappings)
            {
                if (!Mappings.Any())
                {
                    Mappings = new ConcurrentDictionary<string, string>(JObject.Parse(LoadJsonFromURLAsync(TextMapURL))
                        .ToObject<Dictionary<string, string>>()
                        .Where(e => !string.IsNullOrWhiteSpace(e.Value)) // Remove any mapping with empty
                        .ToDictionary(i => i.Key, i => i.Value));
                }
            }
        }

        private void ReleaseMappings()
        {
            Mappings.Clear();
        }

        private string LoadJsonFromURLAsync(string url)
        {
            string json = "";

            try
            {
                using (WebClient client = new WebClient())
                {
                    client.Encoding = System.Text.Encoding.UTF8;
                    json = client.DownloadString(url);
                }
            }
            catch (Exception)
            {

                throw;
            }
            
            return json;
        }

        private UpdateStatus UpdateList(ListType list, bool force = false)
        {
            UpdateStatus status = UpdateStatus.Success;

            Logger.Info("Updating {0}", list);

            try
            {
                switch (list)
                {
                    case ListType.Weapons:
                        status = UpdateWeapons(force);
                        break;

                    case ListType.Artifacts:
                        status = UpdateArtifacts(force);
                        break;

                    case ListType.Characters:
                        status = UpdateCharacters(force);
                        break;

                    case ListType.Materials:
                        status = UpdateMaterials(force);
                        break;

                    default:
                        break;
                }
                Logger.Info("Finished updating {0} ({1})", list, status);
            }
            catch (Exception e)
            {
                Logger.Error("Failed to update {0} data", list);
                Logger.Error(e);
                return UpdateStatus.Fail;
            }

            return status;
        }

        private UpdateStatus UpdateCharacters(bool force)
        {
            var status = UpdateStatus.Skipped;
            JObject traveler = null;

            // Never delete the previous file up front: generate into memory and
            // only replace the file once the new data is complete and plausible.
            // Ported from taiwenlee/Inventory_Kamera (MIT): 45e89685
            var data = new ConcurrentDictionary<string, JObject>();

            try
            {
                string path = Path.Combine(ListsDir, CharactersJson);
                if (!force && File.Exists(path))
                    data = JObject.Parse(File.ReadAllText(path)).ToObject<ConcurrentDictionary<string, JObject>>();
                var avatars = JArray.Parse(LoadJsonFromURLAsync(CharactersURL));
                var characters = avatars.ToObject<List<JObject>>();
                var constellations = JArray.Parse(LoadJsonFromURLAsync(ConstellationsURL)).ToObject<List<JObject>>();
                var talentDefinitions = JArray.Parse(LoadJsonFromURLAsync(TalentsURL));
                var skillDefinitions = JArray.Parse(LoadJsonFromURLAsync(SkillsURL));
                var talents = talentDefinitions.ToObject<List<JObject>>();
                var skills = skillDefinitions.ToObject<List<JObject>>();
                traveler = BuildTravelerData(avatars, JArray.Parse(LoadJsonFromURLAsync(SkillDepotsURL)),
                    skillDefinitions, talentDefinitions, Mappings);
                var rebuilt = JObject.FromObject(data);
                bool travelerChanged = UpsertTraveler(rebuilt, traveler);
                data = rebuilt.ToObject<ConcurrentDictionary<string, JObject>>();
                if (travelerChanged || TravelerUpdateRequiredCore()) status = UpdateStatus.Success;

                characters.RemoveAll(c => !c.ContainsKey("useType") || c["useType"].ToString() != "AVATAR_FORMAL"
                    || (int?)c["id"] == 10000005 || (int?)c["id"] == 10000007);

                Logger.Debug("Found {0} playable characters available", characters.Count);
                characters.AsParallel().ForAll(character =>
                {
                    // One unmapped hash must not abort the whole parallel update.
                    if (!Mappings.TryGetValue(character["nameTextMapHash"]?.ToString() ?? string.Empty, out string mappedName) || string.IsNullOrWhiteSpace(mappedName))
                    {
                        Logger.Warn("Skipping avatar {0}: name hash {1} has no TextMap entry.", character["id"], character["nameTextMapHash"]?.ToString());
                        return;
                    }

                    string name = mappedName;

                    try
                    {
                        string PascalCase = CultureInfo.GetCultureInfo("en").TextInfo.ToTitleCase(name);
                        string nameGOOD = Regex.Replace(PascalCase, @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
                        string nameKey = nameGOOD.ToLower();
                        int characterID = (int)character["id"];

                        // Placeholder avatars (beta Columbina/Ineffa, trial duplicates,
                        // UGC templates) reuse real characters' keys but have no fetter
                        // data, which yields empty elements and can shadow the real
                        // entry depending on parallel processing order.
                        // Ported from taiwenlee/Inventory_Kamera (MIT): 43c16129
                        if (characterID > 10000900) return;

                        string const3Description = "";
                        string const5Description = "";
                        string skill = "";
                        string auto = "Normal Attack";

                        var value = new JObject();

                        if (!data.ContainsKey(nameKey))
                        {
                            value.Add("GOOD", nameGOOD);

                            // Some characters have different internal names.
                            // Ex: Jean -> Qin, Yanfei -> Feiyan, etc.
                            name = character["iconName"].ToString().Split('_').Last(); // UI_AvatarIcon_[Qin] -> Qin

                            // Traveler was rebuilt once above from explicit source-ID joins.
                            {
                                // Not every avatar has a Skill_S_ entry (e.g. test ones),
                                // so look it up tolerantly instead of throwing.
                                // Ported from taiwenlee/Inventory_Kamera (MIT): 480222b9
                                var characterSkill = skills.FirstOrDefault(entry => entry["skillIcon"]?.ToString().Contains($"Skill_S_{name}") == true);

                                if (characterSkill == null)
                                {
                                    Logger.Debug("Skipping {0} - no skill data available", name);
                                    return;
                                }

                                skill = Mappings[characterSkill["nameTextMapHash"].ToString()].ToString();


                                value.Add("ConstellationName", new JArray
                                {
                                    GetConstellationNameFromId(characterID)
                                });

                                var constellationOrder = new JArray();

                                // The skill/burst name is always mentioned in the constellation's description
                                // so we'll check for it. C3 and C5 can also level the normal attack for
                                // characters such as Freminet, Sethos, Arlecchino or Neuvillette.
                                // Ported from taiwenlee/Inventory_Kamera (MIT): 191fa8f2
                                const3Description = talents.Where(entry => entry["icon"]?.ToString().Contains(name) == true).ElementAt(2)["descTextMapHash"].ToString();
                                const3Description = Mappings[const3Description].ToString();

                                if (const3Description.Contains(auto))
                                {
                                    constellationOrder.Add("auto");
                                }
                                else if (const3Description.Contains(skill))
                                {
                                    constellationOrder.Add("skill");
                                }
                                else
                                {
                                    constellationOrder.Add("burst");
                                }

                                const5Description = talents.Where(entry => entry["icon"]?.ToString().Contains(name) == true).ElementAt(4)["descTextMapHash"].ToString();
                                const5Description = Mappings[const5Description].ToString();

                                if (const5Description.Contains(auto))
                                {
                                    constellationOrder.Add("auto");
                                }
                                else if (const5Description.Contains(skill))
                                {
                                    constellationOrder.Add("skill");
                                }
                                else
                                {
                                    constellationOrder.Add("burst");
                                }

                                value.Add("ConstellationOrder", constellationOrder);

                                value.Add("Element", new JArray(GetElementFromID(characterID)));
                            }

                            var weaponArchetype = character["weaponType"].ToString();
                            WeaponType weaponType;
                            if (weaponArchetype.Contains("SWORD_ONE_HAND")) weaponType = WeaponType.Sword;
                            else if (weaponArchetype.Contains("CLAYMORE")) weaponType = WeaponType.Claymore;
                            else if (weaponArchetype.Contains("POLE")) weaponType = WeaponType.Polearm;
                            else if (weaponArchetype.Contains("BOW")) weaponType = WeaponType.Bow;
                            else if (weaponArchetype.Contains("CATALYST")) weaponType = WeaponType.Catalyst;
                            else if (weaponArchetype.Contains("CROSSBOW")) weaponType = WeaponType.Sword; // Not a released weapon class; current dumps only tag the traveler rows with it
                            else throw new IndexOutOfRangeException($"{name} uses unknown weapon type {weaponArchetype}");

                            value.Add("WeaponType", (int)weaponType);

                            if (data.TryAdd(nameKey, value)) status = UpdateStatus.Success;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Problem when generating character information for {0}", name);
                        Logger.Warn(ex);
                    }
                });

                string GetConstellationNameFromId(int targetID)
                {
                    foreach (var constellation in from constellation in constellations
                                                  where (((int)constellation["avatarId"]) == targetID)
                                                  select constellation)
                    {
                        return Mappings[constellation["avatarConstellationBeforTextMapHash"].ToString()].ToString();
                    }

                    return "";
                }

                string GetElementFromID(int targetID)
                {
                    foreach (var element in from constellation in constellations
                                            where (((int)constellation["avatarId"]) == targetID)
                                            select constellation)
                    {
                        return Mappings[element["avatarVisionBeforTextMapHash"].ToString()].ToString().ToLower();
                    }
                    return "";
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex);
                status = UpdateStatus.Fail;
            }            

            if (status == UpdateStatus.Success)
            {
                bool saved = IsPlausibleCount(ListType.Characters, data.Count)
                    && SaveJsonToFile(JsonConvert.SerializeObject(new SortedDictionary<string, JObject>(data)), CharactersJson);

                if (!saved || !SaveTravelerMetadata(traveler)) status = UpdateStatus.Fail;
            }

            return status;
        }

        private UpdateStatus UpdateArtifacts(bool force)
        {
            // DisplayItem names can be absent from the English TextMap even for
            // released sets. Set definitions and their equip affixes are authoritative.
            var definitions = JArray.Parse(LoadJsonFromURLAsync(ArtifactSetsURL));
            var affixes = JArray.Parse(LoadJsonFromURLAsync(ArtifactAffixesURL));
            var pieces = JArray.Parse(LoadJsonFromURLAsync(ArtifactSetConfig));
            var data = BuildArtifactLookup(definitions, affixes, pieces, Mappings);
            if (!IsPlausibleCount(ListType.Artifacts, data.Count)) return UpdateStatus.Fail;

            bool repairRequired = ArtifactUpdateRequiredCore();
            string tablePath = Path.Combine(ListsDir, ArtifactsJson);
            bool changed = force || !File.Exists(tablePath);
            if (!changed)
            {
                try { changed = !JToken.DeepEquals(data, JObject.Parse(File.ReadAllText(tablePath))); }
                catch { changed = true; }
            }
            if (!changed && !repairRequired) return UpdateStatus.Skipped;
            if (changed && !SaveJsonToFile(data.ToString(Formatting.None), ArtifactsJson)) return UpdateStatus.Fail;

            var metadata = new JObject
            {
                ["format"] = "InventoryKameraArtifactCatalog",
                ["generatorRevision"] = ArtifactGeneratorRevision,
                ["tableSha256"] = ArtifactTableHash(tablePath),
                ["setCount"] = data.Count,
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            if (!SaveJsonToFile(metadata.ToString(Formatting.None), ArtifactMetadataJson)) return UpdateStatus.Fail;
            Logger.Info("Updated artifact catalog: {0} enabled sets, generator revision {1}", data.Count, ArtifactGeneratorRevision);
            return UpdateStatus.Success;
        }

        internal static JObject BuildArtifactLookup(JArray setDefs, JArray affixes, JArray pieces,
            IDictionary<string, string> textmap)
        {
            if (setDefs == null) throw new ArgumentNullException(nameof(setDefs));
            if (affixes == null) throw new ArgumentNullException(nameof(affixes));
            if (pieces == null) throw new ArgumentNullException(nameof(pieces));
            if (textmap == null) throw new ArgumentNullException(nameof(textmap));

            var affixesById = affixes.OfType<JObject>().ToLookup(row => (int?)row["id"] ?? 0);
            var piecesById = pieces.OfType<JObject>().ToLookup(row => (int?)row["id"] ?? 0);
            var slotsByEquipment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["EQUIP_BRACER"] = "flower", ["EQUIP_NECKLACE"] = "plume",
                ["EQUIP_SHOES"] = "sands", ["EQUIP_RING"] = "goblet", ["EQUIP_DRESS"] = "circlet"
            };
            var prayerSets = new HashSet<int> { 15009, 15010, 15011, 15013 };
            var seenSetIds = new HashSet<int>();
            var seenPieceNames = new HashSet<string>(StringComparer.Ordinal);
            var result = new SortedDictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var token in setDefs)
            {
                var definition = token as JObject;
                if (definition == null) throw new InvalidDataException("An artifact set definition is not an object.");
                if (((int?)definition["disableFilter"] ?? 0) != 0) continue;
                int setId = RequireArtifactId(definition["setId"], "setId");
                if (!seenSetIds.Add(setId)) throw new InvalidDataException("Duplicate artifact set ID " + setId + ".");
                int affixId = RequireArtifactId(definition["equipAffixId"], "equipAffixId for set " + setId);
                var names = affixesById[affixId].Select(row => RequireArtifactName(row["nameTextMapHash"], textmap,
                    "affix for set " + setId)).Distinct(StringComparer.Ordinal).ToList();
                if (names.Count != 1) throw new InvalidDataException("Artifact set " + setId + " has no unique mapped affix name.");
                string setName = names[0], setGood = ConvertToGOOD(setName), setKey = setGood.ToLowerInvariant();
                if (setKey.Length == 0 || result.ContainsKey(setKey))
                    throw new InvalidDataException("Artifact set " + setId + " has an empty or ambiguous normalized name.");

                var references = definition["containsList"] as JArray;
                int expectedCount = prayerSets.Contains(setId) ? 1 : 5;
                if (references == null || references.Count != expectedCount)
                    throw new InvalidDataException("Artifact set " + setId + " does not contain its expected " + expectedCount + " pieces.");
                var seenPieceIds = new HashSet<int>();
                var artifacts = new JObject();
                foreach (var reference in references)
                {
                    int pieceId = RequireArtifactId(reference, "piece ID for set " + setId);
                    var matchingPieces = piecesById[pieceId].ToList();
                    if (!seenPieceIds.Add(pieceId) || matchingPieces.Count != 1)
                        throw new InvalidDataException("Artifact piece " + pieceId + " is missing or ambiguous.");
                    var piece = matchingPieces[0];
                    if (RequireArtifactId(piece["setId"], "setId for piece " + pieceId) != setId)
                        throw new InvalidDataException("Artifact piece " + pieceId + " belongs to a different set.");
                    string slot;
                    if (!slotsByEquipment.TryGetValue((string)piece["equipType"] ?? string.Empty, out slot)
                        || artifacts[slot] != null || prayerSets.Contains(setId) && slot != "circlet")
                        throw new InvalidDataException("Artifact set " + setId + " has an unsupported or duplicate slot.");
                    string pieceName = RequireArtifactName(piece["nameTextMapHash"], textmap, "piece " + pieceId);
                    string pieceGood = ConvertToGOOD(pieceName), pieceKey = pieceGood.ToLowerInvariant();
                    if (pieceKey.Length == 0 || !seenPieceNames.Add(pieceKey))
                        throw new InvalidDataException("Artifact piece " + pieceId + " has an empty or ambiguous normalized name.");
                    artifacts[slot] = new JObject
                    {
                        ["artifactName"] = pieceName, ["GOOD"] = pieceGood, ["normalizedName"] = pieceKey
                    };
                }
                result.Add(setKey, new JObject
                {
                    ["setName"] = setName, ["GOOD"] = setGood, ["normalizedName"] = setKey, ["artifacts"] = artifacts
                });
            }
            if (result.Count == 0) throw new InvalidDataException("No enabled artifact sets were generated.");
            return new JObject(result.Select(pair => new JProperty(pair.Key, pair.Value)));
        }

        private static int RequireArtifactId(JToken token, string field)
        {
            long value;
            if (token == null || token.Type != JTokenType.Integer || !long.TryParse(token.ToString(), out value)
                || value <= 0 || value > int.MaxValue)
                throw new InvalidDataException("Invalid artifact " + field + ".");
            return (int)value;
        }

        private static string RequireArtifactName(JToken hash, IDictionary<string, string> textmap, string context)
        {
            string name;
            if (hash == null || !textmap.TryGetValue(hash.ToString(), out name) || string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException("No English artifact name is mapped for " + context + ".");
            return name.Trim();
        }

        private UpdateStatus UpdateWeapons(bool force)
        {
            var status = UpdateStatus.Skipped;

            // Never delete the previous file up front: generate into memory and
            // only replace the file once the new data is complete and plausible.
            // Ported from taiwenlee/Inventory_Kamera (MIT): 45e89685
            var data = force
                ? new ConcurrentDictionary<string, string>()
                : JToken.Parse(LoadJsonFromFile(WeaponsJson)).ToObject<ConcurrentDictionary<string, string>>();

            try
            {
                List<JObject> weapons = JArray.Parse(LoadJsonFromURLAsync(WeaponsURL)).ToObject<List<JObject>>();

                weapons.AsParallel().ForAll(weapon =>
                {
                    try
                    {
                        if (Mappings.ContainsKey(weapon["nameTextMapHash"].ToString()))
                        {
                            var name = Mappings[weapon["nameTextMapHash"].ToString()];
                            string PascalCase = CultureInfo.GetCultureInfo("en").TextInfo.ToTitleCase(name); // Dull Blade
                            string nameGOOD = Regex.Replace(PascalCase, @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);              // DullBlade
                            string nameKey = nameGOOD.ToLower();                                             // dullblade

                            // An added weapon is new data; the inverted check used to
                            // leave the list unsaved on a forced (empty) update.
                            if (data.TryAdd(nameKey, nameGOOD)) status = UpdateStatus.Success;
                        }
                        else Logger.Warn("Weapon hash {0} not found in Mappings. It's likely unreleased.", weapon["nameTextMapHash"].ToString());
                    }
                    catch (Exception ex) { Logger.Warn(ex, weapon["nameTextMapHash"].ToString()); }
                });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex);
                status = UpdateStatus.Fail;
            }

            if (status == UpdateStatus.Success)
            {
                bool saved = IsPlausibleCount(ListType.Weapons, data.Count)
                    && SaveJsonToFile(JsonConvert.SerializeObject(new SortedDictionary<string, string>(data)), WeaponsJson);

                if (!saved) status = UpdateStatus.Fail;
            }

            return status;
        }

        private UpdateStatus UpdateMaterials(bool force)
        {
            var status = UpdateStatus.Skipped;

            var materialCategories = new List<string>
            {
                "MATERIAL_EXP_FRUIT",
                "MATERIAL_AVATAR_MATERIAL",
                "MATERIAL_EXCHANGE",
                "MATERIAL_WOOD",
                "MATERIAL_FISH_BAIT",
                "MATERIAL_WEAPON_EXP_STONE",
            };

            // Never delete the previous file up front: generate into memory and
            // only replace the file once the new data is complete and plausible.
            // Ported from taiwenlee/Inventory_Kamera (MIT): 45e89685
            var data = force
                ? new ConcurrentDictionary<string, string>()
                : JToken.Parse(LoadJsonFromFile(MaterialsJson)).ToObject<ConcurrentDictionary<string, string>>();

            var materials = JArray.Parse(LoadJsonFromURLAsync(MaterialsURL)).ToObject<List<JObject>>();
            materials.RemoveAll(material => !(material.TryGetValue("materialType", out var materialType) && materialCategories.Contains(materialType.ToString())));

            materials.AsParallel().ForAll(material =>
            {
                try
                {
                    JToken jToken = material["nameTextMapHash"];
                    if (Mappings.TryGetValue(jToken.ToString(), out var name))
                    {
                        var PascalCase = CultureInfo.GetCultureInfo("en").TextInfo.ToTitleCase(name);
                        var nameGood = Regex.Replace(PascalCase, @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
                        var nameKey = nameGood.ToLower();

                        if (data.TryAdd(nameKey, nameGood) && status != UpdateStatus.Fail) status = UpdateStatus.Success;
                    }
                    else
                    {
                        Logger.Warn("Material hash {0} not found in Mappings.", material["nameTextMapHash"].ToString());
                    }
                }
                catch (Exception ex) { Logger.Warn(ex); }
            });

            if (status == UpdateStatus.Success)
            {
                bool saved = IsPlausibleCount(ListType.Materials, data.Count)
                    && SaveJsonToFile(JsonConvert.SerializeObject(new SortedDictionary<string, string>(data)), MaterialsJson);

                if (!saved) status = UpdateStatus.Fail;
            }

            return status;
        }

        // A generated table far below these counts means the download or dump was
        // broken; the caller keeps the previous file instead of replacing it.
        // Ported from taiwenlee/Inventory_Kamera (MIT): 45e89685
        private bool IsPlausibleCount(ListType list, int count)
        {
            int minimum;
            switch (list)
            {
                case ListType.Characters: minimum = MinimumCharacters; break;
                case ListType.Artifacts: minimum = MinimumArtifacts; break;
                case ListType.Weapons: minimum = MinimumWeapons; break;
                case ListType.Materials: minimum = MinimumMaterials; break;
                default: return true;
            }

            if (count >= minimum) return true;

            Logger.Error("Generated {0} table has only {1} entries (expected at least {2}).", list, count, minimum);
            return false;
        }

        private string FetchHTML(string url)
        {
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.Encoding = System.Text.Encoding.UTF8;
                    return client.DownloadString(url);
                }
            }
            catch { throw; }

        }

        private string LoadJsonFromFile(string fileName)
        {
            lock (this)
            {
                try
                {
                    using (StreamReader file = File.OpenText(ListsDir + fileName))
                    using (JsonTextReader reader = new JsonTextReader(file))
                    {
                        return JToken.ReadFrom(reader).ToString();
                    }
                }
                catch (Exception)
                {
                    File.Create(ListsDir + fileName).Close();
                    return "{}";
                }
            }
        }

        private bool SaveJsonToFile(string json, string fileName)
        {
            lock (this)
            {
                string path = ListsDir + fileName;
                string tempPath = path + ".tmp";

                try
                {
                    // Write to a temporary file and swap it in, so a failure
                    // halfway through never truncates the previous data file.
                    using (StreamWriter file = new StreamWriter(tempPath))
                    using (JsonTextWriter writer = new JsonTextWriter(file))
                    {
                        writer.Formatting = Formatting.Indented;
                        JToken.Parse(json).WriteTo(writer);
                    }

                    if (File.Exists(path))
                        File.Replace(tempPath, path, null);
                    else
                        File.Move(tempPath, path);

                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex);

                    try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                    catch { /* best effort cleanup */ }

                    return false;
                }
            }
        }

        private static string ConvertToGOOD(string text)
        {
            foreach (Match match in Regex.Matches(text, @"&#\d*;", RegexOptions.None, RegexTimeouts.RegexTimeout))
            {
                if (int.TryParse(Regex.Replace(match.Value, @"\D", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout), out int dec))
                    text = text.Replace(match.Value, Char.ToString(Convert.ToChar(dec)));

                else
                    Logger.Warn("Failed to parse decimal value {0} from string {1}", match.Value, text);
            }
            var pascal = CultureInfo.GetCultureInfo("en-US").TextInfo.ToTitleCase(text);
            return Regex.Replace(pascal, @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
        }
    }

    public enum ListType
    {
        Characters,
        Weapons,
        Artifacts,
        CharacterDevelopmentItems,
        Materials,
    }

    public enum UpdateStatus
    {
        Fail,
        Skipped,
        Success,
    }
}
