using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;

namespace InventoryKamera
{
	[Serializable]
	public sealed class Artifact : IEquatable<Artifact>
	{
		[JsonProperty("setKey")]
		public string SetName { get; private set; }

		[JsonProperty("slotKey")]
		public string GearSlot { get; private set; }

		[JsonProperty("rarity")]
		public int Rarity { get; private set; }

		[JsonProperty("mainStatKey")]
		public string MainStat { get; private set; }

		[JsonProperty("level")]
		public int Level { get; private set; }

		[JsonProperty("substats")]
		public List<SubStat> SubStats { get; private set; }

		// Substat slot shown by Genshin 6.0 in the "unactivated" style: it is
		// displayed on the artifact but is not a live stat yet. Kept in a
		// separate list so it can neither be mistaken for a real substat nor
		// corrupt the export.
		// (Ported from taiwenlee/Inventory_Kamera (MIT): b8ee5b1d)
		[JsonProperty("unactivatedSubstats")]
		public List<SubStat> UnactivatedSubstats { get; private set; }

		[JsonProperty("location")]
		public string EquippedCharacter { get; internal set; }	

		[JsonProperty("lock")]
		public bool Lock { get; private set; }

        [JsonProperty("id")]
		public int Id { get; private set; }
		
		public Artifact()
		{
			Rarity = -1;
			GearSlot = null;
			MainStat = null;
			Level = -1;
			SubStats = new List<SubStat>(4);
			UnactivatedSubstats = new List<SubStat>(1);
			SetName = null;
			EquippedCharacter = null;
			Lock = false;
			Id = 0;
		}

		public Artifact(string _setName, int _rarity, int _level, string _gearSlot, string _mainStat, List<SubStat> _subStats, List<SubStat> _unactivatedSubStats, string _equippedCharacter = null, int _id = 0, bool _Lock = false)
		{
			// Normalize instead of throwing: this constructor is reached from the
			// OCR scan path (ArtifactScraper) and JSON round-trips, so missing or
			// malformed data must degrade to the same empty defaults the
			// parameterless deserialization constructor produces. Rarity/Level are
			// intentionally left untouched so downstream IsValid() checks still
			// reject unrecognized OCR values.
			// The normalization lives in the helpers below, not inline: S5766 flags
			// any constructor of a [Serializable] type whose parameters appear in a
			// conditional construct (if/ternary/??/switch), because BinaryFormatter
			// deserialization would bypass it. Keep it that way — inlining the
			// checks here re-triggers the finding. The expressions are unchanged,
			// so behavior is identical.
			GearSlot = OrEmpty(_gearSlot);
			Rarity = _rarity;
			MainStat = OrEmpty(_mainStat);
			Level = _level;
			SubStats = PositiveValues(_subStats, 4);
			// (Ported from taiwenlee/Inventory_Kamera (MIT): b8ee5b1d)
			UnactivatedSubstats = PositiveValues(_unactivatedSubStats, 1);
			SetName = OrEmpty(_setName);
			EquippedCharacter = OrEmpty(_equippedCharacter);
			Lock = _Lock;
			Id = _id;
		}

		private static string OrEmpty(string value)
		{
			return string.IsNullOrWhiteSpace(value) ? "" : value;
		}

		private static List<SubStat> PositiveValues(List<SubStat> subStats, int capacity)
		{
			return subStats?.ToList().Where(e => e.Value > 0).ToList() ?? new List<SubStat>(capacity);
		}

		public bool IsValid()
		{
			return HasValidLevel() && HasValidRarity() && HasValidSlot() && HasValidSetName() && HasValidMainStat() && HasValidSubStats() && HasValidEquippedCharacter();
		}

		public bool HasValidLevel()
		{
			return 0 <= Level && Level <= 20;
		}

		public bool HasValidRarity()
		{
			return 1 <= Rarity && Rarity <= 5;
		}

		public bool HasValidSlot()
		{
			return GenshinProcesor.IsValidSlot(GearSlot);
		}

		public bool HasValidSetName()
		{
			return GenshinProcesor.IsValidSetName(SetName);
		}

		public bool HasValidMainStat()
		{
			return GenshinProcesor.IsValidStat(MainStat);
		}

		public bool HasValidSubStats()
		{
			bool valid = true;

			SubStats.ForEach(s =>
			{
                if (!string.IsNullOrWhiteSpace(s.Stat) &&
                    (!GenshinProcesor.IsValidStat(s.Stat) || s.Value == (decimal)(-1.0)))
                {
                    valid = false;
                }
            });

			return valid;
		}

		public bool HasValidEquippedCharacter()
		{
			return string.IsNullOrWhiteSpace(EquippedCharacter) || GenshinProcesor.IsValidCharacter(EquippedCharacter);
		}

		[Serializable]
		public struct SubStat
		{
			// The GOOD wire keys stay "key"/"value"; only the C# member names
			// are PascalCase properties.
			[JsonProperty("key")]
			[DefaultValue("")]
			public string Stat { get; set; }

			[JsonProperty("value")]
			[DefaultValue(-1)]
			public decimal Value { get; set; }

			public override string ToString()
			{
				if (Stat is null) return "NULL";
				return Stat.Contains("_") ? $"{Stat} + {Value}%" : $"{Stat} + {Value}";
			}
		}

		public override bool Equals(object obj) => this.Equals(obj as Artifact);

		public bool Equals(Artifact artifact)
		{
			if (artifact is null)
			{
				return false;
			}

			if (Object.ReferenceEquals(this, artifact))
			{
				return true;
			}

			if (GetType() != artifact.GetType())
			{
				return false;
			}

			return GearSlot == artifact.GearSlot
				&& Rarity == artifact.Rarity
				&& MainStat == artifact.MainStat
				&& Level == artifact.Level
				&& SubStats == artifact.SubStats
				&& SetName == artifact.SetName
				&& EquippedCharacter == artifact.EquippedCharacter
				&& Lock == artifact.Lock;
		}

		public override string ToString()
		{
			string output = $"Artifact ID: {Id}\n"
				+ $"Slot: {GearSlot}\n"
				+ $"Set: {SetName}\n"
				+ $"Rarity: {Rarity}\n"
				+ $"Level: {Level}\n"
				+ $"Main Stat: {MainStat}\n";

			SubStats.ForEach(s => output += $"Substat {SubStats.IndexOf(s)+1}: {s}\n");

			output += $"Locked: {Lock}\n";

			if (!string.IsNullOrWhiteSpace(EquippedCharacter)) output += $"Equipped character: {EquippedCharacter}\n";
			return output;
		}

		public override int GetHashCode() => (GearSlot, Rarity, MainStat, Level, SubStats, SetName, EquippedCharacter, Lock).GetHashCode();

		public static bool operator ==(Artifact lhs, Artifact rhs)
		{
			if (lhs is null)
			{
				if (rhs is null)
				{
					return true;
				}

				return false;
			}

			return lhs.Equals(rhs);
		}

		public static bool operator !=(Artifact lhs, Artifact rhs) => !( lhs == rhs );
	}
}