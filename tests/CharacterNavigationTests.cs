using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using WindowsInput.Native;

namespace InventoryKamera.Tests
{
    /// <summary>Offline controller traces and opt-in saved-frame replay. No game input.</summary>
    internal static class CharacterNavigationTests
    {
        private static int passed, failed;

        private static int Main(string[] args)
        {
            string captures = null, navigationCaptures = null, paneCaptures = null, countCaptures = null, paimon = null;
            for (int index = 0; index < args.Length; ++index)
            {
                if (index + 1 >= args.Length ||
                    (args[index] != "--capture-directory" && args[index] != "--navigation-capture-directory"
                    && args[index] != "--pane-capture-directory" && args[index] != "--count-capture-directory" && args[index] != "--paimon-screenshot"))
                {
                    Console.Error.WriteLine("Usage: CharacterNavigationTests.exe [--capture-directory <saved character screenshots>] [--navigation-capture-directory <saved entry failure frames>] [--pane-capture-directory <saved constellation pane and detail frames>] [--count-capture-directory <saved roster-count failure frames>] [--paimon-screenshot <saved Paimon image>]");
                    return 2;
                }
                if (args[index] == "--capture-directory") captures = args[++index];
                else if (args[index] == "--navigation-capture-directory") navigationCaptures = args[++index];
                else if (args[index] == "--pane-capture-directory") paneCaptures = args[++index];
                else if (args[index] == "--count-capture-directory") countCaptures = args[++index];
                else paimon = args[++index];
            }
            Run("Detail cleanup cannot Escape from gameplay or a closed panel", DetailCleanup);
            Run("Character actions require positive menu evidence and a focused game", ActionGuards);
            Run("Recovery uses verified entry controls and remains bounded", RecoveryTraces);
            Run("Initial entry and compatible click retries remain bounded", EntryAndClickTraces);
            Run("Tolerant verification requires a fresh exact-pane quorum without issuing input", TolerantVerification);
            Run("Unknown avatar headers do not erase structural menu evidence", UnknownHeaderEvidence);
            Run("Blank full client screenshots cannot authorize gameplay or menu input", BlankFrameEvidence);
            Run("Roster counting rejects a stalled or interrupted traversal", RosterCounting);
            Run("Recovery restores the exact character without counting a false wrap", ResumeCheckpoint);
            Run("Failed character details retry within a bound and require a verified checkpoint before omission", FailedCharacterRecovery);
            if (captures != null || paimon != null)
                Run("Saved Xiangling failure frames and optional Paimon replay", () => CaptureReplay(captures, paimon));
            if (navigationCaptures != null)
                Run("Actual gameplay and Paimon character-entry failure replay", () => NavigationCaptureReplay(navigationCaptures));
            if (paneCaptures != null)
                Run("Glowing constellation pane replay avoids repeated clicks and preserves detail overlays", () => PaneCaptureReplay(paneCaptures));
            if (countCaptures != null)
                Run("Actual roster-count Attributes frames verify before any roster input", () => CountCaptureReplay(countCaptures));
            Console.WriteLine("Character navigation regressions: {0} passed, {1} failed.", passed, failed);
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string description, Action action)
        {
            try { action(); ++passed; Console.WriteLine("PASS " + description); }
            catch (Exception exception)
            {
                ++failed;
                Console.Error.WriteLine("FAIL " + description + ": " + exception.GetBaseException().Message);
            }
            finally { CharacterNavigation.EndScan(); }
        }

        private sealed class FakeInput : ICharacterNavigationInput
        {
            internal CharacterScreenState State;
            internal bool FocusAvailable = true;
            internal int Captures, Refocuses;
            internal readonly List<VirtualKeyCode> Keys = new List<VirtualKeyCode>();
            internal readonly List<PointF> Clicks = new List<PointF>();
            internal readonly List<int> Waits = new List<int>();
            internal readonly List<CharacterScreenState> ObservedStates = new List<CharacterScreenState>();
            internal Action<VirtualKeyCode> OnKey;
            internal Action OnClick;
            internal Action<int> OnWait;
            internal Func<int, CharacterScreenState> StateAtRead;
            internal Func<Bitmap, CharacterScreenState, CharacterScreenRecognition, CharacterScreenRecognition> TolerantRead;

            internal FakeInput(CharacterScreenState state) { State = state; }
            public Bitmap Capture() { ++Captures; return new Bitmap(1280, 720); }
            public bool Refocus(out string reason)
            {
                ++Refocuses;
                reason = FocusAvailable ? null : "Game window unavailable in regression trace";
                return FocusAvailable;
            }
            public void Click(double x, double y)
            {
                Check(x >= 0 && x <= 1 && y >= 0 && y <= 1, "Controller click coordinates must be normalized.");
                Clicks.Add(new PointF((float)x, (float)y));
                OnClick?.Invoke();
            }
            public void PressKey(VirtualKeyCode key) { Keys.Add(key); OnKey?.Invoke(key); }
            public void Wait(int milliseconds) { Waits.Add(milliseconds); OnWait?.Invoke(milliseconds); }
            internal CharacterScreenRecognition Read(Bitmap frame)
            {
                if (StateAtRead != null) State = StateAtRead(Captures);
                ObservedStates.Add(State);
                return new CharacterScreenRecognition {
                    State = State, Evidence = "fake " + State,
                    CharacterTileBounds = State == CharacterScreenState.PaimonMenu
                        ? (Rectangle?)new Rectangle(280, 330, 100, 90) : null
                };
            }
            internal void Begin() { CharacterNavigation.BeginScan(this, Read, TolerantRead ?? ((frame, expected, strict) => null)); }
        }

        private static void DetailCleanup()
        {
            var gameplay = new FakeInput(CharacterScreenState.Gameplay);
            gameplay.Begin();
            NavigationFails(() => CharacterNavigation.CloseDetail(CharacterScreenState.TalentDetails, "failed talent read"));
            Equal(0, gameplay.Keys.Count, "A failed talent read on gameplay must not press Escape");
            Equal(0, gameplay.Clicks.Count, "A failed talent read on gameplay must not click");
            CharacterNavigation.EndScan();

            var closed = new FakeInput(CharacterScreenState.Talents);
            closed.Begin();
            CharacterNavigation.CloseDetail(CharacterScreenState.TalentDetails, "already closed talent panel");
            Equal(0, closed.Keys.Count, "Base Talents page already has no overlay to close");
            CharacterNavigation.EndScan();

            var detail = new FakeInput(CharacterScreenState.TalentDetails);
            detail.OnKey = key => { if (key == VirtualKeyCode.ESCAPE) detail.State = CharacterScreenState.Talents; };
            detail.Begin();
            CharacterNavigation.CloseDetail(CharacterScreenState.TalentDetails, "verified talent detail");
            Check(detail.Keys.SequenceEqual(new[] { VirtualKeyCode.ESCAPE }), "Exactly one Escape closes a positively verified detail panel.");
            Equal(CharacterScreenState.Talents, detail.State, "Detail cleanup stays in the character menu");
        }

        private static void ActionGuards()
        {
            foreach (CharacterScreenState state in new[] { CharacterScreenState.Gameplay, CharacterScreenState.PaimonMenu, CharacterScreenState.Unknown })
            {
                var input = new FakeInput(state);
                input.Begin();
                NavigationFails(() => CharacterNavigation.Require(CharacterScreenState.CharacterMenu, "next character guard"));
                NavigationFails(() => CharacterNavigation.NextCharacter(.96, .48, "next outside menu"));
                NavigationFails(() => CharacterNavigation.ClickDetail("talent outside menu", .88, .16, CharacterScreenState.TalentDetails));
                Equal(0, input.Keys.Count + input.Clicks.Count, "Outside character menu must stop actions for " + state);
                CharacterNavigation.EndScan();
            }
            var unfocused = new FakeInput(CharacterScreenState.Attributes) { FocusAvailable = false };
            unfocused.Begin();
            NavigationFails(() => CharacterNavigation.Require(CharacterScreenState.CharacterMenu, "game closed"));
            NavigationFails(() => CharacterNavigation.NextCharacter(.96, .48, "next with unavailable game"));
            Equal(0, unfocused.Keys.Count + unfocused.Clicks.Count, "Unavailable foreground game must receive no character input");
            CharacterNavigation.EndScan();

            var unknownName = new FakeInput(CharacterScreenState.CharacterMenu);
            unknownName.Begin();
            Check(CharacterNavigation.Require(CharacterScreenState.CharacterMenu, "unknown avatar").IsCharacterMenu,
                "Positive structural sidebar still permits menu handling for an unknown avatar.");
            CharacterNavigation.EndScan();
            var detail = new FakeInput(CharacterScreenState.TalentDetails);
            detail.Begin();
            NavigationFails(() => CharacterNavigation.NextCharacter(.96, .48, "next with open detail"));
            Equal(0, detail.Keys.Count + detail.Clicks.Count, "Next cannot advance while a detail overlay is open");
        }

