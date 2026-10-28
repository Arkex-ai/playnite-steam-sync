using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Xunit;

namespace SteamShortcutsImporter.Tests;

public class ParserTests
{
    [Fact]
    public void RoundTripsSampleVdf()
    {
        var tmp = Path.Combine(Path.GetTempPath(),
            "shortcuts_" + Guid.NewGuid().ToString("N") + ".vdf");

        try
        {
            var sample = new[]
            {
                new SteamShortcut
                {
                    AppName = "Sample Game",
                    Exe = "C:/Games/Sample/Sample.exe",
                    StartDir = "C:/Games/Sample",
                    LaunchOptions = "-windowed",
                    Tags = new System.Collections.Generic.List<string>{"Action"}
                }
            };

            ShortcutsFile.Write(tmp, sample);
            Assert.True(File.Exists(tmp));

            var items = ShortcutsFile.Read(tmp).ToList();
            Assert.True(items.Count == 1);

            var first = items.First();
            Assert.Equal("Sample Game", first.AppName);
            Assert.Contains("Sample.exe", first.Exe);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void WriteAtomic_PreservesNumericShortcutOrderPastNineEntries()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_order_" + Guid.NewGuid().ToString("N") + ".vdf");

        try
        {
            var shortcuts = Enumerable.Range(0, 12)
                .Select(index => new SteamShortcut { AppName = "Game " + index, Exe = "game" + index + ".exe", AppId = (uint)(1000 + index) })
                .ToList();

            ShortcutsFile.WriteAtomic(tmp, shortcuts);

            var loaded = ShortcutsFile.Read(tmp).ToList();
            Assert.Equal(shortcuts.Select(item => item.AppName), loaded.Select(item => item.AppName));
            Assert.Equal(shortcuts.Select(item => item.AppId), loaded.Select(item => item.AppId));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void StableId_ConsistentAcrossQuotedAndUnquotedPaths()
    {
        var shortcut1 = new SteamShortcut
        {
            AppName = "Test Game",
            Exe = "C:\\Games\\Test\\game.exe"
        };

        var shortcut2 = new SteamShortcut
        {
            AppName = "Test Game",
            Exe = "\"C:\\Games\\Test\\game.exe\""
        };

        Assert.Equal(shortcut1.StableId, shortcut2.StableId);
    }

    [Fact]
    public void StableId_DifferentForDifferentGames()
    {
        var shortcut1 = new SteamShortcut
        {
            AppName = "Game 1",
            Exe = "game1.exe"
        };

        var shortcut2 = new SteamShortcut
        {
            AppName = "Game 2",
            Exe = "game2.exe"
        };

        Assert.NotEqual(shortcut1.StableId, shortcut2.StableId);
    }

    [Fact]
    public void WriteAtomic_PreservesUnknownShortcutFields()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_unknown_" + Guid.NewGuid().ToString("N") + ".vdf");
        try
        {
            var shortcut = new SteamShortcut
            {
                AppName = "Preserve Me",
                Exe = @"C:\\Games\\Preserve.exe",
                AppId = 123456,
                ExtraFields = new Dictionary<string, object>
                {
                    ["FutureIntField"] = 42,
                    ["FutureObjectField"] = new Dictionary<string, object> { ["Nested"] = "kept" }
                }
            };
            ShortcutsFile.Write(tmp, new[] { shortcut });

            var loaded = ShortcutsFile.Read(tmp).Single();
            loaded.AppName = "Preserve Me Updated";
            ShortcutsFile.WriteAtomic(tmp, new[] { loaded });

            using var stream = File.OpenRead(tmp);
            var root = BinaryKv.ReadObject(stream);
            var shortcuts = Assert.IsType<Dictionary<string, object>>(root[Constants.ShortcutsKey]);
            var raw = Assert.IsType<Dictionary<string, object>>(shortcuts["0"]);
            Assert.Equal(42, raw["FutureIntField"]);
            var nested = Assert.IsType<Dictionary<string, object>>(raw["FutureObjectField"]);
            Assert.Equal("kept", nested["Nested"]);
            Assert.Equal("Preserve Me Updated", raw[Constants.AppNameKey]);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void WriteAtomic_WhenSafetyCheckFails_LeavesOriginalUntouched()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_atomic_" + Guid.NewGuid().ToString("N") + ".vdf");
        try
        {
            ShortcutsFile.Write(tmp, new[] { new SteamShortcut { AppName = "Original", Exe = "game.exe", AppId = 22 } });
            var original = File.ReadAllBytes(tmp);
            var changed = new[] { new SteamShortcut { AppName = "Changed", Exe = "other.exe", AppId = 33 } };

            Assert.Throws<OperationCanceledException>(() => ShortcutsFile.WriteAtomic(tmp, changed, () => false));
            Assert.Equal(original, File.ReadAllBytes(tmp));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void WriteAtomic_WhenTargetIsLocked_LeavesOriginalUntouchedAndRemovesTemporaryFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_atomic_locked_" + Guid.NewGuid().ToString("N") + ".vdf");
        try
        {
            ShortcutsFile.Write(tmp, new[] { new SteamShortcut { AppName = "Original", Exe = "game.exe", AppId = 22 } });
            var original = File.ReadAllBytes(tmp);
            var changed = new[] { new SteamShortcut { AppName = "Changed", Exe = "other.exe", AppId = 33 } };

            using (File.Open(tmp, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => ShortcutsFile.WriteAtomic(tmp, changed));
            }

            Assert.Equal(original, File.ReadAllBytes(tmp));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(tmp)!, Path.GetFileName(tmp) + ".*.tmp"));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            foreach (var tempPath in Directory.GetFiles(Path.GetDirectoryName(tmp)!, Path.GetFileName(tmp) + ".*.tmp"))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void WriteAtomic_WhenSafetyCheckDetectsConcurrentUpdate_PreservesNewTargetAndRemovesTemporaryFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_atomic_race_" + Guid.NewGuid().ToString("N") + ".vdf");
        try
        {
            ShortcutsFile.Write(tmp, new[] { new SteamShortcut { AppName = "Original", Exe = "original.exe", AppId = 22 } });
            var originalFingerprint = ShortcutsFile.GetFileFingerprint(tmp);
            var concurrentShortcut = new SteamShortcut { AppName = "Concurrent update", Exe = "concurrent.exe", AppId = 44 };
            var queuedShortcut = new SteamShortcut { AppName = "Queued update", Exe = "queued.exe", AppId = 33 };
            var detectedConcurrentUpdate = false;

            Assert.Throws<OperationCanceledException>(() => ShortcutsFile.WriteAtomic(tmp, new[] { queuedShortcut }, () =>
            {
                ShortcutsFile.Write(tmp, new[] { concurrentShortcut });
                detectedConcurrentUpdate = !string.Equals(
                    originalFingerprint,
                    ShortcutsFile.GetFileFingerprint(tmp),
                    StringComparison.Ordinal);
                return !detectedConcurrentUpdate;
            }));

            Assert.True(detectedConcurrentUpdate);
            Assert.Equal("Concurrent update", ShortcutsFile.Read(tmp).Single().AppName);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(tmp)!, Path.GetFileName(tmp) + ".*.tmp"));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            foreach (var tempPath in Directory.GetFiles(Path.GetDirectoryName(tmp)!, Path.GetFileName(tmp) + ".*.tmp"))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public void StableId_TrimsWhitespaceInName()
    {
        var shortcut1 = new SteamShortcut
        {
            AppName = "Test Game",
            Exe = "game.exe"
        };

        var shortcut2 = new SteamShortcut
        {
            AppName = "  Test Game  ",
            Exe = "game.exe"
        };

        Assert.Equal(shortcut1.StableId, shortcut2.StableId);
    }

    [Fact]
    public void StableId_EmptyExeAndName_ReturnsConsistentHash()
    {
        var shortcut1 = new SteamShortcut { AppName = "", Exe = "" };
        var shortcut2 = new SteamShortcut { AppName = "", Exe = "" };

        Assert.Equal(shortcut1.StableId, shortcut2.StableId);
        Assert.False(string.IsNullOrEmpty(shortcut1.StableId));
    }

    [Fact]
    public void StableId_NullValues_HandledGracefully()
    {
        var shortcut = new SteamShortcut { AppName = null!, Exe = null! };
        
        // Should not throw and should return a valid hash
        var stableId = shortcut.StableId;
        Assert.False(string.IsNullOrEmpty(stableId));
    }

    [Fact]
    public void StableId_CaseSensitiveForName()
    {
        var shortcut1 = new SteamShortcut { AppName = "Test Game", Exe = "game.exe" };
        var shortcut2 = new SteamShortcut { AppName = "test game", Exe = "game.exe" };

        // Names are case-sensitive
        Assert.NotEqual(shortcut1.StableId, shortcut2.StableId);
    }

    [Fact]
    public void StableId_ForwardAndBackslashPaths_AreDifferent()
    {
        // Paths with different separators should produce different IDs
        // (normalization only removes quotes, doesn't change separators)
        var shortcut1 = new SteamShortcut { AppName = "Game", Exe = "C:\\Games\\game.exe" };
        var shortcut2 = new SteamShortcut { AppName = "Game", Exe = "C:/Games/game.exe" };

        // These should be different since we don't normalize path separators
        Assert.NotEqual(shortcut1.StableId, shortcut2.StableId);
    }

    [Fact]
    public void Read_NonexistentFile_ThrowsFileNotFoundException()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N") + ".vdf");
        
        Assert.Throws<FileNotFoundException>(() => ShortcutsFile.Read(nonExistentPath).ToList());
    }

    [Fact]
    public void Read_UnexpectedRootData_ThrowsInsteadOfSilentlyDroppingIt()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_unexpected_root_" + Guid.NewGuid().ToString("N") + ".vdf");
        try
        {
            using var stream = new MemoryStream();
            BinaryKv.WriteObject(stream, new Dictionary<string, object>
            {
                [Constants.ShortcutsKey] = new Dictionary<string, object>(),
                ["FutureRootField"] = "must be preserved"
            });
            File.WriteAllBytes(tmp, stream.ToArray());

            Assert.Throws<InvalidDataException>(() => ShortcutsFile.Read(tmp).ToList());
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Read_NonemptyRootWithoutShortcuts_ThrowsInvalidDataException()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_missing_root_key_" + Guid.NewGuid().ToString("N") + ".vdf");
        try
        {
            using var stream = new MemoryStream();
            BinaryKv.WriteObject(stream, new Dictionary<string, object> { ["Unexpected"] = "data" });
            File.WriteAllBytes(tmp, stream.ToArray());

            Assert.Throws<InvalidDataException>(() => ShortcutsFile.Read(tmp).ToList());
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Read_EmptyRootObject_ReturnsEmptyLibrary()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_empty_root_" + Guid.NewGuid().ToString("N") + ".vdf");
        try
        {
            using var stream = new MemoryStream();
            BinaryKv.WriteObject(stream, new Dictionary<string, object>());
            File.WriteAllBytes(tmp, stream.ToArray());

            Assert.Empty(ShortcutsFile.Read(tmp));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Read_NonObjectShortcutEntry_ThrowsInsteadOfDroppingIt()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_invalid_entry_" + Guid.NewGuid().ToString("N") + ".vdf");
        try
        {
            using var stream = new MemoryStream();
            BinaryKv.WriteObject(stream, new Dictionary<string, object>
            {
                [Constants.ShortcutsKey] = new Dictionary<string, object> { ["0"] = "unexpected scalar" }
            });
            File.WriteAllBytes(tmp, stream.ToArray());

            Assert.Throws<InvalidDataException>(() => ShortcutsFile.Read(tmp).ToList());
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Write_EmptyList_CreatesValidFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_empty_" + Guid.NewGuid().ToString("N") + ".vdf");

        try
        {
            ShortcutsFile.Write(tmp, Array.Empty<SteamShortcut>());
            
            Assert.True(File.Exists(tmp));
            
            // Should be readable and return empty list
            var items = ShortcutsFile.Read(tmp).ToList();
            Assert.Empty(items);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void Write_MultipleShortcuts_PreservesOrder()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_order_" + Guid.NewGuid().ToString("N") + ".vdf");

        try
        {
            var shortcuts = new[]
            {
                new SteamShortcut { AppName = "Game A", Exe = "a.exe" },
                new SteamShortcut { AppName = "Game B", Exe = "b.exe" },
                new SteamShortcut { AppName = "Game C", Exe = "c.exe" }
            };

            ShortcutsFile.Write(tmp, shortcuts);
            var items = ShortcutsFile.Read(tmp).ToList();

            Assert.Equal(3, items.Count);
            Assert.Equal("Game A", items[0].AppName);
            Assert.Equal("Game B", items[1].AppName);
            Assert.Equal("Game C", items[2].AppName);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_fields_" + Guid.NewGuid().ToString("N") + ".vdf");

        try
        {
            var original = new SteamShortcut
            {
                AppName = "Full Game",
                Exe = "C:\\Games\\Full\\full.exe",
                StartDir = "C:\\Games\\Full",
                Icon = "C:\\Games\\Full\\icon.ico",
                ShortcutPath = "",
                LaunchOptions = "-fullscreen -vsync",
                AppId = 0x80001234,
                IsHidden = 1,
                AllowDesktopConfig = 0,
                AllowOverlay = 1,
                OpenVR = 1,
                Tags = new System.Collections.Generic.List<string> { "Action", "RPG", "Favorite" }
            };

            ShortcutsFile.Write(tmp, new[] { original });
            var items = ShortcutsFile.Read(tmp).ToList();

            Assert.Single(items);
            var loaded = items[0];

            Assert.Equal(original.AppName, loaded.AppName);
            Assert.Contains("full.exe", loaded.Exe); // May be quoted
            Assert.Equal(original.StartDir, loaded.StartDir);
            Assert.Equal(original.Icon, loaded.Icon);
            Assert.Equal(original.LaunchOptions, loaded.LaunchOptions);
            Assert.Equal(original.AppId, loaded.AppId);
            Assert.Equal(original.IsHidden, loaded.IsHidden);
            Assert.Equal(original.AllowDesktopConfig, loaded.AllowDesktopConfig);
            Assert.Equal(original.AllowOverlay, loaded.AllowOverlay);
            Assert.Equal(original.OpenVR, loaded.OpenVR);
            Assert.NotNull(loaded.Tags);
            Assert.Equal(3, loaded.Tags!.Count);
            Assert.Contains("Action", loaded.Tags);
            Assert.Contains("RPG", loaded.Tags);
            Assert.Contains("Favorite", loaded.Tags);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void RoundTrip_HandlesSpecialCharactersInName()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_special_" + Guid.NewGuid().ToString("N") + ".vdf");

        try
        {
            var shortcuts = new[]
            {
                new SteamShortcut { AppName = "Game: The Sequel (2024)", Exe = "game.exe" },
                new SteamShortcut { AppName = "日本語ゲーム", Exe = "japanese.exe" },
                new SteamShortcut { AppName = "Émojis 🎮 & Symbols™", Exe = "emoji.exe" }
            };

            ShortcutsFile.Write(tmp, shortcuts);
            var items = ShortcutsFile.Read(tmp).ToList();

            Assert.Equal(3, items.Count);
            Assert.Equal("Game: The Sequel (2024)", items[0].AppName);
            Assert.Equal("日本語ゲーム", items[1].AppName);
            Assert.Equal("Émojis 🎮 & Symbols™", items[2].AppName);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    [Fact]
    public void RoundTrip_HandlesPathsWithSpaces()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "shortcuts_spaces_" + Guid.NewGuid().ToString("N") + ".vdf");

        try
        {
            var shortcut = new SteamShortcut
            {
                AppName = "Space Game",
                Exe = "C:\\Program Files (x86)\\My Games\\Space Game\\game.exe",
                StartDir = "C:\\Program Files (x86)\\My Games\\Space Game"
            };

            ShortcutsFile.Write(tmp, new[] { shortcut });
            var items = ShortcutsFile.Read(tmp).ToList();

            Assert.Single(items);
            // Exe should contain the path (may be quoted)
            Assert.Contains("Program Files (x86)", items[0].Exe);
            Assert.Contains("Space Game", items[0].Exe);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }
}
