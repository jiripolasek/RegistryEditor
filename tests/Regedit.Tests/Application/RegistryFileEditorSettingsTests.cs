// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.ViewModels;

namespace JPSoftworks.Regedit.Tests.Application;

internal static class RegistryFileEditorSettingsTests
{
    internal static void Run(Action<bool, string> check)
    {
        var directory = Directory.CreateTempSubdirectory("RegeditEditorSettings.");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, "{}");
            var settings = AppSettings.Load(path);
            check(
                settings.RegistryFileLayout == RegistryFileLayout.Split && settings.EditorFont == AppSettings.DefaultEditorFont,
                "Existing profiles retain the split view and native editor font defaults");

            var savedProperties = new List<string>();
            var writes = 0;
            var model = new SettingsViewModel(
                settings, change =>
                {
                    writes++;
                    settings.UpdatePreferences(change, path);
                });
            model.PreferenceSaved += savedProperties.Add;
            model.EditorLayoutIndex = model.EditorLayoutIndex;
            model.EditorFontFamily = model.EditorFontFamily;
            model.EditorFontSize = model.EditorFontSize;
            model.EditorLayoutIndex = -1;
            model.EditorLayoutIndex = 3;
            check(writes == 0, "Initializing editor controls and transient selection indices do not save preferences");

            model.EditorLayoutIndex = 2;
            model.EditorFontFamily = "  Courier New  ";
            model.EditorFontSize = 17.5;
            var loaded = AppSettings.Load(path);
            check(
                loaded.RegistryFileLayout == RegistryFileLayout.Preview && loaded.EditorFont == new RegistryFont("Courier New", 17.5),
                "Editor view, trimmed family, and fractional point size round-trip through settings");
            check(
                savedProperties.SequenceEqual(
                    new[] { nameof(model.EditorLayoutIndex), nameof(model.EditorFontFamily), nameof(model.EditorFontSize) }),
                "Editor preference changes notify the host once per saved change");

            var saved = File.ReadAllBytes(path);

            void Reject(Action<AppSettings> change, string label)
            {
                var rejected = false;
                try
                {
                    settings.UpdatePreferences(
                        value =>
                        {
                            value.Theme = "Light";
                            change(value);
                        }, path);
                }
                catch (ArgumentException)
                {
                    rejected = true;
                }

                check(
                    rejected
                    && settings.Theme == "Default"
                    && settings.RegistryFileLayout == RegistryFileLayout.Preview
                    && settings.EditorFont == new RegistryFont("Courier New", 17.5)
                    && saved.SequenceEqual(File.ReadAllBytes(path)), label);
            }

            Reject(
                value => value.RegistryFileLayout = (RegistryFileLayout)99,
                "An invalid editor view restores all preferences and leaves the saved file intact");
            Reject(
                value => value.EditorFont = new RegistryFont(" ", 11),
                "A blank editor font restores all preferences and leaves the saved file intact");
            Reject(value => value.EditorFont = null!, "A missing editor font cannot be saved");
            foreach (var size in new[] { double.NaN, double.PositiveInfinity, 7, 33 })
            {
                Reject(
                    value => value.EditorFont = new RegistryFont("Consolas", size),
                    $"Invalid editor size {size} does not change accepted preferences");
            }

            model.EditorFontSize = double.NaN;
            check(
                model.EditorFontSize == 17.5 && model.IsSaveErrorOpen && savedProperties.Count == 3,
                "The editor font controls restore the accepted value and surface validation failures");

            var first = AppSettings.Load(path);
            var second = AppSettings.Load(path);
            first.UpdatePreferences(value => value.EditorFont = new RegistryFont("Consolas", 19), path);
            second.UpdatePreferences(value => value.RegistryFileLayout = RegistryFileLayout.Editor, path);
            loaded = AppSettings.Load(path);
            check(
                loaded.EditorFont == new RegistryFont("Consolas", 19) && loaded.RegistryFileLayout == RegistryFileLayout.Editor,
                "Independent windows merge editor view and font changes without overwriting one another");

            saved = File.ReadAllBytes(path);
            var failed = false;
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    loaded.UpdatePreferences(
                        value =>
                        {
                            value.RegistryFileLayout = RegistryFileLayout.Split;
                            value.EditorFont = new RegistryFont("Arial", 22);
                        }, path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                }
            }

            check(
                failed
                && loaded.RegistryFileLayout == RegistryFileLayout.Editor
                && loaded.EditorFont == new RegistryFont("Consolas", 19)
                && saved.SequenceEqual(File.ReadAllBytes(path)), "A failed settings write restores both editor preferences");
            check(!Directory.EnumerateFiles(directory.FullName, "*.tmp").Any(), "Editor preference save failures leave no temporary files");

            File.WriteAllText(path, """{"Theme":"Dark","RegistryFileLayout":99,"EditorFont":{"Family":" ","Size":100}}""");
            loaded = AppSettings.Load(path);
            check(
                loaded.LoadError is null
                && loaded.Theme == "Dark"
                && loaded.RegistryFileLayout == RegistryFileLayout.Split
                && loaded.EditorFont == AppSettings.DefaultEditorFont,
                "Malformed editor preferences fall back without discarding valid application preferences");
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
