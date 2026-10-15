using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace InventoryKamera
{
    /// <summary>Canonical IDs and tier descriptions, independent of the game's visible list order.</summary>
    public sealed class AchievementCatalog
    {
        public const string CatalogUrl = "https://raw.githubusercontent.com/MadeBaruna/paimon-moe/main/src/data/achievement/en.json";
        private static readonly object LoadLock = new object();
        private static AchievementCatalog current;
        private static readonly Regex Markup = new Regex("<[^>]+>", RegexOptions.Compiled, RegexTimeouts.RegexTimeout);
        private static readonly Regex Numbers = new Regex(@"\b\d+(?:\.\d+)?\b", RegexOptions.Compiled, RegexTimeouts.RegexTimeout);

        public List<AchievementCategory> Categories { get; private set; } = new List<AchievementCategory>();
        public string SourceUrl { get; private set; } = CatalogUrl;
        public DateTime? FetchedAtUtc { get; private set; }
        public string SourceContentHash { get; private set; }
        public string LatestVersion { get; private set; }
        public string Warning { get; private set; }
        public int AchievementCount
        {
            get
            {
                int count = 0;
                foreach (var category in Categories)
                    foreach (var family in category.Families) count += family.Tiers.Count;
                return count;
            }
        }

        private AchievementCatalog() { }

        public static AchievementCatalog Load(bool refresh = false)
        {
            lock (LoadLock)
            {
                if (!refresh && current != null) return current;
                var cachePath = Path.GetFullPath(Path.Combine("inventorylists", "achievements.json"));
                var metadataPath = Path.Combine(Path.GetDirectoryName(cachePath), "achievements.metadata.json");
                AchievementCatalog cached = null;
                string cacheError = null;
                try
                {
                    if (File.Exists(cachePath))
                    {
                        cached = FromJson(File.ReadAllText(cachePath));
                        ValidateDownload(cached);
                        ReadMetadata(cached, metadataPath);
                    }
                }
                catch (Exception exception)
                {
                    cacheError = exception.Message;
                    cached = null;
                }
                if (cached == null && current != null) cached = current;
                if (cached != null && !refresh)
                {
                    current = cached;
                    return current;
                }

                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    string json;
                    using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                    {
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("InventoryKamera/1.0");
                        json = client.GetStringAsync(CatalogUrl).GetAwaiter().GetResult();
                    }
                    var downloaded = FromJson(json);
                    ValidateDownload(downloaded);
                    downloaded.FetchedAtUtc = DateTime.UtcNow;
                    try
                    {
                        WriteAtomic(cachePath, json);
                        WriteAtomic(metadataPath, new JObject
                        {
                            ["SourceUrl"] = CatalogUrl,
                            ["FetchedAtUtc"] = downloaded.FetchedAtUtc.Value,
                            ["SourceContentHash"] = downloaded.SourceContentHash,
                            ["LatestVersion"] = downloaded.LatestVersion,
                            ["CategoryCount"] = downloaded.Categories.Count,
                            ["AchievementCount"] = downloaded.AchievementCount
                        }.ToString(Formatting.Indented));
                    }
                    catch (Exception exception)
                    {
                        downloaded.Warning = "The achievement catalog was downloaded but could not be cached: " + exception.Message;
                    }
                    current = downloaded;
                    return current;
                }
                catch (Exception exception)
                {
                    if (cached != null)
                    {
                        cached.Warning = "The achievement catalog could not be refreshed; using the valid cached catalog. " + exception.Message;
                        current = cached;
                        return current;
                    }
                    throw new InvalidOperationException("The achievement catalog could not be loaded and no valid cache is available. "
                        + (cacheError == null ? string.Empty : "Cache: " + cacheError + ". ")
                        + "Download: " + exception.Message, exception);
                }
            }
        }

        // Kept separate from downloading so matching can be tested against small offline fixtures.
        internal static AchievementCatalog FromJson(string json)
        {
            var source = JObject.Parse(json);
            var result = new AchievementCatalog();
            using (var hash = SHA256.Create())
                result.SourceContentHash = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", string.Empty).ToLowerInvariant();
            var ids = new HashSet<int>();
            Version latest = null;
            foreach (var property in source.Properties())
            {
                int categoryId;
                var categoryObject = property.Value as JObject;
                if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out categoryId)
                    || categoryId < 0 || categoryObject == null)
                    throw new InvalidDataException("Invalid achievement category ID or object: " + property.Name);
                var name = ReadText(categoryObject, "name");
                var orderToken = categoryObject["order"];
                var entries = categoryObject["achievements"] as JArray;
                if (orderToken == null || orderToken.Type != JTokenType.Integer || entries == null || entries.Count == 0)
                    throw new InvalidDataException("Invalid achievement category metadata: " + name);
                var category = new AchievementCategory { Id = categoryId, Name = name, Order = (int)orderToken };
                foreach (var entry in entries)
                {
                    var tiers = entry is JArray ? ((JArray)entry).ToList() : new List<JToken> { entry };
                    if (tiers.Count == 0) throw new InvalidDataException("Empty achievement tier family in " + name);
                    var family = new AchievementFamily { CategoryId = categoryId };
                    foreach (var token in tiers)
                    {
                        var definitionObject = token as JObject;
                        if (definitionObject == null) throw new InvalidDataException("Invalid achievement tier in " + name);
                        var idToken = definitionObject["id"];
                        var rewardToken = definitionObject["reward"];
                        if (idToken == null || idToken.Type != JTokenType.Integer || (int)idToken <= 0
                            || rewardToken == null || rewardToken.Type != JTokenType.Integer || (int)rewardToken <= 0)
                            throw new InvalidDataException("Invalid achievement ID or reward in " + name);
                        var definition = new AchievementDefinition
                        {
                            Id = (int)idToken,
                            CategoryId = categoryId,
                            Name = ReadText(definitionObject, "name"),
                            Description = ReadText(definitionObject, "desc"),
                            Reward = (int)rewardToken,
                            TierIndex = family.Tiers.Count
                        };
                        if (!ids.Add(definition.Id)) throw new InvalidDataException("Duplicate achievement ID: " + definition.Id);
                        if (family.Tiers.Count == 0)
                        {
                            family.Id = definition.Id;
                            family.Name = definition.Name;
                        }
                        family.Tiers.Add(definition);
                        Version version;
                        if (Version.TryParse((string)definitionObject["ver"], out version) && (latest == null || version > latest))
                            latest = version;
                    }
                    category.Families.Add(family);
                }
                result.Categories.Add(category);
            }
            if (result.Categories.Count == 0) throw new InvalidDataException("The achievement catalog is empty.");
            result.Categories = result.Categories.OrderBy(category => category.Order).ThenBy(category => category.Id).ToList();
            result.LatestVersion = latest == null ? null : latest.ToString();
            return result;
        }

        public AchievementCategory MatchCategory(string text)
        {
            var normalized = Normalize(text);
            if (normalized.Length == 0) return null;
            var exact = Categories.Where(category => Normalize(category.Name) == normalized).ToList();
            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1) return null;
            var ranked = Categories.Select(category => new { Category = category, Score = Similarity(normalized, Normalize(category.Name)) })
                .OrderByDescending(candidate => candidate.Score).ToList();
            return ranked.Count > 0 && ranked[0].Score >= 90
                && (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= 7) ? ranked[0].Category : null;
        }

        public AchievementMatch Resolve(AchievementCategory category, string title, string description, int? reward)
        {
            if (category == null) return Failure("The achievement category was not recognized.");
            var normalizedTitle = Normalize(title);
            if (normalizedTitle.Length == 0) return Failure("The achievement title was not recognized.");
            var normalizedDescription = Normalize(description);
            var hasDescription = normalizedDescription.Length > 0;
            var observedNumbers = ExtractNumbers(description);
            // Some families use different names for each tier (for example a Roman-numeral suffix).
            var families = category.Families.Where(family => family.Tiers.Count > 0)
                .Select(family => new { Family = family, TitleScore = family.Tiers.Max(tier => Similarity(normalizedTitle, Normalize(tier.Name))) })
                .Where(candidate => candidate.TitleScore >= 84).ToList();
            bool hasExactTitle = families.Any(candidate => IsExactTitleScore(candidate.TitleScore));
            if (hasExactTitle) families = families.Where(candidate => IsExactTitleScore(candidate.TitleScore)).ToList();
            var candidates = new List<Candidate>();
            foreach (var familyCandidate in families)
            {
                foreach (var tier in familyCandidate.Family.Tiers)
                {
                    var titleScore = Similarity(normalizedTitle, Normalize(tier.Name));
                    if (titleScore < 84 || (hasExactTitle && !IsExactTitleScore(titleScore))) continue;
                    if (reward.HasValue && reward.Value != tier.Reward) continue;
                    var expectedNumbers = ExtractNumbers(tier.Description);
                    if (observedNumbers.Count > 0 && !NumbersAgree(observedNumbers, expectedNumbers)) continue;
                    var descriptionScore = hasDescription ? Similarity(normalizedDescription, Normalize(tier.Description)) : 0;
                    if (hasDescription && descriptionScore < 60) continue;
                    if (titleScore < 92 && (!hasDescription || descriptionScore < 75)) continue;
                    candidates.Add(new Candidate
                    {
                        Family = familyCandidate.Family,
                        Tier = tier,
                        Score = hasDescription ? titleScore * .65 + descriptionScore * .35 : titleScore
                    });
                }
            }
            candidates = candidates.OrderByDescending(candidate => candidate.Score).ToList();
            if (candidates.Count == 0)
            {
                // Do not change ordinary title thresholds. A digit confused with O in a short
                // title needs independent, strong evidence before it can be repaired.
                if (!hasExactTitle)
                {
                    var repaired = ResolveConfusableTitle(category, normalizedTitle, normalizedDescription, observedNumbers, reward);
                    if (repaired != null) return repaired;
                }
                return Failure("No catalog achievement matches the title, description, numbers and reward.");
            }
            var best = candidates[0];
            if (best.Score < 86) return Failure("Achievement recognition confidence is too low.", best.Score);
            var otherFamily = candidates.FirstOrDefault(candidate => candidate.Family.Id != best.Family.Id);
            if (otherFamily != null && best.Score - otherFamily.Score < 6)
                return Failure("The achievement title is ambiguous within this category.", best.Score);
            var otherTier = candidates.FirstOrDefault(candidate => candidate.Family.Id == best.Family.Id && candidate.Tier.Id != best.Tier.Id);
            if (otherTier != null && best.Score - otherTier.Score < 4)
                return new AchievementMatch { Family = best.Family, Confidence = best.Score, Error = "The achievement tier is ambiguous; its description or reward needs to be read again." };
            return new AchievementMatch { Family = best.Family, Tier = best.Tier, Confidence = best.Score };
        }

        private static AchievementMatch ResolveConfusableTitle(AchievementCategory category, string title,
            string description, List<string> observedNumbers, int? reward)
        {
            if (!reward.HasValue || description.Length < 24) return null;
            // Tesseract can duplicate the round glyph as "0O", or read O as 0 inside a word.
            // Preserve real title numbers, including goals and Roman-numeral tier suffixes.
            string repairedTitle = Regex.Replace(title, @"0o|o0", "o", RegexOptions.None, RegexTimeouts.RegexTimeout);
            repairedTitle = Regex.Replace(repairedTitle, @"(?<=[a-z])0|0(?=[a-z])", "o", RegexOptions.None, RegexTimeouts.RegexTimeout);
            if (repairedTitle == title) return null;
            var candidates = new List<Candidate>();
            foreach (var family in category.Families)
            {
                foreach (var tier in family.Tiers)
                {
                    if (Normalize(tier.Name) != repairedTitle || tier.Reward != reward.Value) continue;
                    // This exceptional path requires every description number, in order; it
                    // cannot repair a title on the strength of an incomplete numeric goal.
                    if (!observedNumbers.SequenceEqual(ExtractNumbers(tier.Description))) continue;
                    double descriptionScore = Similarity(description, Normalize(tier.Description));
                    if (descriptionScore < 60) continue;
                    candidates.Add(new Candidate { Family = family, Tier = tier, Score = 65 + descriptionScore * .35 });
                }
            }
            if (candidates.Count == 0) return null;
            candidates = candidates.OrderByDescending(candidate => candidate.Score).ToList();
            var best = candidates[0];
            // At least 95% description similarity is required, in addition to matching reward,
            // category, numeric goals and the exact repaired title.
            if (best.Score < 98.25) return null;
            var otherFamily = candidates.FirstOrDefault(candidate => candidate.Family.Id != best.Family.Id);
            if (otherFamily != null && best.Score - otherFamily.Score < 6)
                return Failure("The repaired OCR title is ambiguous within this category.", Math.Min(95, best.Score));
            var otherTier = candidates.FirstOrDefault(candidate => candidate.Family.Id == best.Family.Id && candidate.Tier.Id != best.Tier.Id);
            if (otherTier != null && best.Score - otherTier.Score < 4)
                return new AchievementMatch { Family = best.Family, Confidence = Math.Min(95, best.Score), Error = "The repaired OCR title has an ambiguous achievement tier." };
            return new AchievementMatch { Family = best.Family, Tier = best.Tier, Confidence = Math.Min(95, best.Score) };
        }

        private static AchievementMatch Failure(string error, double confidence = 0)
        {
            return new AchievementMatch { Error = error, Confidence = confidence };
        }

        private sealed class Candidate
        {
            public AchievementFamily Family;
            public AchievementDefinition Tier;
            public double Score;
        }

        private static string ReadText(JObject value, string property)
        {
            var token = value[property];
            if (token == null || token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token))
                throw new InvalidDataException("Missing achievement text: " + property);
            return (string)token;
        }

        private static void ValidateDownload(AchievementCatalog catalog)
        {
            if (catalog.Categories.Count < 10 || catalog.AchievementCount < 100)
                throw new InvalidDataException("The downloaded achievement catalog is unexpectedly small.");
        }

        private static void ReadMetadata(AchievementCatalog catalog, string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                var metadata = JObject.Parse(File.ReadAllText(path));
                if ((string)metadata["SourceUrl"] == CatalogUrl && (string)metadata["SourceContentHash"] == catalog.SourceContentHash)
                {
                    var fetched = metadata["FetchedAtUtc"];
                    if (fetched != null && fetched.Type != JTokenType.Null) catalog.FetchedAtUtc = fetched.ToObject<DateTime>().ToUniversalTime();
                }
            }
            catch (Exception)
            {
                // Invalid optional metadata must not invalidate a structurally valid raw catalog.
            }
        }

        private static List<string> ExtractNumbers(string text)
        {
            return Numbers.Matches(Markup.Replace(text ?? string.Empty, string.Empty)).Cast<Match>()
                .Select(match => match.Value).ToList();
        }

        private static bool NumbersAgree(List<string> observed, List<string> expected)
        {
            // A clipped description may omit numbers, but a conflicting visible threshold is never guessed.
            int position = 0;
            foreach (var number in observed)
            {
                while (position < expected.Count && expected[position] != number) position++;
                if (position == expected.Count) return false;
                position++;
            }
            return true;
        }

        internal static string Normalize(string text)
        {
            var value = WebUtility.HtmlDecode(Markup.Replace(text ?? string.Empty, string.Empty)).Normalize(NormalizationForm.FormD);
            var result = new StringBuilder();
            bool space = false;
            foreach (var character in value)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(character))
                {
                    if (space && result.Length > 0) result.Append(' ');
                    result.Append(char.ToLowerInvariant(character));
                    space = false;
                }
                else if (char.IsWhiteSpace(character)) space = true;
                // Quotes, periods and dashes vary between OCR and the catalog; letters and digits carry identity.
            }
            return result.ToString();
        }

        // Similarity() returns exactly 100 only for identical strings; any other
        // pair scores at most 100 - 100/max(left.Length, right.Length), so a
        // 0.0001 tolerance admits only the exact 100.0 for any realistic text
        // (a false positive would need strings over a million characters).
        private const double ExactScoreTolerance = 0.0001;

        private static bool IsExactTitleScore(double score)
        {
            return Math.Abs(score - 100) < ExactScoreTolerance;
        }

        private static double Similarity(string left, string right)
        {
            if (left == right) return 100;
            if (left.Length == 0 || right.Length == 0) return 0;
            var previous = new int[right.Length + 1];
            var next = new int[right.Length + 1];
            for (int j = 0; j <= right.Length; j++) previous[j] = j;
            for (int i = 1; i <= left.Length; i++)
            {
                next[0] = i;
                for (int j = 1; j <= right.Length; j++)
                    next[j] = Math.Min(Math.Min(previous[j] + 1, next[j - 1] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                var swap = previous;
                previous = next;
                next = swap;
            }
            return 100.0 * (1.0 - (double)previous[right.Length] / Math.Max(left.Length, right.Length));
        }

        private static void WriteAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}