        private static void RecoveryTraces()
        {
            var gameplay = RecoveryInput(CharacterScreenState.Gameplay);
            gameplay.Begin();
            Check(CharacterNavigation.RecoverCharacterScreen("lost after talent timeout"), "Gameplay must recover through the configured Character key.");
            Check(gameplay.Keys.SequenceEqual(new[] { Navigation.characterKey }), "Gameplay recovery uses C without resetting the party or reopening Paimon.");
            Equal(0, gameplay.Clicks.Count, "Gameplay recovery needs no Paimon tile click");
            Equal(CharacterScreenState.Attributes, gameplay.State, "Recovery finishes on a verified character page");
            CharacterNavigation.EndScan();

            var paimon = RecoveryInput(CharacterScreenState.PaimonMenu);
            paimon.Begin();
            Check(CharacterNavigation.RecoverCharacterScreen("already in Paimon"), "Existing Paimon menu must open Character.");
            Equal(0, paimon.Keys.Count, "Recovery does not toggle away from an already visible Paimon menu");
            Equal(1, paimon.Clicks.Count, "Existing Paimon recovery uses its Character tile");
            CharacterNavigation.EndScan();

            var unknown = new FakeInput(CharacterScreenState.Unknown);
            unknown.Begin();
            CharacterNavigation.ConfirmRosterProgress("Yelan");
            Check(!CharacterNavigation.RecoverCharacterScreen("unrecognized screen"), "Unknown screen cannot claim recovery success.");
            Check(unknown.Keys.Count <= 8 && unknown.Clicks.Count == 0, "Unknown recovery must be bounded and cannot guess a Character tile.");
            for (int attempt = 0; attempt < 2; ++attempt)
                Check(!CharacterNavigation.RecoverCharacterScreen("bounded repeated recovery"), "Unknown repeated recovery remains unsuccessful.");
            int previousActions = unknown.Keys.Count + unknown.Clicks.Count, previousCaptures = unknown.Captures;
            for (int attempt = 0; attempt < 2; ++attempt)
                Check(!CharacterNavigation.RecoverCharacterScreen("exhausted session budget"), "Recovery budget remains exhausted.");
            Equal(previousActions, unknown.Keys.Count + unknown.Clicks.Count, "Exhausted recovery budget sends no further input");
            Equal(previousCaptures, unknown.Captures, "Exhausted recovery budget performs no further captures");
            CharacterNavigation.ConfirmRosterProgress(null);
            CharacterNavigation.ConfirmRosterProgress("  ");
            CharacterNavigation.ConfirmRosterProgress("Yelan");
            Check(!CharacterNavigation.RecoverCharacterScreen("same or absent identity cannot renew retries"),
                "An unchanged checkpoint cannot reset exhausted recovery.");
            Equal(previousActions, unknown.Keys.Count + unknown.Clicks.Count, "Same or absent checkpoint sends no further recovery input");
            Equal(previousCaptures, unknown.Captures, "Same or absent checkpoint performs no further recovery captures");
            CharacterNavigation.ConfirmRosterProgress("Xiangling");
            Check(!CharacterNavigation.RecoverCharacterScreen("new verified roster position renews bounded retries"),
                "A new checkpoint renews retries without fabricating successful recovery.");
            Check(unknown.Captures > previousCaptures, "A different confirmed roster identity resets the recovery budget.");
            CharacterNavigation.EndScan();

            var unfocused = RecoveryInput(CharacterScreenState.Gameplay);
            unfocused.FocusAvailable = false;
            unfocused.Begin();
            Check(!CharacterNavigation.RecoverCharacterScreen("game unavailable"), "Unavailable game cannot recover.");
            Equal(0, unfocused.Keys.Count + unfocused.Clicks.Count, "Recovery sends no input when game focus cannot be acquired");
        }

        private static FakeInput RecoveryInput(CharacterScreenState state)
        {
            var input = new FakeInput(state);
            input.OnKey = key => {
                if (key == Navigation.characterKey && input.State == CharacterScreenState.Gameplay)
                    input.State = CharacterScreenState.Attributes;
                else if (key == Navigation.escapeKey && input.State == CharacterScreenState.Gameplay)
                    input.State = CharacterScreenState.PaimonMenu;
            };
            input.OnClick = () => {
                if (input.State == CharacterScreenState.PaimonMenu) input.State = CharacterScreenState.Attributes;
            };
            return input;
        }

        private static void EntryAndClickTraces()
        {
            VerifyInitialEntry(false);
            VerifyInitialEntry(true);
            VerifyInitialEntry(false, CharacterScreenState.Attributes);
            VerifyInitialEntry(false, CharacterScreenState.Unknown);
            VerifyInitialEntry(false, CharacterScreenState.PaimonMenu, true);
            VerifyCharacterKeyRetries();

            var existing = new FakeInput(CharacterScreenState.Attributes);
            existing.Begin();
            CharacterNavigation.OpenCharacterScreen("resume already visible character", false);
            Equal(0, existing.Keys.Count + existing.Clicks.Count, "Recovery entry preserves the current character without resetting party selection");
            CharacterNavigation.EndScan();

            var tile = new FakeInput(CharacterScreenState.PaimonMenu);
            tile.OnClick = () => { if (tile.Clicks.Count == 2) tile.State = CharacterScreenState.Attributes; };
            tile.Begin();
            CharacterNavigation.OpenCharacterScreen("retry missed Character tile");
            Equal(2, tile.Clicks.Count, "One missed tile click is retried after rereading Paimon");
            Equal(0, tile.Keys.Count, "Tile retry cannot toggle an already open Paimon menu");
            CharacterNavigation.EndScan();

            var pane = new FakeInput(CharacterScreenState.Attributes);
            pane.OnClick = () => { if (pane.Clicks.Count == 2) pane.State = CharacterScreenState.Talents; };
            pane.Begin();
            CharacterNavigation.SelectPane(CharacterScreenState.Talents, .12, .40, "retry missed Talents tab");
            Equal(2, pane.Clicks.Count, "One missed pane click is retried only on a verified character base pane");
            Equal(0, pane.Keys.Count, "Pane retry sends no Escape");
            CharacterNavigation.EndScan();

            var detail = new FakeInput(CharacterScreenState.Talents);
            detail.OnClick = () => { if (detail.Clicks.Count == 2) detail.State = CharacterScreenState.TalentDetails; };
            detail.Begin();
            CharacterNavigation.ClickDetail("retry missed auto talent", .88, .16, CharacterScreenState.TalentDetails);
            Equal(2, detail.Clicks.Count, "One missed detail click is retried only on its verified base pane");
            Equal(0, detail.Keys.Count, "Detail retry sends no Escape");
            CharacterNavigation.EndScan();

            var preceding = new FakeInput(CharacterScreenState.TalentDetails);
            preceding.OnKey = key => { if (key == Navigation.escapeKey) preceding.State = CharacterScreenState.Talents; };
            preceding.OnClick = () => { if (preceding.Clicks.Count == 2) preceding.State = CharacterScreenState.TalentDetails; };
            preceding.Begin();
            CharacterNavigation.ClickDetail("preceding talent must not satisfy new selection", .88, .24, CharacterScreenState.TalentDetails);
            Check(preceding.Keys.SequenceEqual(new[] { Navigation.escapeKey }), "A preceding verified talent overlay closes once before selecting a fresh target.");
            Equal(2, preceding.Clicks.Count, "A missed new target click cannot accept the old detail; it must retry from Talents");
            Check(preceding.ObservedStates.Contains(CharacterScreenState.Talents), "The fresh target transition must observe the base pane.");
            Equal(CharacterScreenState.TalentDetails, preceding.State, "Fresh target overlay is verified after the retry");
            CharacterNavigation.EndScan();

            foreach (CharacterScreenState outside in new[] { CharacterScreenState.Gameplay, CharacterScreenState.PaimonMenu, CharacterScreenState.Unknown })
            {
                var escaped = new FakeInput(CharacterScreenState.Talents);
                escaped.OnClick = () => escaped.State = outside;
                escaped.Begin();
                NavigationFails(() => CharacterNavigation.ClickDetail("screen lost after click", .88, .16, CharacterScreenState.TalentDetails));
                Equal(1, escaped.Clicks.Count, "A detail click cannot retry after screen loss to " + outside);
                Equal(0, escaped.Keys.Count, "Failed detail navigation cannot send Escape from " + outside);
                CharacterNavigation.EndScan();
            }
        }

