namespace Weir.Tray;

/// <summary>
/// Where Weir's releases live: the one place the tray writes the repository's owner and name. The server, the web app and
/// the scripts each keep their own copy, and <c>scripts/check-repository-owner.mjs</c> fails when any of them differs or the
/// name is written anywhere else.
/// </summary>
static class ProjectLocation
{
    internal const string Owner = "EJM-Digital";
    internal const string Repo = "Weir";
    internal const string Slug = Owner + "/" + Repo;
    internal const string RepositoryUrl = "https://github.com/" + Slug;
    internal const string ApiRepositoryUrl = "https://api.github.com/repos/" + Slug;
}
