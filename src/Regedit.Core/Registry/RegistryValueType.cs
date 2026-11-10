// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Core.Registry;

public enum RegistryValueType : uint
{
    None = 0,
    String = 1,
    ExpandString = 2,
    Binary = 3,
    DWord = 4,
    DWordBigEndian = 5,
    Link = 6,
    MultiString = 7,
    ResourceList = 8,
    FullResourceDescriptor = 9,
    ResourceRequirementsList = 10,
    QWord = 11
}
