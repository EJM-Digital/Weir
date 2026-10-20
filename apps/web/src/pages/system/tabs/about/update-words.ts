/** How this install got here, in words a reader outside engineering recognises. */
export function installTypeLabel(installType: string): string {
  switch (installType) {
    case "windows":
      return "Windows installer";
    case "docker":
      return "Docker";
    case "source":
      return "Built from source";
    default:
      return installType;
  }
}

/** "up to date" -> "Up to date": status pills across Weir are sentence case. */
function sentenceCase(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

const STATUS_LABELS: Readonly<Record<string, string>> = {
  up_to_date: "Up to date",
  update_available: "Update available",
  rate_limited: "Limited by GitHub",
  off: "Off",
};

/** What a status pill says about the update check. */
export function updateStatusLabel(status: string): string {
  return STATUS_LABELS[status] ?? sentenceCase(status.replaceAll("_", " "));
}