        private static void TolerantVerification()
        {
            int fallbackCalls = 0;
            var strict = new FakeInput(CharacterScreenState.Constellation);
            strict.TolerantRead = (frame, expected, current) => { ++fallbackCalls; return TolerantResult(expected); };
            strict.Begin();
            Equal(CharacterScreenState.Constellation, CharacterNavigation.Require(CharacterScreenState.Constellation, "strict pane success").State,
                "A strict pane match returns immediately");
            Equal(1, strict.Captures, "Strict success requires one fresh capture");
            Equal(0, fallbackCalls, "Strict success cannot invoke the tolerant recognizer");
            CharacterNavigation.EndScan();

            var fallbackFrames = new List<int>();
            var tolerant = new FakeInput(CharacterScreenState.CharacterMenu);
            tolerant.TolerantRead = (frame, expected, current) => {
                Equal(CharacterScreenState.Constellation, expected, "Tolerant verification receives the exact expected pane");
                Equal(CharacterScreenState.CharacterMenu, current.State, "Tolerant verification retains the strict result from the same frame");
                fallbackFrames.Add(tolerant.Captures);
                return TolerantResult(expected);
            };
            tolerant.Begin();
            Equal(CharacterScreenState.Constellation, CharacterNavigation.Require(CharacterScreenState.Constellation, "two fresh tolerant pane votes").State,
                "Two consecutive tolerant matches may establish the requested pane");
            Equal(2, fallbackFrames.Count, "One tolerant vote cannot establish the pane");
            Check(fallbackFrames.SequenceEqual(new[] { 4, 5 }),
                "Tolerant recognition starts only after three strict misses and its votes come from distinct fresh captures.");
            Equal(0, tolerant.Keys.Count + tolerant.Clicks.Count, "Strict and tolerant confirmation issue no click, key or recovery input");
            CharacterNavigation.EndScan();

            fallbackFrames.Clear();
            var quorum = new FakeInput(CharacterScreenState.CharacterMenu);
            quorum.TolerantRead = (frame, expected, current) => {
                fallbackFrames.Add(quorum.Captures);
                return quorum.Captures == 5 ? null : TolerantResult(expected);
            };
            quorum.Begin();
            Equal(CharacterScreenState.Attributes, CharacterNavigation.Require(CharacterScreenState.Attributes, "one inconclusive capture between matching Attributes votes").State,
                "Two matching exact Attributes votes may survive one inconclusive second-pass capture");
            Check(fallbackFrames.SequenceEqual(new[] { 4, 5, 6 }), "The quorum uses only the existing three fresh second-pass captures.");
            Equal(0, quorum.Keys.Count + quorum.Clicks.Count, "An inconclusive vote between agreeing panes does not add navigation input");
            CharacterNavigation.EndScan();

            fallbackCalls = 0;
            var unqualifiedMenu = new FakeInput(CharacterScreenState.Unknown);
            unqualifiedMenu.TolerantRead = (frame, expected, current) => { ++fallbackCalls; return TolerantResult(CharacterScreenState.CharacterMenu); };
            unqualifiedMenu.Begin();
            NavigationFails(() => CharacterNavigation.Require(CharacterScreenState.CharacterMenu, "unqualified generic menu fallback"));
            Equal(3, fallbackCalls, "A generic sidebar candidate is checked only in the bounded second pass");
            Equal(0, unqualifiedMenu.Keys.Count + unqualifiedMenu.Clicks.Count, "Generic sidebar words alone cannot authorize character input");
            CharacterNavigation.EndScan();

            foreach (CharacterScreenState unsupported in new[] { CharacterScreenState.Gameplay, CharacterScreenState.PaimonMenu })
            {
                fallbackCalls = 0;
                var unsupportedExpected = new FakeInput(CharacterScreenState.Unknown);
                unsupportedExpected.TolerantRead = (frame, expected, current) => { ++fallbackCalls; return TolerantResult(expected); };
                unsupportedExpected.Begin();
                NavigationFails(() => CharacterNavigation.Require(unsupported, "outside screen expectations are strict " + unsupported));
                Equal(0, fallbackCalls, "Gameplay and Paimon expectations cannot use character-pane tolerance");
                Equal(0, unsupportedExpected.Keys.Count + unsupportedExpected.Clicks.Count, "Unsupported tolerant expectations send no input");
                CharacterNavigation.EndScan();
            }

            foreach (CharacterScreenState outside in new[] { CharacterScreenState.Gameplay, CharacterScreenState.PaimonMenu,
                CharacterScreenState.Talents, CharacterScreenState.ConstellationDetails })
            {
                fallbackCalls = 0;
                var guarded = new FakeInput(outside);
                guarded.TolerantRead = (frame, expected, current) => { ++fallbackCalls; return TolerantResult(expected); };
                guarded.Begin();
                NavigationFails(() => CharacterNavigation.Require(CharacterScreenState.Constellation, "positive conflicting state " + outside));
                Equal(0, fallbackCalls, "Positive gameplay, Paimon or a different exact pane/overlay cannot be relaxed: " + outside);
                Equal(0, guarded.Keys.Count + guarded.Clicks.Count, "Conflicting strict evidence pauses all input: " + outside);
                CharacterNavigation.EndScan();
            }

            fallbackCalls = 0;
            var alternating = new FakeInput(CharacterScreenState.CharacterMenu);
            alternating.TolerantRead = (frame, expected, current) => {
                ++fallbackCalls;
                return TolerantResult(fallbackCalls % 2 == 1 ? expected : CharacterScreenState.TalentDetails);
            };
            alternating.Begin();
            NavigationFails(() => CharacterNavigation.Require(CharacterScreenState.Attributes, "positive conflicting pane between matching Attributes votes"));
            Check(fallbackCalls >= 2, "The mismatch trace reaches the bounded tolerant pass.");
            Equal(0, alternating.Keys.Count + alternating.Clicks.Count, "Mismatched tolerant pane votes never authorize input");
            CharacterNavigation.EndScan();

            fallbackCalls = 0;
            var missedVotes = new FakeInput(CharacterScreenState.CharacterMenu);
            missedVotes.TolerantRead = (frame, expected, current) => {
                ++fallbackCalls;
                return missedVotes.Captures == 4 ? TolerantResult(expected) : null;
            };
            missedVotes.Begin();
            NavigationFails(() => CharacterNavigation.Require(CharacterScreenState.Attributes, "two inconclusive captures cannot confirm a single Attributes vote"));
            Equal(3, fallbackCalls, "Two misses exhaust the bounded second pass without a quorum");
            Equal(0, missedVotes.Keys.Count + missedVotes.Clicks.Count, "A lone first vote followed by two misses issues no input");
            CharacterNavigation.EndScan();

            fallbackCalls = 0;
            var siblingPanes = new FakeInput(CharacterScreenState.Unknown);
            siblingPanes.TolerantRead = (frame, expected, current) => {
                ++fallbackCalls;
                return TolerantResult(fallbackCalls % 2 == 1 ? CharacterScreenState.Constellation : CharacterScreenState.ConstellationDetails);
            };
            siblingPanes.Begin();
            NavigationFails(() => CharacterNavigation.Require(CharacterScreenState.CharacterMenu, "different qualifying pane and overlay votes"));
            Equal(3, fallbackCalls, "Each differing qualified character subtype starts a new single vote");
            Equal(0, siblingPanes.Keys.Count + siblingPanes.Clicks.Count, "Base-pane and detail votes cannot combine to authorize input");
            CharacterNavigation.EndScan();

            // Only the final capture can vote. It cannot borrow confirmation
            // from a prior strict capture or another screen verification.
            fallbackCalls = 0;
            var lastVote = new FakeInput(CharacterScreenState.CharacterMenu);
            lastVote.TolerantRead = (frame, expected, current) => {
                ++fallbackCalls;
                return lastVote.Captures == 6 ? TolerantResult(expected) : null;
            };
            lastVote.Begin();
            NavigationFails(() => CharacterNavigation.Require(CharacterScreenState.Constellation, "last single tolerant vote"));
            Check(fallbackCalls > 0 && lastVote.Captures == 6, "Tolerant verification remains bounded by six fresh captures.");
            Equal(0, lastVote.Keys.Count + lastVote.Clicks.Count, "A final unconfirmed tolerant vote issues no input");
            CharacterNavigation.EndScan();

            fallbackCalls = 0;
            var interrupted = new FakeInput(CharacterScreenState.CharacterMenu);
            interrupted.StateAtRead = capture => capture == 5 ? CharacterScreenState.Gameplay : CharacterScreenState.CharacterMenu;
            interrupted.TolerantRead = (frame, expected, current) => {
                ++fallbackCalls;
                return interrupted.Captures == 4 || interrupted.Captures == 6 ? TolerantResult(expected) : null;
            };
            interrupted.Begin();
            NavigationFails(() => CharacterNavigation.Require(CharacterScreenState.Constellation, "outside screen resets tolerant confirmation"));
            Check(interrupted.ObservedStates.Contains(CharacterScreenState.Gameplay), "The trace includes a fresh positive gameplay interruption.");
            Equal(0, interrupted.Keys.Count + interrupted.Clicks.Count, "Separated votes across an outside screen do not authorize input");
            CharacterNavigation.EndScan();

            fallbackCalls = 0;
            var lostFocus = new FakeInput(CharacterScreenState.CharacterMenu);
            lostFocus.TolerantRead = (frame, expected, current) => {
                ++fallbackCalls;
                lostFocus.FocusAvailable = false;
                return TolerantResult(expected);
            };
            lostFocus.Begin();
            NavigationFails(() => CharacterNavigation.Require(CharacterScreenState.Constellation, "focus lost between tolerant captures"));
            Equal(1, fallbackCalls, "Loss of focus prevents a second tolerant vote");
            Equal(0, lostFocus.Keys.Count + lostFocus.Clicks.Count, "Loss of focus cannot send fallback navigation input");
        }

