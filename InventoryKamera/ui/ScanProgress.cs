using System;

namespace InventoryKamera
{
    public enum ScanPhase
    {
        Weapons,
        Artifacts,
        Characters,
        CharacterDevelopmentItems,
        Materials,
        Achievements,
        EquipmentProcessing
    }

    public sealed class ScanProgressUpdate
    {
        public ScanPhase Phase { get; }
        public int Current { get; }
        public int? Total { get; }
        public string Detail { get; }
        public bool Starting { get; }
        public bool Finished { get; }
        public bool TotalIsProvisional { get; }

        public ScanProgressUpdate(ScanPhase phase, int current, int? total, string detail, bool starting, bool finished,
            bool totalIsProvisional = false)
        {
            Phase = phase;
            Current = Math.Max(0, current);
            Total = total.HasValue && total.Value >= 0 ? total : null;
            Detail = detail;
            Starting = starting;
            Finished = finished;
            TotalIsProvisional = totalIsProvisional;
        }

        public int Percentage
        {
            get
            {
                // A provisional total (best observed roster count) still fills the
                // bar; it is clamped at 100 in case the denominator under-counts.
                if (Total.HasValue && Total.Value > 0) return (int)Math.Min(100, 100L * Current / Total.Value);
                if (Finished && Total == 0) return 100;
                return 0;
            }
        }

        public string Name
        {
            get
            {
                switch (Phase)
                {
                    case ScanPhase.CharacterDevelopmentItems: return "Development items";
                    case ScanPhase.EquipmentProcessing: return "Equipment OCR";
                    default: return Phase.ToString();
                }
            }
        }
    }
}
