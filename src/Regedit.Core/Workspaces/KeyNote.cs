// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;

namespace JPSoftworks.Regedit.Core.Workspaces;

public sealed record KeyNote(RegistryLocation Location, string Text)
{
    // Null denotes a key note. The empty name denotes the unnamed value.
    public string? ValueName { get; init; }

    public ValueNoteRule? MatchRule { get; init; }

    public bool Matches(RegistryLocation location)
    {
        return this.Location.IsSameLocation(location);
    }

    public bool HasSameTarget(KeyNote other)
    {
        return this.Matches(other.Location) &&
               string.Equals(this.ValueName, other.ValueName, StringComparison.OrdinalIgnoreCase) &&
               (this.MatchRule is null) == (other.MatchRule is null);
    }

    public bool AppliesAt(RegistryLocation location)
    {
        return this.MatchRule is null ? this.Matches(location) : location.IsAtOrBelow(this.Location);
    }

    public bool MatchesValue(RegistryLocation location, string name, RegistryValue? value)
    {
        return string.Equals(this.ValueName, name, StringComparison.OrdinalIgnoreCase) && this.AppliesAt(location) &&
               (this.MatchRule is not { } rule || value is not null &&
                   (rule.Type is null || rule.Type == value.Type) &&
                   (rule.ByteLength is null || rule.ByteLength == value.Data.Length));
    }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(this.Text);
        RegistryWorkspace.ValidateLocation(this.Location);
        if (this.ValueName is { } name)
        {
            RegistryService.ValidateValueName(name);
        }

        if (this.MatchRule is { } rule && (this.ValueName is null || rule.ByteLength is < 0))
        {
            throw new ArgumentException("A matching note requires a value name and a nonnegative byte length.");
        }
    }
}

public sealed record ValueNoteRule(RegistryValueType? Type, int? ByteLength)
{
    /// <summary>Formats the criteria as a suffix for the rule's scope summary.</summary>
    public string Describe()
    {
        return (this.Type is { } type ? " · " + RegistryValue.FormatType(type) : string.Empty) +
               (this.ByteLength is { } length ? $" · {length:N0} bytes" : string.Empty);
    }
}
