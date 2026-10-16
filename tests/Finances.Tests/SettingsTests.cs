namespace Finances.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void Environment_beats_local_file_beats_committed_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "finances-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "appsettings.json"), """{"A": "base", "B": "base", "C": "base"}""");
        File.WriteAllText(Path.Combine(root, "appsettings.local.json"), """{"B": "local", "C": "local", "D": ""}""");
        Environment.SetEnvironmentVariable("FINANCES_TEST_C", "env");
        File.AppendAllText(Path.Combine(root, "appsettings.local.json"), "");
        try
        {
            var settings = Settings.Load(root);
            Assert.Equal("base", settings.Get("A"));
            Assert.Equal("local", settings.Get("B"));
            Assert.Null(settings.Get("D"));          // blank counts as unset
            Assert.Equal("fallback", settings.Get("E", "fallback"));
            var error = Assert.Throws<DataException>(() => settings.Require("E"));
            Assert.Contains("appsettings.local.json", error.Message);
            Assert.Equal("env", Settings.Load(root).Get("FINANCES_TEST_C"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FINANCES_TEST_C", null);
            Directory.Delete(root, recursive: true);
        }
    }
}
