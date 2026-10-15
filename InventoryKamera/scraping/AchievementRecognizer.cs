using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using Tesseract;

namespace InventoryKamera
{
    public sealed class AchievementCategoryEntryOnScreen
    {
        public AchievementCategory Category { get; set; }
        public Rectangle Bounds { get; set; }
        public bool Selected { get; set; }
        public string RawText { get; set; }
    }

    /// <summary>Reads visible cards without using the game's search box or changing game state.</summary>
    public sealed class AchievementRecognizer
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly AchievementCatalog catalog;
        private Dictionary<string, AchievementObservation> previousViewport = new Dictionary<string, AchievementObservation>();
        private int? previousCategoryId;

        public int LastOcrRowCount { get; private set; }
        public int LastReusedRowCount { get; private set; }

        /// <summary>The bitmap is borrowed for the synchronous callback; clone it before retaining it.</summary>
        public Action<Bitmap, AchievementObservation> RowPreview { get; set; }

        public AchievementRecognizer(AchievementCatalog catalog)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public List<AchievementObservation> ReadPage(Bitmap frame, AchievementCategory category, int page)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (category == null) throw new ArgumentNullException(nameof(category));
            LastOcrRowCount = 0;
            LastReusedRowCount = 0;
            if (previousCategoryId != category.Id) previousViewport.Clear();
            var nextViewport = new Dictionary<string, AchievementObservation>();
            var result = new List<AchievementObservation>();
            var geometry = new Geometry(frame);
            using (var pixels = new Pixels(frame))
            {
                foreach (var bounds in FindRows(pixels, geometry))
                {
                    string fingerprint = RowFingerprint(pixels, geometry, bounds);
                    AchievementObservation cached;
                    if (previousViewport.TryGetValue(fingerprint, out cached))
                    {
                        // The returned observation and its ID list must not mutate the cached viewport.
                        var reused = CloneObservation(cached);
                        reused.Page = page;
                        reused.Top = bounds.Top;
                        result.Add(reused);
                        nextViewport[fingerprint] = CloneObservation(reused);
                        ++LastReusedRowCount;
                        continue;
                    }
                    var observation = new AchievementObservation
                    {
                        CategoryId = category.Id,
                        Page = page,
                        Top = bounds.Top,
                        CompletedIds = new List<int>()
                    };
                    ++LastOcrRowCount;
                    try
                    {
                        ReadCard(frame, pixels, geometry, bounds, category, observation);
                    }
                    catch (ThreadAbortException) { throw; }
                    catch (Exception ex)
                    {
                        observation.Error = "Could not read achievement card: " + ex.Message;
                    }
                    if (!string.IsNullOrEmpty(observation.Error)) SaveEvidence(frame, bounds, observation);
                    result.Add(observation);
                    if (string.IsNullOrEmpty(observation.Error) && observation.FamilyId.HasValue && observation.MatchedId.HasValue)
                        nextViewport[fingerprint] = CloneObservation(observation);
                    var preview = RowPreview;
                    if (preview != null)
                    {
                        try
                        {
                            using (var crop = frame.Clone(bounds, PixelFormat.Format24bppRgb)) preview(crop, observation);
                        }
                        catch (ThreadAbortException) { throw; }
                        catch (Exception ex) { Logger.Warn(ex, "Could not update the achievement preview"); }
                    }
                }
            }
            // Keep only the adjacent viewport. Unresolved cards are deliberately never cached.
            previousViewport = nextViewport;
            previousCategoryId = category.Id;
            return result;
        }

        private static AchievementObservation CloneObservation(AchievementObservation observation)
        {
            return new AchievementObservation
            {
                CategoryId = observation.CategoryId,
                Page = observation.Page,
                Top = observation.Top,
                Title = observation.Title,
                Description = observation.Description,
                Progress = observation.Progress,
                CompletionDate = observation.CompletionDate,
                Reward = observation.Reward,
                Total = observation.Total,
                CompletedStars = observation.CompletedStars,
                CompletedText = observation.CompletedText,
                Claimable = observation.Claimable,
                FamilyId = observation.FamilyId,
                MatchedId = observation.MatchedId,
                CompletedIds = observation.CompletedIds == null ? new List<int>() : new List<int>(observation.CompletedIds),
                Error = observation.Error,
                Confidence = observation.Confidence
            };
        }

        private static string RowFingerprint(Pixels pixels, Geometry geometry, Rectangle bounds)
        {
            using (var buffer = new MemoryStream())
            using (var writer = new BinaryWriter(buffer))
            {
                // Preserve completion evidence while excluding every other animated badge pixel.
                writer.Write(CountStars(pixels, geometry, bounds));
                // These are exactly the text columns, not the animated badge or primogem sprite.
                WriteGlyphMask(writer, pixels, geometry.Clip(Rectangle.FromLTRB(geometry.X(859),
                    bounds.Top + geometry.Y(7), geometry.X(1568), bounds.Bottom - geometry.Y(7))),
                    (c, x, y) => c.R < 135 && c.G < 135 && c.B < 135 || IsTanText(c));
                WriteGlyphMask(writer, pixels, geometry.Clip(Rectangle.FromLTRB(geometry.X(1605),
                    bounds.Bottom - geometry.Y(44), geometry.X(1636), bounds.Bottom - geometry.Y(25))),
                    (c, x, y) => c.R >= 220 && c.G >= 215 && c.B >= 205
                        && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 25);
                WriteGlyphMask(writer, pixels, geometry.Clip(Rectangle.FromLTRB(geometry.X(1680),
                    bounds.Top + geometry.Y(20), geometry.X(1807), bounds.Bottom - geometry.Y(3))),
                    (c, x, y) => IsTanText(c) || c.R < 135 && c.G < 135 && c.B < 135
                        || IsWhiteButtonInk(pixels, c, x, y));
                writer.Flush();
                using (var hash = SHA256.Create())
                    return Convert.ToBase64String(hash.ComputeHash(buffer.ToArray()));
            }
        }

        private static bool IsWhiteButtonInk(Pixels pixels, Color color, int x, int y)
        {
            if (color.R < 220 || color.G < 210 || color.B < 190) return false;
            // White claim-button letters are visible against their warm button, whereas the
            // cream card background is not ink. Nearby text outlines also remain deterministic.
            for (int dy = -1; dy <= 1; ++dy)
                for (int dx = -1; dx <= 1; ++dx)
                {
                    var neighbour = pixels.Get(x + dx, y + dy);
                    if (neighbour.R >= 170 && neighbour.R - neighbour.B >= 45 && neighbour.G <= 210) return true;
                }
            return false;
        }

        private static void WriteGlyphMask(BinaryWriter writer, Pixels pixels, Rectangle bounds,
            Func<Color, int, int, bool> isInk)
        {
            var mask = new bool[Math.Max(0, bounds.Width * bounds.Height)];
            int left = bounds.Width, top = bounds.Height, right = -1, bottom = -1;
            for (int y = 0; y < bounds.Height; ++y)
                for (int x = 0; x < bounds.Width; ++x)
                {
                    int px = bounds.Left + x, py = bounds.Top + y;
                    if (!isInk(pixels.Get(px, py), px, py)) continue;
                    mask[y * bounds.Width + x] = true;
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
            int width = right < left ? 0 : right - left + 1;
            int height = bottom < top ? 0 : bottom - top + 1;
            writer.Write(width);
            writer.Write(height);
            // Trim whitespace only. Do not resize, blur, or permit a differing glyph pixel.
            // This accommodates a one-pixel scroll translation without losing internal spacing.
            byte packed = 0;
            int bit = 0;
            for (int y = top; y <= bottom; ++y)
                for (int x = left; x <= right; ++x)
                {
                    if (mask[y * bounds.Width + x]) packed |= (byte)(1 << bit);
                    if (++bit != 8) continue;
                    writer.Write(packed);
                    packed = 0;
                    bit = 0;
                }
            if (bit != 0) writer.Write(packed);
        }

        public List<AchievementCategoryEntryOnScreen> ReadCategories(Bitmap frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            var geometry = new Geometry(frame);
            var result = new List<AchievementCategoryEntryOnScreen>();
            using (var pixels = new Pixels(frame))
            {
                int top = geometry.Y(162), bottom = geometry.Y(1018);
                var selectedBands = Bands(top, bottom, y =>
                {
                    int matches = 0, samples = 0;
                    // This strip exists only on the wider selected tile. Long selected names
                    // can occupy its right edge, but never this empty margin left of the icon.
                    for (int x = geometry.X(55); x < geometry.X(60); ++x)
                    {
                        var c = pixels.Get(x, y);
                        if (c.R > 215 && c.G > 205 && c.B > 180) ++matches;
                        ++samples;
                    }
                    return samples > 0 && matches >= samples * .8;
                }, geometry.Y(3), geometry.Y(65));
                var colorBands = Bands(top, bottom, y =>
                {
                    int matches = 0, samples = 0;
                    for (int x = geometry.X(65); x < geometry.X(71); ++x)
                    {
                        var c = pixels.Get(x, y);
                        if (c.R < 95 && c.G < 110 && c.B < 125 && c.B - c.R >= 8) ++matches;
                        ++samples;
                    }
                    return samples > 0 && matches >= samples * .8;
                }, geometry.Y(1), geometry.Y(65));
                // The sidebar tiles are translucent. A blue scene can make the
                // gaps satisfy the same dark-color predicate as the tiles and
                // join several cards into one band. The left card rule remains
                // lighter than its adjacent interior, regardless of the scene.
                var edgeBands = Bands(top, bottom, y =>
                {
                    int edge = 0, inside = 0, samples = 0;
                    for (int x = geometry.X(65); x < geometry.X(67); ++x)
                    {
                        var border = pixels.Get(x, y);
                        var fill = pixels.Get(x + Math.Max(1, geometry.X(6)), y);
                        if (border.R >= 180 || fill.R >= 180) return false;
                        edge += border.R + border.G + border.B;
                        inside += fill.R + fill.G + fill.B;
                        ++samples;
                    }
                    return samples > 0 && edge - inside >= samples * 24;
                }, geometry.Y(2), geometry.Y(65));
                var ordinaryBands = colorBands.Where(band => band.Height <= geometry.Y(112)).ToList();
                foreach (var band in edgeBands.Where(band => band.Height <= geometry.Y(112)))
                {
                    // Keep a previously detected full card instead of adding
                    // its inset border a second time. Hovered dark tiles still
                    // use their color band; their highlight shifts the left rule.
                    if (!ordinaryBands.Any(existing => Math.Min(existing.Bottom, band.Bottom) -
                        Math.Max(existing.Top, band.Top) >= Math.Min(existing.Height, band.Height) * .7))
                        ordinaryBands.Add(band);
                }
                var tiles = ordinaryBands.Concat(selectedBands).OrderBy(b => b.Top).ToList();
                foreach (var tile in tiles)
                {
                    // A sidebar tile touching the viewport edge may be clipped. Its title alone
                    // is insufficient to choose a reliable click location.
                    if (tile.Top <= top || tile.Bottom >= bottom) continue;
                    bool isSelected = selectedBands.Any(b => b.Top == tile.Top && b.Bottom == tile.Bottom);
                    var names = Bands(tile.Top + geometry.Y(6), tile.Bottom - geometry.Y(5), y =>
                    {
                        int count = 0;
                        for (int x = geometry.X(124); x < geometry.X(isSelected ? 665 : 653); ++x)
                        {
                            var c = pixels.Get(x, y);
                            bool ink = isSelected ? c.R < 130 && c.G < 135 && c.B < 140
                                : c.R > 195 && c.G > 190 && c.B > 170 && c.R - c.G < 22;
                            if (ink) ++count;
                        }
                        // A wrapped "(I)" has only 7-9 white pixels on most scanlines.
                        // Requiring a dense row discarded the entire series suffix and
                        // made its full-height, visible card ambiguous with (II)/(III).
                        // Keep sparse glyph strokes; the existing minimum line height
                        // rejects isolated highlights, and the color gate excludes gold
                        // completion percentages for both selected and ordinary cards.
                        return count >= Math.Max(2, geometry.X(3));
                    }, geometry.Y(3), geometry.Y(12));
                    if (names.Count == 0) continue;
                    // Include every title line inside this actual tile; percentages are gold
                    // and excluded by the ink predicate, even for a selected wrapped name.
                    var name = Rectangle.FromLTRB(0, names[0].Top, 1, names[names.Count - 1].Bottom);
                    var crop = geometry.Clip(Rectangle.FromLTRB(geometry.X(122), name.Top - geometry.Y(5),
                        geometry.X(isSelected ? 665 : 655), name.Bottom + geometry.Y(5)));
                    var mode = names.Count == 1 ? PageSegMode.SingleLine : PageSegMode.SingleBlock;
                    var read = Read(frame, crop, mode);
                    string text = Clean(read.Text);
                    var category = catalog.MatchCategory(text);
                    if (category == null)
                    {
                        // Retry the exact same title pixels. White unselected labels need
                        // inversion; selected dark labels already have a light background.
                        string retry = Clean(Read(frame, crop, mode, true, !isSelected).Text);
                        category = catalog.MatchCategory(retry);
                        if (category != null) text = retry;
                        else Logger.Debug("Unmatched achievement category at {0}: raw '{1}', threshold '{2}'",
                            tile.Top, text, retry);
                    }
                    if (names.Count > 1)
                    {
                        // A short, standalone series suffix is easily read as [1], or
                        // (III) as (II), in a full-width multiline caption. Verify its
                        // actual glyphs separately even when that caption already matched.
                        var suffixLine = names[names.Count - 1];
                        int suffixLeft = geometry.X(isSelected ? 665 : 653), suffixRight = -1;
                        for (int y = suffixLine.Top; y < suffixLine.Bottom; ++y)
                            for (int x = geometry.X(124); x < geometry.X(isSelected ? 665 : 653); ++x)
                            {
                                var c = pixels.Get(x, y);
                                bool ink = isSelected ? c.R < 130 && c.G < 135 && c.B < 140
                                    : c.R > 195 && c.G > 190 && c.B > 170 && c.R - c.G < 22;
                                if (!ink) continue;
                                suffixLeft = Math.Min(suffixLeft, x);
                                suffixRight = Math.Max(suffixRight, x);
                            }
                        if (suffixRight >= suffixLeft && suffixRight - suffixLeft <= geometry.X(100))
                        {
                            var prefixCrop = geometry.Clip(Rectangle.FromLTRB(geometry.X(122),
                                names[0].Top - geometry.Y(5), geometry.X(isSelected ? 665 : 655),
                                Math.Min(suffixLine.Top - geometry.Y(2), names[names.Count - 2].Bottom + geometry.Y(3))));
                            var prefixMode = names.Count == 2 ? PageSegMode.SingleLine : PageSegMode.SingleBlock;
                            string prefix = Clean(Read(frame, prefixCrop, prefixMode).Text);
                            // Exact prefix identity is required for repairing 1/l/| as I.
                            // Do not choose a series from its position or a fuzzy prefix.
                            var series = catalog.Categories.Select(candidate => new
                            {
                                Category = candidate,
                                Suffix = Regex.Match(candidate.Name ?? "", @"\s*\(([IVXLCDM]+)\)\s*$")
                            }).Where(candidate => candidate.Suffix.Success &&
                                AchievementCatalog.Normalize(candidate.Category.Name.Substring(0, candidate.Suffix.Index)) ==
                                AchievementCatalog.Normalize(prefix)).ToList();
                            if (series.Count == 0)
                            {
                                string retryPrefix = Clean(Read(frame, prefixCrop, prefixMode, true, !isSelected).Text);
                                series = catalog.Categories.Select(candidate => new
                                {
                                    Category = candidate,
                                    Suffix = Regex.Match(candidate.Name ?? "", @"\s*\(([IVXLCDM]+)\)\s*$")
                                }).Where(candidate => candidate.Suffix.Success &&
                                    AchievementCatalog.Normalize(candidate.Category.Name.Substring(0, candidate.Suffix.Index)) ==
                                    AchievementCatalog.Normalize(retryPrefix)).ToList();
                                if (series.Count > 0) prefix = retryPrefix;
                            }
                            if (series.Count > 0)
                            {
                                string fullText = text;
                                var suffixCrop = geometry.Clip(Rectangle.FromLTRB(suffixLeft - geometry.X(5),
                                    suffixLine.Top - geometry.Y(3), suffixRight + 1 + geometry.X(5),
                                    suffixLine.Bottom + geometry.Y(3)));
                                string suffixRaw = Clean(Read(frame, suffixCrop, PageSegMode.SingleLine).Text);
                                string suffixThreshold = Clean(Read(frame, suffixCrop, PageSegMode.SingleLine, true, !isSelected).Text);
                                var columns = Bands(suffixLeft, suffixRight + 1, x =>
                                {
                                    int count = 0;
                                    for (int y = suffixLine.Top; y < suffixLine.Bottom; ++y)
                                        if (IsSidebarNameInk(pixels.Get(x, y), isSelected)) ++count;
                                    return count >= Math.Max(1, geometry.Y(2));
                                }, 0, Math.Max(1, geometry.X(2)));
                                string middleRaw = "", middleThreshold = "", verifiedSuffix = "";
                                int? straightICount = null;
                                bool brackets = columns.Count >= 3 &&
                                    IsSidebarBracket(pixels, columns[0], suffixLine, isSelected, true, geometry) &&
                                    IsSidebarBracket(pixels, columns[columns.Count - 1], suffixLine, isSelected, false, geometry);
                                if (brackets)
                                {
                                    // Parentheses can themselves become extra I strokes in
                                    // OCR. Separate the curved outer glyphs from the numeral
                                    // using empty columns, then read only the actual middle.
                                    int middleLeft = Math.Max(columns[0].Bottom + 1,
                                        columns[1].Top - geometry.X(2));
                                    int middleRight = Math.Min(columns[columns.Count - 1].Top - 1,
                                        columns[columns.Count - 2].Bottom + geometry.X(2));
                                    var middleCrop = geometry.Clip(Rectangle.FromLTRB(middleLeft,
                                        suffixLine.Top - geometry.Y(3), middleRight,
                                        suffixLine.Bottom + geometry.Y(3)));
                                    middleRaw = Clean(Read(frame, middleCrop, PageSegMode.SingleLine).Text);
                                    middleThreshold = Clean(Read(frame, middleCrop, PageSegMode.SingleLine, true, !isSelected).Text);
                                    using (var numeral = frame.Clone(middleCrop, PixelFormat.Format24bppRgb))
                                        straightICount = ReadSidebarStraightICount(numeral, isSelected);
                                    // OCR can collapse two or three complete I stems into
                                    // one digit. Positive stem geometry, not unanimous OCR,
                                    // preserves the distinction between I, II and III.
                                    verifiedSuffix = ResolveSidebarRomanEvidence(true, brackets, straightICount,
                                        series.Select(candidate => candidate.Suffix.Groups[1].Value),
                                        middleRaw, middleThreshold, suffixThreshold, ExtractSidebarCaptionSuffix(fullText));
                                }
                                category = null;
                                if (verifiedSuffix.Length > 0)
                                {
                                    var matching = series.Where(candidate => candidate.Suffix.Groups[1].Value == verifiedSuffix).ToList();
                                    if (matching.Count == 1)
                                    {
                                        category = matching[0].Category;
                                        text = prefix + " (" + verifiedSuffix + ")";
                                    }
                                }
                                Logger.Debug("Achievement category suffix at {0}: caption '{1}', prefix '{2}', raw '{3}', threshold '{4}', glyph groups {5}, curved brackets {6}, numeral raw '{7}', numeral threshold '{8}', verified '{9}' (category {10}), qualified straight I count {11}",
                                    tile.Top, fullText, prefix, suffixRaw, suffixThreshold, columns.Count, brackets,
                                    middleRaw, middleThreshold, text,
                                    category == null ? "unresolved" : category.Id.ToString(CultureInfo.InvariantCulture),
                                    straightICount.HasValue ? straightICount.Value.ToString(CultureInfo.InvariantCulture) : "unqualified");
                            }
                            else if (category != null && Regex.IsMatch(category.Name ?? "", @"\s*\([IVXLCDM]+\)\s*$"))
                            {
                                // A nominal whole-caption series match cannot override an
                                // unverified independent prefix/suffix pair.
                                Logger.Debug("Unverified achievement category series at {0}: caption '{1}', prefix '{2}'", tile.Top, text, prefix);
                                category = null;
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(text)) continue;
                    result.Add(new AchievementCategoryEntryOnScreen
                    {
                        Category = category,
                        RawText = text,
                        Selected = isSelected,
                        Bounds = geometry.Clip(Rectangle.FromLTRB(geometry.X(isSelected ? 52 : 62),
                            tile.Top,
                            geometry.X(isSelected ? 668 : 657),
                            tile.Bottom))
                    });
                }
            }
            return result;
        }

        private static bool IsSidebarNameInk(Color color, bool selected)
        {
            return selected ? color.R < 130 && color.G < 135 && color.B < 140
                : color.R > 195 && color.G > 190 && color.B > 170 && color.R - color.G < 22;
        }

        private static string NormalizeSidebarRoman(string text)
        {
            string glyphs = Regex.Replace(text ?? "", @"\s+", "");
            // Only an independently isolated numeral with an exact canonical prefix
            // reaches this repair. Do not force arbitrary OCR with a whitelist.
            return Regex.IsMatch(glyphs, @"^[IVXivx1l|!]+$")
                ? glyphs.Replace('1', 'I').Replace('l', 'I').Replace('|', 'I').Replace('!', 'I').ToUpperInvariant() : "";
        }

        private static string ExtractSidebarCaptionSuffix(string caption)
        {
            var suffix = Regex.Match(caption ?? "", @"[\(\[\{]\s*([IVXivx1l|!]+)\s*[\)\]\}]\s*$");
            return suffix.Success ? suffix.Groups[1].Value : "";
        }

        internal static string ResolveSidebarRomanEvidence(bool exactPrefix, bool curvedBrackets,
            int? straightICount, IEnumerable<string> availableSuffixes, string rawNumeral,
            string thresholdNumeral, string thresholdSuffix, string captionSuffix)
        {
            if (!exactPrefix || !curvedBrackets || availableSuffixes == null) return "";
            var available = availableSuffixes.Where(value => value != null).Select(value => value.ToUpperInvariant()).ToList();
            Func<string, string> observed = value => NormalizeSidebarRoman((value ?? "").Trim().Trim('(', ')', '[', ']', '{', '}'));
            if (straightICount.HasValue)
            {
                if (straightICount.Value < 1 || straightICount.Value > 3) return "";
                string physical = new string('I', straightICount.Value);
                // A threshold or full-caption read must corroborate the independent
                // geometry. Raw text alone cannot repair or choose a numeral.
                return available.Count(value => value == physical) == 1 &&
                    new[] { thresholdNumeral, thresholdSuffix, captionSuffix }.Any(value => observed(value) == physical)
                    ? physical : "";
            }
            string raw = observed(rawNumeral), threshold = observed(thresholdNumeral);
            // V/X and mixed Roman numerals retain strict agreement. A failed straight
            // I qualification cannot be bypassed by two identical I-only OCR reads.
            return raw.Length > 0 && raw == threshold && !Regex.IsMatch(raw, @"^I+$") &&
                available.Count(value => value == raw) == 1 ? raw : "";
        }

        internal static int? ReadSidebarStraightICount(Bitmap numeralCrop, bool selected = false)
        {
            if (numeralCrop == null || numeralCrop.Width == 0 || numeralCrop.Height == 0) return null;
            using (var pixels = new Pixels(numeralCrop))
            {
                int minimumColumnInk = Math.Max(2, (int)Math.Round(numeralCrop.Height * .06));
                var columns = Bands(0, numeralCrop.Width, x =>
                {
                    int count = 0;
                    for (int y = 0; y < numeralCrop.Height; ++y)
                        if (IsSidebarNameInk(pixels.Get(x, y), selected)) ++count;
                    return count >= minimumColumnInk;
                }, 0, 1);
                if (columns.Count < 1 || columns.Count > 3) return null;
                int firstTop = -1, firstBottom = -1;
                foreach (var column in columns)
                {
                    int top = numeralCrop.Height, bottom = -1, longestSpine = 0;
                    for (int x = column.Top; x < column.Bottom; ++x)
                    {
                        int run = 0;
                        for (int y = 0; y < numeralCrop.Height; ++y)
                        {
                            if (IsSidebarNameInk(pixels.Get(x, y), selected))
                            {
                                top = Math.Min(top, y);
                                bottom = Math.Max(bottom, y);
                                longestSpine = Math.Max(longestSpine, ++run);
                            }
                            else run = 0;
                        }
                    }
                    int height = bottom - top + 1;
                    // Count a glyph only if it has a narrow, nearly full-height fixed
                    // vertical spine. Counting disconnected components alone would
                    // confuse diagonal V/X strokes or punctuation with I stems.
                    if (height < Math.Max(8, (int)Math.Round(numeralCrop.Height * .5)) ||
                        column.Height > height * .45 || longestSpine < height * .85) return null;
                    int alignmentTolerance = Math.Max(2, (int)Math.Round(height * .12));
                    if (firstTop >= 0 && (Math.Abs(top - firstTop) > alignmentTolerance ||
                        Math.Abs(bottom - firstBottom) > alignmentTolerance)) return null;
                    firstTop = top;
                    firstBottom = bottom;
                }
                return columns.Count;
            }
        }

        private static bool IsSidebarBracket(Pixels pixels, Rectangle columns, Rectangle line,
            bool selected, bool opening, Geometry geometry)
        {
            var edges = new List<int>();
            for (int y = line.Top; y < line.Bottom; ++y)
            {
                int edge = opening ? int.MaxValue : int.MinValue;
                for (int x = columns.Top; x < columns.Bottom; ++x)
                {
                    if (!IsSidebarNameInk(pixels.Get(x, y), selected)) continue;
                    edge = opening ? Math.Min(edge, x) : Math.Max(edge, x);
                }
                if (edge != int.MaxValue && edge != int.MinValue) edges.Add(edge);
            }
            if (edges.Count < Math.Max(8, geometry.Y(10))) return false;
            int third = edges.Count / 3;
            double ends = edges.Take(third).Concat(edges.Skip(edges.Count - third)).Average();
            double middle = edges.Skip(third).Take(edges.Count - third * 2).Average();
            // Both parentheses curve away from their end positions at mid-height.
            // An I stem/serif has the opposite shape and cannot be stripped here.
            return (opening ? ends - middle : middle - ends) >= Math.Max(1, geometry.X(1));
        }

        public int? ReadCompletedTotal(Bitmap frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            var geometry = new Geometry(frame);
            var read = Read(frame, geometry.Rect(1390, 25, 1785, 68), PageSegMode.SingleLine);
            var match = Regex.Match(read.Text ?? "", @"completed\s*:\s*(\d[\d,]*)", RegexOptions.IgnoreCase);
            int total;
            return match.Success && int.TryParse(match.Groups[1].Value.Replace(",", ""), out total)
                ? (int?)total : null;
        }

        public bool IsDetailScreen(Bitmap frame)
        {
            if (frame == null) return false;
            var geometry = new Geometry(frame);
            using (var pixels = new Pixels(frame))
            {
                // A cream list panel plus card rules distinguishes the detail screen from the category grid.
                return IsCream(pixels.Get(geometry.X(730), geometry.Y(140)))
                    && FindRows(pixels, geometry).Count > 0;
            }
        }

        /// <summary>Coarse list-only fingerprint; callers must also verify scrollbar/end evidence.</summary>
        public string PageSignature(Bitmap frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            var geometry = new Geometry(frame);
            ulong hash = 14695981039346656037UL;
            using (var pixels = new Pixels(frame))
            {
                var area = geometry.Rect(855, 150, 1558, 978);
                const int columns = 48, rows = 80;
                for (int gy = 0; gy < rows; ++gy)
                {
                    for (int gx = 0; gx < columns; ++gx)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < 3; ++dy)
                            for (int dx = 0; dx < 3; ++dx)
                            {
                                int x = area.Left + (gx * 3 + dx) * area.Width / (columns * 3);
                                int y = area.Top + (gy * 3 + dy) * area.Height / (rows * 3);
                                var c = pixels.Get(x, y);
                                sum += (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                            }
                        hash = (hash ^ (uint)(sum / 9 / 24)) * 1099511628211UL;
                    }
                }
            }
            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        internal static List<Rectangle> DetectRows(Bitmap frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            using (var pixels = new Pixels(frame)) return FindRows(pixels, new Geometry(frame));
        }

        /// <summary>Top of the scrollable track, excluding a pinned category progress card.</summary>
        internal static int GetAchievementListTop(Bitmap frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            var geometry = new Geometry(frame);
            using (var pixels = new Pixels(frame))
            {
                int headerBottom = FindProgressHeaderBottom(pixels, geometry, FindCardRules(pixels, geometry));
                return headerBottom > 0 ? headerBottom + geometry.Y(22) : geometry.Y(151);
            }
        }

        private static List<Rectangle> FindCardRules(Pixels pixels, Geometry geometry)
        {
            // Scrolling can put a complete first card above the initial y146 position.
            return Bands(geometry.Y(125), geometry.Y(987), y =>
            {
                int count = 0, samples = 0;
                for (int x = geometry.X(850); x < geometry.X(1500); x += Math.Max(1, geometry.X(3)))
                {
                    var c = pixels.Get(x, y);
                    if (c.R >= 218 && c.R <= 229 && c.G >= 206 && c.G <= 219 && c.B >= 188 && c.B <= 208) ++count;
                    ++samples;
                }
                return samples > 0 && count >= samples * .88;
            }, 0, 1);
        }

        private static int FindProgressHeaderBottom(Pixels pixels, Geometry geometry, List<Rectangle> rules)
        {
            for (int i = 0; i + 1 < rules.Count && rules[i].Top < geometry.Y(200); ++i)
            {
                int height = rules[i + 1].Bottom - rules[i].Top;
                if (height < geometry.Y(75) || height > geometry.Y(155)) continue;
                // The progress card has a long orange/gray bar. Ordinary title/description
                // glyphs, cream backgrounds and card borders cannot fill this horizontal span.
                var bars = Bands(rules[i].Top + geometry.Y(20), rules[i + 1].Bottom - geometry.Y(15), y =>
                {
                    int matches = 0, samples = 0;
                    for (int x = geometry.X(870); x < geometry.X(1555); x += Math.Max(1, geometry.X(3)))
                    {
                        var c = pixels.Get(x, y);
                        int difference = Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
                        bool orange = c.R >= 200 && c.G >= 140 && c.G <= 215 && c.B <= 140;
                        bool grey = c.R >= 60 && c.R <= 180 && difference <= 22;
                        if (orange || grey) ++matches;
                        ++samples;
                    }
                    return samples > 0 && matches >= samples * .85;
                }, 0, Math.Max(1, geometry.Y(2)));
                if (bars.Count > 0) return rules[i + 1].Bottom;
            }
            return 0;
        }

        private static List<Rectangle> FindRows(Pixels pixels, Geometry geometry)
        {
            var rules = FindCardRules(pixels, geometry);
            int headerBottom = FindProgressHeaderBottom(pixels, geometry, rules);
            var rows = new List<Rectangle>();
            for (int i = 0; i + 1 < rules.Count; ++i)
            {
                int height = rules[i + 1].Bottom - rules[i].Top;
                // Card bottoms and the next card's top are only a few pixels apart.
                // A clipped first/last card lacks a matching pair and is deferred to an overlapping page.
                if (height < geometry.Y(75) || height > geometry.Y(155)) continue;
                // The pinned header's lower rule is not a top border of a partly occluded card.
                if (headerBottom > 0 && rules[i].Top < headerBottom) continue;
                var bounds = geometry.Clip(Rectangle.FromLTRB(geometry.X(739), rules[i].Top,
                    geometry.X(1819), rules[i + 1].Bottom));
                if (FindTitleBands(pixels, geometry, bounds).Count == 0) continue;
                rows.Add(bounds);
            }
            if (rows.Count > 1)
            {
                // Measure the current card layout instead of assuming its height. A fragment
                // paired with a viewport/frame border is shorter than the full card majority.
                int cardHeight = rows.Select(r => r.Height).OrderBy(h => h).ElementAt(rows.Count / 2);
                rows.RemoveAll(r => r.Height < cardHeight - Math.Max(1, geometry.Y(2)));
            }
            return rows;
        }

        private static List<Rectangle> FindTitleBands(Pixels pixels, Geometry geometry, Rectangle bounds)
        {
            return Bands(bounds.Top + geometry.Y(7), bounds.Bottom - geometry.Y(7), y =>
            {
                int count = 0;
                for (int x = geometry.X(859); x < geometry.X(1568); ++x)
                {
                    var c = pixels.Get(x, y);
                    if (c.R < 135 && c.G < 135 && c.B < 135) ++count;
                }
                return count >= Math.Max(8, geometry.X(12));
            }, geometry.Y(2), geometry.Y(10));
        }

        private void ReadCard(Bitmap frame, Pixels pixels, Geometry geometry, Rectangle bounds,
            AchievementCategory category, AchievementObservation observation)
        {
            int left = geometry.X(859), right = geometry.X(1568);
            var titleBands = FindTitleBands(pixels, geometry, bounds);
            if (titleBands.Count == 0) throw new InvalidDataException("No complete title line detected.");
            var titleBand = MergeBands(titleBands, geometry.Y(18))[0];
            // Short titles have few pixels in their capital/ascender strokes. Include space above
            // the dense band so titles such as "Zoo Tycoon" are not clipped by line detection.
            var title = Read(frame, geometry.Clip(Rectangle.FromLTRB(left, titleBand.Top - geometry.Y(12),
                right, titleBand.Bottom + geometry.Y(8))), PageSegMode.SingleBlock);
            observation.Title = Clean(title.Text);

            var descriptionBands = Bands(titleBand.Bottom + geometry.Y(4), bounds.Bottom - geometry.Y(8), y =>
            {
                int count = 0;
                for (int x = left; x < right; ++x)
                {
                    var c = pixels.Get(x, y);
                    if (IsTanText(c)) ++count;
                }
                return count >= Math.Max(8, geometry.X(12));
            }, geometry.Y(3), geometry.Y(8));
            OcrRead description = new OcrRead("", 0);
            if (descriptionBands.Count > 0)
            {
                description = Read(frame, geometry.Clip(Rectangle.FromLTRB(left,
                    descriptionBands[0].Top - geometry.Y(4), right,
                    descriptionBands[descriptionBands.Count - 1].Bottom + geometry.Y(4))), PageSegMode.SingleBlock);
            }
            observation.Description = Clean(description.Text);
            var reward = Read(frame, geometry.Clip(Rectangle.FromLTRB(geometry.X(1605),
                bounds.Bottom - geometry.Y(44), geometry.X(1636), bounds.Bottom - geometry.Y(25))),
                PageSegMode.SingleLine, true, true, true);
            int number;
            if (int.TryParse(Clean(reward.Text), out number) && (number == 5 || number == 10 || number == 20))
                observation.Reward = number;

            ReadState(frame, pixels, geometry, bounds, observation);
            observation.CompletedStars = CountStars(pixels, geometry, bounds);
            var match = catalog.Resolve(category, observation.Title, observation.Description, observation.Reward);
            observation.Confidence = match.Confidence;
            if (!match.Success)
            {
                observation.Error = match.Error ?? "Achievement could not be uniquely matched.";
                return;
            }
            observation.FamilyId = match.Family.Id;
            observation.MatchedId = match.Tier.Id;
            observation.Confidence = Math.Min(match.Confidence, Math.Min(title.Confidence, description.Confidence));
            Reconcile(observation, match.Family, match.Tier);
        }

        private static void ReadState(Bitmap frame, Pixels pixels, Geometry geometry, Rectangle bounds,
            AchievementObservation observation)
        {
            var dateBounds = geometry.Clip(Rectangle.FromLTRB(geometry.X(1680), bounds.Bottom - geometry.Y(35),
                geometry.X(1806), bounds.Bottom - geometry.Y(3)));
            // Thresholding destroys the thin hyphens in some dates; preserve the original pixels.
            bool dateHasInk = false;
            for (int y = dateBounds.Top; y < dateBounds.Bottom && !dateHasInk; ++y)
            {
                int count = 0;
                for (int x = dateBounds.Left; x < dateBounds.Right; ++x)
                    if (IsTanText(pixels.Get(x, y))) ++count;
                dateHasInk = count >= Math.Max(5, geometry.X(7));
            }
            var dateRead = dateHasInk ? Read(frame, dateBounds, PageSegMode.SingleLine) : new OcrRead("", 0);
            var date = Regex.Match(dateRead.Text ?? "", @"\b(20\d{2})[-./](\d{2})[-./](\d{2})\b");
            DateTime parsedDate;
            if (date.Success && DateTime.TryParseExact(date.Value.Replace('/', '-').Replace('.', '-'),
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate))
                observation.CompletionDate = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var bands = Bands(bounds.Top + geometry.Y(20), bounds.Bottom - geometry.Y(34), y =>
            {
                int count = 0;
                for (int x = geometry.X(1680); x < geometry.X(1807); ++x)
                    if (IsTanText(pixels.Get(x, y))) ++count;
                return count >= Math.Max(5, geometry.X(7));
            }, geometry.Y(2), geometry.Y(8));
            var readings = new List<string>();
            foreach (var band in bands)
            {
                var region = geometry.Clip(Rectangle.FromLTRB(geometry.X(1680), band.Top - geometry.Y(4),
                    geometry.X(1807), band.Bottom + geometry.Y(4)));
                readings.Add(Clean(Read(frame, region, PageSegMode.SingleLine).Text));
                readings.Add(Clean(Read(frame, region, PageSegMode.SingleLine, true).Text));
            }
            string state = string.Join(" ", readings);
            observation.CompletedText = HasCompletedStampText(state, observation.CompletionDate);
            observation.Claimable = Regex.IsMatch(state, @"\bclaim\b", RegexOptions.IgnoreCase);
            var progress = Regex.Match(state, @"\b(\d+)\s*/\s*(\d+)\b");
            if (progress.Success) observation.Progress = progress.Groups[1].Value + "/" + progress.Groups[2].Value;
            var total = Regex.Match(state, @"\btotal\s*:\s*(\d[\d,]*)", RegexOptions.IgnoreCase);
            int value;
            if (total.Success && int.TryParse(total.Groups[1].Value.Replace(",", ""), out value)) observation.Total = value;
            if (!observation.CompletedText && !observation.Claimable && !progress.Success)
            {
                // Sparse stamp strokes can split the Completed word into narrow
                // bands. Re-read the entire state column before rejecting it,
                // retaining both completion and unfinished-progress evidence.
                var stateBounds = geometry.Clip(Rectangle.FromLTRB(geometry.X(1668),
                    bounds.Top + geometry.Y(20), geometry.X(1810), bounds.Bottom - geometry.Y(12)));
                readings.Add(Clean(Read(frame, stateBounds, PageSegMode.SingleBlock).Text));
                state = string.Join(" ", readings);
                bool recognized = HasCompletedStampText(state, observation.CompletionDate)
                    || Regex.IsMatch(state, @"\bclaim\b|\b\d+\s*/\s*\d+\b", RegexOptions.IgnoreCase);
                if (!recognized)
                {
                    // Preserve the full label's ascenders/descenders and leave
                    // the separate date line outside this thresholded retry.
                    var labelBounds = geometry.Clip(Rectangle.FromLTRB(geometry.X(1668),
                        bounds.Top + geometry.Y(20), geometry.X(1810), bounds.Bottom - geometry.Y(34)));
                    readings.Add(Clean(Read(frame, labelBounds, PageSegMode.SingleBlock, true).Text));
                    state = string.Join(" ", readings);
                }
                observation.CompletedText = HasCompletedStampText(state, observation.CompletionDate);
                observation.Claimable = Regex.IsMatch(state, @"\bclaim\b", RegexOptions.IgnoreCase);
                progress = Regex.Match(state, @"\b(\d+)\s*/\s*(\d+)\b");
                if (progress.Success) observation.Progress = progress.Groups[1].Value + "/" + progress.Groups[2].Value;
                total = Regex.Match(state, @"\btotal\s*:\s*(\d[\d,]*)", RegexOptions.IgnoreCase);
                if (total.Success && int.TryParse(total.Groups[1].Value.Replace(",", ""), out value)) observation.Total = value;
                Logger.Debug("Achievement state column reread: category={0}, page={1}, top={2}, title='{3}', completed={4}, claimable={5}, progress='{6}', date='{7}', raw='{8}'",
                    observation.CategoryId, observation.Page, bounds.Top, observation.Title, observation.CompletedText,
                    observation.Claimable, observation.Progress, observation.CompletionDate, state);
            }
            if (observation.CompletedText && !Regex.IsMatch(state, @"\bcompleted\b", RegexOptions.IgnoreCase))
                Logger.Debug("Achievement completion stamp repaired lowercase l/i OCR: category={0}, page={1}, top={2}, title='{3}', validDate='{4}', raw='{5}'; tier/star and progress validation still required",
                    observation.CategoryId, observation.Page, bounds.Top, observation.Title, observation.CompletionDate, state);
            if (!observation.CompletedText && !observation.Claimable && string.IsNullOrEmpty(observation.Progress))
            {
                Logger.Warn("Achievement completion evidence unreadable: category={0}, page={1}, top={2}, title='{3}', date='{4}', stateBands={5}, raw='{6}'",
                    observation.CategoryId, observation.Page, bounds.Top, observation.Title, observation.CompletionDate, bands.Count, state);
            }
        }

        private static bool HasCompletedStampText(string text, string validDate)
        {
            if (Regex.IsMatch(text ?? "", @"\bcompleted\b", RegexOptions.IgnoreCase)) return true;
            // The narrow lowercase l in this stamp is sometimes read as i/1.
            // Repair that one glyph only alongside a separately validated date;
            // Reconcile still requires matching filled stars and rejects unfinished progress.
            return !string.IsNullOrEmpty(validDate)
                && Regex.IsMatch(text ?? "", @"\bcomp[i1]eted\b", RegexOptions.IgnoreCase);
        }

        internal static void Reconcile(AchievementObservation observation, AchievementFamily family,
            AchievementDefinition tier)
        {
            int stars = observation.CompletedStars;
            int preceding = tier.TierIndex;
            bool currentComplete = observation.CompletedText || observation.Claimable;
            int expected = preceding + (currentComplete ? 1 : 0);
            if (stars < 0 || stars > family.Tiers.Count || preceding < 0 || preceding >= family.Tiers.Count)
            {
                observation.Error = "Tier count is outside the matched achievement family.";
                return;
            }
            bool hasProgress = !string.IsNullOrEmpty(observation.Progress);
            var progress = Regex.Match(observation.Progress ?? "", @"^(\d+)/(\d+)$");
            long numerator, denominator;
            if (currentComplete && progress.Success
                && long.TryParse(progress.Groups[1].Value, out numerator)
                && long.TryParse(progress.Groups[2].Value, out denominator) && numerator < denominator)
            {
                observation.Error = "Completion status disagrees with unfinished progress.";
                return;
            }
            if (!currentComplete && !hasProgress && stars == preceding + 1 && !string.IsNullOrEmpty(observation.CompletionDate))
            {
                observation.Error = "Completion date was visible but the completion status was unreadable.";
                return;
            }
            // Claimable rows may light their final star only after collecting the reward.
            bool consistent = stars == expected || (observation.Claimable && stars == preceding);
            if (!consistent)
            {
                observation.Error = "Filled stars disagree with the displayed tier and completion status.";
                return;
            }
            if (!currentComplete && !hasProgress)
            {
                observation.Error = "Neither completion status nor unfinished progress could be read.";
                return;
            }
            foreach (var completed in family.Tiers.OrderBy(t => t.TierIndex).Take(expected))
                observation.CompletedIds.Add(completed.Id);
            // Earlier tiers can be inferred, but their dates cannot be inferred from this card.
            if (!currentComplete) observation.CompletionDate = null;
        }

        private static int CountStars(Pixels pixels, Geometry geometry, Rectangle row)
        {
            var region = geometry.Clip(Rectangle.FromLTRB(geometry.X(779), row.Top + geometry.Y(38),
                geometry.X(824), row.Top + geometry.Y(82)));
            var mask = new bool[region.Width * region.Height];
            for (int y = 0; y < region.Height; ++y)
                for (int x = 0; x < region.Width; ++x)
                {
                    var c = pixels.Get(region.Left + x, region.Top + y);
                    mask[y * region.Width + x] = c.B >= 202 && c.B <= 222 && c.G >= 232 && c.G <= 252 && c.R >= 244;
                }
            int stars = 0;
            var queue = new Queue<int>();
            for (int index = 0; index < mask.Length; ++index)
            {
                if (!mask[index]) continue;
                mask[index] = false;
                queue.Enqueue(index);
                int minX = region.Width, maxX = 0, minY = region.Height, maxY = 0;
                while (queue.Count > 0)
                {
                    int pixel = queue.Dequeue(), x = pixel % region.Width, y = pixel / region.Width;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    for (int dy = -1; dy <= 1; ++dy)
                        for (int dx = -1; dx <= 1; ++dx)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= region.Width || ny >= region.Height) continue;
                            int neighbour = ny * region.Width + nx;
                            if (!mask[neighbour]) continue;
                            mask[neighbour] = false;
                            queue.Enqueue(neighbour);
                        }
                }
                int width = maxX - minX + 1, height = maxY - minY + 1;
                if (width >= geometry.X(13) && width <= geometry.X(22)
                    && height >= geometry.Y(13) && height <= geometry.Y(22)) ++stars;
            }
            return stars;
        }

        private static OcrRead Read(Bitmap frame, Rectangle bounds, PageSegMode mode,
            bool threshold = false, bool invert = false, bool numbersOnly = false)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return new OcrRead("", 0);
            using (var crop = frame.Clone(bounds, PixelFormat.Format24bppRgb))
            using (var image = threshold ? PrepareThreshold(crop, invert) : (Bitmap)crop.Clone())
            {
                TesseractEngine engine = null;
                var started = Environment.TickCount;
                while (!GenshinProcesor.engines.TryTake(out engine))
                {
                    if (unchecked(Environment.TickCount - started) > 10000)
                        throw new InvalidOperationException("Achievement OCR engine was unavailable.");
                    Thread.Sleep(10);
                }
                string whitelist = null;
                bool restoreWhitelist = false;
                try
                {
                    if (numbersOnly)
                    {
                        engine.TryGetStringVariable("tessedit_char_whitelist", out whitelist);
                        restoreWhitelist = true;
                        engine.SetVariable("tessedit_char_whitelist", "0123456789");
                    }
                    using (var page = engine.Process(image, mode))
                        return new OcrRead(page.GetText(), page.GetMeanConfidence() * 100.0);
                }
                finally
                {
                    try
                    {
                        if (restoreWhitelist) engine.SetVariable("tessedit_char_whitelist", whitelist ?? "");
                    }
                    finally
                    {
                        // A failed OCR call must return the engine and restore its previous whitelist.
                        GenshinProcesor.engines.Add(engine);
                    }
                }
            }
        }

        private static Bitmap PrepareThreshold(Bitmap source, bool invert)
        {
            var result = new Bitmap(source.Width * 2 + 20, source.Height * 2 + 20, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(result))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(10, 10, source.Width * 2, source.Height * 2));
            }
            var data = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                var bytes = new byte[data.Stride * result.Height];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                for (int y = 10; y < result.Height - 10; ++y)
                    for (int x = 10; x < result.Width - 10; ++x)
                    {
                        int i = y * data.Stride + x * 3;
                        int grey = (bytes[i + 2] * 299 + bytes[i + 1] * 587 + bytes[i] * 114) / 1000;
                        byte value = (byte)((grey >= 180) != invert ? 255 : 0);
                        bytes[i] = value; bytes[i + 1] = value; bytes[i + 2] = value;
                    }
                Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
            }
            finally { result.UnlockBits(data); }
            return result;
        }

        private static List<Rectangle> Bands(int top, int bottom, Func<int, bool> isInk, int mergeGap, int minHeight)
        {
            var bands = new List<Rectangle>();
            int start = -1, last = -1;
            for (int y = top; y < bottom; ++y)
            {
                if (isInk(y))
                {
                    if (start < 0) start = y;
                    last = y;
                }
                else if (start >= 0 && y - last > mergeGap)
                {
                    if (last - start + 1 >= minHeight) bands.Add(new Rectangle(0, start, 1, last - start + 1));
                    start = -1;
                }
            }
            if (start >= 0 && last - start + 1 >= minHeight) bands.Add(new Rectangle(0, start, 1, last - start + 1));
            return bands;
        }

        private static List<Rectangle> MergeBands(List<Rectangle> bands, int maxGap)
        {
            var result = new List<Rectangle>();
            foreach (var band in bands)
            {
                if (result.Count == 0 || band.Top - result[result.Count - 1].Bottom > maxGap) result.Add(band);
                else
                {
                    var previous = result[result.Count - 1];
                    result[result.Count - 1] = Rectangle.FromLTRB(0, previous.Top, 1, band.Bottom);
                }
            }
            return result;
        }

        private static bool IsTanText(Color c)
        {
            return c.R >= 140 && c.R <= 205 && c.G >= 120 && c.G <= 185 && c.B >= 90 && c.B <= 165
                && c.R - c.G >= 8 && c.R - c.G <= 45 && c.G - c.B >= 7;
        }

        private static bool IsCream(Color c) { return c.R > 225 && c.G > 218 && c.B > 200; }
        private static string Clean(string value) { return Regex.Replace(value ?? "", @"\s+", " ").Trim(); }

        private static void SaveEvidence(Bitmap frame, Rectangle bounds, AchievementObservation observation)
        {
            try
            {
                string directory = Path.Combine("logging", "achievements");
                Directory.CreateDirectory(directory);
                string file = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture)
                    + "_category" + observation.CategoryId + "_page" + observation.Page + "_y" + bounds.Top + "_"
                    + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png";
                using (var crop = frame.Clone(bounds, PixelFormat.Format24bppRgb)) crop.Save(Path.Combine(directory, file), System.Drawing.Imaging.ImageFormat.Png);
                Logger.Warn("Unresolved achievement '{0}': {1}. Evidence: {2}", observation.Title, observation.Error, Path.Combine(directory, file));
            }
            catch (Exception ex) { Logger.Warn(ex, "Could not save unresolved achievement evidence"); }
        }

        private sealed class OcrRead
        {
            internal readonly string Text;
            internal readonly double Confidence;
            internal OcrRead(string text, double confidence) { Text = text; Confidence = confidence; }
        }

        private sealed class Geometry
        {
            internal readonly int Width, Height;
            internal Geometry(Bitmap frame) { Width = frame.Width; Height = frame.Height; }
            internal int X(double value) { return (int)Math.Round(value * Width / 1920.0); }
            internal int Y(double value) { return (int)Math.Round(value * Height / 1080.0); }
            internal Rectangle Rect(double left, double top, double right, double bottom)
            { return Clip(Rectangle.FromLTRB(X(left), Y(top), X(right), Y(bottom))); }
            internal Rectangle Clip(Rectangle rectangle)
            { return Rectangle.Intersect(rectangle, new Rectangle(0, 0, Width, Height)); }
        }

        private sealed class Pixels : IDisposable
        {
            private readonly Bitmap copy;
            private readonly byte[] data;
            private readonly int stride;
            internal Pixels(Bitmap bitmap)
            {
                copy = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format24bppRgb);
                var locked = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    stride = Math.Abs(locked.Stride);
                    data = new byte[stride * copy.Height];
                    for (int y = 0; y < copy.Height; ++y)
                        Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), data, y * stride, stride);
                }
                finally { copy.UnlockBits(locked); }
            }
            internal Color Get(int x, int y)
            {
                x = Math.Max(0, Math.Min(copy.Width - 1, x));
                y = Math.Max(0, Math.Min(copy.Height - 1, y));
                int index = y * stride + x * 3;
                return Color.FromArgb(data[index + 2], data[index + 1], data[index]);
            }
            public void Dispose() { copy.Dispose(); }
        }
    }
}