        private static CharacterScreenRecognition TolerantResult(CharacterScreenState state)
        {
            return new CharacterScreenRecognition { State = state, Evidence = "Injected tolerant " + state };
        }

        private static void VerifyInitialEntry(bool transientUnknown, CharacterScreenState initial = CharacterScreenState.PaimonMenu,
            bool unknownAfterSlotOne = false)
        {
            var input = new FakeInput(initial);
            int settlingWaits = 0;
            bool slotSelectedFromGameplay = false;
            bool settlingGameplay = false;
            input.OnKey = key => {
                if (key == Navigation.escapeKey && input.State == CharacterScreenState.Unknown)
                    input.State = CharacterScreenState.PaimonMenu;
                else if (key == Navigation.escapeKey &&
                    (input.State == CharacterScreenState.PaimonMenu || input.State == CharacterScreenState.Attributes))
                {
                    input.State = transientUnknown ? CharacterScreenState.Unknown : CharacterScreenState.Gameplay;
                    settlingGameplay = transientUnknown;
                }
                else if (key == Navigation.slotOneKey)
                {
                    slotSelectedFromGameplay = input.State == CharacterScreenState.Gameplay;
                    if (unknownAfterSlotOne) input.State = CharacterScreenState.Unknown;
                }
                else if (key == Navigation.characterKey)
                    input.State = CharacterScreenState.Attributes;
                else if (key == Navigation.escapeKey && input.State == CharacterScreenState.Gameplay)
                    input.State = CharacterScreenState.PaimonMenu;
            };
            input.OnWait = milliseconds => {
                if (settlingGameplay && input.State == CharacterScreenState.Unknown && ++settlingWaits == 2)
                {
                    input.State = CharacterScreenState.Gameplay;
                    settlingGameplay = false;
                }
            };
            input.OnClick = () => {
                if (input.State == CharacterScreenState.PaimonMenu) input.State = CharacterScreenState.Attributes;
            };
            input.Begin();
            CharacterNavigation.OpenCharacterScreen("initial entry trace", true);
            var expected = new List<VirtualKeyCode>();
            if (initial == CharacterScreenState.Unknown) expected.Add(Navigation.escapeKey);
            expected.AddRange(new[] { Navigation.escapeKey, Navigation.slotOneKey, Navigation.characterKey });
            Check(input.Keys.SequenceEqual(expected),
                "Initial " + initial + " entry reaches gameplay, selects slot one, and sends the configured Character key without reopening Paimon.");
            Check(slotSelectedFromGameplay, "Party selection requires positive gameplay evidence.");
            Equal(0, input.Clicks.Count, "Initial entry needs no Paimon Character tile click");
            Equal(CharacterScreenState.Attributes, input.State, "Initial entry finishes on a verified character pane");
            if (unknownAfterSlotOne)
                Check(!input.ObservedStates.Contains(CharacterScreenState.Unknown),
                    "Switching to slot one proceeds directly to C; it cannot add another gameplay OCR gate.");
            if (transientUnknown)
            {
                Check(input.ObservedStates.Contains(CharacterScreenState.Unknown), "Transient unknown capture must be observed before settling.");
                Equal(2, settlingWaits, "Transient unknown settles by waiting instead of another input");
                Check(input.Captures <= 10 && input.Waits.Count <= 10, "Transition settling remains bounded.");
            }
            CharacterNavigation.EndScan();
        }

        private static void VerifyCharacterKeyRetries()
        {
            var dropped = new FakeInput(CharacterScreenState.Gameplay);
            dropped.OnKey = key => {
                if (key == Navigation.characterKey && dropped.Keys.Count(value => value == key) == 2)
                    dropped.State = CharacterScreenState.Attributes;
            };
            dropped.Begin();
            CharacterNavigation.OpenCharacterScreen("first C was dropped");
            Check(dropped.Keys.SequenceEqual(new[] { Navigation.characterKey, Navigation.characterKey }),
                "A dropped C retries once only while gameplay remains positively verified.");
            Equal(0, dropped.Clicks.Count, "Retrying C sends no character tile or detail clicks");
            CharacterNavigation.EndScan();

            var transition = new FakeInput(CharacterScreenState.Gameplay);
            int transitionWaits = 0;
            transition.OnKey = key => { if (key == Navigation.characterKey) transition.State = CharacterScreenState.Unknown; };
            transition.OnWait = milliseconds => {
                if (transition.State == CharacterScreenState.Unknown && ++transitionWaits == 2)
                    transition.State = CharacterScreenState.Attributes;
            };
            transition.Begin();
            CharacterNavigation.OpenCharacterScreen("character animation after C");
            Check(transition.Keys.SequenceEqual(new[] { Navigation.characterKey }), "An unknown animation after C settles without another key.");
            Check(transition.ObservedStates.Contains(CharacterScreenState.Unknown), "The transient unknown frame is actually observed.");
            Equal(0, transition.Clicks.Count, "Transient entry animation cannot authorize a detail or Next click");
            CharacterNavigation.EndScan();

            foreach (CharacterScreenState failureState in new[] { CharacterScreenState.Gameplay, CharacterScreenState.Unknown })
            {
                var failed = new FakeInput(CharacterScreenState.Gameplay);
                failed.OnKey = key => { if (key == Navigation.characterKey) failed.State = failureState; };
                failed.Begin();
                NavigationFails(() => CharacterNavigation.OpenCharacterScreen("persistent failed C"));
                int characterKeys = failed.Keys.Count(value => value == Navigation.characterKey);
                Check(characterKeys >= 1 && characterKeys <= 4,
                    "Persistent failed C remains bounded by two key attempts per entry attempt.");
                if (failureState == CharacterScreenState.Unknown)
                    Equal(1, characterKeys, "C cannot retry while its last result is an unverified screen");
                Check(!failed.Keys.Contains(Navigation.slotOneKey), "Recovery does not reset party slot one.");
                NavigationFails(() => CharacterNavigation.NextCharacter(.96, .48, "Next after failed C"));
                NavigationFails(() => CharacterNavigation.ClickDetail("detail after failed C", .88, .16, CharacterScreenState.TalentDetails));
                Equal(0, failed.Clicks.Count, "Failed entry never permits Next or ordinary character clicks");
                CharacterNavigation.EndScan();
            }
        }

        private static void UnknownHeaderEvidence()
        {
            const string sidebar = "Attributes\nWeapon\nArtifacts\nConstellation\nTalents\nProfile";
            foreach (string name in new[] { "Lucy", "Roger", "Unknown Avatar" })
                Check(CharacterScreenRecognizer.HasCharacterSidebar("Cryo / " + name + "\n" + sidebar),
                    "Unknown " + name + " does not prevent positive character-sidebar evidence.");
            Check(!CharacterScreenRecognizer.HasCharacterSidebar("Cryo / Lucy"), "A header crop alone cannot establish a character screen.");
            Check(!CharacterScreenRecognizer.HasCharacterSidebar("Attributes\nWeapon"), "Two incidental labels are insufficient for a character sidebar.");
            Check(CharacterScreenRecognizer.HasCharacterSidebar("Weapons\nArtifacts\nConstellation\nTalents"),
                "Four distinct sidebar labels still establish the menu when the highlighted Attributes label is unreadable.");
            Check(!CharacterScreenRecognizer.HasCharacterSidebar("Shop\nCharacter\nCharacter Archive\nAchievements\nInventory"), "Paimon menu text is not a character sidebar.");
        }

        private static void BlankFrameEvidence()
        {
            using (var image = new Bitmap(1280, 720))
            {
                using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.FromArgb(45, 45, 45));
                var result = CharacterScreenRecognizer.Read(image);
                Equal(CharacterScreenState.Unknown, result.State, "Uniform full client image lacks positive screen evidence: " + result.Evidence);
                Check(!result.IsCharacterMenu && !result.CharacterTileBounds.HasValue,
                    "A blank full frame cannot authorize character-menu or tile actions.");
            }
        }

        private static void RosterCounting()
        {
            string starting;
            int index = 0;
            string[] names = { "Vesna", "Xiangling", "Amber", "Vesna" };
            Equal((int?)3, CharacterScraper.CountRosterCycle(() => names[index], () => ++index, 6, out starting),
                "A verified natural cycle counts three unique character positions");
            Equal("Vesna", starting, "Natural cycle starting identity");
            Equal(null, CharacterScraper.CountRosterCycle(() => "Vesna", () => { }, 6, out starting),
                "A stalled Next click is not a completed roster cycle");
            index = 0;
            names = new[] { "Vesna", "Xiangling", null };
            Equal(null, CharacterScraper.CountRosterCycle(() => names[index], () => ++index, 6, out starting),
                "A lost-menu header must not establish a denominator");
            Equal(null, starting, "Interrupted traversal has no verified starting identity");
            BestEffortRosterCounting();
        }

