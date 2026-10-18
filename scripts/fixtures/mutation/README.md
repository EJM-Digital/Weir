# Mutation report fixtures

Two real reports, trimmed, for `scripts/check-mutation-floors.test.mjs`.

- `dotnet/mutation-report.json`: Stryker.NET 5.0.0 on `Weir.Core`, mutating `LibraryFolderPublishing.cs` against
  `Weir.Core.Tests`. The files with no changes and the test sources are removed, and the checkout's folder name is replaced by
  `D:\a\Weir\Weir`, the shape of a runner's. Nothing else is edited.
- `web/mutation-report.json`: StrykerJS 10.0.0 (command runner) on `activity-remove-dialog.tsx` and `library-clean-dialog.tsx`.
  The long test output Stryker puts in each change's `statusReason` and the checkout's folder name are removed.

To record a new one, run the target (docs/coverage/mutation.md) and keep `mutation-report.json` from its report folder.
