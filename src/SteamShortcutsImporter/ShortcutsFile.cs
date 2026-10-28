using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Globalization;

namespace SteamShortcutsImporter;

public class SteamShortcut
{
    public string AppName { get; set; } = string.Empty;
    public string Exe { get; set; } = string.Empty;
    public string StartDir { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string ShortcutPath { get; set; } = string.Empty;
    public string LaunchOptions { get; set; } = string.Empty;
    public uint AppId { get; set; }
    public int IsHidden { get; set; } = 0;
    public int AllowDesktopConfig { get; set; } = 1;
    public int AllowOverlay { get; set; } = 1;
    public int OpenVR { get; set; } = 0;
    public List<string>? Tags { get; set; }
    internal Dictionary<string, object>? ExtraFields { get; set; }

    // A stable id derived from exe + app name similar to Steam's appid seed.
    // Paths are normalized (unquoted) to ensure consistency.
    public string StableId => Utils.HashString($"{Utils.NormalizePath(Exe)}|{AppName?.Trim() ?? string.Empty}");
}

public static class ShortcutsFile
{
    internal static string GetFileFingerprint(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
    }

    public static IEnumerable<SteamShortcut> Read(string path)
    {
        using var fs = File.OpenRead(path);
        var root = BinaryKv.ReadObject(fs);
        var list = new List<SteamShortcut>();
        if (root.Count == 0)
        {
            return list;
        }

        if (root.Count != 1
            || !root.TryGetValue(Constants.ShortcutsKey, out var shortcutsNode)
            || shortcutsNode is not Dictionary<string, object> shortcuts)
        {
            throw new InvalidDataException("shortcuts.vdf has an unexpected root structure.");
        }

        foreach (var kv in shortcuts.OrderBy(k => k.Key, Comparer<string>.Create(CompareShortcutKeys)))
        {
            if (kv.Value is not Dictionary<string, object> sc)
            {
                throw new InvalidDataException($"shortcuts.vdf entry '{kv.Key}' has an unexpected value type.");
            }

            list.Add(ParseShortcut(sc));
        }
        return list;
    }