        private static void BestEffortRosterCounting()
        {
            string starting = null;
            string[] names = { "Vesna", "Xiangling", "Amber", "Vesna" };
            int index = 0, preparations = 0, nextMoves = 0, restores = 0, recoveries = 0;
            Equal((int?)3, CharacterScraper.CountRosterBestEffort(() => ++preparations, () => names[index],
                () => { ++nextMoves; ++index; }, identity => { ++restores; Equal("Vesna", identity, "Exact count restoration origin"); return true; },
                identity => { ++recoveries; return false; }, 8, out starting), "A verified count and restored origin preserve the denominator");
            Equal("Vesna", starting, "Exact count retains the initial full-scan checkpoint");
            Equal(3, nextMoves, "Exact count makes one natural roster traversal");
            Equal(1, restores, "The initial checkpoint is verified after counting");
            Equal(0, recoveries, "A restored exact count needs no menu recovery");

            var failure = new CharacterNavigationException("read optional count header", CharacterScreenState.Unknown, null, "Regression count interruption");
            foreach (bool failDuringPreparation in new[] { true, false })
            {
                bool recovered = false;
                preparations = nextMoves = restores = recoveries = 0;
                Equal(null, CharacterScraper.CountRosterBestEffort(() => {
                    ++preparations;
                    if (!recovered && failDuringPreparation) throw failure;
                }, () => {
                    if (!recovered && !failDuringPreparation) throw failure;
                    return "Vesna";
                }, () => ++nextMoves, identity => { ++restores; return false; }, identity => {
                    ++recoveries;
                    Equal(null, identity, "Failure before the initial checkpoint must recover the verified initial ordering");
                    recovered = true;
                    return true;
                }, 8, out starting), "Initial verification failure abandons the optional count after one bounded recovery");
                Equal("Vesna", starting, "Initial recovery reacquires the starting full-scan checkpoint");
                Equal(2, preparations, "Initial count preparation and its single recovery remain within the same envelope");
                Equal(1, recoveries, "Initial count verification receives one recovery attempt");
                Equal(0, nextMoves + restores, "Failed initial verification never begins a count traversal or advances the roster");
            }

            foreach (string blank in new[] { null, "  " })
            {
                nextMoves = restores = recoveries = 0;
                Equal(null, CharacterScraper.CountRosterBestEffort(() => { }, () => blank, () => ++nextMoves,
                    identity => { ++restores; return false; }, identity => { ++recoveries; return false; }, 8, out starting),
                    "An unreadable initial header proceeds without a count denominator");
                Equal(null, starting, "Blank initial evidence cannot fabricate a starting identity");
                Equal(0, nextMoves + restores + recoveries, "A blank initial header leaves the current roster position unchanged");
            }

            foreach (bool restoreSucceeds in new[] { true, false })
            {
                index = 0;
                nextMoves = restores = recoveries = preparations = 0;
                Equal(null, CharacterScraper.CountRosterBestEffort(() => ++preparations,
                    () => { if (index == 1) throw failure; return names[index]; },
                    () => { ++nextMoves; ++index; }, identity => {
                        ++restores;
                        Equal("Vesna", identity, "Interrupted count must restore the original checkpoint");
                        if (restoreSucceeds) index = 0;
                        return restoreSucceeds;
                    }, identity => {
                        ++recoveries;
                        Equal("Vesna", identity, "Failed count restoration receives the exact original identity");
                        index = 0;
                        return true;
                    }, 8, out starting), "Interrupted count restores its origin and continues without recounting");
                Equal("Vesna", starting, "Interrupted count retains the exact full-scan checkpoint");
                Equal(1, nextMoves, "A failed optional pass is not retried after restoration or recovery");
                Equal(1, restores, "Interrupted count attempts one exact origin restoration");
                Equal(restoreSucceeds ? 0 : 1, recoveries, "Menu recovery occurs only when the origin cannot be restored directly");
                Equal(0, index, "The full scan resumes from the original roster position");
            }

            // Reproduce the reported leak: the optional count returns no total,
            // then its final restoration read throws on a correctly open pane.
            index = nextMoves = restores = recoveries = preparations = 0;
            Equal(null, CharacterScraper.CountRosterBestEffort(() => ++preparations,
                () => index == 0 ? "Vesna" : null,
                () => { ++nextMoves; ++index; }, identity => {
                    ++restores;
                    Equal("Vesna", identity, "Failed final restoration keeps the original checkpoint");
                    throw failure;
                }, identity => {
                    ++recoveries;
                    Equal("Vesna", identity, "Final restoration exception recovers the original identity");
                    index = 0;
                    return true;
                }, 8, out starting), "A final restoration exception is recovered instead of escaping the optional count");
            Equal("Vesna", starting, "Final restoration failure retains the starting character for the full scan");
            Equal(1, nextMoves, "Final restoration failure cannot restart the optional count");
            Equal(1, restores, "One failed final restoration is attempted");
            Equal(1, recoveries, "The leaked final restoration exception receives one bounded recovery");
            Equal(2, preparations, "Recovery re-verifies Attributes before continuing the actual scan");
            Equal(0, index, "The actual scan resumes at the verified original character");

            for (int mode = 0; mode < 3; ++mode)
            {
                index = nextMoves = restores = recoveries = 0;
                int failureMode = mode;
                NavigationFails(() => CharacterScraper.CountRosterBestEffort(() => { }, () => names[index],
                    () => { ++nextMoves; ++index; }, identity => { ++restores; return false; }, identity => {
                        ++recoveries;
                        Equal("Vesna", identity, "Unverified count recovery must preserve the initial checkpoint");
                        if (failureMode == 0) return false;
                        if (failureMode == 1) throw new InvalidDataException("Regression generic count recovery failure");
                        index = 1;
                        return true;
                    }, 8, out starting));
                Equal("Vesna", starting, "Failed recovery cannot replace the full-scan origin with a rotated character");
                Equal(3, nextMoves, "Failed origin recovery cannot start another counting pass");
                Equal(1, restores, "Failed origin recovery follows one restoration attempt");
                Equal(1, recoveries, "False, exceptional or wrong-origin recovery is bounded and stops character actions");
            }
        }

        private static void ResumeCheckpoint()
        {
            int index = 0, moves;
            string[] names = { "Vesna", "Xiangling" };
            Check(CharacterScraper.RestoreRosterPosition("Xiangling", () => names[index], () => ++index, 8, out moves),
                "Reopening on Vesna restores interrupted Xiangling rather than declaring a completed cycle.");
            Equal(1, moves, "Restore requires one roster move from first party character");
            Equal("Xiangling", names[index], "Effective current character after recovery");
            Check(CharacterScraper.RestoreRosterPosition("Xiangling", () => "Xiangling", () => { throw new InvalidOperationException("Unexpected Next"); }, 8, out moves),
                "An already restored checkpoint needs no navigation.");
            Equal(0, moves, "Already restored checkpoint does not count a move");

            index = 0;
            names = new[] { "Vesna", "Amber", "Vesna" };
            Check(!CharacterScraper.RestoreRosterPosition("Xiangling", () => names[index], () => ++index, 8, out moves),
                "A full roster cycle without the exact interrupted character fails recovery.");
            Equal(2, moves, "Missing checkpoint is bounded by positive cycle evidence");
            int clicks = 0;
            Check(!CharacterScraper.RestoreRosterPosition("Xiangling", () => "Vesna", () => ++clicks, 8, out moves),
                "Repeated lost Next clicks cannot fabricate resume success.");
            Equal(3, clicks, "Initial Next plus two retry clicks exhaust the stalled selection budget");
            Equal(clicks, moves, "Stalled restore reports only actual attempted roster moves");
            clicks = 0;
            Check(!CharacterScraper.RestoreRosterPosition("Xiangling", () => null, () => ++clicks, 4, out moves),
                "Missing header evidence cannot fabricate resume success.");
            Equal(4, clicks, "Unreadable checkpoint restoration remains bounded");

            var failure = new InvalidDataException("Lost menu while restoring checkpoint");
            bool propagated = false;
            try { CharacterScraper.RestoreRosterPosition("Xiangling", () => { throw failure; }, () => { }, 8, out moves); }
            catch (InvalidDataException exception) { propagated = ReferenceEquals(exception, failure); }
            Check(propagated, "A navigation/read exception propagates rather than pretending a checkpoint was restored.");
        }

