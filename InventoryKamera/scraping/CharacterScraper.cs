using Accord.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace InventoryKamera
{
    public static class CharacterScraper
	{
		private static NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

		private static readonly List<string> skippedIdentities = new List<string>();
		private static readonly Dictionary<Character, int> retainedRosterPositions = new Dictionary<Character, int>();
		public static bool LastScanComplete { get; private set; }
		public static int LastSkippedCount => skippedIdentities.Count;
		public static IReadOnlyList<string> LastSkippedIdentities => skippedIdentities.AsReadOnly();

		internal static void BeginRosterCoverage()
		{
			LastScanComplete = false;
			skippedIdentities.Clear();
			retainedRosterPositions.Clear();
		}

		internal static void RecordSkippedIdentity(string identity)
		{
			if (!string.IsNullOrWhiteSpace(identity) && !skippedIdentities.Contains(identity))
				skippedIdentities.Add(identity);
		}

		internal static void FinalizeRosterCoverage(bool wrapped)
		{
			LastScanComplete = wrapped && LastSkippedCount == 0;
		}

		// Diagnostic saves must never abort a scan (GDI+ generic errors, missing
		// directories, disconnected or full drives). Create the directory and
		// warn instead of throwing.
		private static void SaveDiagnosticBitmap(Bitmap bm, string file)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(file));
				bm.Save(file);
			}
			catch (Exception ex)
			{
				Logger.Warn(ex, "Could not save diagnostic screenshot {0}. Continuing scan...", file);
			}
		}

		/// <summary>Mutable roster-walk state shared by the ScanCharacters phases.</summary>
		private sealed class RosterScanContext
		{
			internal List<Character> Characters;
			internal readonly HashSet<string> Scanned = new HashSet<string>();
			internal readonly HashSet<string> InspectedIdentities = new HashSet<string>();
			internal int Viewed;
			internal bool Wrapped;
			internal int StalledSelections;
			internal string LastInspectedIdentity;
			internal string First;
			internal int? RosterTotal;
		}

		public static void ScanCharacters(List<Character> Characters)
		{
			BeginRosterCoverage();
			var context = new RosterScanContext { Characters = Characters };
			MeasureRosterCount(context);
			UserInterface.ResetCharacterDisplay();
			WalkCharacterRoster(context);
			ReportRosterCoverage(context);
			ApplyTartagliaTalentCorrection(Characters);
		}

		private static void MeasureRosterCount(RosterScanContext context)
		{
			UserInterface.ReportScanProgress(ScanPhase.Characters, 0, null, "Counting roster", totalIsProvisional: true);
			try
			{
				context.RosterTotal = CountOwnedCharacters(out string rosterStart);
				context.First = rosterStart;
			}
			finally
			{
				// An interrupted count must not leave its partial denominator in the pill.
				UserInterface.ReportScanProgress(ScanPhase.Characters, 0, context.RosterTotal,
					context.RosterTotal.HasValue ? "Scanning roster" : "Scanning roster (total unavailable)");
			}
		}

		private static void WalkCharacterRoster(RosterScanContext context)
		{
			while (context.Viewed < 256)
			{
				Character character = ScanCharacterSlot(context, out string currentIdentity, out bool omittedAfterNavigationFailure);
				if (RetryStalledSelection(context, currentIdentity))
				{
					continue;
				}
				if (ReachedRosterWrapCheckpoint(context, currentIdentity))
				{
					break;
				}
				RecordScannedCharacter(context, character, currentIdentity);

				// The verified starting name remains the wrap sentinel even when its full
				// detail read failed. Count inspected slots rather than only valid exports.
				string readableIdentity = string.IsNullOrWhiteSpace(character.NameGOOD) ? "Unreadable character" : character.NameGOOD;
				string scanDetail = omittedAfterNavigationFailure ? "Partial: skipped " + currentIdentity : readableIdentity;
				UserInterface.ReportScanProgress(ScanPhase.Characters, ++context.Viewed, context.RosterTotal, scanDetail);
				context.LastInspectedIdentity = currentIdentity;
				CharacterNavigation.ConfirmRosterProgress(currentIdentity);
				AdvanceCharacterWithRecovery(currentIdentity);
				UserInterface.ResetCharacterDisplay();
			}
		}

		private static Character ScanCharacterSlot(RosterScanContext context, out string currentIdentity,
			out bool omittedAfterNavigationFailure)
		{
			string identityCheckpoint = null;
			int viewed = context.Viewed;
			try
			{
				Character character = ScanCharacterWithRecovery(viewed == 0 ? null : context.First, context.LastInspectedIdentity,
					identity => identityCheckpoint = identity, out omittedAfterNavigationFailure);
				currentIdentity = identityCheckpoint;
				return character;
			}
			catch (ThreadAbortException) { throw; }
			catch (CharacterNavigationException) { throw; }
			catch (Exception ex)
			{
				// An unreadable or unknown character must never abort the whole scan.
				// Log it and continue with an invalid character so the loop advances
				// to the next one.
				UserInterface.AddError($"    [FAILURE] Character at position {viewed + 1}: {ex.Message}. Skipping character.");
				Logger.Debug(ex, "Character scan exception at position {0}", viewed + 1);
				omittedAfterNavigationFailure = false;
				currentIdentity = identityCheckpoint;
				return new Character();
			}
		}

		private static bool RetryStalledSelection(RosterScanContext context, string currentIdentity)
		{
			if (currentIdentity == null || currentIdentity != context.LastInspectedIdentity)
			{
				context.StalledSelections = 0;
				return false;
			}
			if (++context.StalledSelections > 2)
				throw new CharacterNavigationException("advance character roster", CharacterScreenState.CharacterMenu,
					null, "The roster did not advance after two verified retry clicks; stopping the scan.");
			Logger.Warn("Character roster selection did not advance from {0}; retry {1}/2 without counting the slot again", currentIdentity, context.StalledSelections);
			AdvanceCharacterWithRecovery(currentIdentity);
			return true;
		}

		private static bool ReachedRosterWrapCheckpoint(RosterScanContext context, string currentIdentity)
		{
			if (context.First == null && currentIdentity != null) context.First = currentIdentity;
			bool returnedToStart = currentIdentity != null && context.First != null && currentIdentity == context.First;
			if (context.Viewed > 0 && returnedToStart)
			{
				// A lost Next click must not finish a previously measured larger roster.
				context.Wrapped = !context.RosterTotal.HasValue || context.Viewed == context.RosterTotal.Value;
				return true;
			}
			if (currentIdentity != null && !context.InspectedIdentities.Add(currentIdentity))
				throw new CharacterNavigationException("verify character roster cycle", CharacterScreenState.CharacterMenu,
					null, "An already inspected character reappeared before the starting checkpoint; the roster cycle could not be verified.");
			return false;
		}

		private static void RecordScannedCharacter(RosterScanContext context, Character character, string currentIdentity)
		{
			if (character.IsValid())
			{
				if (context.Scanned.Add(character.NameGOOD))
				{
					context.Characters.Add(character);
					retainedRosterPositions[character] = context.Viewed;
					UserInterface.IncrementCharacterCount();
					Logger.Info("    Completed scan of character {0} successfully", character.NameGOOD);
				}
				else
				{
					Logger.Info("    Skipping duplicate character {0}", character.NameGOOD);
				}
			}
			else
			{
				LogInvalidCharacter(context, character, currentIdentity);
			}
		}

		private static void LogInvalidCharacter(RosterScanContext context, Character character, string currentIdentity)
		{
			string skipped = currentIdentity ?? "unreadable position " + (context.Viewed + 1);
			RecordSkippedIdentity(skipped);
			string error = "";
			if (!character.HasValidName()) error += "        Invalid character name\n";
			if (!character.HasValidLevel()) error += "        Invalid level\n";
			if (!character.HasValidElement()) error += "        Invalid element\n";
			if (!character.HasValidConstellation()) error += "        Invalid constellation\n";
			if (!character.HasValidTalents()) error += "        Invalid talents\n";
			Logger.Error("    [FAILURE] Character {0} could not be validated and was skipped.\n{1}",
				string.IsNullOrWhiteSpace(character.NameGOOD) ? "at position " + (context.Viewed + 1) : character.NameGOOD,
				error.TrimEnd());
			Logger.Debug("Invalid character details: {0}", character);
		}

		private static void ReportRosterCoverage(RosterScanContext context)
		{
			FinalizeRosterCoverage(context.Wrapped);
			if (LastScanComplete)
				UserInterface.FinishScanPhase(ScanPhase.Characters, context.Viewed, context.RosterTotal ?? context.Viewed, "Roster cycle completed");
			else if (context.Wrapped)
			{
				Logger.Warn("Character roster cycle verified with partial data: inspected={0}, recorded={1}, skipped={2}: {3}",
					context.Viewed, context.Characters.Count, LastSkippedCount, string.Join(", ", skippedIdentities));
				UserInterface.ReportScanProgress(ScanPhase.Characters, context.Viewed, context.RosterTotal,
					"Partial roster: " + LastSkippedCount + " characters skipped");
			}
			else
			{
				Logger.Warn("Character roster scan ended without a verified cycle: inspected={0}, recorded={1}, total={2}", context.Viewed, context.Characters.Count, context.RosterTotal?.ToString() ?? "unknown");
				UserInterface.ReportScanProgress(ScanPhase.Characters, context.Viewed, context.RosterTotal, "Roster cycle could not be verified");
			}
		}

		internal static void ApplyTartagliaTalentCorrection(List<Character> Characters,
			IDictionary<Character, int> rosterPositions = null)
		{
			var positions = rosterPositions ?? retainedRosterPositions;
			// Apply this to retained characters even when navigation stops early.
			for (int i = 0; i < Characters.Count; i++)
			{
				if (!IsTartaglia(Characters[i])) continue;
				if (Characters[i].Ascension >= 4)
				{
					ApplyPartyWideTartagliaFix(Characters, i, positions);
				}
				break;
			}
		}

		private static bool IsTartaglia(Character character)
		{
			return character.NameGOOD.ToLower() == "tartaglia";
		}

		private static void ApplyPartyWideTartagliaFix(List<Character> Characters, int index, IDictionary<Character, int> positions)
		{
			bool positionKnown = TryResolveRetainedPosition(positions, Characters[index], index, out int originalPosition);
			// Offline callers without a production position map preserve the
			// historical ordered-list contract. A populated production map
			// must never substitute a compacted export index for a skipped slot.
			Logger.Info("Ascension 4+ Tartaglia found at original roster position {0}.", positionKnown ? originalPosition.ToString() : "unknown");
			if (positionKnown && originalPosition < 4)
			{
				for (int j = 0; j < Characters.Count; j++)
				{
					bool knownPartyPosition = TryResolveRetainedPosition(positions, Characters[j], j, out int partyPosition);
					if (!knownPartyPosition || partyPosition >= 4) continue;
					Characters[j].Talents["auto"] -= 1;
					Logger.Info("Applied Tartaglia auto attack fix to {0} at position {1}.", Characters[j].NameGOOD, j);
				}
			}
			else
			{
				Characters[index].Talents["auto"] -= 1;
				Logger.Info("Applied Tartaglia auto attack fix to self only.");
			}
		}

		private static bool TryResolveRetainedPosition(IDictionary<Character, int> positions, Character character,
			int fallbackIndex, out int position)
		{
			bool positionKnown = positions.TryGetValue(character, out position);
			if (!positionKnown && positions.Count == 0)
			{
				position = fallbackIndex;
				positionKnown = true;
			}
			return positionKnown;
		}

		private static int? CountOwnedCharacters(out string startingName)
		{
			// This is a traversal guard, never a progress denominator or a catalog count.
			const int maxRosterPositions = 256;
			int? total = CountRosterBestEffort(
				() => SelectAttributesAfterDetails("prepare optional roster count"),
				ReadRosterIdentity, Navigation.SelectNextCharacter,
				identity => RestoreRosterPosition(identity, ReadRosterIdentity, Navigation.SelectNextCharacter,
					maxRosterPositions, out _),
				identity =>
				{
					if (identity != null)
						return RecoverRosterPosition(identity, false, "restore optional roster count checkpoint");
					// No roster movement has occurred before the first checkpoint.
					// Reestablish the same initial party ordering if entry verification failed.
					CharacterNavigation.OpenCharacterScreen("recover initial roster checkpoint", true);
					SelectAttributesAfterDetails("recover initial roster checkpoint Attributes");
					return true;
				}, maxRosterPositions, out startingName,
				count => UserInterface.ReportScanProgress(ScanPhase.Characters, 0, count, "Counting roster", totalIsProvisional: true));
			if (total.HasValue) Logger.Info("Verified owned character roster cycle: {0} characters", total.Value);
			else Logger.Info("Optional roster total unavailable; scanning from verified initial checkpoint {0} without a denominator",
				startingName ?? "header not yet readable");
			return total;
		}

		internal static int? CountRosterBestEffort(Action prepareAttributes, Func<string> readIdentity,
			Action nextCharacter, Func<string, bool> restoreOrigin, Func<string, bool> recoverOrigin,
			int boundedLimit, out string startingIdentity, Action<int> reportCount = null)
		{
			if (prepareAttributes == null) throw new ArgumentNullException(nameof(prepareAttributes));
			if (readIdentity == null) throw new ArgumentNullException(nameof(readIdentity));
			if (nextCharacter == null) throw new ArgumentNullException(nameof(nextCharacter));
			if (restoreOrigin == null) throw new ArgumentNullException(nameof(restoreOrigin));
			if (recoverOrigin == null) throw new ArgumentNullException(nameof(recoverOrigin));
			startingIdentity = null;
			try
			{
				prepareAttributes();
				startingIdentity = readIdentity();
			}
			catch (ThreadAbortException) { throw; }
			catch (Exception failure)
			{
				Logger.Warn(failure, "Initial roster checkpoint verification failed before any Next; making one bounded entry recovery and abandoning the optional count");
				startingIdentity = RecoverCountCheckpoint(prepareAttributes, readIdentity, recoverOrigin, null, failure);
				return null;
			}
			if (string.IsNullOrWhiteSpace(startingIdentity))
			{
				Logger.Warn("Initial roster header unreadable; no count traversal performed and the original position is unchanged");
				startingIdentity = null;
				return null;
			}
			int? total = null;
			Exception checkpointFailure = null;
			try
			{
				string countedStart;
				total = CountRosterCycle(readIdentity, nextCharacter, boundedLimit, out countedStart, reportCount);
				if (countedStart != startingIdentity) total = null;
			}
			catch (ThreadAbortException) { throw; }
			catch (Exception failure)
			{
				checkpointFailure = failure;
				Logger.Warn(failure, "Optional roster count interrupted; preserving the original checkpoint and discarding its total");
			}
			try
			{
				if (restoreOrigin(startingIdentity)) return total;
			}
			catch (ThreadAbortException) { throw; }
			catch (Exception failure)
			{
				checkpointFailure = failure;
				Logger.Warn(failure, "Optional roster count could not restore its original checkpoint; attempting one bounded exact-position recovery");
			}
			Logger.Warn("Recovering optional roster count checkpoint {0}; the total will remain unavailable and this count will not be retried", startingIdentity);
			RecoverCountCheckpoint(prepareAttributes, readIdentity, recoverOrigin, startingIdentity, checkpointFailure);
			return null;
		}

		private static string RecoverCountCheckpoint(Action prepareAttributes, Func<string> readIdentity,
			Func<string, bool> recoverOrigin, string origin, Exception failure)
		{
			try
			{
				if (!recoverOrigin(origin)) throw RosterCheckpointFailure("recover optional roster count", failure);
				prepareAttributes();
				string recoveredIdentity = readIdentity();
				if (origin != null && recoveredIdentity != origin)
					throw RosterCheckpointFailure("verify recovered optional count checkpoint", failure);
				return recoveredIdentity;
			}
			catch (ThreadAbortException) { throw; }
			catch (CharacterNavigationException) { throw; }
			catch (Exception recoveryFailure)
			{
				Logger.Warn(recoveryFailure, "Bounded optional roster count recovery failed for {0}", origin ?? "initial party position");
				throw RosterCheckpointFailure("recover optional roster count", recoveryFailure);
			}
		}

		private static CharacterNavigationException RosterCheckpointFailure(string context, Exception failure)
		{
			return new CharacterNavigationException(context, CharacterScreenState.Unknown,
				(failure as CharacterNavigationException)?.EvidencePath,
				"The original Attributes roster position could not be verified after bounded recovery; stopping rather than scanning a rotated roster.");
		}

		internal static int? CountRosterCycle(Func<string> readName, Action nextCharacter, int boundedLimit,
			out string startingName, Action<int> reportCount = null)
		{
			if (readName == null) throw new ArgumentNullException(nameof(readName));
			if (nextCharacter == null) throw new ArgumentNullException(nameof(nextCharacter));
			startingName = null;
			if (boundedLimit < 2) return null;
			string firstName = readName();
			if (string.IsNullOrWhiteSpace(firstName)) return null;
			var names = new HashSet<string> { firstName };
			reportCount?.Invoke(names.Count);
			for (int position = 1; position < boundedLimit; ++position)
			{
				nextCharacter();
				string name = readName();
				if (string.IsNullOrWhiteSpace(name)) return null;
				if (name == firstName)
				{
					// A single repeated label can also mean the Next click was lost.
					if (names.Count < 2) return null;
					startingName = firstName;
					return names.Count;
				}
				if (!names.Add(name)) return null;
				reportCount?.Invoke(names.Count);
			}
			return null;
		}

		private static Character ScanCharacterWithRecovery(string firstCharacter, string previousIdentity,
			Action<string> checkpoint, out bool skipped)
		{
			return ScanCharacterWithRecovery(callback =>
			{
				UserInterface.ResetCharacterDisplay();
				return ScanCharacter(firstCharacter, callback);
			}, previousIdentity, checkpoint,
				(identity, advance) => RecoverRosterPosition(identity, advance, "retry interrupted character"),
				PrepareSkippedCharacter, out skipped);
		}

		internal static Character ScanCharacterWithRecovery(Func<Action<string>, Character> scanCharacter,
			string previousIdentity, Action<string> checkpoint, Func<string, bool, bool> recoverPosition,
			Func<string, bool> prepareSkip, out bool skipped)
		{
			if (scanCharacter == null) throw new ArgumentNullException(nameof(scanCharacter));
			if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
			if (recoverPosition == null) throw new ArgumentNullException(nameof(recoverPosition));
			if (prepareSkip == null) throw new ArgumentNullException(nameof(prepareSkip));
			skipped = false;
			string target = null;
			// Recovery gives this slot three attempts; the second failure always
			// omits or rethrows, so the loop can never run a fourth iteration.
			for (int attempt = 0; attempt <= 2; attempt++)
			{
				string expectedTarget = target;
				try
				{
					return scanCharacter(identity =>
					{
						VerifyInterruptedCheckpoint(expectedTarget, identity);
						target = identity;
						checkpoint(identity);
					});
				}
				catch (CharacterNavigationException ex)
				{
					Logger.Warn(ex, "Character navigation interrupted: target={0}, attempt={1}/3; this slot has not been counted",
						target ?? previousIdentity ?? "initial character", attempt + 1);
					bool recovered = TryRecoverRetryPosition(attempt, target, previousIdentity, recoverPosition);
					if (!recovered)
					{
						// A failed detail read can be omitted only after a fresh positive
						// menu and the exact known header checkpoint are restored.
						if (target == null || !prepareSkip(target)) throw;
						return OmitUnreadableCharacter(ex, target, attempt, checkpoint, ref skipped);
					}
				}
			}
			// Unreachable: the final attempt always returns above or rethrows.
			throw new InvalidOperationException("Character scan retries exhausted without a result.");
		}

		private static void VerifyInterruptedCheckpoint(string expectedTarget, string identity)
		{
			if (expectedTarget != null && identity != expectedTarget)
				throw new CharacterNavigationException("verify interrupted character checkpoint", CharacterScreenState.Attributes,
					null, "Expected " + expectedTarget + " after recovery, but read " + (identity ?? "an unreadable header") + ". This slot has not been counted.");
		}

		private static bool TryRecoverRetryPosition(int attempt, string target, string previousIdentity,
			Func<string, bool, bool> recoverPosition)
		{
			if (attempt >= 2) return false;
			try
			{
				return recoverPosition(target ?? previousIdentity, target == null && previousIdentity != null);
			}
			catch (CharacterNavigationException recoveryFailure)
			{
				Logger.Warn(recoveryFailure, "Character retry position could not be restored for {0}", target ?? "unreadable character");
				return false;
			}
		}

		private static Character OmitUnreadableCharacter(CharacterNavigationException ex, string target, int attempt,
			Action<string> checkpoint, ref bool skipped)
		{
			checkpoint(target);
			skipped = true;
			Logger.Error(ex, "Omitting character {0} after {1}/3 scan attempts; Attributes and its exact roster checkpoint were verified. Remaining characters will continue; evidence {2}",
				target, attempt + 1, ex.EvidencePath ?? "unavailable");
			UserInterface.AddError("    [PARTIAL] Could not read " + target + "; character omitted after bounded recovery.");
			return new Character();
		}

		private static bool PrepareSkippedCharacter(string identity)
		{
			CharacterNavigation.Require(CharacterScreenState.CharacterMenu, "verify menu before omitting " + identity);
			SelectAttributesAfterDetails("omit failed character " + identity);
			if (!RestoreRosterPosition(identity, ReadRosterIdentity, Navigation.SelectNextCharacter, 256, out int moves))
			{
				Logger.Error("Failed character {0} could not be restored for a safe skip ({1} moves); stopping", identity, moves);
				return false;
			}
			Logger.Info("Verified Attributes and checkpoint {0} for omission after {1} roster moves", identity, moves);
			return true;
		}

		private static void AdvanceCharacterWithRecovery(string currentIdentity)
		{
			// The catch below always rethrows by the second failed attempt.
			for (int attempt = 0; attempt <= 2; attempt++)
			{
				try { Navigation.SelectNextCharacter(); return; }
				catch (CharacterNavigationException ex)
				{
					Logger.Warn(ex, "Character navigation interrupted while advancing from {0}, attempt={1}/3", currentIdentity ?? "unreadable character", attempt + 1);
					// Without an identity there is no verified position to restore.
					if (attempt >= 2 || currentIdentity == null ||
						!RecoverRosterPosition(currentIdentity, false, "advance to next character")) throw;
				}
			}
		}

		private static bool RecoverRosterPosition(string identity, bool advanceAfterRestore, string context)
		{
			if (!CharacterNavigation.RecoverCharacterScreen(context)) return false;
			SelectAttributesAfterDetails(context + " after menu recovery");
			if (identity != null)
			{
				if (!RestoreRosterPosition(identity, ReadRosterIdentity, Navigation.SelectNextCharacter, 256, out int moves))
				{
					Logger.Error("Character menu recovered, but target {0} could not be restored within a bounded roster cycle ({1} moves). Stopping without counting recovery as scan progress.", identity, moves);
					return false;
				}
				Logger.Info("Restored character scan checkpoint {0} after {1} roster moves; scan progress unchanged", identity, moves);
				if (advanceAfterRestore) Navigation.SelectNextCharacter();
			}
			return true;
		}

		private static void SelectAttributesAfterDetails(string context)
		{
			var current = CharacterNavigation.Require(CharacterScreenState.CharacterMenu, context);
			if (current.State == CharacterScreenState.ConstellationDetails || current.State == CharacterScreenState.TalentDetails)
				CharacterNavigation.CloseDetail(current.State, context + " close verified detail");
			Navigation.SelectCharacterAttributes();
		}

		private enum RosterStepOutcome
		{
			Advance, ReachedTarget, SearchFailed
		}

		// Menu recovery may reopen on the first party member. A return to that
		// member is not scan completion: restore the exact interrupted position.
		internal static bool RestoreRosterPosition(string expectedIdentity, Func<string> readIdentity,
			Action selectNext, int boundedLimit, out int moves)
		{
			if (readIdentity == null) throw new ArgumentNullException(nameof(readIdentity));
			if (selectNext == null) throw new ArgumentNullException(nameof(selectNext));
			moves = 0;
			if (string.IsNullOrWhiteSpace(expectedIdentity) || boundedLimit < 1) return false;
			var visited = new HashSet<string>();
			string previous = null;
			int stalled = 0;
			for (int position = 0; position < boundedLimit; position++)
			{
				string identity = readIdentity();
				RosterStepOutcome outcome = StepRosterPosition(identity, expectedIdentity, visited, ref previous, ref stalled);
				if (outcome == RosterStepOutcome.ReachedTarget) return true;
				if (outcome == RosterStepOutcome.SearchFailed) return false;
				selectNext();
				moves++;
			}
			return false;
		}

		private static RosterStepOutcome StepRosterPosition(string identity, string expectedIdentity,
			HashSet<string> visited, ref string previous, ref int stalled)
		{
			if (identity == expectedIdentity) return RosterStepOutcome.ReachedTarget;
			if (!string.IsNullOrWhiteSpace(identity))
			{
				if (identity == previous)
				{
					if (++stalled > 2) return RosterStepOutcome.SearchFailed;
				}
				else
				{
					if (!visited.Add(identity)) return RosterStepOutcome.SearchFailed;
					stalled = 0;
				}
				previous = identity;
			}
			return RosterStepOutcome.Advance;
		}

		private static string ReadRosterIdentity()
		{
			CharacterNavigation.Require(CharacterScreenState.Attributes, "read roster recovery checkpoint");
			var unknownReads = new Dictionary<string, int>();
			for (int attempt = 0; attempt < 3; attempt++)
			{
				using (Bitmap header = Navigation.CaptureRegion(CharacterHeaderRegion()))
				{
					bool matched = TryReadCharacterHeader(header, out string name, out string element, out string rawName, out _);
					string identity = CharacterIdentity(name, element, rawName);
					if (matched && identity != null) return identity;
					if (identity != null)
						unknownReads[identity] = unknownReads.TryGetValue(identity, out int count) ? count + 1 : 1;
				}
				Navigation.SystemWait(Navigation.Speed.Fast);
			}
			return unknownReads.Where(read => read.Value >= 2).Select(read => read.Key).FirstOrDefault();
		}

		private static string CharacterIdentity(string name, string element, string rawName)
		{
			if (string.IsNullOrWhiteSpace(rawName) || string.IsNullOrWhiteSpace(element)
				|| !GenshinProcesor.IsValidElement(element)) return null;
			if (GenshinProcesor.IsValidCharacter(name) && GenshinProcesor.CharacterMatchesElement(name, element))
				return name == "Traveler" ? name + element : name;
			return "unresolved:" + element.ToLowerInvariant() + ":" + rawName.ToLowerInvariant();
		}

		private static Rectangle CharacterHeaderRegion()
		{
			return GetCharacterHeaderRegion(new Size(Navigation.GetWidth(), Navigation.GetHeight()));
		}

        internal static Rectangle GetCharacterHeaderRegion(Size clientSize)
        {
            // The roster Q key begins at x477 in a 1920-wide client. Preserve
            // the available header width without admitting that key or portraits.
            // A longer name still touching this edge uses the bounded tail repair.
            return Rectangle.FromLTRB((int)(85 / 1280.0 * clientSize.Width),
                (int)(10 / 720.0 * clientSize.Height), (int)(315 / 1280.0 * clientSize.Width),
                (int)(55 / 720.0 * clientSize.Height));
        }

		private static Character ScanCharacter(string firstCharacter, Action<string> checkpoint)
		{
			var character = new Character();
			Navigation.SelectCharacterAttributes();
			string name = null;
			string element = null;

			// Scan the Name and element of Character. Attempt 75 times max.
			ScanNameAndElement(ref name, ref element, checkpoint: checkpoint);

			if (string.IsNullOrWhiteSpace(name))
			{
				// ScanCharacters reports the invalid fields together in one failure.
				return character;
			}

			character.NameGOOD = name;
			character.Element = element;

			// Check if character was first scanned
			if (CharacterIdentity(name, element, name) != firstCharacter)
			{
				ScanCharacterDetails(character, name);
				return character;
			}
			Logger.Info("Repeat character {0} detected. Finishing character scan...", name);
			return character;
		}

		private static void ScanCharacterDetails(Character character, string name)
		{
			bool ascended = false;
			// Scan Level and ascension
			Logger.Debug("Starting scan of character {0} ({1}): scanning level and ascension...", character.NameGOOD, character.Element);
			int level = ScanLevel(ref ascended);
			if (level == -1)
			{
				UserInterface.AddError($"        [PARTIAL] Could not determine {character.NameGOOD}'s level. Character will be omitted.");
				return;
			}
			character.Level = level;
			character.Ascended = ascended;

			Logger.Info("        {0}: level {1}, ascended {2}", character.NameGOOD, character.Level, character.Ascended);

			// Scan Experience

			// Scan Constellation
			Logger.Debug("    Scanning {0}'s constellations...", character.NameGOOD);
			Navigation.SelectCharacterConstellation();
			character.Constellation = ScanConstellations(character);
			Logger.Info("        {0}: constellation {1}", character.NameGOOD, character.Constellation);
			Navigation.SystemWait(Navigation.Speed.Normal);

			// Scan Talents
			Logger.Debug("    Scanning {0}'s talents...", character.NameGOOD);
			Navigation.SelectCharacterTalents();
			character.Talents = ScanTalents(character);
			Logger.Debug("        {0}'s talents: {1}", character.NameGOOD, string.Join(", ", character.Talents.Select(kv => kv.Key + "=" + kv.Value).ToArray()));
			Navigation.SystemWait(Navigation.Speed.Normal);
			if (!character.HasValidTalents()) return;

			// Scale down talents due to constellations
			if (character.Constellation >= 3 && !ApplyConstellationTalentAdjustments(character, name)) return;
		}

		private static bool ApplyConstellationTalentAdjustments(Character character, string name)
		{
			if (!GenshinProcesor.Characters.TryGetValue(name.ToLower(), out var characterData) ||
				characterData == null ||
				!characterData.TryGetValue("ConstellationOrder", out var constellationOrder) ||
				constellationOrder == null)
			{
				Logger.Warn("        [WARNING] {0} is missing from the character data. Keeping scanned talent levels.", character.NameGOOD);
				return false;
			}
			if (!TryResolveConstellationTalentOrder(character, constellationOrder, out string talentLeveledAtConst3, out string talentLeveledAtConst5))
			{
				return false;
			}

			// Scale down talents. Ported from taiwenlee/Inventory_Kamera (MIT): 191fa8f2
			// C3 and C5 each raise one talent by 3 levels, and the character data records
			// which talent each targets. That can be the normal attack ("auto") for
			// characters such as Freminet, Sethos, Arlecchino or Neuvillette.
			if (character.Constellation >= 3)
			{
				Logger.Info("        Adjusting {0}'s {1} talent by -3 levels for constellation 3", character.NameGOOD, talentLeveledAtConst3);
				character.Talents[talentLeveledAtConst3] -= 3;
			}

			if (character.Constellation >= 5)
			{
				if (talentLeveledAtConst5 is null)
				{
					Logger.Warn("        [WARNING] {0} is missing constellation 5 order data. Keeping scanned talent levels.", character.NameGOOD);
					return false;
				}
				Logger.Info("        Adjusting {0}'s {1} talent by -3 levels for constellation 5", character.NameGOOD, talentLeveledAtConst5);
				character.Talents[talentLeveledAtConst5] -= 3;
			}
			return true;
		}

		private static bool TryResolveConstellationTalentOrder(Character character, JToken constellationOrder,
			out string talentLeveledAtConst3, out string talentLeveledAtConst5)
		{
			if (character.NameGOOD.Contains("Traveler"))
			{
				// The Traveler's constellation order is stored per element. Elements
				// missing from the data (e.g. a new Traveler element on outdated tables)
				// must not crash the scan.
				var elementOrder = character.Element is null ? null : constellationOrder[character.Element.ToLower()];
				if (elementOrder is null || elementOrder.First is null)
				{
					Logger.Warn("        [WARNING] {0} ({1}) is missing constellation order data. Keeping scanned talent levels.", character.NameGOOD, character.Element);
					talentLeveledAtConst3 = null;
					talentLeveledAtConst5 = null;
					return false;
				}
				talentLeveledAtConst3 = (string)elementOrder[0];
				talentLeveledAtConst5 = elementOrder.Count() > 1 ? (string)elementOrder[1] : null;
				return true;
			}

			if (constellationOrder.First is null)
			{
				Logger.Warn("        [WARNING] {0} is missing constellation order data. Keeping scanned talent levels.", character.NameGOOD);
				talentLeveledAtConst3 = null;
				talentLeveledAtConst5 = null;
				return false;
			}
			talentLeveledAtConst3 = (string)constellationOrder[0];
			talentLeveledAtConst5 = constellationOrder.Count() > 1 ? (string)constellationOrder[1] : null;
			return true;
		}

		public static string ScanMainCharacterName()
		{
			var xReference = 1280.0;
			var yReference = 720.0;
			if (Navigation.GetAspectRatio() == new Size(8, 5))
			{
				yReference = 800.0;
			}

			Rect region = new Rect(
				Left:   (int)(185 / xReference * Navigation.GetWidth()),
				Top:    (int)(26  / yReference * Navigation.GetHeight()),
				Right:  (int)(460 / xReference * Navigation.GetWidth()),
				Bottom: (int)(60  / yReference * Navigation.GetHeight()));

			Bitmap nameBitmap = Navigation.CaptureRegion(region);

			//Image Operations
			GenshinProcesor.SetGamma(0.2, 0.2, 0.2, ref nameBitmap);
			GenshinProcesor.SetInvert(ref nameBitmap);
			Bitmap n = GenshinProcesor.ConvertToGrayscale(nameBitmap);

			UserInterface.SetNavigation_Image(nameBitmap);

			string text = GenshinProcesor.AnalyzeText(n).Trim();
			if (text != "")
			{
				// Only keep a-Z and 0-9
				text = Regex.Replace(text, @"[\W_]", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout).ToLower();

				// Only keep text up until first space
				text = Regex.Replace(text, @"\s+\w*", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);

			}
			else
			{
				UserInterface.AddError("    [FAILURE] Could not read the Traveler's name from the character screen.");
			}
			n.Dispose();
			nameBitmap.Dispose();
			return text;
		}

        /// <summary>Replay a captured header without navigation or UI updates.</summary>
        internal static bool TryReadCharacterHeader(Bitmap header, out string name, out string element,
            out string rawName, out string rawText)
        {
            if (header == null) throw new ArgumentNullException(nameof(header));
            name = null;
            element = null;
            rawName = null;
            string block, line;
            Bitmap grayscale = GenshinProcesor.ConvertToGrayscale(header);
            try
            {
                GenshinProcesor.SetThreshold(110, ref grayscale);
                GenshinProcesor.SetInvert(ref grayscale);
                using (Bitmap scaled = GenshinProcesor.ResizeImage(grayscale, grayscale.Width * 2, grayscale.Height * 2))
                {
                    block = GenshinProcesor.AnalyzeText(scaled, Tesseract.PageSegMode.Auto).ToLowerInvariant().Trim();
                    line = GenshinProcesor.AnalyzeText(scaled, Tesseract.PageSegMode.SingleLine).ToLowerInvariant().Trim();
                }
            }
            finally { grayscale.Dispose(); }
            bool clipped = CharacterHeaderTouchesRightEdge(header);
            bool matched = TryResolveCharacterHeaderReadings(block, line, clipped,
                out name, out element, out rawName, out string evidence);
            rawText = "block='" + block + "'; line='" + line + "'; name-clipped=" + clipped + "; resolution=" + evidence;
            return matched;
        }

        private struct CharacterHeaderReading
        {
            internal bool Matched;
            internal string Name;
            internal string Element;
            internal string RawName;
        }

        internal static bool TryResolveCharacterHeaderReadings(string block, string line, bool nameClipped,
            out string name, out string element, out string rawName, out string evidence)
        {
            name = null;
            element = null;
            rawName = null;
            evidence = "no complete catalog name matched";
            CharacterHeaderReading lineReading = ReadHeaderSide(line, nameClipped);
            CharacterHeaderReading blockReading = ReadHeaderSide(block, nameClipped);
            string resolvedName = lineReading.Matched ? lineReading.Name : blockReading.Name;
            string agreement;
            if (HasConflictingHeaderEvidence(lineReading, blockReading, nameClipped, resolvedName, out agreement))
            {
                element = lineReading.Element;
                rawName = lineReading.RawName;
                evidence = DescribeConflictingHeaderEvidence(lineReading, blockReading, agreement);
                return false;
            }
            if (lineReading.Matched || blockReading.Matched)
            {
                bool useLine = ShouldUseLineHeaderReading(lineReading, blockReading);
                name = useLine ? lineReading.Name : blockReading.Name;
                element = useLine ? lineReading.Element : blockReading.Element;
                rawName = useLine ? lineReading.RawName : blockReading.RawName;
                evidence = DescribeMatchingHeaderEvidence(useLine, block, agreement);
                return true;
            }
            element = !string.IsNullOrEmpty(lineReading.Element) ? lineReading.Element : blockReading.Element;
            rawName = !string.IsNullOrEmpty(lineReading.RawName) ? lineReading.RawName : blockReading.RawName;
            return false;
        }

        private static CharacterHeaderReading ReadHeaderSide(string text, bool nameClipped)
        {
            CharacterHeaderReading reading;
            reading.Matched = TryResolveCharacterHeader(text, nameClipped, out reading.Name, out reading.Element, out reading.RawName);
            return reading;
        }

        private static bool HasConflictingHeaderEvidence(CharacterHeaderReading line, CharacterHeaderReading block,
            bool nameClipped, string resolvedName, out string agreement)
        {
            agreement = "no name corroboration";
            return (line.Matched || block.Matched) && !string.IsNullOrEmpty(line.Element) && !string.IsNullOrEmpty(block.Element)
                && (line.Element != block.Element || (line.Matched && block.Matched && line.Name != block.Name)
                    || !CharacterHeaderReadingsAgree(line.RawName, block.RawName, nameClipped, resolvedName, out agreement));
        }

        private static string DescribeConflictingHeaderEvidence(CharacterHeaderReading line, CharacterHeaderReading block,
            string agreement)
        {
            if (line.Element != block.Element) return "conflicting elements";
            if (line.Matched && block.Matched && line.Name != block.Name) return "conflicting canonical characters";
            return "unexplained name text; " + agreement;
        }

        private static bool ShouldUseLineHeaderReading(CharacterHeaderReading line, CharacterHeaderReading block)
        {
            return line.Matched && (!block.Matched || line.RawName.Length >= block.RawName.Length);
        }

        private static string DescribeMatchingHeaderEvidence(bool useLine, string block, string agreement)
        {
            return "matched " + (useLine ? "line" : "block") + "; wrapped-block=" +
                ((block ?? "").Contains("\n") || (block ?? "").Contains("\r")) + "; " + agreement;
        }

        internal static bool TryResolveCharacterHeader(string text, bool nameClipped,
            out string name, out string element, out string rawName)
        {
            name = null;
            element = null;
            rawName = null;
            // Ignore only leading decorative/empty slash fields. The meaningful
            // pre-slash field must end in a whole element line; preceding name
            // lines are retained, rather than searching arbitrary words for an element.
            var fields = (text ?? "").Split('/').Select(value => value.Trim()).ToList();
            while (fields.Count > 0 && !Regex.IsMatch(fields[0], @"[\p{L}\p{N}]", RegexOptions.None, RegexTimeouts.RegexTimeout)) fields.RemoveAt(0);
            if (fields.Count != 2) return false;
            // Long names wrap above and below the element/slash line. Tesseract
            // therefore emits "Yumemizuki\nAnemo / Mizuki". The element must
            // occupy the LAST whole pre-slash line; all preceding text remains
            // part of the name, in its observed order, rather than being dropped.
            var beforeSlash = fields[0].Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).Where(value => value.Length > 0).ToList();
            if (beforeSlash.Count == 0) return false;
            string observedElement = Regex.Replace(beforeSlash[beforeSlash.Count - 1], @"[^\p{L}]", "", RegexOptions.None, RegexTimeouts.RegexTimeout).ToLowerInvariant();
            if (!GenshinProcesor.Elements.TryGetValue(observedElement, out element)) return false;
            rawName = (string.Join(" ", beforeSlash.Take(beforeSlash.Count - 1)) + " " + fields[1]).FoldToOcrName();
            name = MatchCharacterHeaderName(rawName, element, nameClipped);
            return GenshinProcesor.CharacterMatchesElement(name, element);
        }

        /// <summary>Candidate catalogs shared by the character-header name matching tiers.</summary>
        private sealed class HeaderAliasCatalog
        {
            internal List<KeyValuePair<string, string>> Canonical;
            internal List<KeyValuePair<string, string>> MatchingCanonical;
            internal List<KeyValuePair<string, string>> Aliases;
        }

        internal static string MatchCharacterHeaderName(string rawName, string element, bool nameClipped)
        {
            string observed = (rawName ?? "").FoldToOcrName();
            if (observed.Length == 0 || !GenshinProcesor.IsValidElement(element)) return "";
            HeaderAliasCatalog catalog = BuildHeaderAliasCatalog(element);
            string match;
            if (TryMatchExactCanonicalName(catalog.Canonical, observed, element, out match)) return match;
            if (TryMatchExactAliasName(catalog.Aliases, observed, out match)) return match;
            if (observed.Length < 4) return "";
            if (TryRankHeaderAlias(catalog.Aliases, observed, out match)) return match;
            // Recover only a physically clipped, long canonical prefix. The final
            // partial glyph may have been substituted; full unknown/future names and
            // custom aliases never receive this lower-confidence tail repair.
            if (TryRepairClippedHeaderName(catalog.MatchingCanonical, observed, nameClipped, out match)) return match;
            return "";
        }

        private static HeaderAliasCatalog BuildHeaderAliasCatalog(string element)
        {
            var catalog = new HeaderAliasCatalog();
            catalog.Canonical = GenshinProcesor.Characters.Where(pair => pair.Value?["GOOD"] != null)
                .Select(pair => new KeyValuePair<string, string>(pair.Key.FoldToOcrName(), (string)pair.Value["GOOD"]))
                .ToList();
            catalog.MatchingCanonical = catalog.Canonical.Where(candidate => GenshinProcesor.CharacterMatchesElement(candidate.Value, element)).ToList();
            var aliases = new List<KeyValuePair<string, string>>(catalog.MatchingCanonical);
            var reserved = new HashSet<string>(catalog.Canonical.Select(candidate => candidate.Key), StringComparer.Ordinal);
            foreach (var pair in GenshinProcesor.Characters)
            {
                string alias = ((string)pair.Value?["CustomName"])?.FoldToOcrName();
                string good = (string)pair.Value?["GOOD"];
                // Real character names keep priority, including when their element does
                // not match; custom Traveler/Wanderer aliases cannot steal those names.
                if (!string.IsNullOrEmpty(alias) && !reserved.Contains(alias) && GenshinProcesor.CharacterMatchesElement(good, element))
                    aliases.Add(new KeyValuePair<string, string>(alias, good));
            }
            catalog.Aliases = aliases;
            return catalog;
        }

        private static bool TryMatchExactCanonicalName(List<KeyValuePair<string, string>> canonical, string observed,
            string element, out string match)
        {
            var exactCanonical = canonical.Where(candidate => candidate.Key == observed).Select(candidate => candidate.Value).Distinct().ToList();
            if (exactCanonical.Count > 0)
            {
                match = exactCanonical.Count == 1 && GenshinProcesor.CharacterMatchesElement(exactCanonical[0], element)
                    ? exactCanonical[0] : "";
                return true;
            }
            match = null;
            return false;
        }

        private static bool TryMatchExactAliasName(List<KeyValuePair<string, string>> aliases, string observed, out string match)
        {
            var exact = aliases.Where(candidate => candidate.Key == observed).Select(candidate => candidate.Value).Distinct().ToList();
            if (exact.Count > 0)
            {
                match = exact.Count == 1 ? exact[0] : "";
                return true;
            }
            match = null;
            return false;
        }

        private static bool TryRankHeaderAlias(List<KeyValuePair<string, string>> aliases, string observed, out string match)
        {
            var ranked = aliases.GroupBy(candidate => candidate.Value).Select(group => new
            {
                Name = group.Key,
                Score = group.Max(candidate => 100.0 * (1.0 - CharacterHeaderEditDistance(observed, candidate.Key) /
                    (double)Math.Max(observed.Length, candidate.Key.Length)))
            }).OrderByDescending(candidate => candidate.Score).ToList();
            if (ranked.Count > 0 && ranked[0].Score >= 90 &&
                (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= 7))
            {
                match = ranked[0].Name;
                return true;
            }
            match = null;
            return false;
        }

        private static bool TryRepairClippedHeaderName(List<KeyValuePair<string, string>> matchingCanonical, string observed,
            bool nameClipped, out string match)
        {
            if (!nameClipped || observed.Length < 8)
            {
                match = null;
                return false;
            }
            var clipped = matchingCanonical.Where(candidate => candidate.Key.Length > observed.Length &&
                candidate.Key.Length - observed.Length <= 4 && (candidate.Key.StartsWith(observed, StringComparison.Ordinal) ||
                    candidate.Key.StartsWith(observed.Substring(0, observed.Length - 1), StringComparison.Ordinal)))
                .Select(candidate => candidate.Value).Distinct().ToList();
            match = clipped.Count == 1 ? clipped[0] : "";
            return true;
        }

        private static bool CharacterHeaderTouchesRightEdge(Bitmap header)
        {
            int margin = Math.Max(3, (int)Math.Round(header.Height * .06));
            bool[] textRows = ComputeHeaderTextRows(header, margin);
            int minimumRun = Math.Max(8, (int)Math.Round(header.Height * .12));
            return HasClippedHeaderInkRun(header, textRows, margin, minimumRun);
        }

        private static bool[] ComputeHeaderTextRows(Bitmap header, int margin)
        {
            var textRows = new bool[header.Height];
            for (int y = header.Height / 6; y < header.Height * 5 / 6; ++y)
            {
                int foreground = 0;
                // Locate the gold header's actual text band from its left-hand
                // element/name text, independent of any glimmer at the crop edge.
                for (int x = 0; x < Math.Min(header.Width - margin, header.Width * 2 / 3); ++x)
                    if (IsCharacterHeaderGoldInk(header.GetPixel(x, y))) ++foreground;
                textRows[y] = foreground >= 5;
            }
            return textRows;
        }

        private static bool HasClippedHeaderInkRun(Bitmap header, bool[] textRows, int margin, int minimumRun)
        {
            for (int x = Math.Max(0, header.Width - margin); x < header.Width; ++x)
            {
                int run = 0;
                for (int y = 0; y < header.Height; ++y)
                {
                    // Solid glyph strokes stop a few pixels before an antialiased
                    // clipped edge. Require a sustained stroke inside the text
                    // band rather than counting isolated border/glow pixels.
                    if (textRows[y] && IsCharacterHeaderGoldInk(header.GetPixel(x, y)))
                    {
                        if (++run >= minimumRun) return true;
                    }
                    else run = 0;
                }
            }
            return false;
        }

        private static bool IsCharacterHeaderGoldInk(Color color)
        {
            return color.R > 170 && color.G > 140 && color.B < 215 && color.R >= color.G && color.G > color.B + 12;
        }

        private static bool CharacterHeaderReadingsAgree(string left, string right, bool clipped,
            string resolvedName, out string evidence)
        {
            evidence = "no conflicting name evidence";
            // An absent name is inconclusive, not a conflicting positive identity.
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return true;
            // An exact known name remains positive conflicting evidence even if
            // that reading's element made its full match invalid. For example,
            // Cryo/Ayato must not corroborate Cryo/Ayaka just because they differ
            // by two letters.
            foreach (var pair in GenshinProcesor.Characters)
            {
                if (pair.Value?["GOOD"] == null || (string)pair.Value["GOOD"] == resolvedName) continue;
                string knownName = pair.Key.FoldToOcrName();
                if (knownName == left || knownName == right)
                {
                    evidence = "known canonical name '" + knownName + "' conflicts with " + resolvedName;
                    return false;
                }
            }
            if (CharacterHeaderEditDistance(left, right) <= 2)
            {
                evidence = "nearby OCR names";
                return true;
            }
            if (clipped && Math.Abs(left.Length - right.Length) <= 4 &&
                (left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal)))
            {
                evidence = "clipped-name prefix corroboration";
                return true;
            }
            // A shorter OCR view can corroborate an independently matched FULL
            // canonical name. It cannot establish that identity by itself, and
            // custom aliases do not acquire partial-name matching through this path.
            string canonical = (resolvedName ?? "").FoldToOcrName();
            if (CharacterHeaderFragmentFits(left, canonical, out string leftEvidence) &&
                CharacterHeaderFragmentFits(right, canonical, out string rightEvidence))
            {
                evidence = "canonical corroboration: line " + leftEvidence + ", block " + rightEvidence;
                return true;
            }
            evidence = "neither nearby OCR nor compatible canonical fragments";
            return false;
        }

        private static bool CharacterHeaderFragmentFits(string fragment, string canonical, out string evidence)
        {
            evidence = "unrelated text";
            if (CharacterHeaderEditDistance(fragment, canonical) <= 2)
            {
                evidence = "full name";
                return true;
            }
            if (canonical.Length < 12 || fragment.Length > canonical.Length - 4) return false;
            if (fragment.Length >= 8 && canonical.StartsWith(fragment, StringComparison.Ordinal))
            {
                evidence = "long prefix";
                return true;
            }
            if (fragment.Length >= 5 && CharacterHeaderEditDistance(fragment,
                canonical.Substring(canonical.Length - fragment.Length)) <= 1)
            {
                evidence = "long suffix";
                return true;
            }
            return false;
        }

        private static int CharacterHeaderEditDistance(string left, string right)
        {
            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (int index = 0; index <= right.Length; ++index) previous[index] = index;
            for (int row = 1; row <= left.Length; ++row)
            {
                current[0] = row;
                for (int column = 1; column <= right.Length; ++column)
                    current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1),
                        previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
                var swap = previous;
                previous = current;
                current = swap;
            }
            return previous[right.Length];
        }

		private sealed class HeaderScanState
		{
			internal const int MaxUnknownAttempts = 5;
			internal int MaxAttempts;
			internal bool Quiet;
			internal bool HeaderMatched;
			internal string UnknownName;
			internal string UnknownElement;
			internal int UnknownAttempts;
			internal bool GiveUp;
		}

		private static void ScanNameAndElement(ref string name, ref string element, int maxAttempts = 75, bool quiet = false,
			Action<string> checkpoint = null)
		{
			int attempts = 0;

			// Some characters on the character screen are not in the character data at all
			// (e.g. newly released characters, new Traveler elements on outdated tables, or
			// game test avatars). Once OCR consistently reads the same unresolvable
			// name/element pair, retrying is pointless, so bail out early and let the scan
			// skip the character instead of exhausting the full attempt budget.
			var state = new HeaderScanState { MaxAttempts = maxAttempts, Quiet = quiet };

			Rectangle region = CharacterHeaderRegion();

			do
			{
				Navigation.SystemWait(Navigation.Speed.Fast);
				if (attempts % 5 == 0)
					CharacterNavigation.Require(CharacterScreenState.CharacterMenu, "read character name and element");
				using (Bitmap bm = Navigation.CaptureRegion(region))
				{
					string ocrName, headerText;
					state.HeaderMatched = TryReadCharacterHeader(bm, out name, out element, out ocrName, out headerText);
					string identity = CharacterIdentity(name, element, ocrName);
					if (identity != null) checkpoint?.Invoke(identity);
					TrackUnresolvedHeaderName(state, ref name, ref element, ocrName, headerText, bm);
					HeaderReadOutcome outcome = ResolveHeaderReadOutcome(state, ref name, ref element, headerText, bm, attempts);
					if (outcome != HeaderReadOutcome.Retry)
						return;
				}
				attempts++;
				Navigation.SystemWait(Navigation.Speed.Fast);
			} while ( attempts < maxAttempts );
			name = null;
			element = null;
		}

		private enum HeaderReadOutcome
		{
			Retry, Matched, Abandoned
		}

		private static void TrackUnresolvedHeaderName(HeaderScanState state, ref string name, ref string element,
			string ocrName, string headerText, Bitmap header)
		{
			if (string.IsNullOrWhiteSpace(ocrName) || state.HeaderMatched) return;

			// A name that is not in the character data, or a known character
			// whose read element is missing from its data, will never resolve
			// no matter how many times it is read.
			bool unknownCharacter = !string.IsNullOrWhiteSpace(ocrName) && !GenshinProcesor.IsValidCharacter(name);
			bool unknownPair = !unknownCharacter && GenshinProcesor.IsValidCharacter(name) && GenshinProcesor.IsValidElement(element);

			if (unknownCharacter || unknownPair)
				TrackRepeatedUnresolvedName(state, ocrName, name, element, headerText, header);
			else
			{
				state.UnknownName = null;
				state.UnknownElement = null;
				state.UnknownAttempts = 0;
			}

			// Clear the read on every failed match so a partially parsed
			// name/element pair is never used
			name = state.GiveUp ? null : "";
			element = state.GiveUp ? null : "";
		}

		private static void TrackRepeatedUnresolvedName(HeaderScanState state, string ocrName, string name,
			string element, string headerText, Bitmap header)
		{
			if (ocrName == state.UnknownName && element == state.UnknownElement)
				state.UnknownAttempts++;
			else
			{
				state.UnknownName = ocrName;
				state.UnknownElement = element;
				state.UnknownAttempts = 1;
			}
			if (state.UnknownAttempts == 1)
				Logger.Debug("Character header mismatch: raw name '{0}', resolved '{1}', observed element '{2}', catalog elements [{3}]; {4}",
					ocrName, name, element, string.Join(", ", GenshinProcesor.GetCharactersElements(name)), headerText);

			if (state.UnknownAttempts >= HeaderScanState.MaxUnknownAttempts)
			{
				if (!state.Quiet)
				{
					string evidence = $"./logging/characters/unknown_{ocrName}.png";
					Logger.Error("    [FAILURE] Character '{0}' ({1}) could not be matched to the character data. Skipping character. Resolved name '{2}', catalog elements [{3}], header OCR {4}; evidence {5}",
					ocrName, element, name, string.Join(", ", GenshinProcesor.GetCharactersElements(name)), headerText, Path.GetFullPath(evidence));
					SaveDiagnosticBitmap(header, evidence);
				}
				state.GiveUp = true;
			}
		}

		private static HeaderReadOutcome ResolveHeaderReadOutcome(HeaderScanState state, ref string name, ref string element,
			string headerText, Bitmap header, int attempts)
		{
			if (state.GiveUp) return HeaderReadOutcome.Abandoned;

			if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(element))
			{
				Logger.Debug("Scanned character name as {0} with element {1}; header OCR {2}", name, element, headerText);
				if (!state.Quiet) UserInterface.SetCharacter_NameAndElement(header, name, element);
				return HeaderReadOutcome.Matched;
			}
			Logger.Debug("Could not parse character name/element (Atempt {0}/{1}). Retrying...", attempts + 1, state.MaxAttempts);
			if (!state.Quiet) SaveDiagnosticBitmap(header, $"./logging/characters/{header.GetHashCode()}.png");
			return HeaderReadOutcome.Retry;
		}

		private static int ScanLevel(ref bool ascended)
		{
            int attempt = 0;

            var xRef = 1280.0;
			var yRef = 720.0;
			if (Navigation.GetAspectRatio() == new Size(8, 5))
			{
				yRef = 800.0;
			}

			Rectangle region =  new Rect(
				Left:   (int)( 960  / xRef * Navigation.GetWidth() ),
				Top:    (int)( 135  / yRef * Navigation.GetHeight() ),
				Right:  (int)( 1125 / xRef * Navigation.GetWidth() ),
				Bottom: (int)( 163  / yRef * Navigation.GetHeight() ));

			do
			{
				if (attempt % 10 == 0)
					CharacterNavigation.Require(CharacterScreenState.Attributes, "read character level and ascension");
				Bitmap bm = Navigation.CaptureRegion(region);

				bm = GenshinProcesor.ResizeImage(bm, bm.Width * 2, bm.Height * 2);
				Bitmap n = GenshinProcesor.ConvertToGrayscale(bm);
				GenshinProcesor.SetInvert(ref n);
				GenshinProcesor.SetContrast(30.0, ref bm);

				string text = GenshinProcesor.AnalyzeText(n).Trim();
				Logger.Debug("Scanned character level as {0}", text);

				text = Regex.Replace(text, @"(?![0-9/]).", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout);
				Logger.Debug("Filtered scanned text to {0}", text);
				if (text.Contains("/"))
				{
					var values = text.Split('/');
                    if (int.TryParse(values[0], out int level) && int.TryParse(values[1], out int maxLevel))
                    {
                        maxLevel = (int)Math.Round(maxLevel / 10.0, MidpointRounding.AwayFromZero) * 10;
                        ascended = 20 <= level && level < maxLevel;
                        UserInterface.SetCharacter_Level(bm, level, maxLevel);
                        n.Dispose();
                        bm.Dispose();
                        Logger.Debug("Parsed character level as {0}", level);
                        return level;
                    }
				}
				Logger.Debug("Failed to parse character level and ascension from {0} (text), retrying", text);

				attempt++;

                n.Dispose();
                bm.Dispose();
                Navigation.SystemWait(Navigation.Speed.Fast);
			} while (attempt < 50);

			return -1;
		}

		private static int ScanConstellations(Character character)
		{
			double yReference = 720.0;
			int constellation;

			if (Navigation.GetAspectRatio() == new Size(8, 5))
			{
				yReference = 800.0;
			}

			Rectangle constActivate =  new Rect(
				Left:   (int)( 70 / 1280.0 * Navigation.GetWidth() ),
				Top:    (int)( 665 / 720.0 * Navigation.GetHeight() ),
				Right:  (int)( 100 / 1280.0 * Navigation.GetWidth() ),
				Bottom: (int)( 695 / 720.0 * Navigation.GetHeight() ));

			for (constellation = 0; constellation < 6; constellation++)
			{
				// Select Constellation
				int yOffset = (int)( ( 180 + ( constellation * 75 ) ) / yReference * Navigation.GetHeight() );

				if (Navigation.GetAspectRatio() == new Size(8, 5))
				{
					yOffset = (int)( ( 225 + ( constellation * 75 ) ) / yReference * Navigation.GetHeight() );
				}

				CharacterNavigation.ClickDetail($"open {character.NameGOOD} constellation {constellation + 1}",
					1130 / 1280.0, yOffset / (double)Navigation.GetHeight(), CharacterScreenState.ConstellationDetails);

				if (Properties.Settings.Default.LogScreenshots)
				{
					using (var screenshot = Navigation.CaptureWindow())
						SaveDiagnosticBitmap(screenshot, $"./logging/characters/{character.NameGOOD}/constellation_{constellation + 1}.png");
				}

				// Grab Color
				using (Bitmap region = Navigation.CaptureRegion(constActivate))
				{
					// Check a small region next to the text "Activate"
					// for a mostly white backround
					ImageStatistics statistics = new ImageStatistics(region);
					if (statistics.Red.Mean >= 190 && statistics.Green.Mean >= 190 && statistics.Blue.Mean >= 190) 
						break;
					
				}
			}

			CharacterNavigation.CloseDetail(CharacterScreenState.ConstellationDetails, "close constellation details");
			UserInterface.SetCharacter_Constellation(constellation);
			return constellation;
		}

		private static Dictionary<string, int> ScanTalents(Character character)
		{
			var talents = new Dictionary<string, int>
			{
				{ "auto" , -1 },
				{ "skill", -1 },
				{ "burst", -1 }
			};

			int specialOffset = 0;

			// Check if character has a movement talent like
			// Mona or Ayaka
			if (character.NameGOOD.Contains("Mona") || character.NameGOOD.Contains("Ayaka")) specialOffset = 1;

			var xRef = 1280.0;
			var yRef = 720.0;

			if (Navigation.GetAspectRatio() == new Size(8, 5)) yRef = 800.0;

			// Ported from taiwenlee/Inventory_Kamera (MIT): 84874142
			// Non-16:9 aspect ratios previously produced a zero-sized region,
			// which made the talent level impossible to read.
			// x/width/height are identical for 16:9 and 16:10 today; re-split
			// them if real 16:10 values ever diverge.
			Rectangle region =  new Rectangle(
				x:		(int)(0.0003 * Navigation.GetWidth() ),
				y:		(int)((Navigation.IsNormal ? 0.1278 : 0.1078) * Navigation.GetHeight() ),
				width:	(int)(0.2913 * Navigation.GetWidth() ),
				height:	(int)(0.0711 * Navigation.GetHeight() ));

			for (int i = 0; i < 3; i++)
			{
				string talent;
				// Change y-offset for talent clicking
				int yOffset = (int)( 110 / yRef * Navigation.GetHeight() ) + ( i + ( ( i == 2 ) ? specialOffset : 0 ) ) * (int)(60 / yRef * Navigation.GetHeight() );

				CharacterNavigation.ClickDetail($"open {character.NameGOOD} talent {i + 1}",
					1130 / xRef, yOffset / (double)Navigation.GetHeight(), CharacterScreenState.TalentDetails);
                switch (i)
                {
					default:
						talent = "auto";
						break;
					case 1:
						talent = "skill";
						break;
					case 2:
						talent = "burst";
						break;
                }

				if (!TryScanTalentLevel(character, talents, talent, i, region)) break;
			}

			CharacterNavigation.CloseDetail(CharacterScreenState.TalentDetails, "close talent details");
			return talents;
		}

		private static bool TryScanTalentLevel(Character character, Dictionary<string, int> talents, string talent,
			int talentIndex, Rectangle region)
		{
			const int maxTalentAttempts = 50;
			int talentAttempts = 0;
			string lastTalentText = null;
			Logger.Info("        Scanning {0}'s {1} talent level...", character.NameGOOD, talent);
			while ((talents[talent] < 1 || talents[talent] > 15) && talentAttempts < maxTalentAttempts)
			{
				if (talentAttempts > 0 && talentAttempts % 10 == 0)
					CharacterNavigation.Require(CharacterScreenState.TalentDetails, $"retry {character.NameGOOD} {talent} talent OCR");
				Bitmap talentLevel;
				using (Bitmap captured = Navigation.CaptureRegion(region))
					talentLevel = GenshinProcesor.ResizeImage(captured, captured.Width * 2, captured.Height * 2);

				Bitmap n = GenshinProcesor.ConvertToGrayscale(talentLevel);
				GenshinProcesor.SetContrast(60, ref n);
				GenshinProcesor.SetInvert(ref n);

				lastTalentText = GenshinProcesor.AnalyzeText(n, Tesseract.PageSegMode.SingleBlock).Trim();
				var text = lastTalentText.Split('\n').ToList();

				if (int.TryParse(Regex.Replace(text[text.Count - 1], @"\D", string.Empty, RegexOptions.None, RegexTimeouts.RegexTimeout), out int level)
					&& level >= 1 && level <= 15)
				{
					talents[talent] = level;
					UserInterface.SetCharacter_Talent(talentLevel, level.ToString(), talentIndex);
				}

				n.Dispose();
				talentLevel.Dispose();

				talentAttempts++;
			}

			if (talents[talent] < 1 || talents[talent] > 15)
			{
				string evidence = Path.GetFullPath($"./logging/characters/{character.NameGOOD}/talent_{talent}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_failed.png");
				using (Bitmap screenshot = Navigation.CaptureWindow()) SaveDiagnosticBitmap(screenshot, evidence);
				CharacterNavigation.Require(CharacterScreenState.TalentDetails, "verify failed talent read before closing details");
				// The talent level could not be read (e.g. navigation issues or characters
				// without a usable talent page). Leave the talent invalid so this character
				// is skipped, instead of looping forever and stalling the whole scan.
				Logger.Error("        [FAILURE] Could not scan {0}'s {1} talent level after {2} attempts. Character will be skipped. Screen=TalentDetails, crop={3}, lastOCR='{4}', evidence={5}",
					character.NameGOOD, talent, maxTalentAttempts, region, lastTalentText?.Replace("\n", "\\n"), evidence);
				return false;
			}
			return true;
		}
	}
}
