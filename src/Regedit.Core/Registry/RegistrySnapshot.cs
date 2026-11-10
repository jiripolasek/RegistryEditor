// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Registry;

public sealed record RegistrySnapshot(string[] SubKeys, RegistryValue[] Values)
{
    public DateTimeOffset? LastWriteTimeUtc { get; init; }

    internal void PushChildren(Stack<RegistryLocation> stack, RegistryLocation location)
    {
        for (var i = this.SubKeys.Length - 1; i >= 0; i--)
        {
            stack.Push(location.Child(this.SubKeys[i]));
        }
    }
}