        private static void FailedCharacterRecovery()
        {
            var failure = new CharacterNavigationException("read Yelan constellation", CharacterScreenState.CharacterMenu,
                null, "Regression detail read failure");
            var input = new FakeInput(CharacterScreenState.Constellation);
            input.OnClick = () => input.State = CharacterScreenState.Attributes;
            input.Begin();
            int attempts = 0, recoveries = 0, checkpoints = 0, skipPreparations = 0, moves;
            bool skipped;
            Character omitted = CharacterScraper.ScanCharacterWithRecovery(checkpoint => {
                ++attempts;
                checkpoint("Yelan");
                throw failure;
            }, "Xiangling", identity => {
                Equal("Yelan", identity, "Only the effective failed character may become the omission checkpoint");
                ++checkpoints;
            }, (identity, advance) => {
                Equal("Yelan", identity, "Recovery restores the exact known failed character");
                Check(!advance, "Recovery cannot advance past an already known failed character.");
                ++recoveries;
                return true;
            }, identity => {
                ++skipPreparations;
                CharacterNavigation.SelectPane(CharacterScreenState.Attributes, .12, .14, "prepare regression safe omission");
                CharacterNavigation.Require(CharacterScreenState.Attributes, "verify Attributes before omission");
                return CharacterScraper.RestoreRosterPosition(identity, () => "Yelan",
                    () => { throw new InvalidOperationException("An exact omission checkpoint must not click Next."); }, 8, out moves);
            }, out skipped);
            Check(skipped, "Exhausted detail attempts may omit an exactly verified known character.");
            Equal(3, attempts, "At most three attempts are made for one failed character");
            Equal(2, recoveries, "The third failure cannot initiate an unbounded fourth scan attempt");
            Equal(1, skipPreparations, "Exact Attributes/checkpoint verification occurs once before omission");
            Equal(4, checkpoints, "Retries do not fabricate a new character identity; the verified omission retains the same checkpoint");
            Equal(CharacterScreenState.Attributes, input.State, "Safe omission leaves the verified Attributes pane");
            Equal(1, input.Clicks.Count, "Only the verified Attributes tab is clicked; omission itself cannot click Next");
            Equal(0, input.Keys.Count, "Safe omission needs no Escape from an outside screen");
            Check(omitted != null && omitted.NameGOOD == null && omitted.Level == 0
                && omitted.Talents.Values.All(level => level == 0) && !omitted.IsValid(),
                "Omission returns no fabricated character name, level or talents for export.");
            CharacterScraper.BeginRosterCoverage();
            CharacterScraper.RecordSkippedIdentity("Yelan");
            CharacterScraper.RecordSkippedIdentity("Yelan");
            CharacterScraper.FinalizeRosterCoverage(true);
            Check(!CharacterScraper.LastScanComplete, "A verified roster wrap with an omitted character remains partial.");
            Equal(1, CharacterScraper.LastSkippedCount, "Retries of the same omitted checkpoint count as one omission");
            Check(CharacterScraper.LastSkippedIdentities.SequenceEqual(new[] { "Yelan" }), "Partial coverage retains the exact skipped identity.");
            CharacterScraper.BeginRosterCoverage();
            Equal(0, CharacterScraper.LastSkippedCount, "A new scan clears prior omission state");
            Check(!CharacterScraper.LastScanComplete, "Beginning a scan cannot mark its character phase complete.");
            CharacterScraper.FinalizeRosterCoverage(false);
            Check(!CharacterScraper.LastScanComplete, "No omissions alone cannot complete an unverified roster cycle.");
            CharacterScraper.FinalizeRosterCoverage(true);
            Check(CharacterScraper.LastScanComplete, "A verified cycle with zero omissions completes the character phase.");
            CharacterScraper.BeginRosterCoverage();
            CharacterNavigation.EndScan();

            var expected = new Character { NameGOOD = "Yelan" };
            attempts = recoveries = skipPreparations = 0;
            Character completed = CharacterScraper.ScanCharacterWithRecovery(checkpoint => {
                checkpoint("Yelan");
                if (++attempts == 1) throw failure;
                return expected;
            }, "Xiangling", identity => { }, (identity, advance) => { ++recoveries; return true; },
                identity => { ++skipPreparations; return true; }, out skipped);
            Check(ReferenceEquals(expected, completed) && !skipped, "A successful retry returns the actual character result without omission.");
            Equal(2, attempts, "One recoverable failed read is followed by its successful retry");
            Equal(1, recoveries, "A successful retry performs one checkpoint recovery");
            Equal(0, skipPreparations, "Successful character reads do not invoke skip preparation");

            foreach (string driftedIdentity in new[] { "Xiangling", null })
            {
                attempts = recoveries = skipPreparations = 0;
                var observedCheckpoints = new List<string>();
                NavigationFails(() => CharacterScraper.ScanCharacterWithRecovery(checkpoint => {
                    if (++attempts == 1) { checkpoint("Yelan"); throw failure; }
                    checkpoint(driftedIdentity);
                    return new Character { NameGOOD = "Xiangling" };
                }, "Xiangling", observedCheckpoints.Add, (identity, advance) => {
                    Equal("Yelan", identity, "A drifted retry must retain the original interrupted checkpoint");
                    Check(!advance, "A drifted retry cannot advance past the original checkpoint.");
                    ++recoveries;
                    return true;
                }, identity => {
                    Equal("Yelan", identity, "Failed drift recovery may only verify omission of the original checkpoint");
                    ++skipPreparations;
                    return false;
                }, out skipped));
                Equal(3, attempts, "Different or unreadable retry identities remain bounded");
                Equal(2, recoveries, "Drifted retries receive no more than two original-checkpoint restores");
                Equal(1, skipPreparations, "Exhausted drift still requires exact-checkpoint verification before omission");
                Check(observedCheckpoints.SequenceEqual(new[] { "Yelan" }),
                    "A changed or lost retry header cannot overwrite the external checkpoint or export a different character.");
            }

            attempts = recoveries = skipPreparations = 0;
            NavigationFails(() => CharacterScraper.ScanCharacterWithRecovery(checkpoint => {
                ++attempts;
                throw failure;
            }, "Xiangling", identity => { throw new InvalidOperationException("An unreadable header cannot establish a checkpoint."); },
                (identity, advance) => {
                    Equal("Xiangling", identity, "A failure before the header restores the previous verified roster position");
                    Check(advance, "An unreadable next position is reacquired only after restoring and advancing the previous checkpoint.");
                    ++recoveries;
                    return true;
                }, identity => { ++skipPreparations; return true; }, out skipped));
            Equal(3, attempts, "Unreadable current identities remain bounded despite menu recovery");
            Equal(2, recoveries, "An unreadable slot receives at most two checkpoint recovery attempts");
            Equal(0, skipPreparations, "A failed slot without a known current identity cannot be omitted or fabricated");

            foreach (bool hasCheckpoint in new[] { false, true })
            {
                attempts = recoveries = skipPreparations = 0;
                NavigationFails(() => CharacterScraper.ScanCharacterWithRecovery(checkpoint => {
                    ++attempts;
                    if (hasCheckpoint) checkpoint("Yelan");
                    throw failure;
                }, null, identity => { }, (identity, advance) => { ++recoveries; return false; },
                    identity => { ++skipPreparations; return false; }, out skipped));
                Equal(1, attempts, "An unrestored checkpoint stops detail scans rather than reading another character");
                Equal(1, recoveries, "Failed checkpoint recovery is bounded before safe omission is considered");
                Equal(hasCheckpoint ? 1 : 0, skipPreparations,
                    "Unknown identities cannot be omitted; known identities still require successful skip verification");
            }

            // Positive menu evidence alone is insufficient when the Attributes
            // pane cannot be reached. Do not advance/export the failed slot.
            var outside = new FakeInput(CharacterScreenState.Gameplay);
            outside.Begin();
            NavigationFails(() => CharacterScraper.ScanCharacterWithRecovery(checkpoint => {
                checkpoint("Yelan");
                throw failure;
            }, "Xiangling", identity => { }, (identity, advance) => false,
                identity => {
                    CharacterNavigation.Require(CharacterScreenState.Attributes, "unsafe omission outside character menu");
                    return true;
                }, out skipped));
            Equal(0, outside.Clicks.Count + outside.Keys.Count, "Unsafe omission from gameplay sends no Next, tab click or Escape");

            // Omission compacts the export list, but must not move a nonparty
            // character into Tartaglia's original four party positions.
            var partyMember = new Character { NameGOOD = "Yelan", Level = 90 };
            var tartaglia = new Character { NameGOOD = "Tartaglia", Level = 90 };
            var nonpartyMember = new Character { NameGOOD = "Xiangling", Level = 90 };
            var retained = new List<Character> { partyMember, tartaglia, nonpartyMember };
            foreach (Character character in retained) character.Talents["auto"] = 7;
            var originalPositions = new Dictionary<Character, int> {
                [partyMember] = 0, [tartaglia] = 2, [nonpartyMember] = 4
            };
            CharacterScraper.ApplyTartagliaTalentCorrection(retained, originalPositions);
            Equal(6, partyMember.Talents["auto"], "Retained original party member receives the passive correction");
            Equal(6, tartaglia.Talents["auto"], "Original party Tartaglia receives the passive correction");
            Equal(7, nonpartyMember.Talents["auto"], "Skipped slots cannot shift an outside character into the party correction");
            foreach (Character character in retained) character.Talents["auto"] = 7;
            originalPositions[tartaglia] = 4;
            originalPositions[nonpartyMember] = 5;
            CharacterScraper.ApplyTartagliaTalentCorrection(retained, originalPositions);
            Equal(7, partyMember.Talents["auto"], "Outside-party Tartaglia cannot correct a compacted export party");
            Equal(6, tartaglia.Talents["auto"], "Outside-party Tartaglia still corrects his own passive");
            Equal(7, nonpartyMember.Talents["auto"], "An unrelated retained character stays unmodified");
        }

