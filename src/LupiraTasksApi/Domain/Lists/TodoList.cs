using JasperFx.Events;

namespace LupiraTasksApi.Domain.Lists;

/// <summary>A tag definition scoped to a single list.</summary>
public sealed class TagDef
{
    public Guid Id { get; set; }
    public string Label { get; set; } = "";
    public string Color { get; set; } = "";
}

/// <summary>A user's membership of a list.</summary>
public sealed class Member
{
    public string Email { get; set; } = "";
    public ListRole Role { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public string? AddedBy { get; set; }
}

/// <summary>
/// Inline Marten snapshot for the <c>TodoList</c> stream (stream id = ListId).
/// Holds list metadata, tag definitions, and membership. Single-stream aggregate:
/// every event carries the list id, so no LWW is needed here (membership/metadata
/// are last-write-wins by event order, which is fine for these low-contention fields).
/// </summary>
public sealed class TodoList
{
    public Guid Id { get; set; }
    public int Version { get; set; }

    public string Name { get; set; } = "";
    public ListKind Kind { get; set; }
    public string? Color { get; set; }
    public string OwnerEmail { get; set; } = "";

    public bool IsArchived { get; set; }
    public bool IsDeleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<TagDef> Tags { get; set; } = [];
    public List<Member> Members { get; set; } = [];

    public void Apply(IEvent<ListCreated> e)
    {
        var data = e.Data;
        Id = data.ListId;
        Name = data.Name;
        Kind = data.Kind;
        Color = data.Color;
        OwnerEmail = data.OwnerEmail;
        CreatedAt = e.Timestamp;
        UpdatedAt = e.Timestamp;

        // The creator is the first member, as Owner.
        Members.Add(new Member
        {
            Email = data.OwnerEmail,
            Role = ListRole.Owner,
            AddedAt = e.Timestamp,
            AddedBy = data.OwnerEmail,
        });
    }

    public void Apply(IEvent<ListRenamed> e)
    {
        Name = e.Data.Name;
        UpdatedAt = e.Timestamp;
    }

    public void Apply(IEvent<ListRecolored> e)
    {
        Color = e.Data.Color;
        UpdatedAt = e.Timestamp;
    }

    public void Apply(IEvent<ListArchived> e)
    {
        IsArchived = true;
        UpdatedAt = e.Timestamp;
    }

    public void Apply(IEvent<ListRestored> e)
    {
        IsArchived = false;
        UpdatedAt = e.Timestamp;
    }

    public void Apply(IEvent<ListDeleted> e)
    {
        IsDeleted = true;
        UpdatedAt = e.Timestamp;
    }

    public void Apply(IEvent<TagDefined> e)
    {
        var data = e.Data;
        var existing = Tags.Find(t => t.Id == data.TagId);
        if (existing is null)
        {
            Tags.Add(new TagDef { Id = data.TagId, Label = data.Label, Color = data.Color });
        }
        else
        {
            existing.Label = data.Label;
            existing.Color = data.Color;
        }

        UpdatedAt = e.Timestamp;
    }

    public void Apply(IEvent<TagRecolored> e)
    {
        var tag = Tags.Find(t => t.Id == e.Data.TagId);
        if (tag is not null) tag.Color = e.Data.Color;
        UpdatedAt = e.Timestamp;
    }

    public void Apply(IEvent<TagRemoved> e)
    {
        Tags.RemoveAll(t => t.Id == e.Data.TagId);
        UpdatedAt = e.Timestamp;
    }

    public void Apply(IEvent<MemberAdded> e)
    {
        var data = e.Data;
        var existing = Members.Find(m => m.Email == data.Email);
        if (existing is null)
        {
            Members.Add(new Member
            {
                Email = data.Email,
                Role = data.Role,
                AddedAt = e.Timestamp,
                AddedBy = EventActor.Of(e),
            });
        }
        else
        {
            existing.Role = data.Role;
        }

        UpdatedAt = e.Timestamp;
    }

    public void Apply(IEvent<MemberRoleChanged> e)
    {
        var member = Members.Find(m => m.Email == e.Data.Email);
        if (member is not null) member.Role = e.Data.Role;
        UpdatedAt = e.Timestamp;
    }

    public void Apply(IEvent<MemberRemoved> e)
    {
        Members.RemoveAll(m => m.Email == e.Data.Email);
        UpdatedAt = e.Timestamp;
    }
}
