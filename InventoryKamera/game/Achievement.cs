using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InventoryKamera
{
    public sealed class AchievementCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Order { get; set; }
        public List<AchievementFamily> Families { get; set; } = new List<AchievementFamily>();
    }

    public sealed class AchievementFamily
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int CategoryId { get; set; }
        public List<AchievementDefinition> Tiers { get; set; } = new List<AchievementDefinition>();
    }

    public sealed class AchievementDefinition
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Reward { get; set; }
        public int TierIndex { get; set; }
    }

    public sealed class AchievementMatch
    {
        public AchievementFamily Family { get; set; }
        public AchievementDefinition Tier { get; set; }
        public double Confidence { get; set; }
        public string Error { get; set; }
        public bool Success => Family != null && Tier != null && string.IsNullOrEmpty(Error);
    }

    public sealed class AchievementObservation
    {
        public int CategoryId { get; set; }
        public int Page { get; set; }
        public int Top { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Progress { get; set; } = string.Empty;
        public string CompletionDate { get; set; } = string.Empty;
        public int? Reward { get; set; }
        public int? Total { get; set; }
        public int CompletedStars { get; set; }
        public bool CompletedText { get; set; }
        public bool Claimable { get; set; }
        public int? FamilyId { get; set; }
        public int? MatchedId { get; set; }
        public List<int> CompletedIds { get; set; } = new List<int>();
        public string Error { get; set; }
        public double Confidence { get; set; }
    }

    public sealed class AchievementCategoryScan
    {
        public int CategoryId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Pages { get; set; }
        public bool ReachedBottom { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
    }

    public sealed class AchievementScanResult
    {
        public Dictionary<int, int> Completed { get; set; } = new Dictionary<int, int>();
        public List<AchievementCategoryScan> Categories { get; set; } = new List<AchievementCategoryScan>();
        public List<AchievementObservation> Observations { get; set; } = new List<AchievementObservation>();
        public List<string> Issues { get; set; } = new List<string>();
        public int? ExpectedCompleted { get; set; }
        public int? FinalExpectedCompleted { get; set; }
        public bool Cancelled { get; set; }
        public bool Complete { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime FinishedAt { get; set; }

        [JsonIgnore]
        public bool HasData => (Completed != null && Completed.Count > 0)
            || (Observations != null && Observations.Count > 0)
            || (Categories != null && Categories.Any(category => category.Pages > 0));
    }
}
