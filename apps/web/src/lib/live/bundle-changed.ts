function moduleScripts(root: ParentNode): string[] {
  return [...root.querySelectorAll("script[type=module][src]")]
    .map((script) => script.getAttribute("src") as string)
    .sort();
}

/**
 * Whether the server now serves a different build of this app than the page is running. The page carries no build number
 * of its own, but each build's scripts are named for their contents, so a different set of scripts is a different build.
 * False when the server cannot be asked, since a page that cannot tell should stay as it is.
 */
export async function bundleHasChanged(): Promise<boolean> {
  try {
    const response = await fetch("/", { cache: "no-store" });
    if (!response.ok) return false;
    const served = new DOMParser().parseFromString(
      await response.text(),
      "text/html",
    );
    const running = moduleScripts(document);
    const latest = moduleScripts(served);
    return (
      latest.length > 0 &&
      (latest.length !== running.length ||
        latest.some((src, index) => src !== running[index]))
    );
  } catch {
    return false;
  }
}
