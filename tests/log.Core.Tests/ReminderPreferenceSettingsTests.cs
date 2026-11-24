using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class ReminderPreferenceSettingsTests
{
    [Fact]
    public void NewAndLegacySettingsDefaultEveryOptInToOff()
    {
        var defaults = ShellSettings.Defaults;
        var legacy = ShellSettings.FromValues(new Dictionary<string, object>
        {
            ["sourceMode"] = "screen"
        });

        Assert.False(defaults.StartAtSignInEnabled);
        Assert.False(defaults.WakeRemindersEnabled);
        Assert.False(defaults.WakeReminderActiveHoursEnabled);
        Assert.False(legacy.StartAtSignInEnabled);
        Assert.False(legacy.WakeRemindersEnabled);
        Assert.False(legacy.WakeReminderActiveHoursEnabled);
        Assert.True(legacy.IsWithinWakeReminderActiveHours(new TimeOnly(2, 0)));
    }

    [Fact]
    public void OptInsAndActiveHoursRoundTripThroughLocalValuesAndFallbackJson()
    {
        var settings = ShellSettings.Defaults with
        {
            StartAtSignInEnabled = true,
            WakeRemindersEnabled = true,
            WakeReminderActiveHoursEnabled = true,
            WakeReminderActiveHoursStart = new TimeOnly(9, 15),
            WakeReminderActiveHoursEnd = new TimeOnly(17, 45)
        };

        var values = settings.ToValues();

        Assert.Equal("09:15", values["wakeReminderActiveHoursStart"]);
        Assert.Equal("17:45", values["wakeReminderActiveHoursEnd"]);
        Assert.Equal(settings, ShellSettings.FromValues(values));

        var root = CreateTemporaryRoot();

        try
        {
            var path = Path.Combine(root, "settings.json");
            Assert.True(ShellSettingsFileStore.TrySave(
                path,
                settings,
                allowReplacingPreservedBytes: false,
                out var error), error);
            var loaded = ShellSettingsFileStore.Load(path);

            Assert.Null(loaded.Error);
            Assert.Equal(settings, loaded.Settings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ActiveHoursUseInclusiveStartExclusiveEndAndSupportOvernightWindows()
    {
        var daytime = ShellSettings.Defaults with
        {
            WakeReminderActiveHoursEnabled = true,
            WakeReminderActiveHoursStart = new TimeOnly(9, 0),
            WakeReminderActiveHoursEnd = new TimeOnly(17, 0)
        };

        Assert.False(daytime.IsWithinWakeReminderActiveHours(new TimeOnly(8, 59)));
        Assert.True(daytime.IsWithinWakeReminderActiveHours(new TimeOnly(9, 0)));
        Assert.True(daytime.IsWithinWakeReminderActiveHours(new TimeOnly(16, 59)));
        Assert.False(daytime.IsWithinWakeReminderActiveHours(new TimeOnly(17, 0)));

        var overnight = daytime with
        {
            WakeReminderActiveHoursStart = new TimeOnly(22, 0),
            WakeReminderActiveHoursEnd = new TimeOnly(6, 0)
        };

        Assert.False(overnight.IsWithinWakeReminderActiveHours(new TimeOnly(21, 59)));
        Assert.True(overnight.IsWithinWakeReminderActiveHours(new TimeOnly(22, 0)));
        Assert.True(overnight.IsWithinWakeReminderActiveHours(new TimeOnly(5, 59)));
        Assert.False(overnight.IsWithinWakeReminderActiveHours(new TimeOnly(6, 0)));
    }

    [Fact]
    public void EnabledButIncompleteOrEqualActiveHoursFailClosed()
    {
        var incomplete = ShellSettings.FromValues(new Dictionary<string, object>
        {
            ["wakeRemindersEnabled"] = true,
            ["wakeReminderActiveHoursEnabled"] = true,
            ["wakeReminderActiveHoursStart"] = "22:00"
        });
        var equal = ShellSettings.Defaults with
        {
            WakeReminderActiveHoursEnabled = true,
            WakeReminderActiveHoursStart = new TimeOnly(9, 0),
            WakeReminderActiveHoursEnd = new TimeOnly(9, 0)
        };

        Assert.False(incomplete.IsWithinWakeReminderActiveHours(new TimeOnly(22, 0)));
        Assert.False(equal.IsWithinWakeReminderActiveHours(new TimeOnly(9, 0)));
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "log-reminder-preference-settings",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void LegacyFallbackJsonWithoutPreferenceKeysRemainsOptedOut()
    {
        var root = CreateTemporaryRoot();

        try
        {
            var path = Path.Combine(root, "settings.json");
            File.WriteAllText(path, """{"sourceMode":"screen"}""");

            var loaded = ShellSettingsFileStore.Load(path);

            Assert.Null(loaded.Error);
            Assert.False(loaded.PreservedOriginalBytes);
            Assert.False(loaded.Settings.StartAtSignInEnabled);
            Assert.False(loaded.Settings.WakeRemindersEnabled);
            Assert.False(loaded.Settings.WakeReminderActiveHoursEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}