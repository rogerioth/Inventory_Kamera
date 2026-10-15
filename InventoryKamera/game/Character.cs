using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace InventoryKamera
{
    [Serializable]
    public class Character
    {
        private string _nameKey;
        private string _element;

        [JsonProperty("key")]
        public string NameGOOD
        {
            get
            {
                return _nameKey == "Traveler" ? _nameKey + Element : _nameKey;
            }
            internal set
            {
                _nameKey = value;
            }
        }

        [JsonProperty("level")]
        public int Level { get; internal set; }

        [JsonProperty("constellation")]
        public int Constellation { get; internal set; }

        [JsonProperty("ascension")]
        public int Ascension
        { get { return AscensionLevel(); } internal set => _ = value; }

        [JsonProperty("talent")]
        public Dictionary<string, int> Talents { get; internal set; }

        [JsonIgnore]
        public string Element { get => _element; internal set => _element = value; }

        [JsonIgnore]
        public bool Ascended { get; internal set; }

        [JsonIgnore]
        public int Experience { get; internal set; }

        [JsonIgnore]
        public Weapon Weapon { get; internal set; }

        [JsonIgnore]
        public Dictionary<string, Artifact> Artifacts { get; internal set; }

        [JsonIgnore]
        public WeaponType WeaponType
        {
            // Validate the lookup: a character missing from the character data (or with
            // no WeaponType) must not throw a NullReferenceException during export.
            get
            {
                if (_nameKey != null &&
                    GenshinProcesor.Characters.TryGetValue(_nameKey.ToLower(), out var data) &&
                    data?["WeaponType"] != null)
                {
                    return data["WeaponType"].ToObject<WeaponType>();
                }
                return WeaponType.Sword;
            }
        }

        public Character()
        {
            Talents = new Dictionary<string, int>
            {
                ["auto"] = 0,
                ["skill"] = 0,
                ["burst"] = 0
            };
            Artifacts = new Dictionary<string, Artifact>();
        }

        public Character(string _name, string _element, int _level, bool _ascension, int _experience, int _constellation, int[] _talents) : this()
        {
            Element = _element;
            Level = _level;
            Ascended = _ascension;
            Experience = _experience;
            Constellation = _constellation;
            try
            {
                Talents["auto"] = _talents[0];
                Talents["skill"] = _talents[1];
                Talents["burst"] = _talents[2];
            }
            catch (Exception)
            {
                // Intentionally ignored: talents not present in the array keep their default value of 0.
            }
        }

        public bool IsValid()
        {
            return HasValidName() && HasValidLevel() && HasValidElement() && HasValidConstellation() && HasValidTalents();
        }

        public bool HasValidName()
        {
            return !string.IsNullOrWhiteSpace(NameGOOD) && GenshinProcesor.IsValidCharacter(NameGOOD);
        }

        public bool HasValidLevel()
        {
            // Genshin 6.x raised the character level cap to 100.
            // (Ported from taiwenlee/Inventory_Kamera (MIT): b8ee5b1d)
            return 1 <= Level && Level <= 100;
        }

        public bool HasValidElement()
        {
            return !string.IsNullOrWhiteSpace(Element) && GenshinProcesor.IsValidElement(Element)
                && GenshinProcesor.CharacterMatchesElement(_nameKey, Element);
        }

        public bool HasValidConstellation()
        {
            return 0 <= Constellation && Constellation <= 6;
        }

        public bool HasValidTalents()
        {
            if (Talents is null || Talents.Keys.Count != 3) return false;

            foreach (var value in Talents.Values) if (value < 1 || value > 15) return false;

            return true;
        }

        public void AssignWeapon(Weapon newWeapon)
        {
            Weapon = newWeapon;
        }

        public void AssignArtifact(Artifact artifact)
        {
            Artifacts[artifact.GearSlot] = artifact;
        }

        public int AscensionLevel()
        {
            if (Level < 20 || (Level == 20 && !Ascended))
            {
                return 0;
            }
            else if (Level < 40 || (Level == 40 && !Ascended))
            {
                return 1;
            }
            else if (Level < 50 || (Level == 50 && !Ascended))
            {
                return 2;
            }
            else if (Level < 60 || (Level == 60 && !Ascended))
            {
                return 3;
            }
            else if (Level < 70 || (Level == 70 && !Ascended))
            {
                return 4;
            }
            else if (Level < 80 || (Level == 80 && !Ascended))
            {
                return 5;
            }
            // Levels 81-100 map to ascension 6 like 81-90 did before the cap
            // was raised. (Ported from taiwenlee/Inventory_Kamera (MIT): b8ee5b1d)
            else if (Level <= 100)
            {
                return 6;
            }
            return 0;
        }

        public override string ToString()
        {
            var output = new StringBuilder("Character\n");
            output.Append($"Name: {NameGOOD}\n");
            output.Append($"Element: {Element}\n");
            output.Append($"Level: {Level}{(Ascended ? "+" : "")}\n");
            output.Append($"Ascension Level: {Ascension}\n");
            output.Append($"Constellation: {Constellation}\n");
            foreach (var item in Talents)
            {
                output.Append($"{item.Key.ToUpper()} : {item.Value}\n");
            }
            return output.ToString();
        }
    }
}
