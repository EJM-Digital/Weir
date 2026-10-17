namespace Weir.Core.Updates;

/// <summary>
/// Where Weir's source, releases and container image live: the one place the server writes the repository's owner and
/// name. The tray, the web app and the scripts each keep their own copy, and <c>scripts/check-repository-owner.mjs</c>
/// fails when any of them differs or the name is written anywhere else.
/// </summary>
public static class ProjectLocation
{
    public const string Owner = "jampat000";
    public const string Repo = "Weir";
    public const string Slug = Owner + "/" + Repo;
    public const string RepositoryUrl = "https://github.com/" + Slug;
    public const string ApiRepositoryUrl = "https://api.github.com/repos/" + Slug;

    /// <summary>The container image name: the registry needs it in lower case.</summary>
    public static readonly string DockerImage = "ghcr.io/" + Slug.ToLowerInvariant();
}
