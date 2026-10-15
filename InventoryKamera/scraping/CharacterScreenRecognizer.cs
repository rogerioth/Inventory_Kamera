using Accord.Imaging.Filters;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Tesseract;

namespace InventoryKamera
{
    internal enum CharacterScreenState
    {
        Unknown,
        Gameplay,
        PaimonMenu,
        CharacterMenu,
        Attributes,
        Constellation,
        ConstellationDetails,
        Talents,
        TalentDetails
    }

    internal sealed class CharacterScreenRecognition
    {
        internal CharacterScreenState State { get; set; }
        internal string Evidence { get; set; }
        internal Rectangle? CharacterTileBounds { get; set; }
        internal int FrameWidth { get; set; }
        internal int FrameHeight { get; set; }
        internal bool IsCharacterMenu => State == CharacterScreenState.CharacterMenu ||
            State == CharacterScreenState.Attributes || State == CharacterScreenState.Constellation ||
            State == CharacterScreenState.ConstellationDetails || State == CharacterScreenState.Talents ||
            State == CharacterScreenState.TalentDetails;
    }

    // Screen identity comes from menu controls, never a catalog character name.
    // An inconclusive capture remains Unknown and cannot authorize normal
    // character clicks or detail cleanup. Entry recovery is separately bounded.
    internal static class CharacterScreenRecognizer
    {
        internal static CharacterScreenRecognition Read(Bitmap frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            var result = new CharacterScreenRecognition
            {
                State = CharacterScreenState.Unknown,
                FrameWidth = frame.Width,
                FrameHeight = frame.Height
            };
            if (frame.Width < 640 || frame.Height < 360)
            {
                result.Evidence = "A full client screenshot is required; a header crop cannot establish screen identity.";
                return result;
            }
            var geometry = new Geometry(frame);
            var evidence = new List<string>();
            try
            {
                // The live HUD's health bar, numbered party keycaps and chat/
                // action control are spatially independent. Paimon and the
                // character menu hide those controls. Confirm this combination
                // before asking OCR to interpret the much larger world scene;
                // HP digit accuracy is not required to establish screen state.
                Rectangle? health = FindHealthBar(frame);
                int partyKeys = -1;
                string controls = null;
                bool actionKey = false;
                if (health.HasValue)
                {
                    partyKeys = CountPartySlotKeys(frame, geometry);
                    if (partyKeys >= 2)
                    {
                        controls = ReadHudControls(frame, geometry, out actionKey);
                        evidence.Add($"health-bar={health.Value}, party-slot-keys={partyKeys}, hud-controls='{controls}'");
                        if (actionKey)
                        {
                            evidence.Add("gameplay established by three HUD controls; HP OCR not required");
                            return Finish(result, CharacterScreenState.Gameplay, evidence);
                        }
                    }
                }

                // Detail overlays replace the normal sidebar. The saved
                // Xiangling screenshots show a sharp panel edge at x520/1920.
                // Talent overlays can use the slightly wider x560/1920 panel.
                int? panelEdge = FindDetailPanelEdge(frame, geometry);
                if (panelEdge.HasValue)
                {
                    string detail = ReadText(frame, geometry.TopRect(20, 130, 480, 260));
                    evidence.Add($"detail-panel x={panelEdge.Value}, title='{detail}'");
                    if (Regex.IsMatch(detail, @"\bconstellation\s*(?:lv|level)\b", RegexOptions.IgnoreCase))
                        return Finish(result, CharacterScreenState.ConstellationDetails, evidence);

                    // A talent level on the left is combined with the separate
                    // talent-list levels on the right; a generic 'Lv.' alone is
                    // not sufficient to authorize closing an overlay.
                    if (CountLevelLabels(detail) > 0)
                    {
                        string right = ReadText(frame, geometry.TopRect(1430, 115, 440, 790));
                        evidence.Add("right-list='" + right + "'");
                        if (CountLevelLabels(right) >= 2)
                            return Finish(result, CharacterScreenState.TalentDetails, evidence);
                        int levels = Math.Max(CountLevelLabels(right), ReadLightTalentLevelRows(frame, geometry, evidence));
                        if (levels >= 2 || levels >= 1 && ReadTalentDetailTabs(frame, geometry, evidence))
                        {
                            evidence.Add("talent detail established by panel edge, left Lv and " + levels +
                                " independent right Lv rows; one-row fallback additionally requires both detail tabs");
                            return Finish(result, CharacterScreenState.TalentDetails, evidence);
                        }
                    }
                    // A drawer can retain readable controls from the base pane
                    // during animation. Its edge blocks base-pane recognition
                    // until independent detail controls identify the overlay.
                    evidence.Add("strict overlay evidence incomplete; base-pane promotion forbidden");
                    return Finish(result, CharacterScreenState.Unknown, evidence);
                }

                Rectangle sidebarRegion = geometry.TopRect(88, 128, 410, 435);
                string sidebar = ReadText(frame, sidebarRegion);
                evidence.Add("sidebar='" + sidebar + "'");
                if (!HasCharacterSidebar(sidebar))
                {
                    sidebar = ReadRawText(frame, sidebarRegion);
                    evidence.Add("sidebar-raw='" + sidebar + "'");
                }
                if (!HasCharacterSidebar(sidebar))
                    sidebar = ReadLightSidebarRows(frame, geometry, evidence);
                evidence.Add($"sidebar exact controls={CountExactSidebarLabels(sidebar)}, required>=4 including Constellation/Talents; Attributes word={HasExactLabel(sidebar, "attributes")}");
                if (HasCharacterSidebar(sidebar))
                {
                    Rectangle rightRegion = geometry.TopRect(1430, 115, 440, 790);
                    string right = ReadText(frame, rightRegion);
                    evidence.Add("right-pane='" + right + "'");
                    if (!HasAttributePane(right) && CountLevelLabels(right) < 3)
                    {
                        right = ReadRawText(frame, rightRegion);
                        evidence.Add("right-pane-raw='" + right + "'");
                    }
                    if (HasAttributePane(right))
                        return Finish(result, CharacterScreenState.Attributes, evidence);
                    if (CountLevelLabels(right) >= 3)
                        return Finish(result, CharacterScreenState.Talents, evidence);
                    if (HasUniqueSelectedTalentsMarker(frame, geometry))
                    {
                        int levels = Math.Max(CountLevelLabels(right), ReadLightTalentLevelRows(frame, geometry, evidence));
                        if (levels >= 3 || levels >= 1 && ReadTalentDemoControl(frame, geometry, evidence))
                        {
                            evidence.Add("Talents established by verified sidebar, unique selected Talents marker and " + levels +
                                " right Lv rows; fewer-than-three-row alternative additionally requires Talent Demo");
                            return Finish(result, CharacterScreenState.Talents, evidence);
                        }
                    }
                    bool constellationSelected = HasSelectedConstellationMarker(frame, geometry);
                    int circles = CountConstellationCircles(frame, geometry, constellationSelected);
                    evidence.Add("constellation selected-marker=" + constellationSelected + ", medallions=" + circles);
                    if (circles >= 4)
                        return Finish(result, CharacterScreenState.Constellation, evidence);
                    // This positively establishes the menu, but not its selected
                    // subtab. Navigation may choose a tab; it must not Escape.
                    return Finish(result, CharacterScreenState.CharacterMenu, evidence);
                }

                // Menu labels sit on solid tiles, whereas profile text can
                // overlap a bright, detailed namecard. Read the same tile grid
                // used by achievement entry before trying the profile fallback.
                string menuTiles;
                using (Bitmap tiles = frame.Clone(geometry.TopRect(120, 350, 620, 650),
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    menuTiles = GenshinProcesor.AnalyzeText(tiles, PageSegMode.SparseText).Trim();
                evidence.Add("menu-tiles='" + menuTiles + "'");
                bool paimon = HasPaimonTiles(menuTiles);
                if (!paimon)
                {
                    Rectangle profileRegion = geometry.TopRect(290, 145, 465, 182);
                    string profile = ReadText(frame, profileRegion);
                    evidence.Add("profile='" + profile + "'");
                    if (!HasPaimonProfile(profile))
                    {
                        profile = ReadText(frame, profileRegion, false);
                        evidence.Add("profile-grayscale='" + profile + "'");
                    }
                    paimon = HasPaimonProfile(profile);
                }
                if (paimon)
                {
                    Rectangle tileRegion = geometry.TopRect(438, 580, 132, 35);
                    string tile;
                    using (Bitmap crop = frame.Clone(tileRegion, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                        tile = GenshinProcesor.AnalyzeText(crop, PageSegMode.SingleLine).Trim();
                    evidence.Add("character-tile='" + tile + "'");
                    if (Normalize(tile) != "character")
                    {
                        tile = ReadText(frame, tileRegion);
                        evidence.Add("character-tile-threshold='" + tile + "'");
                    }
                    if (Normalize(tile) == "character")
                        result.CharacterTileBounds = geometry.TopRect(432, 494, 144, 130);
                    return Finish(result, CharacterScreenState.PaimonMenu, evidence);
                }

                // CaptureWindow masks UID. Health digits also interrupt the
                // colored fill, and Lunar characters add another bar above it.
                // Read the HP line separately and corroborate it with controls
                // that belong to the live HUD, rather than world colors alone.
                Rectangle healthText = geometry.BottomRect(790, 80, 335, 25);
                string hp = ReadWhiteHudText(frame, healthText);
                bool healthFraction = HasHealthFraction(hp);
                if (!healthFraction)
                {
                    hp = ReadText(frame, healthText, false);
                    healthFraction = HasHealthFraction(hp);
                }
                if (partyKeys < 0) partyKeys = CountPartySlotKeys(frame, geometry);
                if (controls == null) controls = ReadHudControls(frame, geometry, out actionKey);
                evidence.Add($"health-bar={(health.HasValue ? health.Value.ToString() : "absent")}, " +
                    $"health='{hp}', party-slot-keys={partyKeys}, hud-controls='{controls}'");
                if ((healthFraction && (health.HasValue || partyKeys >= 2 || actionKey)) ||
                    (partyKeys >= 3 && actionKey))
                    return Finish(result, CharacterScreenState.Gameplay, evidence);
            }
            catch (ThreadAbortException) { throw; }
            catch (Exception exception)
            {
                evidence.Add("screen OCR error=" + exception.GetType().Name + ": " + exception.Message);
            }
            return Finish(result, CharacterScreenState.Unknown, evidence);
        }

        internal static CharacterScreenRecognition ReadTolerant(Bitmap frame, CharacterScreenState expected,
            CharacterScreenRecognition strict)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (strict == null) throw new ArgumentNullException(nameof(strict));
            // A second pass may complete missing evidence, but never replace a
            // positive screen identity with the state navigation wanted to see.
            if (strict.State != CharacterScreenState.Unknown && strict.State != CharacterScreenState.CharacterMenu)
                return strict;
            if (expected != CharacterScreenState.CharacterMenu && expected != CharacterScreenState.Attributes &&
                expected != CharacterScreenState.Constellation && expected != CharacterScreenState.Talents &&
                expected != CharacterScreenState.ConstellationDetails && expected != CharacterScreenState.TalentDetails)
                return strict;
            if (frame.Width < 640 || frame.Height < 360) return strict;

            var result = new CharacterScreenRecognition
            {
                State = strict.State,
                FrameWidth = frame.Width,
                FrameHeight = frame.Height,
                CharacterTileBounds = strict.CharacterTileBounds
            };
            var geometry = new Geometry(frame);
            var evidence = new List<string>
            {
                $"tolerant pass expected={expected}, strict={strict.State}",
                "strict evidence=" + (strict.Evidence ?? string.Empty)
            };
            try
            {
                int? edge = FindTolerantDetailPanelEdge(frame, geometry);
                if (edge.HasValue)
                {
                    evidence.Add($"relaxed detail-edge x={edge.Value}: contrast>=10, dark-left<190, matching rows>=50%");
                    Rectangle detailRegion = geometry.TopRect(10, 100, 570, 390);
                    string detailGray = ReadText(frame, detailRegion, false);
                    string detailRaw = ReadRawText(frame, detailRegion);
                    evidence.Add("wide detail-grayscale='" + detailGray + "', detail-raw='" + detailRaw + "'");
                    bool constellationLabel = HasConstellationDetailLabel(detailGray) || HasConstellationDetailLabel(detailRaw);
                    if (constellationLabel && (expected == CharacterScreenState.ConstellationDetails ||
                        expected == CharacterScreenState.CharacterMenu))
                    {
                        int circles = CountConstellationCircles(frame, geometry, true);
                        evidence.Add($"specific Constellation Lv label plus independent right medallions={circles}, required>=3");
                        if (circles >= 3)
                            return Finish(result, CharacterScreenState.ConstellationDetails, evidence);
                    }
                    else if (!constellationLabel && (expected == CharacterScreenState.TalentDetails ||
                        expected == CharacterScreenState.CharacterMenu) &&
                        Math.Max(CountLevelLabels(detailGray), CountLevelLabels(detailRaw)) >= 1)
                    {
                        string right = ReadTolerantPaneText(frame, geometry.TopRect(1400, 100, 485, 815),
                            text => CountLevelLabels(text) >= 2, evidence, "detail right-list", CountLevelLabels);
                        int levels = Math.Max(CountLevelLabels(right), ReadLightTalentLevelRows(frame, geometry, evidence));
                        evidence.Add($"detail left Lv>=1 plus independent right Lv labels={levels}; requires >=2, or >=1 plus both exact detail tabs");
                        if (levels >= 2 || levels >= 1 && ReadTalentDetailTabs(frame, geometry, evidence))
                        {
                            if (levels < 2) evidence.Add("one right Lv row independently corroborated by both Talent Info and Skill Attributes tabs");
                            return Finish(result, CharacterScreenState.TalentDetails, evidence);
                        }
                    }
                    // An overlay-shaped frame must retain its uncertainty. In
                    // particular, it cannot become a generic/base menu merely
                    // because sidebar words remain readable during animation.
                    evidence.Add("overlay evidence incomplete; base-pane promotion forbidden");
                    return Finish(result, strict.State, evidence);
                }
                if (expected == CharacterScreenState.ConstellationDetails || expected == CharacterScreenState.TalentDetails)
                {
                    evidence.Add("detail promotion rejected: no independent panel edge");
                    return Finish(result, strict.State, evidence);
                }

                bool menu = strict.State == CharacterScreenState.CharacterMenu;
                if (!menu)
                {
                    string sidebar = ReadTolerantPaneText(frame, geometry.TopRect(84, 110, 430, 485),
                        HasTolerantCharacterSidebar, evidence, "sidebar");
                    menu = HasTolerantCharacterSidebar(sidebar);
                    if (!menu)
                    {
                        sidebar = ReadLightSidebarRows(frame, geometry, evidence);
                        menu = HasTolerantCharacterSidebar(sidebar);
                    }
                    evidence.Add($"relaxed sidebar exact labels={CountExactSidebarLabels(sidebar)}, required>=3 including Constellation/Talents; Attributes word={HasExactLabel(sidebar, "attributes")}; selected marker plus independent pane content still required");
                }
                else evidence.Add("same-frame strict generic menu supplies verified sidebar evidence");
                if (!menu) return Finish(result, strict.State, evidence);

                bool attributes = HasTolerantSelectedSidebarMarker(frame, geometry, 154);
                bool constellation = HasTolerantSelectedSidebarMarker(frame, geometry, 364);
                bool talents = HasTolerantSelectedSidebarMarker(frame, geometry, 434);
                evidence.Add($"relaxed selected diamond+white-label markers: Attributes={attributes}, Constellation={constellation}, Talents={talents}; bright fractions>=1.5%/2.5%");
                if ((attributes ? 1 : 0) + (constellation ? 1 : 0) + (talents ? 1 : 0) != 1)
                {
                    evidence.Add("pane promotion rejected: selected marker absent or ambiguous");
                    return Finish(result, strict.State, evidence);
                }
                bool anyPane = expected == CharacterScreenState.CharacterMenu;
                if (attributes && (anyPane || expected == CharacterScreenState.Attributes))
                {
                    string right = ReadTolerantPaneText(frame, geometry.TopRect(1400, 100, 485, 815),
                        HasTolerantAttributePane, evidence, "attribute right-pane");
                    evidence.Add($"relaxed stat labels={CountAttributeLabels(right)}, required>=2 including Max HP/Max Stamina");
                    if (HasTolerantAttributePane(right))
                        return Finish(result, CharacterScreenState.Attributes, evidence);
                }
                else if (constellation && (anyPane || expected == CharacterScreenState.Constellation))
                {
                    int circles = CountConstellationCircles(frame, geometry, true);
                    evidence.Add($"relaxed right medallions={circles}, required>=3 (strict requires4)");
                    if (circles >= 3)
                        return Finish(result, CharacterScreenState.Constellation, evidence);
                }
                else if (talents && (anyPane || expected == CharacterScreenState.Talents))
                {
                    string right = ReadTolerantPaneText(frame, geometry.TopRect(1400, 100, 485, 815),
                        text => CountLevelLabels(text) >= 2, evidence, "talent right-pane", CountLevelLabels);
                    int levels = Math.Max(CountLevelLabels(right), ReadLightTalentLevelRows(frame, geometry, evidence));
                    evidence.Add($"relaxed talent Lv labels={levels}; requires >=2, or >=1 plus Talent Demo (strict requires3 or the same control alternative)");
                    if (levels >= 2 || levels >= 1 && ReadTalentDemoControl(frame, geometry, evidence))
                    {
                        if (levels < 2) evidence.Add("one right Lv row independently corroborated by Talent Demo control");
                        return Finish(result, CharacterScreenState.Talents, evidence);
                    }
                }
                evidence.Add("expected pane lacks independent qualification; generic promotion forbidden");
            }
            catch (ThreadAbortException) { throw; }
            catch (Exception exception)
            {
                evidence.Add("tolerant screen OCR error=" + exception.GetType().Name + ": " + exception.Message);
            }
            return Finish(result, strict.State, evidence);
        }

        private static string ReadTolerantPaneText(Bitmap frame, Rectangle region, Func<string, bool> qualified,
            List<string> evidence, string label, Func<string, int> evidenceScore = null)
        {
            string gray = ReadText(frame, region, false);
            evidence.Add(label + " grayscale='" + gray + "'");
            if (qualified(gray)) return gray;
            string raw = ReadRawText(frame, region);
            evidence.Add(label + " raw='" + raw + "'");
            if (!qualified(raw) && evidenceScore != null && evidenceScore(gray) > evidenceScore(raw))
            {
                evidence.Add(label + " retains stronger grayscale evidence: " + evidenceScore(gray) + " versus " + evidenceScore(raw));
                return gray;
            }
            return raw;
        }

        private static string ReadLightSidebarRows(Bitmap frame, Geometry geometry, List<string> evidence)
        {
            var rows = new List<string>();
            foreach (int centerY in new[] { 154, 224, 294, 364, 434, 504 })
                rows.Add(ReadWhiteHudText(frame, geometry.TopRect(143, centerY - 25, 280, 50), PageSegMode.SingleLine, true));
            string text = string.Join("\n", rows);
            evidence.Add("sidebar isolated light/gold-glyph rows='" + text + "'");
            return text;
        }

        private static int ReadLightTalentLevelRows(Bitmap frame, Geometry geometry, List<string> evidence)
        {
            int found = 0;
            var rows = new List<string>();
            foreach (var region in new[] { geometry.TopRect(1430, 132, 280, 82),
                geometry.TopRect(1430, 217, 280, 94), geometry.TopRect(1430, 313, 280, 99) })
            {
                string text = ReadWhiteHudText(frame, region, PageSegMode.SparseText, true);
                rows.Add(text);
                // Count spatial rows, not repeated readings of the same Lv label.
                if (CountLevelLabels(text) > 0) found++;
            }
            evidence.Add("isolated light/gold-glyph talent rows='" + string.Join(" | ", rows) + "', Lv rows=" + found);
            return found;
        }

        internal static bool HasTalentDetailTabs(string text)
        {
            return Regex.IsMatch(text ?? string.Empty, @"\btalent\s+info\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(text ?? string.Empty, @"\bskill\s+attributes?\b", RegexOptions.IgnoreCase);
        }

        private static bool ReadTalentDetailTabs(Bitmap frame, Geometry geometry, List<string> evidence)
        {
            // Both exact controls belong to a talent overlay. Their presence is
            // independent of character/talent names and the selected right Lv.
            Rectangle region = geometry.TopRect(35, 205, 505, 65);
            string text = ReadText(frame, region, false);
            evidence.Add("talent detail controls grayscale='" + text + "'");
            if (HasTalentDetailTabs(text)) return true;
            text = ReadRawText(frame, region);
            evidence.Add("talent detail controls raw='" + text + "'");
            if (HasTalentDetailTabs(text)) return true;
            text = ReadText(frame, region);
            evidence.Add("talent detail controls threshold='" + text + "'");
            return HasTalentDetailTabs(text);
        }

        private static bool ReadTalentDemoControl(Bitmap frame, Geometry geometry, List<string> evidence)
        {
            Rectangle region = geometry.BottomRect(1500, 115, 330, 85);
            string text = ReadText(frame, region);
            evidence.Add("talent demo control='" + text + "'");
            if (Regex.IsMatch(text, @"\btalent\s+demo\b", RegexOptions.IgnoreCase)) return true;
            text = ReadRawText(frame, region);
            evidence.Add("talent demo control raw='" + text + "'");
            return Regex.IsMatch(text, @"\btalent\s+demo\b", RegexOptions.IgnoreCase);
        }

        private static bool HasUniqueSelectedTalentsMarker(Bitmap frame, Geometry geometry)
        {
            return HasTolerantSelectedSidebarMarker(frame, geometry, 434)
                && !HasTolerantSelectedSidebarMarker(frame, geometry, 154)
                && !HasTolerantSelectedSidebarMarker(frame, geometry, 364);
        }

        private static bool HasConstellationDetailLabel(string text)
        {
            return Regex.IsMatch(text ?? string.Empty, @"\bconstellation\s*(?:lv|level)\s*[.:]?\s*\d{1,2}\b",
                RegexOptions.IgnoreCase);
        }

        private static int CountExactSidebarLabels(string text)
        {
            string[] labels = { "attributes", "weapons", "artifacts", "constellation", "talents", "profile" };
            return labels.Count(label => HasExactLabel(text, label));
        }

        private static bool HasExactLabel(string text, string label)
        {
            return Regex.IsMatch(text ?? string.Empty, @"(?<![a-z])" + label + @"(?![a-z])", RegexOptions.IgnoreCase);
        }

        private static bool HasTolerantCharacterSidebar(string text)
        {
            // Selected bold text can overlap element-colored sparkles and be
            // unreadable while the other controls remain clear. The caller
            // additionally requires a selected marker and independent content.
            return CountExactSidebarLabels(text) >= 3 &&
                (HasExactLabel(text, "constellation") || HasExactLabel(text, "talents"));
        }

        private static int CountAttributeLabels(string text)
        {
            string normalized = Normalize(text);
            string[] labels = { "maxhp", "atk", "def", "elementalmastery", "maxstamina" };
            return labels.Count(label => normalized.Contains(label));
        }

        private static bool HasTolerantAttributePane(string text)
        {
            string normalized = Normalize(text);
            return CountAttributeLabels(text) >= 2 &&
                (normalized.Contains("maxhp") || normalized.Contains("maxstamina"));
        }

        private static bool HasTolerantSelectedSidebarMarker(Bitmap frame, Geometry geometry, int centerY)
        {
            return BrightPixelFraction(frame, geometry.TopRect(118, centerY - 28, 22, 56)) >= 0.015 &&
                BrightPixelFraction(frame, geometry.TopRect(143, centerY - 28, 280, 56)) >= 0.025;
        }

        private static int? FindTolerantDetailPanelEdge(Bitmap frame, Geometry geometry)
        {
            for (int referenceX = 490; referenceX <= 590; referenceX += 4)
            {
                int x = geometry.ScaleX(referenceX), matches = 0, samples = 0;
                int offset = Math.Max(2, geometry.ScaleX(3));
                for (int referenceY = 35; referenceY <= 930; referenceY += 45)
                {
                    int y = geometry.ScaleY(referenceY);
                    if (x - offset < 0 || x + offset >= frame.Width || y >= frame.Height) continue;
                    Color left = frame.GetPixel(x - offset, y), right = frame.GetPixel(x + offset, y);
                    if (Math.Max(left.R, Math.Max(left.G, left.B)) < 190 && ColorDifference(left, right) >= 10) matches++;
                    samples++;
                }
                if (samples >= 12 && matches >= samples * 0.50) return x;
            }
            return null;
        }

        internal static bool HasCharacterSidebar(string text)
        {
            // Four distinct controls establish the sidebar even if its selected
            // label is obscured. Exact words preserve the multi-control guard;
            // the independent right pane still determines the specific state.
            return CountExactSidebarLabels(text) >= 4 &&
                (HasExactLabel(text, "constellation") || HasExactLabel(text, "talents"));
        }

        private static bool HasAttributePane(string text)
        {
            string normalized = Normalize(text);
            string[] labels = { "maxhp", "atk", "def", "elementalmastery", "maxstamina" };
            return labels.Count(label => normalized.Contains(label)) >= 3 &&
                (normalized.Contains("maxhp") || normalized.Contains("maxstamina"));
        }

        private static bool HasPaimonProfile(string text)
        {
            string normalized = Normalize(text);
            return normalized.Contains("adventurerank") && normalized.Contains("worldlevel") && normalized.Contains("birthday");
        }

        private static bool HasPaimonTiles(string text)
        {
            string normalized = Normalize(text);
            string[] labels = { "shop", "partysetup", "friends", "achievements", "archive",
                "characterarchive", "trainingguide", "inventory", "quests", "map", "events", "wish" };
            return labels.Count(label => normalized.Contains(label)) >= 5 &&
                normalized.Contains("inventory") && normalized.Contains("achievements");
        }

        private static int CountLevelLabels(string text)
        {
            return Regex.Matches(text ?? string.Empty, @"\blv\s*[.:]?\s*\d{1,2}\b", RegexOptions.IgnoreCase).Count;
        }

        private static string Normalize(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"[^a-z]", string.Empty, RegexOptions.IgnoreCase).ToLowerInvariant();
        }

        private static CharacterScreenRecognition Finish(CharacterScreenRecognition result,
            CharacterScreenState state, List<string> evidence)
        {
            result.State = state;
            result.Evidence = string.Join("; ", evidence.Select(part => part.Replace("\r", "\\r").Replace("\n", "\\n")));
            return result;
        }

        private static string ReadText(Bitmap frame, Rectangle region, bool threshold = true)
        {
            region = Rectangle.Intersect(region, new Rectangle(Point.Empty, frame.Size));
            if (region.Width < 2 || region.Height < 2) return string.Empty;
            using (Bitmap crop = frame.Clone(region, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            using (Bitmap gray = GenshinProcesor.ConvertToGrayscale(crop))
            using (Bitmap binary = threshold ? new Threshold(130).Apply(gray) : (Bitmap)gray.Clone())
            using (Bitmap inverted = new Invert().Apply(binary))
            using (Bitmap scaled = GenshinProcesor.ResizeImage(inverted, inverted.Width * 2, inverted.Height * 2))
                return GenshinProcesor.AnalyzeText(scaled, PageSegMode.SingleBlock).Trim();
        }

        private static bool HasHealthFraction(string text)
        {
            Match match = Regex.Match(text ?? string.Empty, @"(?<!\d)(\d{1,7})\s*/\s*(\d{1,7})(?!\d)");
            int current, maximum;
            return match.Success && int.TryParse(match.Groups[1].Value, out current) &&
                int.TryParse(match.Groups[2].Value, out maximum) && maximum > 0 && current <= maximum;
        }

        private static string ReadRawText(Bitmap frame, Rectangle region)
        {
            region = Rectangle.Intersect(region, new Rectangle(Point.Empty, frame.Size));
            if (region.Width < 2 || region.Height < 2) return string.Empty;
            using (Bitmap crop = frame.Clone(region, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                return GenshinProcesor.AnalyzeText(crop, PageSegMode.SparseText).Trim();
        }

        private static string ReadWhiteHudText(Bitmap frame, Rectangle region, PageSegMode mode = PageSegMode.SingleLine,
            bool characterControls = false)
        {
            region = Rectangle.Intersect(region, new Rectangle(Point.Empty, frame.Size));
            if (region.Width < 2 || region.Height < 2) return string.Empty;
            using (Bitmap glyphs = new Bitmap(region.Width, region.Height))
            {
                // HP keeps its neutral-white mask. Character controls additionally
                // use muted gold ink (e.g. RGB210/200/174 on a bright Geo background),
                // which must not be discarded just because its blue channel is below 185.
                for (int y = 0; y < glyphs.Height; y++)
                    for (int x = 0; x < glyphs.Width; x++)
                    {
                        Color color = frame.GetPixel(region.X + x, region.Y + y);
                        int low = Math.Min(color.R, Math.Min(color.G, color.B));
                        int high = Math.Max(color.R, Math.Max(color.G, color.B));
                        bool white = low >= 185 && high - low <= 45;
                        bool gold = characterControls && color.R >= 185 && color.G >= 175 && color.B >= 150
                            && color.R >= color.G && color.G >= color.B && color.R - color.G <= 30 && color.G - color.B <= 50;
                        glyphs.SetPixel(x, y, white || gold ? Color.Black : Color.White);
                    }
                using (Bitmap scaled = GenshinProcesor.ResizeImage(glyphs, glyphs.Width * 3, glyphs.Height * 3))
                    return GenshinProcesor.AnalyzeText(scaled, mode).Trim();
            }
        }

        private static string ReadKeyText(Bitmap frame, Rectangle region, bool numbersOnly = false)
        {
            region = Rectangle.Intersect(region, new Rectangle(Point.Empty, frame.Size));
            if (region.Width < 2 || region.Height < 2) return string.Empty;
            using (Bitmap crop = frame.Clone(region, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            using (Bitmap gray = GenshinProcesor.ConvertToGrayscale(crop))
            using (Bitmap scaled = GenshinProcesor.ResizeImage(gray, gray.Width * 3, gray.Height * 3))
                return GenshinProcesor.AnalyzeText(scaled, PageSegMode.SingleLine, numbersOnly).Trim();
        }

        private static string ReadHudControls(Bitmap frame, Geometry geometry, out bool actionKey)
        {
            string controls = ReadKeyText(frame, geometry.BottomRect(80, 58, 68, 36));
            actionKey = Normalize(controls) == "enter";
            if (!actionKey)
            {
                string space = ReadKeyText(frame, geometry.BottomRect(1545, 58, 92, 36));
                controls += " / " + space;
                actionKey = Normalize(space) == "space";
            }
            return controls;
        }

        private static int CountPartySlotKeys(Bitmap frame, Geometry geometry)
        {
            int centerX = frame.Width - geometry.ScaleX(48);
            int offsetX = Math.Max(3, geometry.ScaleX(9));
            int offsetY = Math.Max(3, geometry.ScaleY(7));
            int start = -1;
            var keys = new HashSet<int>();
            // Discover the aligned white keycaps rather than assuming a party
            // member's name or which row is active. The active key can be gray.
            for (int y = (int)(frame.Height * 0.20); y < frame.Height * 0.62; y++)
            {
                bool key = true;
                foreach (int dx in new[] { -offsetX, offsetX })
                    foreach (int dy in new[] { -offsetY, offsetY })
                    {
                        Color color = frame.GetPixel(centerX + dx, y + dy);
                        if (Math.Min(color.R, Math.Min(color.G, color.B)) < 235 ||
                            Math.Max(color.R, Math.Max(color.G, color.B)) -
                            Math.Min(color.R, Math.Min(color.G, color.B)) > 15) key = false;
                    }
                Color center = frame.GetPixel(centerX, y);
                key &= Math.Max(center.R, Math.Max(center.G, center.B)) < 140;
                if (key) { if (start < 0) start = y; }
                else if (start >= 0)
                {
                    if (y - start >= Math.Max(2, geometry.ScaleY(3)))
                    {
                        int centerY = (start + y - 1) / 2;
                        Rectangle keyRegion = new Rectangle(centerX - geometry.ScaleX(14),
                            centerY - geometry.ScaleY(12), Math.Max(1, geometry.ScaleX(28)),
                            Math.Max(1, geometry.ScaleY(24)));
                        string text = ReadKeyText(frame, keyRegion, true);
                        int number;
                        if (Regex.IsMatch(text, @"^[1-4]$") && int.TryParse(text, out number)) keys.Add(number);
                    }
                    start = -1;
                }
            }
            return keys.Count;
        }

        private static int? FindDetailPanelEdge(Bitmap frame, Geometry geometry)
        {
            for (int referenceX = 508; referenceX <= 568; referenceX += 4)
            {
                int x = geometry.ScaleX(referenceX), matches = 0, samples = 0;
                int offset = Math.Max(2, geometry.ScaleX(3));
                for (int referenceY = 35; referenceY <= 930; referenceY += 45)
                {
                    int y = geometry.ScaleY(referenceY);
                    if (x - offset < 0 || x + offset >= frame.Width || y >= frame.Height) continue;
                    Color left = frame.GetPixel(x - offset, y), right = frame.GetPixel(x + offset, y);
                    int difference = Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);
                    // Locked Hydro constellations have a dark blue background
                    // that is close to the detail panel color. The long edge
                    // plus the specific overlay label still establishes the
                    // panel when that boundary has only modest contrast.
                    if (Math.Max(left.R, Math.Max(left.G, left.B)) < 160 && difference >= 15) matches++;
                    samples++;
                }
                if (samples >= 12 && matches >= samples * 0.65) return x;
            }
            return null;
        }

        private static bool HasSelectedConstellationMarker(Bitmap frame, Geometry geometry)
        {
            // The selected row has a bright right half inside an outlined
            // diamond and a white label. Other rows have a small gray filled
            // diamond. Requiring both regions avoids treating the ever-present
            // word 'Constellation' or an animated sparkle as a selected tab.
            Rectangle marker = geometry.TopRect(118, 336, 22, 56);
            Rectangle label = geometry.TopRect(143, 336, 280, 56);
            return BrightPixelFraction(frame, marker) >= 0.02 &&
                BrightPixelFraction(frame, label) >= 0.04;
        }

        private static double BrightPixelFraction(Bitmap frame, Rectangle region)
        {
            region = Rectangle.Intersect(region, new Rectangle(Point.Empty, frame.Size));
            if (region.Width < 1 || region.Height < 1) return 0;
            int bright = 0;
            for (int y = region.Top; y < region.Bottom; y++)
                for (int x = region.Left; x < region.Right; x++)
                {
                    Color color = frame.GetPixel(x, y);
                    if (Math.Min(color.R, Math.Min(color.G, color.B)) >= 210) bright++;
                }
            return (double)bright / (region.Width * region.Height);
        }

        private static int CountConstellationCircles(Bitmap frame, Geometry geometry, bool selected)
        {
            int found = 0;
            for (int row = 0; row < 6; row++)
            {
                int expectedY = (int)Math.Round(((geometry.Wide ? 180 : 225) + row * 75) * frame.Height /
                    (geometry.Wide ? 720.0 : 800.0));
                bool circle = false;
                for (int x = geometry.ScaleX(1478); x <= geometry.ScaleX(1645) && !circle; x += Math.Max(2, geometry.ScaleX(5)))
                    for (int y = expectedY - geometry.ScaleY(18); y <= expectedY + geometry.ScaleY(18) && !circle; y += Math.Max(2, geometry.ScaleY(5)))
                    {
                        bool glyph = selected && HasMedallionGlyph(frame, geometry, x, y);
                        for (int radius = 27; radius <= 38 && !circle; radius += 3)
                        {
                            int matches = 0, borderMatches = 0;
                            for (int angle = 0; angle < 24; angle++)
                            {
                                double radians = angle * Math.PI / 12;
                                int sampleX = x + (int)Math.Round(Math.Cos(radians) * geometry.ScaleX(radius));
                                int sampleY = y + (int)Math.Round(Math.Sin(radians) * geometry.ScaleX(radius));
                                if (sampleX < 0 || sampleY < 0 || sampleX >= frame.Width || sampleY >= frame.Height) continue;
                                Color color = frame.GetPixel(sampleX, sampleY);
                                if (Math.Max(color.R, Math.Max(color.G, color.B)) >= 205 &&
                                    Math.Min(color.R, Math.Min(color.G, color.B)) >= 165) matches++;
                                int innerX = x + (int)Math.Round(Math.Cos(radians) * geometry.ScaleX(radius - 5));
                                int innerY = y + (int)Math.Round(Math.Sin(radians) * geometry.ScaleX(radius - 5));
                                int outerX = x + (int)Math.Round(Math.Cos(radians) * geometry.ScaleX(radius + 5));
                                int outerY = y + (int)Math.Round(Math.Sin(radians) * geometry.ScaleX(radius + 5));
                                if (innerX < 0 || innerY < 0 || outerX < 0 || outerY < 0 ||
                                    innerX >= frame.Width || innerY >= frame.Height || outerX >= frame.Width || outerY >= frame.Height) continue;
                                Color inner = frame.GetPixel(innerX, innerY), outer = frame.GetPixel(outerX, outerY);
                                if (ColorDifference(color, inner) >= 30 && ColorDifference(color, outer) >= 30) borderMatches++;
                            }
                            // C0 medallions use dark gray/element-colored rings
                            // around a white lock, not an activated white ring.
                            circle = matches >= 18 || borderMatches >= 14 && HasMedallionLock(frame, geometry, x, y);
                            // Activated elemental rings have a very narrow
                            // white rim inside a colored, animated halo. A
                            // coarse center grid can miss that rim entirely.
                            // The selected tab and interior icon corroborate
                            // the tolerant radial edge test; four separate
                            // expected rows are still required by the caller.
                            if (!circle && glyph)
                                circle = HasMedallionRing(frame, geometry, x, y, radius);
                        }
                    }
                if (circle) found++;
            }
            return found;
        }

        private static bool HasMedallionGlyph(Bitmap frame, Geometry geometry, int x, int y)
        {
            int bright = 0;
            for (int offsetY = -18; offsetY <= 18; offsetY += 6)
                for (int offsetX = -18; offsetX <= 18; offsetX += 6)
                {
                    int sampleX = x + geometry.ScaleX(offsetX), sampleY = y + geometry.ScaleY(offsetY);
                    if (sampleX < 0 || sampleY < 0 || sampleX >= frame.Width || sampleY >= frame.Height) continue;
                    Color color = frame.GetPixel(sampleX, sampleY);
                    int low = Math.Min(color.R, Math.Min(color.G, color.B));
                    int high = Math.Max(color.R, Math.Max(color.G, color.B));
                    if (low >= 130 && high - low <= 80) bright++;
                }
            return bright >= 6;
        }

        private static bool HasMedallionRing(Bitmap frame, Geometry geometry, int x, int y, int radius)
        {
            int edges = 0;
            for (int angle = 0; angle < 24; angle++)
            {
                double radians = angle * Math.PI / 12;
                bool edge = false;
                // Three reference pixels accommodate the center-grid offset
                // and antialiasing, without requiring a particular element hue.
                for (int offset = -3; offset <= 3 && !edge; offset++)
                {
                    int ringRadius = radius + offset;
                    int sampleX = x + (int)Math.Round(Math.Cos(radians) * geometry.ScaleX(ringRadius));
                    int sampleY = y + (int)Math.Round(Math.Sin(radians) * geometry.ScaleY(ringRadius));
                    int innerX = x + (int)Math.Round(Math.Cos(radians) * geometry.ScaleX(ringRadius - 5));
                    int innerY = y + (int)Math.Round(Math.Sin(radians) * geometry.ScaleY(ringRadius - 5));
                    int outerX = x + (int)Math.Round(Math.Cos(radians) * geometry.ScaleX(ringRadius + 5));
                    int outerY = y + (int)Math.Round(Math.Sin(radians) * geometry.ScaleY(ringRadius + 5));
                    if (sampleX < 0 || sampleY < 0 || innerX < 0 || innerY < 0 || outerX < 0 || outerY < 0 ||
                        sampleX >= frame.Width || sampleY >= frame.Height || innerX >= frame.Width ||
                        innerY >= frame.Height || outerX >= frame.Width || outerY >= frame.Height) continue;
                    Color ring = frame.GetPixel(sampleX, sampleY);
                    edge = ColorDifference(ring, frame.GetPixel(innerX, innerY)) >= 60 &&
                        ColorDifference(ring, frame.GetPixel(outerX, outerY)) >= 60;
                }
                if (edge) edges++;
            }
            return edges >= 20;
        }

        private static int ColorDifference(Color first, Color second)
        {
            return Math.Abs(first.R - second.R) + Math.Abs(first.G - second.G) + Math.Abs(first.B - second.B);
        }

        private static bool HasMedallionLock(Bitmap frame, Geometry geometry, int x, int y)
        {
            int body = 0, darkCorners = 0;
            foreach (int referenceX in new[] { -4, 4 })
                foreach (int referenceY in new[] { 2, 6, 10 })
                {
                    Color color = frame.GetPixel(x + geometry.ScaleX(referenceX), y + geometry.ScaleX(referenceY));
                    if (Math.Min(color.R, Math.Min(color.G, color.B)) >= 185) body++;
                }
            foreach (int referenceX in new[] { -20, 20 })
                foreach (int referenceY in new[] { -17, 17 })
                {
                    Color color = frame.GetPixel(x + geometry.ScaleX(referenceX), y + geometry.ScaleX(referenceY));
                    if (Math.Max(color.R, Math.Max(color.G, color.B)) < 130) darkCorners++;
                }
            return body >= 5 && darkCorners >= 3;
        }

        private static Rectangle? FindHealthBar(Bitmap frame)
        {
            int minimumRun = Math.Max(60, frame.Width / 16);
            int maximumGap = Math.Max(2, frame.Width / 192);
            int maximumRun = Math.Max(minimumRun, frame.Width / 5);
            for (int y = (int)(frame.Height * 0.89); y < frame.Height * 0.965; y++)
            {
                int start = -1, lastGreen = -1, greenPixels = 0;
                for (int x = (int)(frame.Width * 0.39); x < frame.Width * 0.64; x++)
                {
                    Color color = frame.GetPixel(x, y);
                    bool green = color.G >= 140 && color.G > color.R * 1.18 && color.G > color.B * 1.15;
                    if (green)
                    {
                        if (start < 0) start = x;
                        lastGreen = x;
                        greenPixels++;
                    }
                    if (start >= 0 && ((!green && x - lastGreen > maximumGap) || x == (int)(frame.Width * 0.64) - 1))
                    {
                        int width = lastGreen - start + 1;
                        // Bridge only short glyph-width gaps, then require a
                        // predominantly green horizontal span. The Lunar bar
                        // cannot qualify because its fill is blue/purple.
                        if (width >= minimumRun && width <= maximumRun && greenPixels >= width * 0.60)
                            return new Rectangle(start, y, width, 3);
                        start = -1;
                        greenPixels = 0;
                    }
                }
            }
            return null;
        }

        private sealed class Geometry
        {
            private readonly Bitmap frame;
            internal readonly bool Wide;
            internal Geometry(Bitmap frame)
            {
                this.frame = frame;
                Wide = (double)frame.Width / frame.Height >= 1.7;
            }
            internal int ScaleX(double value) => (int)Math.Round(value * frame.Width / 1920.0);
            internal int ScaleY(double value) => (int)Math.Round(value * frame.Height / (Wide ? 1080.0 : 1200.0));
            internal Rectangle TopRect(int x, int y, int width, int height) =>
                new Rectangle(ScaleX(x), ScaleY(y), Math.Max(1, ScaleX(width)), Math.Max(1, ScaleY(height)));
            internal Rectangle BottomRect(int x, int bottomOffset, int width, int height) =>
                new Rectangle(ScaleX(x), frame.Height - ScaleY(bottomOffset),
                    Math.Max(1, ScaleX(width)), Math.Max(1, ScaleY(height)));
        }
    }
}
