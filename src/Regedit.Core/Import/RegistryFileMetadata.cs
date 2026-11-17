// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Import;

[Flags]
public enum RegistryFileFollowUp
{
    None = 0,
    ExplorerRestart = 1,
    Restart = 2,
    Logoff = 4
}

public sealed record RegistryFileMetadata(
    string Title,
    string Description,
    string Author,
    RegistryFileFollowUp FollowUp)
{
    public static RegistryFileMetadata Empty { get; } = new("", "", "", RegistryFileFollowUp.None);

    public bool HasContent => this.Title.Length > 0 || this.Description.Length > 0 || this.Author.Length > 0 ||
                              this.FollowUp != RegistryFileFollowUp.None;

    public string FollowUpText => string.Join(
        "; ",
        new[]
        {
            this.FollowUp.HasFlag(RegistryFileFollowUp.ExplorerRestart) ? "Restart Windows Explorer" : "",
            this.FollowUp.HasFlag(RegistryFileFollowUp.Logoff) ? "Sign out" : "",
            this.FollowUp.HasFlag(RegistryFileFollowUp.Restart) ? "Restart Windows" : ""
        }.Where(text => text.Length > 0));

    internal RegistryFileMetadata ReadDirective(string comment)
    {
        var equals = comment.IndexOf('=');
        if (equals < 0)
        {
            return this;
        }

        var key = comment[1..equals].Trim();
        var value = comment[(equals + 1)..].Trim();
        return key.ToUpperInvariant() switch
        {
            "TITLE" => this with { Title = value },
            "DESCRIPTION" => this with { Description = value },
            "AUTHOR" => this with { Author = value },
            "REQUIRES" => this with
            {
                FollowUp = this.FollowUp | (value.ToUpperInvariant() switch
                {
                    "EXPLORERRESTART" => RegistryFileFollowUp.ExplorerRestart,
                    "RESTART" => RegistryFileFollowUp.Restart,
                    "LOGOFF" => RegistryFileFollowUp.Logoff,
                    _ => RegistryFileFollowUp.None
                })
            },
            _ => this
        };
    }
}
