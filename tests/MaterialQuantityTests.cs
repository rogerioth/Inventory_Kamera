using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;

internal static class MaterialQuantityTests
{
    private static int Main(string[] args)
    {
        try
        {
            Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var scraper = Assembly.LoadFrom(Path.Combine(Environment.CurrentDirectory, "InventoryKamera.exe"))
                .GetType("InventoryKamera.MaterialScraper", true);
            var votes = scraper.GetMethod("ResolveQuantityVotes", BindingFlags.NonPublic | BindingFlags.Static);
            Equal(39, votes.Invoke(null, new object[] { new Dictionary<int, int> { [0] = 8, [39] = 3, [6] = 2 } }),
                "Return the winning quantity, ignoring failed reads");
            Equal(null, votes.Invoke(null, new object[] { new Dictionary<int, int> { [39] = 2, [6] = 2 } }),
                "Conflicting equally supported quantities remain unreadable");
            Equal(null, votes.Invoke(null, new object[] { new Dictionary<int, int> { [39] = 1 } }),
                "One fallback guess is insufficient");
            Equal(null, votes.Invoke(null, new object[] { new Dictionary<int, int> { [0] = 5 } }),
                "Failed reads never become a quantity");
            Console.WriteLine("PASS material quantity voting");
            var parse = scraper.GetMethod("ParseMaterialCount", BindingFlags.NonPublic | BindingFlags.Static);
            for (int argument = 0; argument < args.Length; argument++)
            {
                if (args[argument] == "--screenshot" && argument + 2 < args.Length)
                {
                    string path = args[++argument];
                    int quantity = int.Parse(args[++argument]);
                    Replay(parse, path, quantity);
                    continue;
                }
                if (args[argument] != "--capture-directory" || argument + 1 >= args.Length)
                    throw new ArgumentException("Expected --capture-directory <path> or --screenshot <path> <quantity>.");
                string directory = args[++argument];
                string[] names = { "EtherealCrystal", "EtherealCrystalscaleStone", "AdventurersExperience", "AgentsSacrificialKnife", "TatteredWarrant" };
                int[] expected = { 39, 6, 83, 168, 484 };
                for (int index = 0; index < names.Length; ++index)
                    Replay(parse, Path.Combine(directory, names[index] + "_Quantity.png"), expected[index]);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex.GetBaseException().Message);
            return 1;
        }
    }

    private static void Replay(MethodInfo parse, string path, int expected)
    {
        using (var crop = new Bitmap(path))
        {
            var parameters = new object[] { crop, null };
            object actual = parse.Invoke(null, parameters);
            Equal(expected, actual, Path.GetFileName(path) + " saved quantity: " + parameters[1]);
            Console.WriteLine("PASS {0}: {1}", Path.GetFileName(path), actual);
        }
    }

    private static void Equal(object expected, object actual, string description)
    {
        if (!Equals(expected, actual))
            throw new InvalidOperationException(description + ": expected " + (expected ?? "unreadable") + ", actual " + (actual ?? "unreadable"));
    }
}
