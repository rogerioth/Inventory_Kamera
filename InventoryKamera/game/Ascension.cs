using System;

namespace InventoryKamera
{
    /// <summary>
    /// Shared ascension math for characters and weapons. Both compute the
    /// ascension tier from level + ascended flag over the same bounds; only
    /// the maximum leveled tier differs (characters 100, weapons 90).
    /// </summary>
    internal static class AscensionMath
    {
        /// <summary>
        /// Returns the ascension tier (0-6) for a level/ascended pair.
        /// <paramref name="bounds"/> are the levels where the next tier
        /// becomes available; below a bound - or exactly on it while not
        /// ascended - the current tier applies. Levels beyond
        /// <paramref name="maxLevel"/> map back to 0.
        /// </summary>
        internal static int FromLevel(int level, bool ascended, int[] bounds, int maxLevel)
        {
            for (int i = 0; i < bounds.Length; i++)
            {
                int bound = bounds[i];
                if (level < bound || (level == bound && !ascended))
                {
                    return i;
                }
            }

            return level <= maxLevel ? bounds.Length : 0;
        }
    }
}
