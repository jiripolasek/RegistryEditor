// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Search;
using JPSoftworks.Regedit.Models.Registry;
using JPSoftworks.Regedit.Models.Tools;
using JPSoftworks.Regedit.ViewModels.Browser;


namespace JPSoftworks.Regedit.Tests.Application;

internal static class RegistryEntryIconTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var type in new RegistryValueType[]
                 {
                     RegistryValueType.String, RegistryValueType.ExpandString, RegistryValueType.MultiString
                 })
        {
            var entry = new RegistryEntry(null, new RegistryValue("Text", type, []));
            check(
                entry is { HasStringIcon: true, HasDataIcon: false, HasNavigationIcon: false, Glyph: "\uECE9" } &&
                entry.IconFontFamily == ToolPresentation.FluentSystemIconsFontFamily,
                $"{entry.Type} uses the string icon and color family");
        }

        var link = new RegistryEntry(null, new RegistryValue("Link", RegistryValueType.Link, []));
        check(
            link is
            {
                IsLink: true, HasLinkIcon: true, HasStringIcon: false, HasDataIcon: false, HasNavigationIcon: false,
                Glyph: "\uE71B"
            }, "REG_LINK uses a distinct link glyph and a separate neutral color family");

        var binary = new RegistryEntry(null, new RegistryValue("Bytes", RegistryValueType.Binary, []));
        check(
            binary is
            {
                HasBinaryIcon: true, HasStringIcon: false, HasDataIcon: false, HasLinkIcon: false,
                HasNavigationIcon: false
            }, "REG_BINARY uses the supplied binary vector icon");

        foreach (var type in new RegistryValueType[]
                 {
                     RegistryValueType.DWord, RegistryValueType.DWordBigEndian, RegistryValueType.QWord
                 })
        {
            var entry = new RegistryEntry(null, new RegistryValue("Number", type, []));
            check(
                entry is { HasDataIcon: true, HasStringIcon: false, HasNavigationIcon: false, Glyph: "\uE887" } &&
                entry.IconFontFamily == ToolPresentation.FluentSystemIconsFontFamily,
                $"{entry.Type} uses the Fluent System Icons number glyph and data color");
        }

        foreach (var type in new RegistryValueType[]
                 {
                     RegistryValueType.None, RegistryValueType.ResourceList,
                     RegistryValueType.FullResourceDescriptor, RegistryValueType.ResourceRequirementsList,
                     (RegistryValueType)uint.MaxValue
                 })
        {
            var entry = new RegistryEntry(null, new RegistryValue("Data", type, []));
            check(
                entry is
                {
                    HasStringIcon: false, HasBinaryIcon: false, HasDataIcon: true, HasNavigationIcon: false,
                    Glyph: "\uE943"
                }, $"{entry.Type} retains its numeric/data icon and color family");
        }

        var placeholder = new RegistryEntry(null, null, true);
        check(
            placeholder is
            {
                IsDefaultValue: true, HasStringIcon: true, HasDataIcon: false, Glyph: "\uE8EC",
                IconFontFamily: "Segoe Fluent Icons, Segoe MDL2 Assets"
            }, "Unset default value uses the tag glyph with the string color");
        var stringDefault = new RegistryEntry(null, new RegistryValue(string.Empty, RegistryValueType.String, []));
        check(
            stringDefault is
            {
                IsDefaultValue: true, HasStringIcon: true, Glyph: "\uE8EC",
                IconFontFamily: "Segoe Fluent Icons, Segoe MDL2 Assets"
            }, "Stored default string retains the tag glyph and its Segoe icon font");
        var numericDefault = new RegistryEntry(
            null, new RegistryValue(string.Empty, RegistryValueType.DWord, [1, 0, 0, 0]));
        check(
            numericDefault is
            {
                IsDefaultValue: true, HasDataIcon: true, HasStringIcon: false, Glyph: "\uE8EC",
                IconFontFamily: "Segoe Fluent Icons, Segoe MDL2 Assets"
            }, "Stored default uses the tag glyph and its numeric type color");
        var binaryDefault = new RegistryEntry(null, new RegistryValue(string.Empty, RegistryValueType.Binary, []));
        check(
            binaryDefault is { IsDefaultValue: true, HasBinaryIcon: false, HasDataIcon: true, Glyph: "\uE8EC" },
            "An unnamed binary value retains the tag glyph and data color");
        var literalName = new RegistryEntry(null, new RegistryValue("(Default)", RegistryValueType.String, []));
        check(
            !literalName.IsDefaultValue && literalName.Glyph == "\uECE9" &&
            !literalName.AccessibleName.Contains("unnamed default value") &&
            numericDefault.AccessibleName.Contains("unnamed default value"),
            "A named value spelled (Default) is visually and audibly distinct from the unnamed value");
        foreach (var entry in new[]
                 {
                     literalName, stringDefault, numericDefault, binaryDefault, link, binary,
                     new RegistryEntry(null, new RegistryValue("Dword", RegistryValueType.DWord, [])),
                     new RegistryEntry(null, new RegistryValue("BigEndian", RegistryValueType.DWordBigEndian, [])),
                     new RegistryEntry(null, new RegistryValue("Qword", RegistryValueType.QWord, []))
                 })
        {
            var inspector = new ValueInspectorViewModel(entry.Value!);
            check(
                inspector.Glyph == entry.Glyph && inspector.IconFontFamily == entry.IconFontFamily &&
                inspector.HasText == (entry.HasStringIcon || entry.HasLinkIcon) &&
                inspector.HasLinkIcon == entry.HasLinkIcon && inspector.HasBinaryIcon == entry.HasBinaryIcon &&
                inspector.HasDataIcon == entry.HasDataIcon,
                "Value inspection shares the list's type icon and color family, including the unnamed value");
        }

        var hive = RegistryLocation.Parse("HKCU");
        foreach (var value in new RegistryValue?[]
                 {
                     null, new("Text", RegistryValueType.String, []), new("Link", RegistryValueType.Link, []),
                     new("Data", RegistryValueType.DWord, []),
                     new("BigEndian", RegistryValueType.DWordBigEndian, []),
                     new("Qword", RegistryValueType.QWord, []), new("Bytes", RegistryValueType.Binary, []),
                     new(string.Empty, RegistryValueType.Binary, []), new(string.Empty, RegistryValueType.DWord, [])
                 })
        {
            var entry = new RegistryEntry(value is null ? hive : null, value);
            var row = new InvestigationRow(
                new RegistrySearchMatch(hive, value?.Name ?? string.Empty, value, "Name", "match"));
            check(
                row.Glyph == entry.Glyph && row.IconFontFamily == entry.IconFontFamily &&
                row.HasNavigationIcon == entry.HasNavigationIcon && row.HasStringIcon == entry.HasStringIcon &&
                row.HasLinkIcon == entry.HasLinkIcon && row.HasBinaryIcon == entry.HasBinaryIcon &&
                row.HasDataIcon == entry.HasDataIcon && !row.HasToolIcon,
                "Search matches reuse the registry list glyph and color family for keys and values");
        }

        foreach (var location in new[] { hive, hive.Child("Software") })
        {
            var entry = new RegistryEntry(location, null);
            check(
                entry is { HasNavigationIcon: true, HasStringIcon: false, HasDataIcon: false, Glyph: "\uE8B7" },
                $"{entry.Type} retains its navigation icon");
            var inspector = new ValueInspectorViewModel(entry);
            check(
                inspector is { HasKeyIcon: true, HasParentIcon: false, HasNavigationIcon: true },
                "Key inspection selects the Windows navigation icon renderer");
        }

        var parent = new RegistryEntry(null, null, ParentLocation: hive);
        check(
            parent is { HasNavigationIcon: true, HasStringIcon: false, HasDataIcon: false, Glyph: "\uE0C3" },
            "Virtual parent row uses the Fluent System Icons up glyph");
        var parentInspector = new ValueInspectorViewModel(parent);
        check(
            parentInspector is { HasParentIcon: true, HasKeyIcon: false, HasNavigationIcon: true, Glyph: "\uE0C3" },
            "Parent inspection selects the separate Fluent System Icons renderer");
    }
}