    private static int CompareShortcutKeys(string left, string right)
    {
        var leftIsIndex = uint.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftIndex);
        var rightIsIndex = uint.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightIndex);
        if (leftIsIndex && rightIsIndex)
        {
            var numericCompare = leftIndex.CompareTo(rightIndex);
            return numericCompare != 0 ? numericCompare : StringComparer.Ordinal.Compare(left, right);
        }
        if (leftIsIndex) return -1;
        if (rightIsIndex) return 1;
        return StringComparer.Ordinal.Compare(left, right);
    }

    public static void Write(string path, IEnumerable<SteamShortcut> shortcuts)
    {
        // Build object tree
        var root = new Dictionary<string, object>();
        var shortcutsObj = new Dictionary<string, object>();
        int idx = 0;
        foreach (var sc in shortcuts)
        {
            shortcutsObj[idx.ToString()] = ToObject(sc);
            idx++;
        }
        root[Constants.ShortcutsKey] = shortcutsObj;

        // Ensure directory exists
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        BinaryKv.WriteObject(fs, root);
        fs.Flush(true);
    }

    /// <summary>
    /// Writes a validated temporary file on the same volume and atomically replaces the target.
    /// The callback is evaluated after validation and immediately before replacement.
    /// </summary>
    internal static void WriteAtomic(string path, IEnumerable<SteamShortcut> shortcuts, Func<bool>? canReplace = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A target path is required.", nameof(path));
        var expected = shortcuts?.ToList() ?? throw new ArgumentNullException(nameof(shortcuts));
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("The shortcuts.vdf directory could not be determined.");
        Directory.CreateDirectory(directory);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Write(tempPath, expected);
            var actual = Read(tempPath).ToList();
            var mismatch = DescribeRoundTripMismatch(expected, actual);
            if (mismatch != null)
            {
                throw new InvalidDataException("The temporary shortcuts.vdf did not match the requested shortcut list: " + mismatch);
            }

            if (canReplace != null && !canReplace())
            {
                throw new OperationCanceledException("The target was no longer safe to replace.");
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static string? DescribeRoundTripMismatch(IReadOnlyList<SteamShortcut> expected, IReadOnlyList<SteamShortcut> actual)
    {
        if (actual.Count != expected.Count) return $"shortcut count expected {expected.Count}, read {actual.Count}";

        for (var index = 0; index < expected.Count; index++)
        {
            var wanted = expected[index];
            var read = actual[index];
            var title = string.IsNullOrEmpty(wanted.AppName) ? "<unnamed>" : wanted.AppName;
            string? field = null;
            string? expectedValue = null;
            string? actualValue = null;

            if (read.AppId != wanted.AppId) { field = "AppId"; expectedValue = wanted.AppId.ToString(); actualValue = read.AppId.ToString(); }
            else if (!string.Equals(read.AppName, wanted.AppName, StringComparison.Ordinal)) { field = "AppName"; expectedValue = wanted.AppName; actualValue = read.AppName; }
            else if (!string.Equals((read.Exe ?? string.Empty).Trim('"'), (wanted.Exe ?? string.Empty).Trim('"'), StringComparison.OrdinalIgnoreCase)) { field = "Exe"; expectedValue = wanted.Exe; actualValue = read.Exe; }
            else if (!string.Equals(read.StartDir, wanted.StartDir, StringComparison.Ordinal)) { field = "StartDir"; expectedValue = wanted.StartDir; actualValue = read.StartDir; }
            else if (!string.Equals(read.LaunchOptions, wanted.LaunchOptions, StringComparison.Ordinal)) { field = "LaunchOptions"; expectedValue = wanted.LaunchOptions; actualValue = read.LaunchOptions; }
            else if (!string.Equals(read.Icon, wanted.Icon, StringComparison.Ordinal)) { field = "Icon"; expectedValue = wanted.Icon; actualValue = read.Icon; }
            else if (!(read.Tags ?? new List<string>()).SequenceEqual(wanted.Tags ?? new List<string>(), StringComparer.Ordinal))
            {
                field = "Tags";
                expectedValue = string.Join(" | ", wanted.Tags ?? new List<string>());
                actualValue = string.Join(" | ", read.Tags ?? new List<string>());
            }

            if (field != null)
            {
                return $"shortcut #{index} '{title}', field {field}: expected {FormatDiagnosticValue(expectedValue)}, read {FormatDiagnosticValue(actualValue)}";
            }
        }

        return null;
    }

    private static string FormatDiagnosticValue(string? value)
    {
        if (value == null) return "<null>";
        var normalized = value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return "\"" + (normalized.Length > 240 ? normalized.Substring(0, 240) + "…" : normalized) + "\"";
    }

    private static SteamShortcut ParseShortcut(Dictionary<string, object> obj)
    {
        string GetStr(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (obj.TryGetValue(k, out var v))
                {
                    return v?.ToString() ?? string.Empty;
                }
            }
            // case-insensitive fallback
            foreach (var kv in obj)
            {
                if (keys.Any(k => string.Equals(k, kv.Key, System.StringComparison.OrdinalIgnoreCase)))
                {
                    return kv.Value?.ToString() ?? string.Empty;
                }
            }
            return string.Empty;
        }

        int GetInt(params string[] keys)
        {
            foreach (var k in keys)
            {
                if (obj.TryGetValue(k, out var v) && v is int i)
                {
                    return i;
                }
            }
            foreach (var kv in obj)
            {
                if (keys.Any(k => string.Equals(k, kv.Key, System.StringComparison.OrdinalIgnoreCase)))
                {
                    if (kv.Value is int ii)
                    {
                        return ii;
                    }
                }
            }
            return 0;
        }

        static string Unquote(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            {
                return s.Substring(1, s.Length - 2);
            }
            return s;
        }

        var shortcut = new SteamShortcut
        {
            AppName = GetStr(Constants.AppNameKey, "AppName"),
            Exe = Unquote(GetStr(Constants.ExeKey, "Exe")),
            StartDir = GetStr(Constants.StartDirKey),
            Icon = GetStr(Constants.IconKey, "Icon"),
            ShortcutPath = GetStr(Constants.ShortcutPathKey),
            LaunchOptions = GetStr(Constants.LaunchOptionsKey),
            AppId = (uint)GetInt(Constants.AppIdKey, "AppId"),
            IsHidden = GetInt(Constants.IsHiddenKey),
            AllowDesktopConfig = GetInt(Constants.AllowDesktopConfigKey),
            AllowOverlay = GetInt(Constants.AllowOverlayKey),
            OpenVR = GetInt(Constants.OpenVRKey),
            ExtraFields = new Dictionary<string, object>(obj, System.StringComparer.Ordinal)
        };

        var tagsObj = obj.FirstOrDefault(kv => string.Equals(kv.Key, Constants.TagsKey, System.StringComparison.OrdinalIgnoreCase)).Value;
        if (tagsObj is Dictionary<string, object> tags)
        {
            shortcut.Tags = tags.OrderBy(k => k.Key).Select(k => k.Value?.ToString() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }

        return shortcut;
    }

    private static Dictionary<string, object> ToObject(SteamShortcut sc)
    {
        // Ensure exe is quoted for spaces, Steam expects quoted path
        var exeOut = sc.Exe ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(exeOut))
        {
            // if not already quoted, and contains space or colon+backslash pattern, quote it
            if (!(exeOut.Length >= 2 && exeOut[0] == '"' && exeOut[exeOut.Length - 1] == '"'))
            {
                exeOut = "\"" + exeOut + "\"";
            }
        }
        var dict = sc.ExtraFields == null
            ? new Dictionary<string, object>(System.StringComparer.Ordinal)
            : new Dictionary<string, object>(sc.ExtraFields, System.StringComparer.Ordinal);
        var knownKeys = new[]
        {
            Constants.AppNameKey, Constants.ExeKey, Constants.StartDirKey, Constants.IconKey,
            Constants.ShortcutPathKey, Constants.LaunchOptionsKey, Constants.AppIdKey,
            Constants.IsHiddenKey, Constants.AllowDesktopConfigKey, Constants.AllowOverlayKey,
            Constants.OpenVRKey, Constants.TagsKey,
            "AppName", "Exe", "StartDir", "Icon", "ShortcutPath", "LaunchOptions", "AppId"
        };
        foreach (var key in dict.Keys.Where(key => knownKeys.Any(known => string.Equals(key, known, System.StringComparison.OrdinalIgnoreCase))).ToList())
        {
            dict.Remove(key);
        }

        var known = new Dictionary<string, object>
        {
            [Constants.AppNameKey] = sc.AppName ?? string.Empty,
            [Constants.ExeKey] = exeOut,
            [Constants.StartDirKey] = sc.StartDir ?? string.Empty,
            [Constants.IconKey] = sc.Icon ?? string.Empty,
            [Constants.ShortcutPathKey] = sc.ShortcutPath ?? string.Empty,
            [Constants.LaunchOptionsKey] = sc.LaunchOptions ?? string.Empty,
            [Constants.AppIdKey] = unchecked((int)sc.AppId),
            [Constants.IsHiddenKey] = sc.IsHidden,
            [Constants.AllowDesktopConfigKey] = sc.AllowDesktopConfig,
            [Constants.AllowOverlayKey] = sc.AllowOverlay,
            [Constants.OpenVRKey] = sc.OpenVR,
        };

        foreach (var item in known) dict[item.Key] = item.Value;

        if (sc.Tags?.Any() == true)
        {
            var tags = new Dictionary<string, object>();
            int i = 0;
            foreach (var tag in sc.Tags)
            {
                tags[i.ToString()] = tag;
                i++;
            }
            dict[Constants.TagsKey] = tags;
        }

        return dict;
    }
}
