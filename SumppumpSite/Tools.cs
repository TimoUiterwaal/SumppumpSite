using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace SumppumpSite;

// Command-line helpers; see the top of Program.cs.
static class Tools
{
    public static void HashPassword()
    {
        Console.Write("Password: ");
        var password = ReadHidden();
        Console.Write("Again:    ");
        if (ReadHidden() != password)
        {
            Console.WriteLine("The two didn't match. Nothing printed.");
            return;
        }
        if (password.Length < 12)
            Console.WriteLine("Tip: this site will be on the internet; use at least 12 characters.");

        Console.WriteLine();
        Console.WriteLine("Auth:PasswordHash value:");
        Console.WriteLine(new PasswordHasher<string>().HashPassword("", password));
    }

    public static void NewApiKey()
    {
        Console.WriteLine("Api:IngestKey value (also goes in the ESP32's secrets.yaml as sump_api_key):");
        Console.WriteLine(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
    }

    // Reads a line without echoing it. Falls back to a normal read if there's no real console.
    static string ReadHidden()
    {
        try
        {
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
                if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
                if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
            }
        }
        catch (InvalidOperationException)
        {
            return Console.ReadLine() ?? "";
        }
    }
}
