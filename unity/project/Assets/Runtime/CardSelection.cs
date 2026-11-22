using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

// The card chosen on the command line: --card <id> --language <fr_FR|en_US|ja_JP|...> [--gold] [--diagnostics].
public static class CardSelection
{
    public static string Root { get; private set; }
    public static string Language { get; private set; }
    public static bool Gold { get; private set; }
    public static bool Diagnostics => Environment.GetCommandLineArgs().Contains("--diagnostics");
    public static CardPreview.CardConfig Config { get; private set; }
    static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    public static CardPreview.CardConfig Load(string root)
    {
        Root = root;
        Language = Argument("--language") ?? "fr_FR";
        Gold = Environment.GetCommandLineArgs().Contains("--gold");
        cardId = Argument("--card") ?? "PK_10_000840_00";
        Config = JsonUtility.FromJson<CardPreview.CardConfig>(Text("card.json"));
        int language = Array.IndexOf(Config.languages, Language);
        if (language < 0) throw new InvalidDataException("Missing language: " + Language);
        if (Gold && !Config.hasGold) throw new InvalidDataException("No gold frame for " + cardId);
        Config.displayName = Config.displayNames[language];
        SetWindowTitle(Config.displayName);
        return Config;
    }
    // Another card in the same process, for the web export's long-lived player.
    public static CardPreview.CardConfig Select(string card, string language, bool gold = false)
    {
        cardId = card;
        Language = language;
        Gold = gold;
        Config = JsonUtility.FromJson<CardPreview.CardConfig>(Text("card.json"));
        if (Gold && !Config.hasGold) throw new InvalidDataException("No gold frame for " + cardId);
        Config.displayName = Config.displayNames[Array.IndexOf(Config.languages, Language)];
        return Config;
    }
    // Unity has no window title API. Only this process's own Unity window is renamed.
    delegate bool WindowVisitor(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowVisitor visitor, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr window, System.Text.StringBuilder name, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr window, string text);
    static void SetWindowTitle(string title)
    {
        if (Application.isBatchMode) return;
        uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        EnumWindows((window, data) => {
            var name = new System.Text.StringBuilder(32);
            GetWindowThreadProcessId(window, out uint process);
            if (process == self && GetClassNameW(window, name, name.Capacity) > 0 && name.ToString() == "UnityWndClass")
                SetWindowTextW(window, title);
            return true;
        }, IntPtr.Zero);
    }
    static string cardId;
    public static void SetRoot(string root) { Root = root; }
    // A compiled card is one zip, cards/<id>.zip, its plans stored as they are; an IM card's movie
    // is the only file beside it, cards/<id>.mp4, as the video player needs a file of its own.
    public static string File(string name)
    {
        if (name != "movie.mp4") throw new ArgumentException("Card files are read with Text: " + name);
        return Path.Combine(Root, "cards", cardId + ".mp4");
    }
    public static string Text(string name)
    {
        using var file = System.IO.File.OpenRead(Path.Combine(Root, "cards", cardId + ".zip"));
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = archive.GetEntry(name) ?? throw new FileNotFoundException("Not in the card " + cardId + ": " + name);
        using var stream = entry.Open();
        if (!name.EndsWith(".gz")) { using var plain = new StreamReader(stream); return plain.ReadToEnd(); }
        using var unpacked = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        return unpacked.ReadToEnd();
    }
}
