using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using WindowsInput.Native;

namespace InventoryKamera
{
    internal interface ICharacterNavigationInput
    {
        Bitmap Capture();
        bool Refocus(out string reason);
        void Click(double x, double y);
        void PressKey(VirtualKeyCode key);
        void Wait(int milliseconds);
    }

    internal sealed class CharacterNavigationException : InvalidOperationException
    {
        public string Context { get; }
        public CharacterScreenState ObservedState { get; }
        public string EvidencePath { get; }

        public CharacterNavigationException(string context, CharacterScreenState observed, string evidencePath, string reason)
            : base("Character screen could not be verified during " + context + ": " + reason
                + " (state " + observed + ")." + (evidencePath == null ? "" : " Evidence: " + evidencePath))
        {
            Context = context;
            ObservedState = observed;
            EvidencePath = evidencePath;
        }
    }

    /// <summary>Character actions require positive screen evidence. Recovery never advances the roster.</summary>
    internal static class CharacterNavigation
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private const int MaximumRecoveries = 3;
        private const int MaximumEvidence = 40;
        private static ICharacterNavigationInput input = new NativeCharacterInput();
        private static Func<Bitmap, CharacterScreenRecognition> readScreen = CharacterScreenRecognizer.Read;
        private static Func<Bitmap, CharacterScreenState, CharacterScreenRecognition, CharacterScreenRecognition>
            readTolerant = CharacterScreenRecognizer.ReadTolerant;
        private static int recoveries, evidenceCount;
        private static string lastProgressIdentity;
        private static Bitmap lastFrame;
        public static bool IsActive { get; private set; }

        public static void BeginScan(ICharacterNavigationInput navigationInput = null,
            Func<Bitmap, CharacterScreenRecognition> recognizer = null,
            Func<Bitmap, CharacterScreenState, CharacterScreenRecognition, CharacterScreenRecognition> tolerantRecognizer = null)
        {
            input = navigationInput ?? new NativeCharacterInput();
            readScreen = recognizer ?? CharacterScreenRecognizer.Read;
            readTolerant = tolerantRecognizer ?? CharacterScreenRecognizer.ReadTolerant;
            recoveries = evidenceCount = 0;
            lastProgressIdentity = null;
            lastFrame?.Dispose();
            lastFrame = null;
            IsActive = true;
            Logger.Info("Character screen navigation verification enabled");
        }

        public static void EndScan()
        {
            IsActive = false;
            input = new NativeCharacterInput();
            readScreen = CharacterScreenRecognizer.Read;
            readTolerant = CharacterScreenRecognizer.ReadTolerant;
            lastFrame?.Dispose();
            lastFrame = null;
        }

