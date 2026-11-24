namespace Log.Core.Store;

public enum GroupType
{
    Class,
    PersonalProject,
    Unassigned
}

public static class GroupTypePolicy
{
    public static GroupType FromStorageValue(string value) => value switch
    {
        "class" => GroupType.Class,
        "personal-project" => GroupType.PersonalProject,
        "unassigned" => GroupType.Unassigned,
        _ => throw new InvalidDataException($"Unknown group type '{value}'.")
    };

}

public sealed record CatalogGroup(
    string Id,
    string Name,
    GroupType Type,
    long CreatedAtTicks,
    int SortOrder)
{
    public int VideoCount { get; init; }
}
