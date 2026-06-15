namespace LupiraTasksApi.Application;

/// <summary>Configuration for share links (bound from the <c>Share</c> section).</summary>
public sealed class ShareLinkOptions
{
    public const string SectionName = "Share";

    /// <summary>Public base URL of the share web client; the returned link is <c>{LinkBaseUrl}/s/{token}</c>.</summary>
    public string LinkBaseUrl { get; set; } = "https://tasks.lupira.com";
}