        private static void CaptureReplay(string captures, string paimon)
        {
            if (captures != null)
            {
                string detail = Path.Combine(captures, "Xiangling", "constellation_6.png");
                string lostHeader = Path.Combine(captures, "44205267.png");
                RequireFile(detail); RequireFile(lostHeader);
                using (var image = new Bitmap(detail))
                {
                    var result = CharacterScreenRecognizer.Read(image);
                    Equal(CharacterScreenState.ConstellationDetails, result.State, "Saved Xiangling C6 detail: " + result.Evidence);
                    Check(result.IsCharacterMenu, "Constellation detail remains within the character menu.");
                }
                string lockedDetail = Path.Combine(captures, "Sigewinne", "constellation_1.png");
                RequireFile(lockedDetail);
                using (var image = new Bitmap(lockedDetail))
                {
                    var result = CharacterScreenRecognizer.Read(image);
                    Equal(CharacterScreenState.ConstellationDetails, result.State, "Saved locked constellation with a long title: " + result.Evidence);
                }
                using (var image = new Bitmap(lostHeader))
                    Check(!CharacterScreenRecognizer.Read(image).IsCharacterMenu, "Saved overworld HUD header cannot be treated as a character screen.");
            }
            if (paimon != null)
            {
                RequireFile(paimon);
                using (var image = new Bitmap(paimon))
                {
                    var result = CharacterScreenRecognizer.Read(image);
                    Equal(CharacterScreenState.PaimonMenu, result.State, "Saved Paimon menu: " + result.Evidence);
                    Check(result.CharacterTileBounds.HasValue && result.CharacterTileBounds.Value.Width > 0,
                        "Saved Paimon menu identifies a usable Character tile: " + result.Evidence);
                }
            }
        }

        private static void NavigationCaptureReplay(string captures)
        {
            string[] gameplay = Directory.GetFiles(captures, "*_Unknown.png")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
            string[] paimon = Directory.GetFiles(captures, "*_PaimonMenu.png")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
            Check(gameplay.Length > 0 && paimon.Length > 0, "Navigation replay requires the saved world frames marked Unknown and their Paimon entry frame.");
            foreach (string path in gameplay)
            {
                using (var image = new Bitmap(path))
                {
                    var result = CharacterScreenRecognizer.Read(image);
                    Equal(CharacterScreenState.Gameplay, result.State,
                        "Saved world frame " + Path.GetFileName(path) + " must establish gameplay: " + result.Evidence);
                    Check(!result.IsCharacterMenu && !result.CharacterTileBounds.HasValue,
                        "Gameplay HUD is neither a character pane nor a Paimon Character tile.");
                    TolerantRejects(image, result, CharacterScreenState.Constellation, "Saved gameplay cannot become a tolerant character pane");
                }
            }
            foreach (string path in paimon)
            {
                using (var image = new Bitmap(path))
                {
                    var result = CharacterScreenRecognizer.Read(image);
                    Equal(CharacterScreenState.PaimonMenu, result.State, "Saved initial Paimon menu " + Path.GetFileName(path) + ": " + result.Evidence);
                    Check(result.State != CharacterScreenState.Gameplay && !result.IsCharacterMenu,
                        "Paimon overlay cannot authorize a gameplay party hotkey or ordinary character-pane click.");
                    Check(result.CharacterTileBounds.HasValue && result.CharacterTileBounds.Value.Width > 0,
                        "Saved Paimon overlay must identify its Character tile: " + result.Evidence);
                    TolerantRejects(image, result, CharacterScreenState.Constellation, "Saved Paimon cannot become a tolerant character pane");
                }
            }

            // Feed actual images through both the controller and real screen
            // recognizer. Only the final menu after C is a synthetic success.
            foreach (string path in gameplay)
            {
                using (var input = new SavedEntryInput(paimon[0], path))
                {
                    CharacterNavigation.BeginScan(input, input.Read);
                    CharacterNavigation.OpenCharacterScreen("saved bitmap initial entry " + Path.GetFileName(path), true);
                    Check(input.Keys.SequenceEqual(new[] { Navigation.escapeKey, Navigation.slotOneKey, Navigation.characterKey }),
                        "Actual world capture must continue through Escape, 1, C without reopening Paimon: " + Path.GetFileName(path));
                    Check(input.RealPaimonReads > 0 && input.RealWorldReads > 0,
                        "Actual Paimon and world bitmaps must pass through the real recognizer during entry.");
                    Check(input.CharacterOpened && input.FinalMenuReads > 0, "Controller must verify its final Character state after C.");
                    Equal(0, input.Clicks, "Direct initial entry performs no Paimon tile or ordinary character clicks");
                    Console.WriteLine("  REPLAY {0}: real Paimon/world -> Escape, 1, C -> verified Character", Path.GetFileName(path));
                    CharacterNavigation.EndScan();
                }
            }
            Console.WriteLine("  Saved navigation coverage: {0} world frames, {1} Paimon frames, {0} controller bitmap replays.", gameplay.Length, paimon.Length);
        }

        private static void PaneCaptureReplay(string captures)
        {
            string[] panes = Directory.GetFiles(captures, "*_CharacterMenu.png")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
            string[] details = Directory.GetFiles(captures, "negative-pane_*_ConstellationDetails.png")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
            Check(panes.Length > 0 && details.Length > 0,
                "Pane replay requires the saved glowing constellation panes and negative detail-overlay frames.");
            foreach (string path in panes)
            {
                using (var input = new SavedPaneInput(path) { TolerantRejectedExpected = CharacterScreenState.Talents })
                {
                    CharacterNavigation.BeginScan(input, input.Read);
                    CharacterNavigation.SelectPane(CharacterScreenState.Constellation, .12, .33, "saved Yelan constellation " + Path.GetFileName(path));
                    Equal(CharacterScreenState.Constellation, input.LastRecognition.State,
                        "Saved glowing constellation pane " + Path.GetFileName(path) + ": " + input.LastRecognition.Evidence);
                    // Repeat against the same real bitmap; a verified open pane
                    // must not be clicked again or enter screen recovery.
                    if (path == panes[0])
                        CharacterNavigation.SelectPane(CharacterScreenState.Constellation, .12, .33, "repeat saved Yelan constellation");
                    Equal(0, input.Clicks + input.Keys.Count, "An already-open constellation pane needs no click or recovery input");
                    Equal(path == panes[0] ? 2 : 1, input.Reads, "An open pane returns after one real recognition per selection");
                    Console.WriteLine("  REPLAY {0}: {1}; no extra input", Path.GetFileName(path), input.LastRecognition.State);
                    CharacterNavigation.EndScan();
                }
            }
            // Remove the lower three medallions in memory. The real strict
            // classifier now sees an inconclusive pane; the selected tab and
            // three independent remaining rings can support the second pass.
            using (var source = new Bitmap(panes[0]))
            using (var degraded = (Bitmap)source.Clone())
            {
                using (var graphics = Graphics.FromImage(degraded))
                using (var brush = new SolidBrush(Color.FromArgb(20, 25, 50)))
                    graphics.FillRectangle(brush, new Rectangle(
                        (int)Math.Round(1430 * degraded.Width / 1920.0), (int)Math.Round(560 * degraded.Height / 1080.0),
                        (int)Math.Round(440 * degraded.Width / 1920.0), (int)Math.Round(340 * degraded.Height / 1080.0)));
                var strict = CharacterScreenRecognizer.Read(degraded);
                Equal(CharacterScreenState.CharacterMenu, strict.State, "Three surviving medallions remain inconclusive in the strict pass: " + strict.Evidence);
                var tolerant = CharacterScreenRecognizer.ReadTolerant(degraded, CharacterScreenState.Constellation, strict);
                Check(tolerant != null, "The tolerant pass must return evidence for the selected three-ring constellation pane.");
                Equal(CharacterScreenState.Constellation, tolerant.State, "Selected three-ring constellation second pass: " + tolerant.Evidence);
                TolerantRejects(degraded, strict, CharacterScreenState.Talents, "Three constellation rings cannot establish Talents");
                TolerantRejects(degraded, strict, CharacterScreenState.Attributes, "Three constellation rings cannot establish Attributes");
                using (var input = new SavedPaneInput(degraded))
                {
                    CharacterNavigation.BeginScan(input, input.Read, input.ReadTolerant);
                    Equal(CharacterScreenState.Constellation,
                        CharacterNavigation.Require(CharacterScreenState.Constellation, "saved three-ring Yelan second pass").State,
                        "Two fresh tolerant bitmap confirmations establish the degraded pane");
                    Check(input.TolerantFrames.SequenceEqual(new[] { 4, 5 }), "Degraded bitmap fallback requires three strict misses and two fresh confirmations.");
                    Equal(0, input.Clicks + input.Keys.Count, "Degraded pane verification does not click, Escape or recover");
                    Console.WriteLine("  REPLAY degraded Yelan: three rings -> two fresh tolerant confirmations; no input");
                    CharacterNavigation.EndScan();
                }
            }
            foreach (string path in details)
            {
                using (var input = new SavedPaneInput(path) { TolerantRejectedExpected = CharacterScreenState.Constellation })
                {
                    CharacterNavigation.BeginScan(input, input.Read);
                    NavigationFails(() => CharacterNavigation.SelectPane(CharacterScreenState.Constellation, .12, .33,
                        "saved constellation detail is not the base pane " + Path.GetFileName(path)));
                    Equal(CharacterScreenState.ConstellationDetails, input.LastRecognition.State,
                        "Saved detail overlay must retain its specific state: " + input.LastRecognition.Evidence);
                    Equal(0, input.Clicks + input.Keys.Count, "An open detail overlay cannot authorize a base-pane click or Escape");
                    Console.WriteLine("  REPLAY {0}: detail overlay remains distinct from the base pane", Path.GetFileName(path));
                    CharacterNavigation.EndScan();
                }
            }
            Console.WriteLine("  Saved pane coverage: {0} glowing constellation panes, {1} negative detail overlays.", panes.Length, details.Length);
        }

