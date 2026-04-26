using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

// Bumps config.json forward as schema versions change so a long-running install doesn't
// silently drop fields. Each migration is idempotent — safe to run multiple times on an
// already-up-to-date config.
public static class ConfigMigrationService
{
    public const int CurrentSchemaVersion = 3;

    public static (bool Changed, string Summary) Migrate(AppConfig config)
    {
        bool changed = false;
        var notes = new List<string>();

        // Downgrade detection. If a future-version config is loaded by an older build (user
        // downgraded the app, or ran an older CLI against a GUI-managed config), we must NOT
        // silently clamp ConfigVersion backwards — that would overwrite the file with the old
        // schema and drop whatever new fields the newer build had stored. Leave the version
        // untouched and flag it so callers can surface a warning.
        if (config.ConfigVersion > CurrentSchemaVersion)
        {
            notes.Add(
                $"Config schema v{config.ConfigVersion} is newer than this build (v{CurrentSchemaVersion}). " +
                "Leaving settings untouched — downgrading would discard fields this build doesn't recognize.");
            return (false, string.Join(" ", notes));
        }

        // v0/v1 → v2: migration that ships in v4.2 — no-op if already v2.
        if (config.ConfigVersion < 2)
        {
            // Historical placeholder: v1 did not have PatchProfile. If it's unset here we keep
            // the Safe default that AppConfig's initializer provides.
            config.ConfigVersion = 2;
            notes.Add("Migrated v1 → v2 (added PatchProfile).");
            changed = true;
        }

        // v2 → v3 (v4.5.0): adopt the watchdog/log-rotation/auto-revert block. There are no
        // destructive field removals — we just stamp the new version number. Leaving the hook
        // here so future migrations have a clear precedent to follow.
        if (config.ConfigVersion < 3)
        {
            config.ConfigVersion = 3;
            notes.Add("Migrated v2 → v3 (watchdog / log rotation defaults).");
            changed = true;
        }

        return (changed, notes.Count == 0 ? "Config already at current schema." : string.Join(" ", notes));
    }
}
