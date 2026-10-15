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
        private static readonly object MappingsLock = new object();
        private static readonly object JsonFileLock = new object();
        private const string CharactersJson = "characters.json";
        private const string TravelerMetadataJson = "traveler.metadata.json";
        private const int TravelerGeneratorRevision = 1;
        private const string MaterialsJson = "materials.json";

        // JSON property names shared by the generated lookup tables and metadata.
        private const string NameTextMapHashKey = "nameTextMapHash";
        private const string GoodKey = "GOOD";
        private const string ElementKey = "Element";
        private const string TravelerKey = "traveler";
        private const string FormatKey = "format";
        private const string GeneratorRevisionKey = "generatorRevision";
        private const string TalentAuto = "auto";
        private const string TalentSkill = "skill";
        private const string TalentBurst = "burst";

        // Prefixes shared by the traveler/artifact generator's validation messages.
        private const string TravelerDepotPrefix = "Traveler depot ";
        private const string ArtifactSetPrefix = "Artifact set ";

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
        private const string commitsFeedAddress = "https://gitlab.com/Dimbreath/animegamedata2/-/commits/main.atom";
        private const string repoBaseAddress = "https://gitlab.com/Dimbreath/animegamedata2/-/raw/main/";
        private const string TextMapURL = repoBaseAddress + "TextMap/TextMap_MediumEN.json";
        private const string CharactersURL = repoBaseAddress + "ExcelBinOutput/AvatarExcelConfigData.json";
        private const string ConstellationsURL = repoBaseAddress + "ExcelBinOutput/FetterInfoExcelConfigData.json";
        private const string TalentsURL = repoBaseAddress + "ExcelBinOutput/AvatarTalentExcelConfigData.json";
        private const string SkillsURL = repoBaseAddress + "ExcelBinOutput/AvatarSkillExcelConfigData.json";
        private const string SkillDepotsURL = repoBaseAddress + "ExcelBinOutput/AvatarSkillDepotExcelConfigData.json";

        private const string ArtifactSetsURL = repoBaseAddress + "ExcelBinOutput/ReliquarySetExcelConfigData.json";
        private const string ArtifactAffixesURL = repoBaseAddress + "ExcelBinOutput/EquipAffixExcelConfigData.json";
        private const string ArtifactSetConfig = repoBaseAddress + "ExcelBinOutput/ReliquaryExcelConfigData.json";

        private const string WeaponsURL = repoBaseAddress + "ExcelBinOutput/WeaponExcelConfigData.json";
        private const string MaterialsURL = repoBaseAddress + "ExcelBinOutput/MaterialExcelConfigData.json";

        private ConcurrentDictionary<string, string> Mappings = new ConcurrentDictionary<string, string>();

        internal Version LocalVersion;
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
                ? throw new InvalidOperationException("Could not get current Genshin version.")
                : RemoteVersion.CompareTo(LocalVersion) > 0;
        }

        private static Version CheckRemoteVersion()
        {
            // Commit titles follow "CNRELWin<game version>_<build hashes>",
            // e.g. "CNRELWin5.8.0_R48379043_S48511369_D48533839", so the first
            // numeric triple found in the newest entry is the game version.
            string pattern = @"(\d+\.\d+\.\d+)";

            using (var response = new HttpClient().GetAsync(commitsFeedAddress))
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
                    || (string)metadata[FormatKey] != "InventoryKameraArtifactCatalog"
                    || (int?)metadata[GeneratorRevisionKey] != ArtifactGeneratorRevision
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
                    Logger.Info("Repaired Traveler data: {0} elements, generator revision {1}", ((JArray)traveler[ElementKey]).Count, TravelerGeneratorRevision);
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
                var traveler = data[TravelerKey] as JObject;
                var metadata = JObject.Parse(File.ReadAllText(metadataPath));
                if (traveler == null || (string)traveler[GoodKey] != "Traveler"
                    || data.Properties().Count(IsTravelerProperty) != 1) return true;
                var supported = traveler[ElementKey] as JArray;
                var orders = traveler["ConstellationOrder"] as JObject;
                if (supported == null || orders == null || supported.Count != elements.Count || orders.Count != elements.Count) return true;
                foreach (string element in elements.Select(x => x.ToLowerInvariant()))
                {
                    if (supported.Count(x => (string)x == element) != 1) return true;
                    var order = orders[element] as JArray;
                    if (order == null || order.Count != 2 || order.Any(x => !new[] { TalentAuto, TalentSkill, TalentBurst }.Contains((string)x))) return true;
                }
                return (string)metadata[FormatKey] != "InventoryKameraTravelerCatalog"
                    || (int?)metadata[GeneratorRevisionKey] != TravelerGeneratorRevision
                    || !string.Equals((string)metadata["entrySha256"], TravelerDataHash(traveler), StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        private bool SaveTravelerMetadata(JObject traveler)
        {
            var metadata = new JObject
            {
                [FormatKey] = "InventoryKameraTravelerCatalog",
                [GeneratorRevisionKey] = TravelerGeneratorRevision,
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
            string good = (string)property.Value[GoodKey];
            return good == "Traveler" || good == "PlayerBoy" || good == "PlayerGirl";
        }

        private static bool UpsertTraveler(JObject data, JObject traveler)
        {
            var existing = data.Properties().Where(IsTravelerProperty).ToList();
            bool changed = existing.Count != 1 || existing[0].Name != TravelerKey || !JToken.DeepEquals(existing[0].Value, traveler);
            foreach (var property in existing) property.Remove();
            data[TravelerKey] = traveler.DeepClone();
            return changed;
        }

        // Joined source rows for the Traveler rebuild, keyed by their game IDs.
        private sealed class TravelerSources
        {
            internal TravelerSources(ILookup<int, JObject> avatarsById, ILookup<int, JObject> depotsById,
                ILookup<int, JObject> skillsById, ILookup<int, JObject> talentsById)
            {
                AvatarsById = avatarsById;
                DepotsById = depotsById;
                SkillsById = skillsById;
                TalentsById = talentsById;
            }

            internal ILookup<int, JObject> AvatarsById { get; }
            internal ILookup<int, JObject> DepotsById { get; }
            internal ILookup<int, JObject> SkillsById { get; }
            internal ILookup<int, JObject> TalentsById { get; }
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
            var sources = new TravelerSources(
                avatars.OfType<JObject>().ToLookup(row => (int?)row["id"] ?? 0),
                depots.OfType<JObject>().ToLookup(row => (int?)row["id"] ?? 0),
                skills.OfType<JObject>().ToLookup(row => (int?)row["id"] ?? 0),
                talents.OfType<JObject>().ToLookup(row => (int?)row["talentId"] ?? 0));
            var orders = new SortedDictionary<string, JArray>(StringComparer.Ordinal);
            CollectTravelerConstellationOrders(sources, nativeElements, textmap, orders);
            return new JObject
            {
                [GoodKey] = "Traveler",
                [ElementKey] = new JArray(orders.Keys),
                ["ConstellationName"] = new JArray("Viator", "Viatrix"),
                ["ConstellationOrder"] = new JObject(orders.Select(pair => new JProperty(pair.Key, pair.Value))),
                ["WeaponType"] = (int)WeaponType.Sword
            };
        }

        private static void CollectTravelerConstellationOrders(TravelerSources sources,
            Dictionary<string, string> nativeElements, IDictionary<string, string> textmap,
            SortedDictionary<string, JArray> orders)
        {
            foreach (int avatarId in new[] { 10000005, 10000007 })
            {
                var avatar = RequireTravelerRow(sources.AvatarsById[avatarId], "avatar " + avatarId);
                if ((string)avatar["useType"] != "AVATAR_FORMAL" || (string)avatar["weaponType"] != "WEAPON_SWORD_ONE_HAND")
                    throw new InvalidDataException("Traveler avatar " + avatarId + " is not a playable sword avatar.");
                var candidates = avatar["candSkillDepotIds"] as JArray;
                if (candidates == null || candidates.Count == 0) throw new InvalidDataException("Traveler candidate skill depots are missing.");
                var seenDepots = new HashSet<int>();
                var seenElements = new HashSet<string>(StringComparer.Ordinal);
                foreach (var candidate in candidates)
                {
                    RecordTravelerDepotOrder(candidate, sources, nativeElements, seenDepots, seenElements, textmap, orders);
                }
                if (seenElements.Count != nativeElements.Count || nativeElements.Values.Any(x => !seenElements.Contains(x)))
                    throw new InvalidDataException("Traveler avatar " + avatarId + " does not provide all seven elemental skill depots.");
            }
        }

        private static void RecordTravelerDepotOrder(JToken candidate, TravelerSources sources,
            Dictionary<string, string> nativeElements, HashSet<int> seenDepots, HashSet<string> seenElements,
            IDictionary<string, string> textmap, SortedDictionary<string, JArray> orders)
        {
            int depotId = RequireTravelerId(candidate, "candidate depot");
            if (!seenDepots.Add(depotId)) throw new InvalidDataException("Duplicate Traveler candidate depot " + depotId + ".");
            var depot = RequireTravelerRow(sources.DepotsById[depotId], "skill depot " + depotId);
            int burstId = (int?)depot["energySkill"] ?? 0;
            if (burstId == 0) return; // The common, elementless depot has no burst.
            var burst = RequireTravelerRow(sources.SkillsById[burstId], "burst " + burstId);
            string element;
            if (!nativeElements.TryGetValue((string)burst["costElemType"] ?? "", out element) || !seenElements.Add(element))
                throw new InvalidDataException(TravelerDepotPrefix + depotId + " has an unknown or duplicate element.");
            var skillIds = depot["skills"] as JArray;
            var talentIds = depot["talents"] as JArray;
            if (skillIds == null || skillIds.Count < 2 || talentIds == null || talentIds.Count != 6)
                throw new InvalidDataException(TravelerDepotPrefix + depotId + " lacks its skill or six constellation references.");
            int autoId = RequireTravelerId(skillIds[0], "normal attack in depot " + depotId);
            int skillId = RequireTravelerId(skillIds[1], "elemental skill in depot " + depotId);
            if (autoId == skillId || skillId == burstId || autoId == burstId)
                throw new InvalidDataException(TravelerDepotPrefix + depotId + " reuses a talent skill ID.");
            var order = BuildTravelerDepotOrder(depotId, talentIds, sources, textmap,
                new TravelerTalentSkills { AutoId = autoId, SkillId = skillId, BurstId = burstId, Burst = burst });
            JArray previous;
            if (orders.TryGetValue(element, out previous) && !JToken.DeepEquals(previous, order))
                throw new InvalidDataException("The two Traveler avatars disagree on " + element + " constellation order.");
            orders[element] = order;
        }

        /// <summary>The depot's normal-attack, elemental-skill and burst rows referenced by its constellations.</summary>
        private sealed class TravelerTalentSkills
        {
            internal int AutoId;
            internal int SkillId;
            internal int BurstId;
            internal JObject Burst;
        }

        private static JArray BuildTravelerDepotOrder(int depotId, JArray talentIds, TravelerSources sources,
            IDictionary<string, string> textmap, TravelerTalentSkills skills)
        {
            var auto = RequireTravelerRow(sources.SkillsById[skills.AutoId], "normal attack " + skills.AutoId);
            var skill = RequireTravelerRow(sources.SkillsById[skills.SkillId], "elemental skill " + skills.SkillId);
            string autoName = RequireTravelerText(auto[NameTextMapHashKey], textmap, "normal attack " + skills.AutoId);
            string skillName = RequireTravelerText(skill[NameTextMapHashKey], textmap, "elemental skill " + skills.SkillId);
            string burstName = RequireTravelerText(skills.Burst[NameTextMapHashKey], textmap, "burst " + skills.BurstId);
            var ids = talentIds.Select(x => RequireTravelerId(x, "constellation in depot " + depotId)).ToList();
            if (ids.Distinct().Count() != 6) throw new InvalidDataException(TravelerDepotPrefix + depotId + " repeats a constellation ID.");
            var c3 = RequireTravelerRow(sources.TalentsById[ids[2]], "C3 talent " + ids[2]);
            var c5 = RequireTravelerRow(sources.TalentsById[ids[4]], "C5 talent " + ids[4]);
            return new JArray
            {
                TravelerUpgradeType(RequireTravelerText(c3["descTextMapHash"], textmap, "C3 talent " + ids[2]), skills.AutoId, skills.SkillId, skills.BurstId, autoName, skillName, burstName),
                TravelerUpgradeType(RequireTravelerText(c5["descTextMapHash"], textmap, "C5 talent " + ids[4]), skills.AutoId, skills.SkillId, skills.BurstId, autoName, skillName, burstName)
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
                CollectLinkedUpgradeTypes(links, autoId, skillId, burstId, matches);
            }
            else
            {
                CollectNamedUpgradeTypes(description, autoName, skillName, burstName, matches);
            }
            if (matches.Count != 1) throw new InvalidDataException("Traveler constellation has no unambiguous talent upgrade.");
            return matches[0];
        }

        private static void CollectLinkedUpgradeTypes(List<int> links, int autoId, int skillId, int burstId, List<string> matches)
        {
            if (links.Any(x => x != autoId && x != skillId && x != burstId))
                throw new InvalidDataException("Traveler constellation links to a skill outside its depot.");
            if (links.Contains(autoId)) matches.Add(TalentAuto);
            if (links.Contains(skillId)) matches.Add(TalentSkill);
            if (links.Contains(burstId)) matches.Add(TalentBurst);
        }

        private static void CollectNamedUpgradeTypes(string description, string autoName, string skillName, string burstName, List<string> matches)
        {
            // Older elemental constellations embed exact mapped names instead of links.
            if (description.IndexOf(autoName, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(TalentAuto);
            if (description.IndexOf(skillName, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(TalentSkill);
            if (description.IndexOf(burstName, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(TalentBurst);
        }

        private void LoadMappings()
        {
            lock (MappingsLock)
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

        private static string LoadJsonFromURLAsync(string url)
        {
            using (WebClient client = new WebClient())
            {
                client.Encoding = System.Text.Encoding.UTF8;
                return client.DownloadString(url);
            }
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
                Logger.Error(e, "Failed to update {0} data", list);
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
                data = LoadExistingCharacterData(force);
                var avatars = JArray.Parse(LoadJsonFromURLAsync(CharactersURL));
                var characters = avatars.ToObject<List<JObject>>();
                var constellations = JArray.Parse(LoadJsonFromURLAsync(ConstellationsURL)).ToObject<List<JObject>>();
                var talentDefinitions = JArray.Parse(LoadJsonFromURLAsync(TalentsURL));
                var skillDefinitions = JArray.Parse(LoadJsonFromURLAsync(SkillsURL));
                var talents = talentDefinitions.ToObject<List<JObject>>();
                var skills = skillDefinitions.ToObject<List<JObject>>();
                traveler = BuildTravelerData(avatars, JArray.Parse(LoadJsonFromURLAsync(SkillDepotsURL)),
                    skillDefinitions, talentDefinitions, Mappings);
                bool travelerChanged;
                data = MergeTravelerData(data, traveler, out travelerChanged);
                if (travelerChanged || TravelerUpdateRequiredCore()) status = UpdateStatus.Success;

                RemoveNonPlayableAvatars(characters);

                Logger.Debug("Found {0} playable characters available", characters.Count);
                status = RecordPlayableCharacters(characters, data, skills, talents, constellations, status);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex);
                status = UpdateStatus.Fail;
            }

            if (status == UpdateStatus.Success)
            {
                status = SaveUpdatedCharacters(data, traveler);
            }

            return status;
        }

        private ConcurrentDictionary<string, JObject> LoadExistingCharacterData(bool force)
        {
            string path = Path.Combine(ListsDir, CharactersJson);
            if (!force && File.Exists(path))
                return JObject.Parse(File.ReadAllText(path)).ToObject<ConcurrentDictionary<string, JObject>>();
            return new ConcurrentDictionary<string, JObject>();
        }

        private static ConcurrentDictionary<string, JObject> MergeTravelerData(ConcurrentDictionary<string, JObject> data,
            JObject traveler, out bool travelerChanged)
        {
            var rebuilt = JObject.FromObject(data);
            travelerChanged = UpsertTraveler(rebuilt, traveler);
            return rebuilt.ToObject<ConcurrentDictionary<string, JObject>>();
        }

        private static void RemoveNonPlayableAvatars(List<JObject> characters)
        {
            characters.RemoveAll(c => !c.ContainsKey("useType") || c["useType"].ToString() != "AVATAR_FORMAL"
                || (int?)c["id"] == 10000005 || (int?)c["id"] == 10000007);
        }

        private UpdateStatus RecordPlayableCharacters(List<JObject> characters, ConcurrentDictionary<string, JObject> data,
            List<JObject> skills, List<JObject> talents, List<JObject> constellations, UpdateStatus status)
        {
            characters.AsParallel().ForAll(character =>
            {
                // One unmapped hash must not abort the whole parallel update.
                if (!Mappings.TryGetValue(character[NameTextMapHashKey]?.ToString() ?? string.Empty, out string mappedName) || string.IsNullOrWhiteSpace(mappedName))
                {
                    Logger.Warn("Skipping avatar {0}: name hash {1} has no TextMap entry.", character["id"], character[NameTextMapHashKey]?.ToString());
                    return;
                }

                if (RecordNewCharacter(character, mappedName, data, skills, talents, constellations))
                    status = UpdateStatus.Success;
            });
            return status;
        }

        private UpdateStatus SaveUpdatedCharacters(ConcurrentDictionary<string, JObject> data, JObject traveler)
        {
            bool saved = IsPlausibleCount(ListType.Characters, data.Count)
                && SaveJsonToFile(JsonConvert.SerializeObject(new SortedDictionary<string, JObject>(data)), CharactersJson);

            if (!saved || !SaveTravelerMetadata(traveler)) return UpdateStatus.Fail;
            return UpdateStatus.Success;
        }

        private bool RecordNewCharacter(JObject character, string mappedName, ConcurrentDictionary<string, JObject> data,
            List<JObject> skills, List<JObject> talents, List<JObject> constellations)
        {
            string name = mappedName;

            try
            {
                string pascalCase = CultureInfo.GetCultureInfo("en").TextInfo.ToTitleCase(name);
                string nameGood = Regex.Replace(pascalCase, @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
                string nameKey = nameGood.ToLower();
                int characterId = (int)character["id"];

                // Placeholder avatars (beta Columbina/Ineffa, trial duplicates,
                // UGC templates) reuse real characters' keys but have no fetter
                // data, which yields empty elements and can shadow the real
                // entry depending on parallel processing order.
                // Ported from taiwenlee/Inventory_Kamera (MIT): 43c16129
                if (characterId > 10000900) return false;

                if (!data.ContainsKey(nameKey))
                {
                    var context = new CharacterImportContext
                    {
                        Character = character,
                        CharacterId = characterId,
                        NameGood = nameGood,
                        Skills = skills,
                        Talents = talents,
                        Constellations = constellations,
                        Mappings = Mappings
                    };
                    JObject value;
                    if (TryBuildNewCharacterValue(ref name, context, out value) && data.TryAdd(nameKey, value)) return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Problem when generating character information for {0}", name);
            }

            return false;
        }

        /// <summary>Source rows and mappings needed to import one character into the lookup table.</summary>
        private sealed class CharacterImportContext
        {
            internal JObject Character;
            internal int CharacterId;
            internal string NameGood;
            internal List<JObject> Skills;
            internal List<JObject> Talents;
            internal List<JObject> Constellations;
            internal ConcurrentDictionary<string, string> Mappings;
        }

        private static bool TryBuildNewCharacterValue(ref string name, CharacterImportContext context, out JObject value)
        {
            value = new JObject();
            value.Add("GOOD", context.NameGood);

            // Some characters have different internal names.
            // Ex: Jean -> Qin, Yanfei -> Feiyan, etc.
            string[] iconParts = context.Character["iconName"].ToString().Split('_'); // UI_AvatarIcon_[Qin] -> Qin
            name = iconParts[iconParts.Length - 1];
            string iconName = name;

            // Traveler was rebuilt once above from explicit source-ID joins.
            // Not every avatar has a Skill_S_ entry (e.g. test ones),
            // so look it up tolerantly instead of throwing.
            // Ported from taiwenlee/Inventory_Kamera (MIT): 480222b9
            var characterSkill = context.Skills.FirstOrDefault(entry => entry["skillIcon"]?.ToString().Contains($"Skill_S_{iconName}") == true);
            if (characterSkill == null)
            {
                Logger.Debug("Skipping {0} - no skill data available", name);
                value = null;
                return false;
            }

            string skill = context.Mappings[characterSkill[NameTextMapHashKey].ToString()].ToString();

            value.Add("ConstellationName", new JArray
            {
                GetConstellationNameFromId(context.CharacterId, context.Constellations, context.Mappings)
            });

            value.Add("ConstellationOrder", BuildConstellationOrder(context.Talents, name, skill, context.Mappings));

            value.Add("Element", new JArray(GetElementFromID(context.CharacterId, context.Constellations, context.Mappings)));

            value.Add("WeaponType", (int)ParseWeaponType(context.Character["weaponType"].ToString(), name));
            return true;
        }

        private static JArray BuildConstellationOrder(List<JObject> talents, string name, string skill,
            ConcurrentDictionary<string, string> mappings)
        {
            var constellationOrder = new JArray();

            // The skill/burst name is always mentioned in the constellation's description
            // so we'll check for it. C3 and C5 can also level the normal attack for
            // characters such as Freminet, Sethos, Arlecchino or Neuvillette.
            // Ported from taiwenlee/Inventory_Kamera (MIT): 191fa8f2
            constellationOrder.Add(ConstellationUpgradeType(talents, name, 2, skill, mappings));
            constellationOrder.Add(ConstellationUpgradeType(talents, name, 4, skill, mappings));
            return constellationOrder;
        }

        private static string ConstellationUpgradeType(List<JObject> talents, string name, int tierIndex, string skill,
            ConcurrentDictionary<string, string> mappings)
        {
            string description = talents.Where(entry => entry["icon"]?.ToString().Contains(name) == true).ElementAt(tierIndex)["descTextMapHash"].ToString();
            description = mappings[description].ToString();
            if (description.Contains("Normal Attack"))
            {
                return TalentAuto;
            }
            else if (description.Contains(skill))
            {
                return TalentSkill;
            }
            else
            {
                return TalentBurst;
            }
        }

        private static WeaponType ParseWeaponType(string weaponArchetype, string name)
        {
            WeaponType weaponType;
            if (weaponArchetype.Contains("SWORD_ONE_HAND")) weaponType = WeaponType.Sword;
            else if (weaponArchetype.Contains("CLAYMORE")) weaponType = WeaponType.Claymore;
            else if (weaponArchetype.Contains("POLE")) weaponType = WeaponType.Polearm;
            else if (weaponArchetype.Contains("BOW")) weaponType = WeaponType.Bow;
            else if (weaponArchetype.Contains("CATALYST")) weaponType = WeaponType.Catalyst;
            else if (weaponArchetype.Contains("CROSSBOW")) weaponType = WeaponType.Sword; // Not a released weapon class; current dumps only tag the traveler rows with it
            else throw new InvalidOperationException($"{name} uses unknown weapon type {weaponArchetype}");
            return weaponType;
        }

        private static string GetConstellationNameFromId(int targetId, List<JObject> constellations,
            ConcurrentDictionary<string, string> mappings)
        {
            var constellation = constellations.FirstOrDefault(entry => ((int)entry["avatarId"]) == targetId);
            return constellation == null ? "" : mappings[constellation["avatarConstellationBeforTextMapHash"].ToString()].ToString();
        }

        private static string GetElementFromID(int targetId, List<JObject> constellations,
            ConcurrentDictionary<string, string> mappings)
        {
            var element = constellations.FirstOrDefault(entry => ((int)entry["avatarId"]) == targetId);
            return element == null ? "" : mappings[element["avatarVisionBeforTextMapHash"].ToString()].ToString().ToLower();
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
                [FormatKey] = "InventoryKameraArtifactCatalog",
                [GeneratorRevisionKey] = ArtifactGeneratorRevision,
                ["tableSha256"] = ArtifactTableHash(tablePath),
                ["setCount"] = data.Count,
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            if (!SaveJsonToFile(metadata.ToString(Formatting.None), ArtifactMetadataJson)) return UpdateStatus.Fail;
            Logger.Info("Updated artifact catalog: {0} enabled sets, generator revision {1}", data.Count, ArtifactGeneratorRevision);
            return UpdateStatus.Success;
        }

        // Joined source rows and shared constants for the artifact lookup rebuild.
        private sealed class ArtifactSources
        {
            internal ArtifactSources(ILookup<int, JObject> affixesById, ILookup<int, JObject> piecesById,
                Dictionary<string, string> slotsByEquipment, HashSet<int> prayerSets)
            {
                AffixesById = affixesById;
                PiecesById = piecesById;
                SlotsByEquipment = slotsByEquipment;
                PrayerSets = prayerSets;
            }

            internal ILookup<int, JObject> AffixesById { get; }
            internal ILookup<int, JObject> PiecesById { get; }
            internal Dictionary<string, string> SlotsByEquipment { get; }
            internal HashSet<int> PrayerSets { get; }
        }

        internal static JObject BuildArtifactLookup(JArray setDefs, JArray affixes, JArray pieces,
            IDictionary<string, string> textmap)
        {
            if (setDefs == null) throw new ArgumentNullException(nameof(setDefs));
            if (affixes == null) throw new ArgumentNullException(nameof(affixes));
            if (pieces == null) throw new ArgumentNullException(nameof(pieces));
            if (textmap == null) throw new ArgumentNullException(nameof(textmap));

            var sources = new ArtifactSources(
                affixes.OfType<JObject>().ToLookup(row => (int?)row["id"] ?? 0),
                pieces.OfType<JObject>().ToLookup(row => (int?)row["id"] ?? 0),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["EQUIP_BRACER"] = "flower", ["EQUIP_NECKLACE"] = "plume",
                    ["EQUIP_SHOES"] = "sands", ["EQUIP_RING"] = "goblet", ["EQUIP_DRESS"] = "circlet"
                },
                new HashSet<int> { 15009, 15010, 15011, 15013 });
            var seenSetIds = new HashSet<int>();
            var seenPieceNames = new HashSet<string>(StringComparer.Ordinal);
            var result = new SortedDictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var token in setDefs)
            {
                var definition = token as JObject;
                if (definition == null) throw new InvalidDataException("An artifact set definition is not an object.");
                if (((int?)definition["disableFilter"] ?? 0) != 0) continue;
                AddArtifactSet(definition, result, sources, seenSetIds, seenPieceNames, textmap);
            }
            if (result.Count == 0) throw new InvalidDataException("No enabled artifact sets were generated.");
            return new JObject(result.Select(pair => new JProperty(pair.Key, pair.Value)));
        }

        private static void AddArtifactSet(JObject definition, SortedDictionary<string, JObject> result,
            ArtifactSources sources, HashSet<int> seenSetIds, HashSet<string> seenPieceNames,
            IDictionary<string, string> textmap)
        {
            int setId = RequireArtifactId(definition["setId"], "setId");
            if (!seenSetIds.Add(setId)) throw new InvalidDataException("Duplicate artifact set ID " + setId + ".");
            int affixId = RequireArtifactId(definition["equipAffixId"], "equipAffixId for set " + setId);
            var names = sources.AffixesById[affixId].Select(row => RequireArtifactName(row[NameTextMapHashKey], textmap,
                "affix for set " + setId)).Distinct(StringComparer.Ordinal).ToList();
            if (names.Count != 1) throw new InvalidDataException(ArtifactSetPrefix + setId + " has no unique mapped affix name.");
            string setName = names[0], setGood = ConvertToGOOD(setName), setKey = setGood.ToLowerInvariant();
            if (setKey.Length == 0 || result.ContainsKey(setKey))
                throw new InvalidDataException(ArtifactSetPrefix + setId + " has an empty or ambiguous normalized name.");

            result.Add(setKey, new JObject
            {
                ["setName"] = setName, [GoodKey] = setGood, ["normalizedName"] = setKey,
                ["artifacts"] = CollectArtifactPieces(definition, setId, sources, seenPieceNames, textmap)
            });
        }

        private static JObject CollectArtifactPieces(JObject definition, int setId, ArtifactSources sources,
            HashSet<string> seenPieceNames, IDictionary<string, string> textmap)
        {
            var references = definition["containsList"] as JArray;
            int expectedCount = sources.PrayerSets.Contains(setId) ? 1 : 5;
            if (references == null || references.Count != expectedCount)
                throw new InvalidDataException(ArtifactSetPrefix + setId + " does not contain its expected " + expectedCount + " pieces.");
            var seenPieceIds = new HashSet<int>();
            var artifacts = new JObject();
            foreach (var reference in references)
            {
                AddArtifactPiece(reference, setId, artifacts, sources, seenPieceIds, seenPieceNames, textmap);
            }
            return artifacts;
        }

        private static void AddArtifactPiece(JToken reference, int setId, JObject artifacts, ArtifactSources sources,
            HashSet<int> seenPieceIds, HashSet<string> seenPieceNames, IDictionary<string, string> textmap)
        {
            int pieceId = RequireArtifactId(reference, "piece ID for set " + setId);
            var matchingPieces = sources.PiecesById[pieceId].ToList();
            if (!seenPieceIds.Add(pieceId) || matchingPieces.Count != 1)
                throw new InvalidDataException("Artifact piece " + pieceId + " is missing or ambiguous.");
            var piece = matchingPieces[0];
            if (RequireArtifactId(piece["setId"], "setId for piece " + pieceId) != setId)
                throw new InvalidDataException("Artifact piece " + pieceId + " belongs to a different set.");
            string slot;
            if (!sources.SlotsByEquipment.TryGetValue((string)piece["equipType"] ?? string.Empty, out slot)
                || artifacts[slot] != null || sources.PrayerSets.Contains(setId) && slot != "circlet")
                throw new InvalidDataException(ArtifactSetPrefix + setId + " has an unsupported or duplicate slot.");
            string pieceName = RequireArtifactName(piece[NameTextMapHashKey], textmap, "piece " + pieceId);
            string pieceGood = ConvertToGOOD(pieceName), pieceKey = pieceGood.ToLowerInvariant();
            if (pieceKey.Length == 0 || !seenPieceNames.Add(pieceKey))
                throw new InvalidDataException("Artifact piece " + pieceId + " has an empty or ambiguous normalized name.");
            artifacts[slot] = new JObject
            {
                ["artifactName"] = pieceName, [GoodKey] = pieceGood, ["normalizedName"] = pieceKey
            };
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
                        if (Mappings.ContainsKey(weapon[NameTextMapHashKey].ToString()))
                        {
                            var name = Mappings[weapon[NameTextMapHashKey].ToString()];
                            string PascalCase = CultureInfo.GetCultureInfo("en").TextInfo.ToTitleCase(name); // Dull Blade
                            string nameGOOD = Regex.Replace(PascalCase, @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);              // DullBlade
                            string nameKey = nameGOOD.ToLower();                                             // dullblade

                            // An added weapon is new data; the inverted check used to
                            // leave the list unsaved on a forced (empty) update.
                            if (data.TryAdd(nameKey, nameGOOD)) status = UpdateStatus.Success;
                        }
                        else Logger.Warn("Weapon hash {0} not found in Mappings. It's likely unreleased.", weapon[NameTextMapHashKey].ToString());
                    }
                    catch (Exception ex) { Logger.Warn(ex, weapon[NameTextMapHashKey].ToString()); }
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
                    JToken jToken = material[NameTextMapHashKey];
                    if (Mappings.TryGetValue(jToken.ToString(), out var name))
                    {
                        var PascalCase = CultureInfo.GetCultureInfo("en").TextInfo.ToTitleCase(name);
                        var nameGood = Regex.Replace(PascalCase, @"[\W]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
                        var nameKey = nameGood.ToLower();

                        if (data.TryAdd(nameKey, nameGood) && status != UpdateStatus.Fail) status = UpdateStatus.Success;
                    }
                    else
                    {
                        Logger.Warn("Material hash {0} not found in Mappings.", material[NameTextMapHashKey].ToString());
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
        private static bool IsPlausibleCount(ListType list, int count)
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

        private string LoadJsonFromFile(string fileName)
        {
            lock (JsonFileLock)
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
            lock (JsonFileLock)
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