        private static void CountCaptureReplay(string captures)
        {
            string[] attributes = Directory.GetFiles(captures, "*_Attributes.png")
                .Concat(Directory.GetFiles(captures, "*_Unknown.png"))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
            string[] paimon = Directory.GetFiles(captures, "*_PaimonMenu.png");
            Check(attributes.Length > 0 && attributes.Any(path => path.EndsWith("_Unknown.png", StringComparison.Ordinal)) && paimon.Length > 0,
                "Count replay requires its saved Attributes entry, failed Unknown checkpoint and Paimon frame.");
            foreach (string path in attributes)
            {
                using (var input = new SavedPaneInput(path))
                {
                    CharacterNavigation.BeginScan(input, input.Read, input.ReadTolerant);
                    var result = CharacterNavigation.Require(CharacterScreenState.Attributes, "saved count checkpoint " + Path.GetFileName(path));
                    Equal(CharacterScreenState.Attributes, result.State, "Saved count frame must qualify Attributes: " + result.Evidence);
                    bool strictVerified = input.LastRecognition.State == CharacterScreenState.Attributes && input.Reads <= 6;
                    int agreeingVotes = input.TolerantStates.Count(state => state == CharacterScreenState.Attributes);
                    Check(strictVerified || agreeingVotes >= 2 && input.TolerantFrames.Distinct().Count() == input.TolerantFrames.Count && input.Reads <= 6,
                        "Count checkpoint requires strict evidence or two agreeing fresh Attributes votes in the bounded second pass.");
                    Equal(0, input.Clicks + input.Keys.Count, "Attributes verification must precede Next, pane clicks, tile clicks or keys");
                    Console.WriteLine("  REPLAY {0}: Attributes verified by {1}; no roster input", Path.GetFileName(path),
                        strictVerified ? "strict evidence" : "two fresh tolerant confirmations");
                    CharacterNavigation.EndScan();
                }
            }
            foreach (string path in paimon)
            {
                using (var frame = new Bitmap(path))
                {
                    var strict = CharacterScreenRecognizer.Read(frame);
                    Equal(CharacterScreenState.PaimonMenu, strict.State, "Saved count-entry Paimon frame stays outside the character roster: " + strict.Evidence);
                    TolerantRejects(frame, strict, CharacterScreenState.Attributes, "Count-entry Paimon cannot authorize an Attributes checkpoint");
                }
            }
            Console.WriteLine("  Saved count coverage: {0} Attributes frames, {1} Paimon guard frames.", attributes.Length, paimon.Length);
        }

        private static void TolerantRejects(Bitmap frame, CharacterScreenRecognition strict, CharacterScreenState expected, string description)
        {
            var tolerant = CharacterScreenRecognizer.ReadTolerant(frame, expected, strict);
            Check(tolerant == null || tolerant.State != expected, description + ": " + tolerant?.Evidence);
            if (strict.State != CharacterScreenState.Unknown && strict.State != CharacterScreenState.CharacterMenu)
                Check(ReferenceEquals(strict, tolerant), "Specific strict evidence must remain unchanged: " + description);
        }

        private sealed class SavedPaneInput : ICharacterNavigationInput, IDisposable
        {
            private readonly Bitmap image;
            internal int Reads, Clicks;
            internal CharacterScreenRecognition LastRecognition;
            internal CharacterScreenState? TolerantRejectedExpected;
            internal readonly List<int> TolerantFrames = new List<int>();
            internal readonly List<CharacterScreenState> TolerantStates = new List<CharacterScreenState>();
            internal readonly List<VirtualKeyCode> Keys = new List<VirtualKeyCode>();

            internal SavedPaneInput(string path) { image = new Bitmap(path); }
            internal SavedPaneInput(Bitmap frame) { image = (Bitmap)frame.Clone(); }
            public Bitmap Capture() { return (Bitmap)image.Clone(); }
            public bool Refocus(out string reason) { reason = null; return true; }
            public void Wait(int milliseconds) { }
            public void Click(double x, double y)
            {
                ++Clicks;
                throw new InvalidOperationException("Saved open-pane replay must not click or recover.");
            }
            public void PressKey(VirtualKeyCode key)
            {
                Keys.Add(key);
                throw new InvalidOperationException("Saved open-pane replay must not press keys or recover.");
            }
            internal CharacterScreenRecognition Read(Bitmap frame)
            {
                ++Reads;
                LastRecognition = CharacterScreenRecognizer.Read(frame);
                if (TolerantRejectedExpected.HasValue)
                    TolerantRejects(frame, LastRecognition, TolerantRejectedExpected.Value, "Wrong expected pane for saved " + LastRecognition.State);
                return LastRecognition;
            }
            internal CharacterScreenRecognition ReadTolerant(Bitmap frame, CharacterScreenState expected, CharacterScreenRecognition strict)
            {
                TolerantFrames.Add(Reads);
                var result = CharacterScreenRecognizer.ReadTolerant(frame, expected, strict);
                TolerantStates.Add(result?.State ?? CharacterScreenState.Unknown);
                return result;
            }
            public void Dispose() { image.Dispose(); }
        }

        private sealed class SavedEntryInput : ICharacterNavigationInput, IDisposable
        {
            private readonly Bitmap paimon, world;
            private bool paimonVisible = true;
            internal bool CharacterOpened;
            internal int RealPaimonReads, RealWorldReads, FinalMenuReads, Clicks;
            internal readonly List<VirtualKeyCode> Keys = new List<VirtualKeyCode>();

            internal SavedEntryInput(string paimonPath, string worldPath)
            {
                paimon = new Bitmap(paimonPath);
                world = new Bitmap(worldPath);
            }
            public Bitmap Capture()
            {
                if (CharacterOpened) return new Bitmap(world.Width, world.Height);
                return (Bitmap)(paimonVisible ? paimon : world).Clone();
            }
            public bool Refocus(out string reason) { reason = null; return true; }
            public void Wait(int milliseconds) { }
            public void Click(double x, double y)
            {
                ++Clicks;
                throw new InvalidOperationException("Saved initial-entry replay must use C instead of any tile/detail click.");
            }
            public void PressKey(VirtualKeyCode key)
            {
                Keys.Add(key);
                if (key == Navigation.escapeKey) paimonVisible = !paimonVisible;
                else if (key == Navigation.slotOneKey) Check(!paimonVisible && !CharacterOpened, "Slot one requires the real gameplay stage.");
                else if (key == Navigation.characterKey)
                {
                    Check(!paimonVisible, "C must follow the real gameplay stage.");
                    CharacterOpened = true;
                }
                else throw new InvalidOperationException("Unexpected entry key in saved-frame replay: " + key);
            }
            internal CharacterScreenRecognition Read(Bitmap frame)
            {
                if (CharacterOpened)
                {
                    ++FinalMenuReads;
                    return new CharacterScreenRecognition { State = CharacterScreenState.Attributes,
                        Evidence = "Synthetic final Character menu after configured C; all preceding captures use the real recognizer." };
                }
                if (paimonVisible) ++RealPaimonReads;
                else ++RealWorldReads;
                return CharacterScreenRecognizer.Read(frame);
            }
            public void Dispose() { paimon.Dispose(); world.Dispose(); }
        }

        private static void NavigationFails(Action action)
        {
            try { action(); }
            catch (CharacterNavigationException) { return; }
            throw new InvalidOperationException("Expected a character navigation exception before any unsupported action.");
        }
        private static void RequireFile(string path) { if (!File.Exists(path)) throw new FileNotFoundException("Required saved regression frame was not found.", path); }
        private static void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); }
        private static void Equal(object expected, object actual, string description)
        {
            if (!Equals(expected, actual)) throw new InvalidOperationException(description + ": expected " + (expected ?? "unknown") + ", actual " + (actual ?? "unknown"));
        }
    }
}