        public static void ConfirmRosterProgress(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity) || identity == lastProgressIdentity) return;
            if (recoveries > 0)
                Logger.Info("Verified roster progress to {0}; resetting the bounded character recovery budget", identity);
            lastProgressIdentity = identity;
            recoveries = 0;
        }

        public static CharacterScreenRecognition Require(CharacterScreenState expected, string context)
        {
            return Verify(context, state => expected == CharacterScreenState.CharacterMenu
                ? state.IsCharacterMenu : state.State == expected, "Expected " + expected + ".", expected);
        }

        public static void OpenCharacterScreen(string context, bool selectPartySlotOne = false)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    OpenCharacterScreenAttempt(context, selectPartySlotOne);
                    return;
                }
                catch (CharacterNavigationException ex)
                {
                    if (attempt >= 2) throw;
                    Logger.Warn(ex, "Character entry attempt {0}/2 failed during {1}; waiting and recognizing the current screen before retrying",
                        attempt, context);
                    input.Wait(650);
                }
            }
        }

        private static void OpenCharacterScreenAttempt(string context, bool selectPartySlotOne)
        {
            var current = Read(context + " before entry", true);
            if (current.State == CharacterScreenState.Unknown)
            {
                // Menus can be mid-animation when the preceding scan finishes.
                // Give them time to settle before the bounded entry Escape.
                TryVerify(context + " settle entry screen", state => state.IsCharacterMenu
                    || state.State == CharacterScreenState.PaimonMenu || state.State == CharacterScreenState.Gameplay,
                    "A recognizable entry screen", out current, CharacterScreenState.CharacterMenu);
            }
            if (current.IsCharacterMenu)
            {
                if (!selectPartySlotOne)
                {
                    Logger.Debug("Character screen already visible during {0}; no entry key sent", context);
                    return;
                }
            }
            if (selectPartySlotOne)
            {
                PrepareInitialParty(context, current);
                // Preserve the working Escape -> 1 -> configured Character
                // hotkey sequence. Switching the active avatar changes the
                // HUD/background and must not add an HP-OCR prerequisite.
                OpenWithCharacterKey(context);
                return;
            }
            if (current.State == CharacterScreenState.Gameplay)
            {
                OpenWithCharacterKey(context);
                return;
            }
            if (current.State != CharacterScreenState.PaimonMenu)
            {
                // A single Escape may open Paimon from the world. Never send it from
                // any positively identified character pane or detail overlay.
                Logger.Info("Opening Paimon for character entry during {0}; observed {1}", context, current.State);
                input.PressKey(Navigation.escapeKey);
                input.Wait(650);
                current = Verify(context + " after Paimon key", state => state.State == CharacterScreenState.PaimonMenu
                    || state.IsCharacterMenu, "Paimon or the character screen did not appear after one Escape.", CharacterScreenState.CharacterMenu);
                if (current.IsCharacterMenu) return;
            }
            if (!current.CharacterTileBounds.HasValue)
                throw Failure(context, current, "The Character tile was not positively recognized in Paimon.");
            Rectangle tile = current.CharacterTileBounds.Value;
            if (tile.Width <= 0 || tile.Height <= 0 || tile.Left < 0 || tile.Top < 0
                || tile.Right > current.FrameWidth || tile.Bottom > current.FrameHeight)
                throw Failure(context, current, "The recognized Character tile bounds are incomplete or outside the captured client.");
            // Tile coordinates come from the same verified Paimon capture; its full
            // bounds are retained rather than guessing a title or icon position.
            double x = (tile.Left + tile.Width * .5) / current.FrameWidth;
            double y = (tile.Top + tile.Height * .5) / current.FrameHeight;
            ValidateCoordinate(x, y);
            Logger.Info("Opening verified Paimon Character tile during {0}: {1}, normalized ({2:F4},{3:F4})", context, tile, x, y);
            input.Click(x, y);
            input.Wait(700);
            Verify(context + " after Character tile", state => state.IsCharacterMenu,
                "The Character tile did not open a verified character screen.", CharacterScreenState.CharacterMenu);
        }

        private static void PrepareInitialParty(string context, CharacterScreenRecognition current)
        {
            if (current.State == CharacterScreenState.Unknown)
            {
                Logger.Info("Opening a recognizable menu before initial party selection during {0}", context);
                input.PressKey(Navigation.escapeKey);
                input.Wait(650);
                current = Verify(context + " locate initial party entry", state => state.IsCharacterMenu
                    || state.State == CharacterScreenState.PaimonMenu || state.State == CharacterScreenState.Gameplay,
                    "No recognized screen appeared after the bounded entry Escape.", CharacterScreenState.CharacterMenu);
            }
            if (current.IsCharacterMenu)
            {
                if (IsDetail(current.State)) CloseDetail(current.State, context + " close initial detail");
                Require(CharacterScreenState.CharacterMenu, context + " before leaving initial character menu");
                input.PressKey(Navigation.escapeKey);
                input.Wait(650);
                current = Verify(context + " leave initial character menu", state => state.State == CharacterScreenState.PaimonMenu
                    || state.State == CharacterScreenState.Gameplay, "The initial Character menu did not close to a recognized screen.");
            }
            if (current.State == CharacterScreenState.PaimonMenu)
            {
                Logger.Info("Closing verified Paimon to choose the initial party character during {0}", context);
                input.PressKey(Navigation.escapeKey);
                input.Wait(650);
                current = Require(CharacterScreenState.Gameplay, context + " before initial party selection");
            }
            if (current.State != CharacterScreenState.Gameplay)
                throw Failure(context, current, "Gameplay must be verified before selecting the initial party character.");
            Logger.Info("Selecting party slot one from verified gameplay before {0}", context);
            input.PressKey(Navigation.slotOneKey);
            input.Wait(650);
        }

        private static void OpenWithCharacterKey(string context)
        {
            for (int attempt = 1; ; attempt++)
            {
                Logger.Info("Opening Character screen during {0}: configured key {1}, attempt {2}/2", context, Navigation.characterKey, attempt);
                input.PressKey(Navigation.characterKey);
                input.Wait(1000);
                if (TryVerify(context + " after Character key", state => state.IsCharacterMenu,
                    "Character menu", out var current, CharacterScreenState.CharacterMenu))
                {
                    if (lastFrame != null) SaveEvidence(lastFrame, context + " Character entry verified", current);
                    return;
                }
                // A dropped key may be retried only on positively recognized
                // gameplay. Never issue character scan inputs on Unknown.
                if (attempt >= 2 || current.State != CharacterScreenState.Gameplay)
                    throw Failure(context, current, "The configured Character key did not open a verified Character menu.");
                Logger.Warn("Character key did not open the menu during {0}; verified gameplay is still visible, retrying once", context);
            }
        }

        public static bool RecoverCharacterScreen(string context)
        {
            if (recoveries >= MaximumRecoveries)
            {
                Logger.Warn("Character screen recovery limit {0} reached during {1}", MaximumRecoveries, context);
                return false;
            }
            recoveries++;
            Logger.Warn("Recovering character screen {0}/{1} during {2}; roster position must be reacquired before scanning resumes",
                recoveries, MaximumRecoveries, context);
            try
            {
                OpenCharacterScreen(context + " recovery");
                return true;
            }
            catch (ThreadAbortException) { throw; }
            catch (CharacterNavigationException ex)
            {
                Logger.Error(ex, "Character screen recovery failed during {0}; no further character actions will be sent", context);
                return false;
            }
        }

        public static void CloseDetail(CharacterScreenState expectedDetail, string context)
        {
            CharacterScreenState basePane = DetailBase(expectedDetail);
            var current = Verify(context + " before closing detail", state => state.State == expectedDetail || state.State == basePane,
                "Expected the detail overlay or its base pane.", CharacterScreenState.CharacterMenu);
            if (current.State == basePane)
            {
                Logger.Debug("Character detail already closed during {0}; no Escape sent", context);
                return;
            }
            Logger.Debug("Closing verified {0} during {1}", expectedDetail, context);
            input.PressKey(Navigation.escapeKey);
            input.Wait(300);
            Require(basePane, context + " after closing detail");
        }

        public static void ClickDetail(string context, double x, double y, CharacterScreenState expectedDetail)
        {
            CharacterScreenState basePane = DetailBase(expectedDetail);
            ValidateCoordinate(x, y);
            for (int attempt = 1; ; attempt++)
            {
                var before = Verify(context + " before detail click", state => state.State == basePane || state.State == expectedDetail,
                    "Expected the verified " + basePane + " pane or its detail overlay.", CharacterScreenState.CharacterMenu);
                // A detail overlay remaining visible does not prove that a click
                // selected the next talent/constellation. Reopen from the base
                // pane so the verified transition cannot accept stale content.
                if (before.State == expectedDetail) CloseDetail(expectedDetail, context + " close preceding detail");
                Logger.Debug("Character detail click during {0}: attempt {1}/2, normalized ({2:F4},{3:F4}), expecting {4}", context, attempt, x, y, expectedDetail);
                input.Click(x, y);
                input.Wait(500);
                if (TryVerify(context + " after detail click", state => state.State == expectedDetail,
                    expectedDetail.ToString(), out var current, expectedDetail)) return;
                if (attempt >= 2 || current.State != basePane)
                    throw Failure(context, current, "The " + expectedDetail + " overlay did not open after a verified click.");
                Logger.Warn("Detail click did not open {0} during {1}; verified {2} is still visible, retrying once", expectedDetail, context, basePane);
            }
        }

        public static void SelectPane(CharacterScreenState pane, double x, double y, string context)
        {
            ValidateCoordinate(x, y);
            for (int attempt = 1; ; attempt++)
            {
                var current = Require(CharacterScreenState.CharacterMenu, context + " before selecting pane");
                if (current.State == pane) return;
                if (IsDetail(current.State)) throw Failure(context, current, "A detail overlay must be closed before changing character panes.");
                Logger.Debug("Selecting character {0} during {1}: attempt {2}/2 at normalized ({3:F4},{4:F4})", pane, context, attempt, x, y);
                input.Click(x, y);
                input.Wait(500);
                if (TryVerify(context + " after selecting pane", state => state.State == pane, pane.ToString(), out current, pane)) return;
                if (attempt >= 2 || !current.IsCharacterMenu || IsDetail(current.State))
                    throw Failure(context, current, "The " + pane + " pane did not appear after a verified click.");
                Logger.Warn("Pane click did not select {0} during {1}; verified character menu is still visible, retrying once", pane, context);
            }
        }

        public static void NextCharacter(double x, double y, string context)
        {
            var current = Require(CharacterScreenState.CharacterMenu, context + " before Next");
            if (IsDetail(current.State)) throw Failure(context, current, "A detail overlay must be closed before advancing the roster.");
            ValidateCoordinate(x, y);
            Logger.Debug("Character Next during {0} at normalized ({1:F4},{2:F4})", context, x, y);
            input.Click(x, y);
            input.Wait(450);
            Require(CharacterScreenState.CharacterMenu, context + " after Next");
        }

        private static CharacterScreenState DetailBase(CharacterScreenState detail)
        {
            if (detail == CharacterScreenState.TalentDetails) return CharacterScreenState.Talents;
            if (detail == CharacterScreenState.ConstellationDetails) return CharacterScreenState.Constellation;
            throw new ArgumentOutOfRangeException(nameof(detail), "Only a verified talent or constellation detail overlay may be dismissed.");
        }

        private static bool IsDetail(CharacterScreenState state)
        {
            return state == CharacterScreenState.TalentDetails || state == CharacterScreenState.ConstellationDetails;
        }

        private static CharacterScreenRecognition Verify(string context, Func<CharacterScreenRecognition, bool> matches,
            string reason, CharacterScreenState? tolerantExpected = null)
        {
            if (TryVerify(context, matches, reason, out var current, tolerantExpected)) return current;
            throw Failure(context, current, reason);
        }

        private static bool TryVerify(string context, Func<CharacterScreenRecognition, bool> matches,
            string expected, out CharacterScreenRecognition current, CharacterScreenState? tolerantExpected = null)
        {
            const int maximumAttempts = 6;
            CharacterScreenState? relaxedState = null;
            int relaxedConfirmations = 0;
            int lastRelaxedCapture = 0;
            current = null;
            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                current = Read(context);
                if (matches(current))
                {
                    if (attempt > 1) Logger.Info("Character screen settled during {0}: {1} after {2}/{3} captures", context, current.State, attempt, maximumAttempts);
                    return true;
                }
                // Start with three ordinary fresh captures. Reconsider only an
                // inconclusive screen. Two of the three remaining fresh captures
                // must agree on the exact pane/overlay. One inconclusive frame
                // may separate them; a positively conflicting state clears them.
                if (attempt >= 4 && tolerantExpected.HasValue && SupportsTolerantDetection(tolerantExpected.Value)
                    && (current.State == CharacterScreenState.Unknown || current.State == CharacterScreenState.CharacterMenu))
                {
                    var relaxed = ReadTolerant(context, tolerantExpected.Value, current);
                    if (relaxed.IsCharacterMenu && relaxed.State != CharacterScreenState.CharacterMenu && matches(relaxed))
                    {
                        relaxedConfirmations = relaxedState == relaxed.State && attempt - lastRelaxedCapture <= 2
                            ? relaxedConfirmations + 1 : 1;
                        relaxedState = relaxed.State;
                        Logger.Debug("Tolerant character detection during {0}: strict={1}, relaxed={2}, confirmation={3}/2 within three fresh captures, capture={4}/{5}, previousVoteCapture={6}",
                            context, current.State, relaxed.State, relaxedConfirmations, attempt, maximumAttempts, lastRelaxedCapture);
                        lastRelaxedCapture = attempt;
                        if (relaxedConfirmations >= 2)
                        {
                            current = relaxed;
                            Logger.Info("Character screen verified by tolerant detection during {0}: {1} on two agreeing frames within three fresh captures ({2}/{3}); evidence {4}",
                                context, current.State, attempt, maximumAttempts, current.Evidence);
                            if (lastFrame != null) SaveEvidence(lastFrame, context + " tolerant detection confirmed", current);
                            return true;
                        }
                    }
                    else if ((relaxed.State == CharacterScreenState.Unknown || relaxed.State == CharacterScreenState.CharacterMenu)
                        && relaxedConfirmations > 0 && attempt - lastRelaxedCapture == 1)
                    {
                        Logger.Debug("Tolerant character detection during {0}: one inconclusive capture {1}/{2}; retaining {3} vote from capture {4} pending another fresh match",
                            context, attempt, maximumAttempts, relaxedState, lastRelaxedCapture);
                    }
                    else
                    {
                        if (relaxedConfirmations > 0)
                            Logger.Debug("Tolerant character detection during {0}: clearing {1} vote after result {2} at capture {3}/{4}; evidence {5}",
                                context, relaxedState, relaxed.State, attempt, maximumAttempts, relaxed.Evidence);
                        relaxedState = null; relaxedConfirmations = 0; lastRelaxedCapture = 0;
                    }
                }
                else
                {
                    if (relaxedConfirmations > 0)
                        Logger.Debug("Tolerant character detection during {0}: clearing {1} vote after strict state {2} at capture {3}/{4}",
                            context, relaxedState, current.State, attempt, maximumAttempts);
                    relaxedState = null; relaxedConfirmations = 0; lastRelaxedCapture = 0;
                }
                Logger.Debug("Waiting for character screen during {0}: attempt {1}/{2}, expected {3}, observed {4}",
                    context, attempt, maximumAttempts, expected, current.State);
                if (attempt < maximumAttempts) input.Wait(attempt <= 2 ? 250 : 500);
            }
            return false;
        }

        private static bool SupportsTolerantDetection(CharacterScreenState state)
        {
            return state == CharacterScreenState.CharacterMenu || state == CharacterScreenState.Attributes
                || state == CharacterScreenState.Constellation || state == CharacterScreenState.Talents || IsDetail(state);
        }

        private static CharacterScreenRecognition ReadTolerant(string context, CharacterScreenState expected,
            CharacterScreenRecognition strict)
        {
            if (lastFrame == null) return strict;
            try
            {
                var relaxed = readTolerant(lastFrame, expected, strict) ?? strict;
                relaxed.FrameWidth = lastFrame.Width;
                relaxed.FrameHeight = lastFrame.Height;
                Logger.Debug("Tolerant character recognition during {0}: expected={1}, strict={2}, result={3}, evidence {4}",
                    context, expected, strict.State, relaxed.State, relaxed.Evidence);
                return relaxed;
            }
            catch (ThreadAbortException) { throw; }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Tolerant character recognition failed during {0}; retaining strict state {1}", context, strict.State);
                return strict;
            }
        }

        private static CharacterScreenRecognition Read(string context, bool save = false)
        {
            string reason;
            if (!input.Refocus(out reason))
            {
                Logger.Warn("Character navigation blocked during {0}: game foreground verification failed ({1})", context, reason);
                throw new CharacterNavigationException(context, CharacterScreenState.Unknown, null,
                    "The game could not be verified as the foreground window. " + reason);
            }
            try
            {
                using (var frame = input.Capture())
                {
                    var current = readScreen(frame) ?? new CharacterScreenRecognition { State = CharacterScreenState.Unknown, Evidence = "Recognizer returned no state." };
                    current.FrameWidth = frame.Width;
                    current.FrameHeight = frame.Height;
                    lastFrame?.Dispose();
                    lastFrame = (Bitmap)frame.Clone();
                    Logger.Debug("Character screen during {0}: state {1}, frame {2}x{3}, evidence {4}", context, current.State, frame.Width, frame.Height, current.Evidence);
                    if (save) SaveEvidence(frame, context, current);
                    return current;
                }
            }
            catch (ThreadAbortException) { throw; }
            catch (CharacterNavigationException) { throw; }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Character capture or screen recognition failed during {0}", context);
                throw new CharacterNavigationException(context, CharacterScreenState.Unknown, null, "Capture or screen recognition failed: " + ex.Message);
            }
        }

        private static CharacterNavigationException Failure(string context, CharacterScreenRecognition current, string reason)
        {
            string path = null;
            try
            {
                if (lastFrame != null) path = SaveEvidence(lastFrame, context, current);
            }
            catch (ThreadAbortException) { throw; }
            catch (Exception ex) { Logger.Warn(ex, "Could not capture character navigation failure evidence"); }
            Logger.Warn("Character navigation blocked during {0}: {1}; observed {2}; recognition {3}; evidence {4}",
                context, reason, current?.State ?? CharacterScreenState.Unknown, current?.Evidence ?? "unavailable", path ?? "unavailable");
            return new CharacterNavigationException(context, current?.State ?? CharacterScreenState.Unknown, path, reason);
        }

        private static string SaveEvidence(Bitmap frame, string context, CharacterScreenRecognition current)
        {
            if (evidenceCount++ >= MaximumEvidence) return null;
            try
            {
                string directory = Path.GetFullPath(Path.Combine("logging", "characters", "navigation"));
                Directory.CreateDirectory(directory);
                string name = Regex.Replace(context ?? "unknown", "[^A-Za-z0-9_-]", "_");
                if (name.Length > 90) name = name.Substring(0, 90);
                string path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff") + "_" + name + "_" + current.State + ".png");
                frame.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Logger.Info("Character navigation evidence during {0}: {1} ({2})", context, path, current.Evidence);
                return path;
            }
            catch (Exception ex) { Logger.Warn(ex, "Could not save character navigation evidence"); return null; }
        }

        private static void ValidateCoordinate(double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || x <= 0 || x >= 1 || y <= 0 || y >= 1)
                throw new ArgumentOutOfRangeException("Character navigation coordinates must lie inside the game client.");
        }

        private sealed class NativeCharacterInput : ICharacterNavigationInput
        {
            public Bitmap Capture() { return Navigation.CaptureWindow(); }
            public bool Refocus(out string reason) { return Navigation.TryRefocusGame(out reason); }
            public void Click(double x, double y)
            {
                EnsureFocus();
                if (!Navigation.SetCursor((int)(x * Navigation.GetWidth()), (int)(y * Navigation.GetHeight())))
                    throw new CharacterNavigationException("moving character cursor", CharacterScreenState.Unknown, null, "The cursor could not reach the game.");
                Navigation.Wait(80);
                EnsureFocus();
                Navigation.Click();
            }
            public void PressKey(VirtualKeyCode key) { EnsureFocus(); Navigation.sim.Keyboard.KeyPress(key); }
            public void Wait(int milliseconds)
            {
                // Input speed may be configured below 1. Menu animations still
                // need their minimum settle time before the next capture/key.
                Navigation.Wait(Math.Max(milliseconds, (int)(milliseconds * Navigation.GetDelay())));
                EnsureFocus();
                Navigation.SetCursor((int)(.975 * Navigation.GetWidth()), (int)(.965 * Navigation.GetHeight()));
                Navigation.Wait(60);
            }
            private static void EnsureFocus()
            {
                string reason;
                if (!Navigation.TryRefocusGame(out reason))
                    throw new CharacterNavigationException("sending character input", CharacterScreenState.Unknown, null, "The game is not the foreground window. " + reason);
            }
        }
    }
}
