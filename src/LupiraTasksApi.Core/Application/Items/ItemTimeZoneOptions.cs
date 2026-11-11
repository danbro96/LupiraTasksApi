namespace LupiraTasksApi.Core.Application.Items;

/// <summary>Binds <c>Tasks</c>. <see cref="DefaultTimezone"/> is the IANA zone a date-only due's day is read in.</summary>
public sealed class ItemTimeZoneOptions
{
    public const string SectionName = "Tasks";

    public string DefaultTimezone { get; set; } = "Europe/Stockholm";
}
