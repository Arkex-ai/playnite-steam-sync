using System;
using System.Diagnostics;

namespace SteamShortcutsImporter;

/// <summary>
/// Utilities for detecting and managing Steam process state.
/// </summary>
internal static class SteamProcessHelper
{
    /// <summary>
    /// Determines whether Steam is running. Failure to inspect processes is treated as running.
    /// </summary>
    internal static bool TryIsSteamRunning(out bool isRunning)
    {
        isRunning = true;
        try
        {
            // Check for common Steam process names across platforms
            var processNames = new[] { "steam", "Steam" };
            
            foreach (var name in processNames)
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
if (processes.Any())
                        {
                            isRunning = true;
                            return true;
                        }
                }
                finally
                {
                    // Ensure array is always disposed, even if empty
                    foreach (var p in processes)
                    {
                        p.Dispose();
                    }
                }
            }
            
            isRunning = false;
            return true;
        }
        catch (Exception ex)
        {
            Playnite.SDK.LogManager.GetLogger().Warn(ex, "Failed to check if Steam is running.");
            isRunning = true;
            return false;
        }
    }

}
