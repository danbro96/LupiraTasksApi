using System.Text.Json.Serialization;

namespace LupiraTasksApi.Dav;

[JsonConverter(typeof(JsonStringEnumConverter<DavCollectionKind>))]
public enum DavCollectionKind
{
    EventCalendar,
    TodoList,
    AddressBook,
}
